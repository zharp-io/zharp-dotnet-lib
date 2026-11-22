// Running the e2e apps in Docker: build with e2e/build.sh, run on the zharp-e2e network.
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const env = { ...process.env, MSYS_NO_PATHCONV: '1' };
const bash = process.platform === 'win32' ? 'bash' : '/bin/bash';

export function sh(script, args = [], opts = {}) {
  const r = spawnSync(bash, [path.join(ROOT, script), ...args], { cwd: ROOT, env: { ...env, ...(opts.env ?? {}) }, encoding: 'utf8', maxBuffer: 64 << 20 });
  if (r.status !== 0) throw new Error(`${script} ${args.join(' ')} failed:\n${(r.stdout ?? '') + (r.stderr ?? '')}`.slice(-4000));
  return (r.stdout ?? '') + (r.stderr ?? '');
}

export function docker(args, opts = {}) {
  const r = spawnSync('docker', args, { env, encoding: 'utf8', maxBuffer: 64 << 20, ...opts });
  return { code: r.status, out: (r.stdout ?? '') + (r.stderr ?? '') };
}

/** A host path Docker on Windows accepts for a bind mount. */
export function hostPath(p) {
  return process.platform === 'win32' ? p.replace(/\\/g, '/') : p;
}

/** Start a container (detached). */
export function run(name, image, { env: vars = {}, ports = [], mounts = [], workdir, command = [], network = 'zharp-e2e', entrypoint } = {}) {
  docker(['rm', '-f', name]);
  const args = ['run', '-d', '--name', name, '--network', network, '--add-host', 'host.docker.internal:host-gateway'];
  for (const [k, v] of Object.entries(vars)) if (v !== undefined && v !== null) args.push('-e', `${k}=${v}`);
  for (const p of ports) args.push('-p', p);
  for (const m of mounts) args.push('-v', m);
  if (workdir) args.push('-w', workdir);
  if (entrypoint) args.push('--entrypoint', entrypoint);
  args.push(image, ...command);
  const r = docker(args);
  if (r.code !== 0) throw new Error(`docker run ${name}: ${r.out}`);
}

export const logs = (name) => docker(['logs', name]).out;
export const remove = (name) => docker(['rm', '-f', name]);

/** Waits for the container to stop by itself; false (and stops it) if it didn't within `ms`. */
export function waitExit(name, ms = 20000) {
  const r = docker(['wait', name], { timeout: ms });
  if (r.code === 0 && !r.error) return Number(r.out.trim());
  docker(['stop', '-t', '2', name]);
  return null;
}

/** Waits until `url` answers. */
export async function waitHttp(url, name, ms = 90000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try {
      const res = await fetch(url);
      if (res.status < 500) return;
    } catch { /* not up yet */ }
    const state = docker(['inspect', '-f', '{{.State.Running}}', name]).out.trim();
    if (state === 'false') throw new Error(`${name} exited:\n${logs(name).slice(-3000)}`);
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`${url} never answered:\n${logs(name).slice(-3000)}`);
}

export function spawnLive(cmd, args) {
  return spawn(cmd, args, { env, stdio: 'inherit' });
}
