// End to end: each scenario builds its app in Docker, runs it against a local Zharp backend with a
// throwaway app's key, drives it, and checks what Zharp stored. Every app (and API key) made here
// is deleted afterwards; KEEP=1 keeps the apps.
//
//   ZHARP_E2E_TOKEN_FILE=…/token.txt node e2e/run.mjs [scenario…]     (SKIP_BUILD=1 reuses e2e/.out)
//
// Needs: the backend on localhost:3000 (apps send to host.docker.internal:3000), Docker, and
// e2e/pack.sh run first (the apps reference the packages it builds). e2e/services.sh starts the
// databases.
import { mkdirSync, writeFileSync } from 'node:fs';
import * as z from './lib/zharp.mjs';
import * as d from './lib/docker.mjs';
import { SCENARIOS } from './scenarios/index.mjs';

const only = process.argv.slice(2);
const chosen = SCENARIOS.filter((s) => !only.length || only.includes(s.name));
if (!chosen.length) {
  console.log(`scenarios: ${SCENARIOS.map((s) => s.name).join(', ')}`);
  process.exit(2);
}
mkdirSync(new URL('./.out/runs/', import.meta.url), { recursive: true });
d.sh('e2e/services.sh', chosen.some((s) => s.mssql) ? ['mssql'] : []);
const summary = [];

for (const s of chosen) {
  console.log(`\n=== ${s.name}`);
  let app;
  try {
    if (process.env.SKIP_BUILD !== '1') s.build();
    app = await z.createApp(`dotnet-e2e-${s.name}-${Date.now() % 100000}`, s.framework);
    if (app.platformUsed !== 'dotnet') console.log(`  (platform "dotnet" rejected by this backend: app created as "${app.platformUsed}")`);
    const ran = await s.run(app);
    writeFileSync(new URL(`./.out/runs/${s.name}.json`, import.meta.url), JSON.stringify({ app: app.id, ...ran }, null, 1));
    const results = s.check(ran);
    for (const r of results) console.log(`  ${r.pass ? 'PASS' : 'FAIL'}  ${r.name}${r.detail && !r.pass ? `  — ${r.detail}` : ''}`);
    summary.push({ name: s.name, passed: results.filter((r) => r.pass).length, failed: results.filter((r) => !r.pass).map((r) => r.name) });
  } catch (error) {
    console.log(`  ERROR ${String(error.stack ?? error).slice(0, 3000)}`);
    summary.push({ name: s.name, passed: 0, failed: [`run: ${String(error.message).slice(0, 200)}`] });
  } finally {
    if (app && process.env.KEEP !== '1') await z.deleteApp(app.id);
  }
}

console.log('\n=== summary');
for (const r of summary) console.log(`${r.failed.length ? 'FAIL' : 'PASS'}  ${r.name}: ${r.passed} passed${r.failed.length ? `, failed: ${r.failed.join('; ')}` : ''}`);
process.exitCode = summary.some((r) => r.failed.length) ? 1 : 0;
