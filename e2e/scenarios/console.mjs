// The .NET tile without a host (e2e/apps/ConsoleApp) and .NET/HTTP, tracing off (e2e/apps/HttpLogsApp).
import * as d from '../lib/docker.mjs';
import * as z from '../lib/zharp.mjs';
import { collect, expectError, nothingElse, result } from '../lib/checks.mjs';

/** Run a published app to completion in a container. */
function runOnce(name, app, appName, tfm, vars, args = []) {
  const container = `zharp-e2e-${name}`;
  d.run(container, `mcr.microsoft.com/dotnet/sdk:${tfm.slice(3)}`, {
    env: { ZHARP_KEY: app.ingestKey, ZHARP_ENDPOINT: z.INGEST, ZHARP_ENVIRONMENT: 'e2e', ...vars },
    mounts: [`${d.hostPath(d.ROOT)}/e2e/.out/${appName}-${tfm}:/app`],
    workdir: '/app',
    command: ['dotnet', `${appName}.dll`, ...args],
  });
  const exit = d.waitExit(container, 60000);
  const output = d.logs(container);
  d.remove(container);
  return { exit, output };
}

export const consoleApp = {
  name: 'console',
  framework: 'dotnet',
  build: () => d.sh('e2e/build.sh', ['ConsoleApp', 'net10.0']),
  async run(app) {
    const { exit, output } = runOnce('console', app, 'ConsoleApp', 'net10.0', { ZHARP_LOGS: 'true', DB_URL: 'Host=pg;Username=e2e;Password=e2e;Database=shop' }, ['--crash']);
    const got = await collect(app, { wantIssues: 2, wantSpans: 1 });
    const job = got.spans.find((s) => s.name === 'import-orders');
    if (job) got.traces.job = await z.trace(job.traceId);
    return { got, exit, output };
  },
  check({ got, exit, output }) {
    const out = [];
    const job = got.spans.find((s) => s.name === 'import-orders');
    out.push(result('the job is a span (ZharpSdk.StartSpan)', !!job));
    out.push(...expectError(got, 'unhandled exception (the crash)', (e) => /InvalidOperationException$/.test(e.exception_type), { handled: false, context: true, attributes: { 'zharp.mechanism': 'uncaught' } }));
    out.push(...expectError(got, 'failed query the job survived', (e) => e.exception_type === 'UndefinedTable', { handled: true, attributes: { 'db.statement': /orders_archive/, 'db.system': 'postgresql', 'db.error.code': '42P01', 'zharp.mechanism': 'db' } }));
    out.push(nothingElse(got, [(e) => /InvalidOperationException$/.test(e.exception_type), (e) => e.exception_type === 'UndefinedTable']));
    const started = got.logs.find((l) => l.body === 'Starting import');
    out.push(result('ZHARP_LOGS=true: lines arrive, in the job\'s trace', started && job && started.traceId === job.traceId, started?.traceId));
    out.push(result('console lines stamped', job && new RegExp(`Starting import trace_id=${job.traceId} span_id=`).test(output), output.split('\n').find((l) => l.includes('Starting import'))));
    out.push(result('the crash still ends the process (exit code not 0)', exit !== 0 && exit !== null, `exit=${exit}`));
    return out;
  },
};

export const httpLogs = {
  name: 'http-logs',
  framework: 'dotnet-http',
  build: () => d.sh('e2e/build.sh', ['HttpLogsApp', 'net10.0']),
  async run(app) {
    const { exit, output } = runOnce('http-logs', app, 'HttpLogsApp', 'net10.0', { ZHARP_LOGS: 'true', SYNC_URL: 'http://host.docker.internal:3000/api/v1/no-such-route' });
    await z.sleep(5000);
    const got = await collect(app, { wantIssues: 1, wantSpans: 0 });
    return { got, exit, output };
  },
  check({ got, exit }) {
    const out = [];
    out.push(result('tracing off: no spans sent', got.spans.length === 0, `${got.spans.length} spans`));
    out.push(...expectError(got, 'logged HttpRequestException', (e) => /HttpRequestException$/.test(e.exception_type), { handled: true, context: true, attributes: { 'zharp.mechanism': 'logger' } }));
    out.push(nothingElse(got, [(e) => /HttpRequestException$/.test(e.exception_type)]));
    const bodies = got.logs.map((l) => l.body);
    out.push(result('logs arrive', bodies.includes('Sync started') && bodies.includes('Sync finished'), bodies.join(' | ').slice(0, 200)));
    out.push(result('exited cleanly', exit === 0, `exit=${exit}`));
    return out;
  },
};
