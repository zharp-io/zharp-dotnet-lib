// ASP.NET Core (e2e/apps/WebApp): minimal API + controller, EF Core on Postgres or SQL Server.
import * as d from '../lib/docker.mjs';
import * as z from '../lib/zharp.mjs';
import { browserCall, collect, expectError, nothingElse, result } from '../lib/checks.mjs';

const DB = {
  pg: { url: 'Host=pg;Username=e2e;Password=e2e;Database=shop', unique: 'UniqueViolation', missing: 'UndefinedTable' },
  mssql: { url: 'Server=mssql;User Id=sa;Password=E2e_Passw0rd!;Database=shop;TrustServerCertificate=true', unique: /^(DuplicateKeyViolation|UniqueKeyViolation)$/, missing: 'InvalidObjectName' },
};

export function web({ tfm, db }) {
  const name = `web-${tfm}-${db}`;
  return {
    name,
    framework: 'aspnetcore',
    build: () => d.sh('e2e/build.sh', ['WebApp', tfm]),
    async run(app) {
      const container = `zharp-e2e-${name}`;
      const port = 18080;
      d.run(container, `mcr.microsoft.com/dotnet/sdk:${tfm.slice(3)}`, {
        env: {
          ASPNETCORE_URLS: 'http://+:8080', ZHARP_KEY: app.ingestKey, ZHARP_ENDPOINT: z.INGEST, ZHARP_ENVIRONMENT: 'e2e',
          ZHARP_LOGS: 'true', ZHARP_CAPTURE_BODIES: 'errors', DB: db, DB_URL: DB[db].url,
        },
        ports: [`${port}:8080`],
        mounts: [`${d.hostPath(d.ROOT)}/e2e/.out/WebApp-${tfm}:/app`],
        workdir: '/app',
        command: ['dotnet', 'WebApp.dll'],
      });
      const base = `http://localhost:${port}`;
      try {
        await d.waitHttp(`${base}/users/1`, container, 180000);
        const call = browserCall();
        await fetch(`${base}/hello`, { headers: call.headers });
        await fetch(`${base}/users/5`);
        await fetch(`${base}/boom`);
        await fetch(`${base}/boom-async`);
        await fetch(`${base}/api/orders/0`);
        await fetch(`${base}/users?email=dup@example.com`, { method: 'POST' });
        await fetch(`${base}/users?email=dup@example.com`, { method: 'POST' });
        await fetch(`${base}/db/caught`);
        await fetch(`${base}/log`);
        await fetch(`${base}/outbound`);
        await fetch(`${base}/echo`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name: 'Ada', password: 'hunter2', address: { city: 'Lagos', cvv: '123' } }) });
        await z.sleep(1000);
        await fetch(`${base}/exit`).catch(() => {});
        const exit = d.waitExit(container, 30000);
        const output = d.logs(container);
        const got = await collect(app, { traceIds: [call.traceId], wantIssues: 6, wantSpans: 10 });
        got.db = db;
        for (const n of ['GET /outbound', 'POST /echo']) {
          const span = got.spans.find((x) => x.name === n);
          if (span) got.traces[n] = await z.trace(span.traceId);
        }
        return { got, call, output, exit };
      } finally {
        d.remove(container);
      }
    },
    check({ got, call, output, exit }) {
      const out = [];
      const names = new Set(got.spans.map((s) => s.name));
      out.push(result('spans named by route', ['GET /users/{id:int}', 'POST /users', 'GET api/orders/{id}', 'GET /hello'].every((n) => names.has(n)), [...names].join(', ')));
      const trace = got.traces[call.traceId];
      const root = trace?.spans?.find((s) => s.name === 'GET /hello');
      out.push(result("browser's trace continued (traceparent)", root && root.parentSpanId === call.parentId, `parent=${root?.parentSpanId}`));
      out.push(result('user and session from baggage', root?.userId === 'u-42' && root?.sessionId === 's-9', `user=${root?.userId} session=${root?.sessionId}`));
      out.push(result('secret request headers filtered', root?.attributes?.['http.request.header.authorization'] === '[Filtered]' && root?.attributes?.['http.request.header.cookie'] === '[Filtered]' && root?.attributes?.['http.request.header.x-request-id'] === 'req-1'));
      const outbound = got.spans.find((s) => s.name === 'GET /outbound');
      const client = (got.traces['GET /outbound']?.spans ?? []).find((s) => s.kind === 'CLIENT');
      out.push(result('HttpClient call joined the trace', !!client, client?.name));
      out.push(result('server span of the call back in the same trace', got.spans.some((s) => s.traceId === outbound?.traceId && s.name === 'GET /users/{id:int}')));
      const errors = (e) => e.exception_type;
      out.push(...expectError(got, 'minimal API throw', (e) => /InvalidOperationException$/.test(errors(e)) && e.message === 'Cart is empty', { handled: false, context: true, attributes: { 'zharp.mechanism': 'aspnetcore' } }));
      out.push(...expectError(got, 'async throw', (e) => /KeyNotFoundException$/.test(errors(e)), { handled: false, context: true }));
      out.push(...expectError(got, 'controller throw', (e) => e.message === 'Order 0 is reserved', { handled: false }));
      const db = DB[got.db ?? 'pg'];
      out.push(...expectError(got, 'failed save (unique violation)', (e) => (db.unique instanceof RegExp ? db.unique.test(errors(e)) : errors(e) === db.unique), {
        handled: false, context: true, attributes: { 'db.statement': /INSERT INTO/, 'db.system': /postgresql|mssql/, 'db.error.code': /\d/, 'zharp.mechanism': 'aspnetcore' },
      }));
      out.push(...expectError(got, 'caught failed query', (e) => errors(e) === db.missing, { handled: true, attributes: { 'db.statement': /no_such_table/, 'zharp.mechanism': 'db' } }));
      out.push(...expectError(got, 'exception logged at Error', (e) => /ArgumentOutOfRangeException$/.test(errors(e)), { handled: true, context: true, attributes: { 'zharp.mechanism': 'logger' } }));
      out.push(nothingElse(got, [
        (e) => /InvalidOperationException$|KeyNotFoundException$|ArgumentOutOfRangeException$/.test(errors(e)),
        (e) => [db.unique, db.missing].some((t) => (t instanceof RegExp ? t.test(errors(e)) : t === errors(e))),
        // EF Core's EnsureDeleted/EnsureCreated probe for a database that isn't there yet: a real,
        // caught failed query at startup.
        (e) => /^(InvalidCatalogName|InvalidDatabase|LoginFailed)$/.test(errors(e)) && Number(e.handled) === 1,
      ]));
      const hello = got.logs.find((l) => l.body === 'Saying hello');
      out.push(result('ZHARP_LOGS=true: ILogger lines arrive, in their trace', hello && hello.traceId === call.traceId, hello ? `trace=${hello.traceId}` : `${got.logs.length} logs`));
      const logged = got.logs.find((l) => /^Validation failed for/.test(l.body ?? ''));
      out.push(result('the logged error line arrives without its exception', logged && !logged.attributes?.['exception.type'], JSON.stringify(logged?.attributes ?? {}).slice(0, 100)));
      out.push(result('console lines stamped with the trace', new RegExp(`Saying hello trace_id=${call.traceId} span_id=[0-9a-f]{16}`).test(output)));
      const echo = (got.traces['POST /echo']?.spans ?? []).find((s) => s.name === 'POST /echo');
      const body = echo?.attributes?.['http.request.body'] ?? '';
      out.push(result('captured request body with secrets filtered (ZHARP_CAPTURE_BODIES=errors)', /"password":"\[Filtered\]"/.test(body) && /"cvv":"\[Filtered\]"/.test(body) && /Lagos/.test(body), body.slice(0, 120)));
      const url = client?.attributes?.['url.full'] ?? '';
      out.push(result('secrets in outgoing URLs filtered', url.includes('/users/7') && !url.includes('abc'), url));
      out.push(result('outgoing secret header filtered', client?.attributes?.['http.request.header.x-api-key'] === '[Filtered]'));
      out.push(result('app stopped cleanly', exit === 0, `exit=${exit}`));
      return out;
    },
  };
}
