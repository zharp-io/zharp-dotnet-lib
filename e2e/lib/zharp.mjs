// The local Zharp backend's API, as the e2e runs use it: throwaway apps, and what they stored.
import { readFileSync } from 'node:fs';

export const API = process.env.ZHARP_E2E_API ?? 'http://localhost:3000/api/v1';
/** What the apps in containers send to. */
export const INGEST = process.env.ZHARP_E2E_INGEST ?? 'http://host.docker.internal:3000/api/v1/otlp';
const tokenFile = process.env.ZHARP_E2E_TOKEN_FILE;
if (!tokenFile) throw new Error('Set ZHARP_E2E_TOKEN_FILE to a file holding a user JWT for the local backend.');
const token = readFileSync(tokenFile, 'utf8').trim();
const H = { Authorization: `Bearer ${token}`, 'x-workspace-id': process.env.ZHARP_E2E_WORKSPACE ?? '3', 'content-type': 'application/json' };

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export async function api(path, init = {}) {
  const go = () => fetch(`${API}/${path}`, { ...init, headers: { ...H, ...(init.headers ?? {}) } });
  // A kept-alive connection the server closed while a synchronous `docker wait` held the event
  // loop fails once: try again on a new one.
  const res = await go().catch(() => sleep(500).then(go));
  const text = await res.text();
  try { return JSON.parse(text); } catch { return { status: res.status, text }; }
}

/** A throwaway app. The backend may not know platform "dotnet" yet: then "otel". */
export async function createApp(name, framework) {
  let r = await api('applications', { method: 'POST', body: JSON.stringify({ name, platform: 'dotnet', framework }) });
  let platform = 'dotnet';
  if (!r.data?.id) {
    r = await api('applications', { method: 'POST', body: JSON.stringify({ name, platform: 'otel', framework }) });
    platform = 'otel';
  }
  if (!r.data?.id) throw new Error(`could not create an app: ${JSON.stringify(r).slice(0, 300)}`);
  return { ...r.data, platformUsed: platform };
}

export const deleteApp = (id) => api(`applications/${id}`, { method: 'DELETE' });

export async function traces(appId) {
  const r = await api(`applications/${appId}/traces?window=1h&limit=200`);
  return r.data?.traces ?? r.data ?? [];
}
export const trace = async (traceId) => (await api(`applications/traces/${traceId}`)).data;
export async function issues(appId) {
  const r = (await api(`applications/${appId}/issues?window=1h`)).data;
  return Array.isArray(r) ? r : (r?.issues ?? []);
}
export const issue = async (appId, issueId) => (await api(`applications/${appId}/issues/${issueId}`)).data;
export async function logs(appId) {
  const r = (await api(`applications/${appId}/logs?window=1h&limit=500`)).data;
  return Array.isArray(r) ? r : (r?.logs ?? r?.items ?? []);
}

/** A workspace API key with artifacts:write, and its plain value. */
export async function artifactKey(name) {
  const created = (await api('api-keys', { method: 'POST', body: JSON.stringify({ name, scopes: ['artifacts:write'] }) })).data;
  const rolled = (await api(`api-keys/${created.id}/roll`, { method: 'POST' })).data;
  return { id: created.id, key: rolled.plainKey };
}
export const deleteKey = (id) => api(`api-keys/${id}`, { method: 'DELETE' });
export const artifacts = async (appId) => (await api(`applications/${appId}/artifacts`)).data;

/** Waits until `get` returns something `done` accepts (or the time is up), and returns the last value. */
export async function until(get, done, ms = 30000) {
  const end = Date.now() + ms;
  let value;
  do {
    value = await get();
    if (done(value)) return value;
    await sleep(1500);
  } while (Date.now() < end);
  return value;
}
