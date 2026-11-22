// AWS Lambda (the dotnet10 base image's Runtime Interface Emulator) and Google Cloud Run functions
// (the Functions Framework, an ASP.NET Core process). Each container is killed right after the
// last call: what Zharp stored was sent before the response.
import * as d from '../lib/docker.mjs';
import * as z from '../lib/zharp.mjs';
import { collect, expectError, nothingElse, result } from '../lib/checks.mjs';

export const lambda = {
  name: 'lambda',
  framework: 'aws-lambda',
  build: () => d.sh('e2e/build.sh', ['LambdaApp', 'net10.0']),
  async run(app) {
    const container = 'zharp-e2e-lambda';
    d.run(container, 'public.ecr.aws/lambda/dotnet:10', {
      env: { ZHARP_KEY: app.ingestKey, ZHARP_ENDPOINT: z.INGEST, ZHARP_ENVIRONMENT: 'e2e', AWS_LAMBDA_FUNCTION_NAME: 'e2e-orders' },
      ports: ['19000:8080'],
      mounts: [`${d.hostPath(d.ROOT)}/e2e/.out/LambdaApp-net10.0:/var/task:ro`],
      command: ['LambdaApp::LambdaApp.Function::Handler'],
    });
    const url = 'http://localhost:19000/2015-03-31/functions/function/invocations';
    try {
      const invoke = async (body) => {
        for (let i = 0; i < 30; i++) {
          try {
            const res = await fetch(url, { method: 'POST', body: JSON.stringify(body) });
            return await res.text();
          } catch {
            await z.sleep(1000);
          }
        }
        throw new Error(`the emulator never answered:\n${d.logs(container)}`);
      };
      const ok = await invoke({ sku: 'tea', quantity: 2 });
      const failed = await invoke({ sku: 'tea', quantity: 0 });
      d.docker(['kill', container]);
      const output = d.logs(container);
      const got = await collect(app, { wantIssues: 1, wantSpans: 2 });
      for (const s of got.spans) got.traces[s.traceId] = await z.trace(s.traceId);
      return { got, ok, failed, output };
    } finally {
      d.remove(container);
    }
  },
  check({ got, ok, failed }) {
    const out = [];
    out.push(result('the handler answered', /placed tea/.test(ok) && /Quantity must be positive/.test(failed), `${ok} | ${failed.slice(0, 100)}`));
    out.push(result('each invocation is a span, sent before the process froze', got.spans.length >= 2, `${got.spans.length} spans: ${got.spans.map((s) => s.name).join(', ')}`));
    const roots = Object.values(got.traces).flatMap((t) => t?.spans ?? []).filter((s) => !s.parentSpanId);
    const cold = roots.map((s) => s.attributes?.['faas.coldstart']);
    out.push(result('the first invocation is the cold start', cold.filter((c) => c === 'true').length === 1 && cold.includes('false'), JSON.stringify(cold)));
    out.push(...expectError(got, 'what the handler threw', (e) => /ArgumentOutOfRangeException$/.test(e.exception_type), { handled: false, context: true, attributes: { 'zharp.mechanism': 'aws-lambda' } }));
    out.push(nothingElse(got, [(e) => /ArgumentOutOfRangeException$/.test(e.exception_type)]));
    return out;
  },
};

export const gcf = {
  name: 'gcf',
  framework: 'gcp-functions',
  build: () => d.sh('e2e/build.sh', ['GcfApp', 'net10.0']),
  async run(app) {
    const container = 'zharp-e2e-gcf';
    d.run(container, 'mcr.microsoft.com/dotnet/sdk:10.0', {
      env: { ZHARP_KEY: app.ingestKey, ZHARP_ENDPOINT: z.INGEST, ZHARP_ENVIRONMENT: 'e2e', FUNCTION_TARGET: 'GcfApp.Checkout', PORT: '8080' },
      ports: ['18090:8080'],
      mounts: [`${d.hostPath(d.ROOT)}/e2e/.out/GcfApp-net10.0:/app`],
      workdir: '/app',
      command: ['dotnet', 'GcfApp.dll'],
    });
    try {
      await d.waitHttp('http://localhost:18090/?quantity=1', container, 120000);
      const ok = await (await fetch('http://localhost:18090/?quantity=2')).text();
      const status = (await fetch('http://localhost:18090/?quantity=0')).status;
      d.docker(['kill', container]);
      const output = d.logs(container);
      const got = await collect(app, { wantIssues: 1, wantSpans: 3 });
      return { got, ok, status, output };
    } finally {
      d.remove(container);
    }
  },
  check({ got, ok, status }) {
    const out = [];
    out.push(result('the function answered', ok === 'ok' && status === 500, `${ok} / ${status}`));
    out.push(result('invocations are spans named by the function, sent before the response', got.spans.filter((s) => s.name === 'GET GcfApp.Checkout').length >= 3, got.spans.map((s) => s.name).join(', ')));
    out.push(...expectError(got, 'what the function threw', (e) => /InvalidOperationException$/.test(e.exception_type), { handled: false, context: true, attributes: { 'zharp.mechanism': 'gcp-functions' } }));
    out.push(nothingElse(got, [(e) => /InvalidOperationException$/.test(e.exception_type)]));
    return out;
  },
};
