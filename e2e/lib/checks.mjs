// What Zharp stored for a run, and the checks every scenario shares.
import { spawnSync } from 'node:child_process';
import * as z from './zharp.mjs';

const CLICKHOUSE = process.env.ZHARP_E2E_CLICKHOUSE ?? 'zharp-clickhouse';

/**
 * The error events as stored, from ClickHouse: the issue API hides `zharp.source_context` (it
 * becomes the frames' code, which needs the backend's .NET frame parser).
 */
export function storedErrors(appId) {
  const q = `SELECT exception_type, message, handled, attributes, stacktrace FROM zharp.error_events WHERE app_id=${Number(appId)} FORMAT JSONEachRow`;
  const r = spawnSync('docker', ['exec', CLICKHOUSE, 'clickhouse-client', '-q', q], { encoding: 'utf8', maxBuffer: 64 << 20 });
  if (r.status !== 0) return null;
  return r.stdout.split('\n').filter(Boolean).map((l) => JSON.parse(l));
}

/** Everything the app sent, once it stopped arriving. */
export async function collect(app, { traceIds = [], wantIssues = 0, wantSpans = 1 } = {}) {
  let spans = await z.until(() => z.traces(app.id), (t) => t.length >= wantSpans, 40000);
  await z.sleep(4000);
  spans = await z.traces(app.id);
  const issues = await z.until(() => z.issues(app.id), (i) => i.length >= wantIssues, 30000);
  await z.sleep(2000);
  const finalIssues = await z.issues(app.id);
  const details = [];
  for (const i of finalIssues) details.push(await z.issue(app.id, i.id));
  const traces = {};
  for (const id of traceIds) traces[id] = await z.trace(id);
  const logs = await z.logs(app.id);
  return { spans, issues: finalIssues.length >= issues.length ? finalIssues : issues, details, traces, logs, stored: storedErrors(app.id) };
}

export const result = (name, pass, detail = '') => ({ name, pass: !!pass, detail });

/** The stored events of one expected error. */
export function eventsOf(got, test) {
  return (got.stored ?? []).filter((e) => test(e));
}

/** An expected error: exactly one event, handled as said, with its code when asked. */
export function expectError(got, label, test, { handled, context, attributes = {} } = {}) {
  const events = eventsOf(got, test);
  const out = [];
  out.push(result(`${label}: reported once`, events.length === 1, `${events.length} events`));
  const e = events[0];
  if (!e) return out;
  if (handled !== undefined) out.push(result(`${label}: handled=${handled}`, String(e.handled) === (handled ? '1' : '0') || e.handled === handled, `handled=${e.handled}, mechanism=${e.attributes['zharp.mechanism']}`));
  if (context) {
    let ok = false;
    try { ok = JSON.parse(e.attributes['zharp.source_context'] ?? '[]').some((f) => f.code && f.line); } catch { ok = false; }
    out.push(result(`${label}: code that ran (zharp.source_context)`, ok));
  }
  for (const [k, v] of Object.entries(attributes)) {
    const actual = e.attributes[k];
    out.push(result(`${label}: ${k}`, v instanceof RegExp ? v.test(actual ?? '') : actual === v, `${k}=${(actual ?? '').slice(0, 120)}`));
  }
  return out;
}

/** Every stored event is one the scenario expected: nothing was sent twice or by a span event. */
export function nothingElse(got, allowed) {
  const extra = (got.stored ?? []).filter((e) => !allowed.some((test) => test(e)));
  return result('nothing sent twice (no unexpected errors)', extra.length === 0, extra.map((e) => `${e.exception_type}: ${e.message.slice(0, 60)}`).join(' | '));
}

/** A browser-like call: its own traceparent, the browser SDK's baggage, and secret headers. */
export function browserCall() {
  const hex = (n) => [...crypto.getRandomValues(new Uint8Array(n))].map((b) => b.toString(16).padStart(2, '0')).join('');
  const traceId = hex(16);
  const parentId = hex(8);
  return {
    traceId,
    parentId,
    headers: {
      traceparent: `00-${traceId}-${parentId}-01`,
      baggage: 'enduser.id=u-42,session.id=s-9',
      authorization: 'Bearer very-secret',
      cookie: 'session=abc',
      'x-request-id': 'req-1',
    },
  };
}

export function spanAttr(trace, predicate, key) {
  const span = (trace?.spans ?? []).find(predicate);
  return span?.attributes?.[key];
}
