// What the backend makes of the SDK's .NET stacks (needs a backend with .NET frames, platform
// "dotnet"): frames parsed with file, line and function; the framework's frames not in-app; the
// culprit the app's frame; the code that ran on in-app frames; and the same error grouped into one
// issue — twice in one build, and again after a Release rebuild that moved every line.
import * as d from '../lib/docker.mjs';
import * as z from '../lib/zharp.mjs';
import { result, storedErrors } from '../lib/checks.mjs';

const SHIFT = 7;

async function runWebApp(app, outName, hits) {
  const container = 'zharp-e2e-stacks';
  d.run(container, 'mcr.microsoft.com/dotnet/sdk:10.0', {
    env: { ASPNETCORE_URLS: 'http://+:8080', ZHARP_KEY: app.ingestKey, ZHARP_ENDPOINT: z.INGEST, ZHARP_ENVIRONMENT: 'e2e', DB: 'pg', DB_URL: 'Host=pg;Username=e2e;Password=e2e;Database=shop' },
    ports: ['18081:8080'],
    mounts: [`${d.hostPath(d.ROOT)}/e2e/.out/${outName}:/app`],
    workdir: '/app',
    command: ['dotnet', 'WebApp.dll'],
  });
  try {
    await d.waitHttp('http://localhost:18081/users/1', container, 180000);
    for (const path of hits) await fetch(`http://localhost:18081${path}`);
    await z.sleep(1500);
    await fetch('http://localhost:18081/exit').catch(() => {});
    d.waitExit(container, 30000);
  } finally {
    d.remove(container);
  }
}

export const stacks = {
  name: 'stacks',
  framework: 'ASP.NET Core',
  build: () => {
    d.sh('e2e/build.sh', ['WebApp', 'net10.0']);
    d.sh('e2e/build.sh', ['WebApp', 'net10.0'], { env: { OUT_NAME: 'WebApp-shifted', SHIFT_LINES: String(SHIFT) } });
  },
  async run(app) {
    // Build A: each failure twice. Build B (every line of Program.cs 7 lower): once more.
    await runWebApp(app, 'WebApp-net10.0', ['/boom-async', '/boom-async', '/log', '/log']);
    await runWebApp(app, 'WebApp-shifted', ['/boom-async', '/log']);
    const issues = await z.until(() => z.issues(app.id), (list) => list.reduce((n, i) => n + (i.events ?? 0), 0) >= 6, 60000);
    const details = [];
    for (const i of issues) details.push(await z.issue(app.id, i.id));
    // Each issue's events, for the lines of both builds.
    const events = {};
    for (const i of issues) events[i.id] = (await z.api(`applications/${app.id}/issues/${i.id}/events`)).data;
    return { app: { id: app.id, platform: app.platformUsed }, issues, details, events, stored: storedErrors(app.id) };
  },
  check({ app, issues, details, stored }) {
    const out = [];
    out.push(result('app created as platform "dotnet"', app.platform === 'dotnet', app.platform));
    const byType = (re) => details.filter((x) => re.test(x.exceptionType));
    for (const [label, re, file, fn, framework] of [
      ['async request failure', /KeyNotFoundException$/, 'Program.cs', /LoadAsync/, true],
      // Caught and logged in the handler: its stack runs from the throw to the catch, all app code.
      ['exception logged at Error', /ArgumentOutOfRangeException$/, 'Program.cs', /Validate/, false],
    ]) {
      const found = byType(re);
      out.push(result(`${label}: one issue across both builds`, found.length === 1, `${found.length} issues: ${found.map((x) => x.title).join(' | ')}`));
      const issue = found[0];
      if (!issue) continue;
      out.push(result(`${label}: 3 events (twice in build A, once after the rebuild)`, issue.events === 3, `events=${issue.events}`));
      const frames = issue.latestEvent?.frames ?? [];
      const first = frames[0];
      out.push(result(`${label}: frames parsed (function, file, line)`, !!first && fn.test(first.function) && first.file.endsWith(file) && Number.isInteger(first.line), JSON.stringify(first ?? {}).slice(0, 200)));
      const fw = frames.filter((f) => /^(System|Microsoft)\./.test(f.function));
      if (framework) out.push(result(`${label}: System/Microsoft frames not in-app`, fw.length > 0 && fw.every((f) => !f.inApp), `${fw.length} framework frames, in-app: ${fw.filter((f) => f.inApp).length}`));
      out.push(result(`${label}: no frame with arguments read as its file`, frames.every((f) => !f.file || /\.cs$/.test(f.file)), frames.map((f) => f.file).filter(Boolean).slice(0, 3).join(', ')));
      out.push(result(`${label}: culprit is the app's frame`, fn.test(issue.culprit ?? '') && /Program\.cs/.test(issue.culprit ?? ''), issue.culprit));
      const withCode = frames.filter((f) => f.inApp && f.context);
      out.push(result(`${label}: the code that ran on in-app frames (from zharp.source_context)`, withCode.length > 0 && withCode.every((f) => f.contextFrom === 'app'), JSON.stringify(withCode[0]?.context ?? {}).slice(0, 160)));
      // The thrown-at line in each stored event: build A's, and build B's SHIFT lower.
      const lines = (stored ?? []).filter((e) => re.test(e.exception_type)).map((e) => Number((e.stacktrace.split(/\r?\n/).find((l) => fn.test(l)) ?? '').match(/:line (\d+)/)?.[1])).sort((a, b) => a - b);
      out.push(result(`${label}: grouped although the rebuild moved the line by ${SHIFT}`, lines.length === 3 && lines[0] === lines[1] && lines[2] === lines[0] + SHIFT, `lines ${lines.join(', ')}`));
    }
    out.push(result('no other issues', issues.length === 2, issues.map((i) => i.title).join(' | ')));
    return out;
  },
};
