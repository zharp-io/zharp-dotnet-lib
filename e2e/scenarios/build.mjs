// Zharp.Build (e2e/apps/BuildApp): sources not embedded; the Release build uploads them as a source
// bundle under the PDB's ID, and the app's errors carry that ID (zharp.build_id).
import * as d from '../lib/docker.mjs';
import * as z from '../lib/zharp.mjs';
import { collect, eventsOf, result } from '../lib/checks.mjs';

export const buildBundle = {
  name: 'build-bundle',
  framework: 'dotnet',
  build: () => {},
  async run(app) {
    const key = await z.artifactKey(`dotnet-e2e-build-${Date.now() % 100000}`);
    try {
      // The build container sends to the backend on the host.
      const buildOutput = d.sh('e2e/build.sh', ['BuildApp', 'net10.0', '-v', 'n'], {
        env: { BUILD_ENV: `--add-host host.docker.internal:host-gateway -e ZHARP_APP_ID=${app.id} -e ZHARP_AUTH_TOKEN=${key.key} -e ZHARP_URL=http://host.docker.internal:3000` },
      });
      const container = 'zharp-e2e-build-bundle';
      d.run(container, 'mcr.microsoft.com/dotnet/sdk:10.0', {
        env: { ZHARP_KEY: app.ingestKey, ZHARP_ENDPOINT: z.INGEST, ZHARP_ENVIRONMENT: 'e2e' },
        mounts: [`${d.hostPath(d.ROOT)}/e2e/.out/BuildApp-net10.0:/app`],
        workdir: '/app',
        command: ['dotnet', 'BuildApp.dll'],
      });
      const exit = d.waitExit(container, 60000);
      const output = d.logs(container);
      d.remove(container);
      const got = await collect(app, { wantIssues: 1, wantSpans: 0 });
      const artifacts = await z.artifacts(app.id);
      // Without a token the step only says what it would upload.
      const dry = d.sh('e2e/build.sh', ['BuildApp', 'net10.0', '-v', 'n'], { env: { OUT_NAME: 'BuildApp-dry' } });
      return { got, artifacts, buildOutput: buildOutput.split('\n').filter((l) => /Zharp/.test(l)).join('\n'), dry: dry.split('\n').filter((l) => /Zharp/.test(l)).join('\n'), exit, output };
    } finally {
      await z.deleteKey(key.id);
    }
  },
  check({ got, artifacts, buildOutput, dry }) {
    const out = [];
    const list = Array.isArray(artifacts) ? artifacts : (artifacts?.artifacts ?? artifacts?.items ?? []);
    const bundle = list.find((a) => a.kind === 'source-bundle');
    out.push(result('the Release build uploaded a source bundle', !!bundle && /uploaded \d+ source file/.test(buildOutput), `${buildOutput} ${JSON.stringify(list).slice(0, 200)}`));
    const events = eventsOf(got, (e) => /InvalidOperationException$/.test(e.exception_type));
    out.push(result('the error was reported once', events.length === 1, `${events.length}`));
    const id = events[0]?.attributes?.['zharp.build_id'];
    out.push(result("the error's zharp.build_id is the bundle's ID", !!id && bundle && (bundle.debugId ?? bundle.debug_id) === id, `${id} vs ${bundle?.debugId ?? bundle?.debug_id}`));
    out.push(result('no source context without embedded sources (the bundle is the fallback)', !events[0]?.attributes?.['zharp.source_context']));
    out.push(result('without a token: a dry run, the build still succeeds', /dry run .* would upload 2 source file\(s\)/.test(dry), dry));
    return out;
  },
};
