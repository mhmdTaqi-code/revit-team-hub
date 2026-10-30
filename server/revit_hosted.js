'use strict';
// Revit projects whose central model lives on this server ("hosted"): no
// Windows file share needed. The central file and its _backup folder travel
// as one zip ("package"). Every member keeps a copy of central on their own PC:
//   sync   = take the turn → bring my copy up to date → Synchronize with
//            Central (into my copy) → upload the new package → give the turn back
//   reload = a teammate synced → download the new package → Reload Latest
// One turn at a time (see /revit/sync/begin) keeps central consistent.
// Every package is kept as a revision (the newest REVIT_KEEP), so they double
// as backups.

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const CHUNK = 2 * 1024 * 1024;
const REVIT_KEEP = 30;

function install({ route, db, DATA_DIR, getRevitProject, requireRole, readJson, httpError, now, syncTurns, SYNC_LEASE_MS, publicUser }) {
  const root = (pid) => path.join(DATA_DIR, 'revit-central', String(Number(pid)));
  const metaPath = (pid) => path.join(root(pid), 'central.json');
  const pkgPath = (pid, rev) => path.join(root(pid), `${rev}.zip`);
  const uploads = new Map(); // upload id -> { pid, user, file, next }

  function meta(pid) {
    try { return JSON.parse(fs.readFileSync(metaPath(pid), 'utf8')); } catch { return { rev: 0 }; }
  }

  function info(p) {
    const m = meta(p.id);
    const size = m.rev ? fs.statSync(pkgPath(p.id, m.rev)).size : 0;
    return { rev: m.rev, size, chunk: CHUNK, file: m.file || null, updated_at: m.updated_at || null,
      updated_by: m.updated_by ? publicUser(db.prepare('SELECT * FROM users WHERE id = ?').get(m.updated_by)) : null };
  }

  const hostedProject = (pid) => {
    const p = getRevitProject(pid);
    if (!String(p.central_path || '').startsWith('hub:')) throw httpError(400, 'not_hosted');
    return p;
  };

  route('GET', '/api/projects/:pid/revit/central', (ctx) => {
    requireRole(ctx.user, 'admin', 'editor', 'viewer');
    const p = hostedProject(ctx.params.pid);
    const i = info(p);
    const offset = ctx.url.searchParams.get('offset');
    if (offset == null) return { central: i };
    const rev = Number(ctx.url.searchParams.get('rev')) || i.rev;
    const file = pkgPath(p.id, rev);
    if (!fs.existsSync(file)) throw httpError(404, 'no_such_revision');
    const size = fs.statSync(file).size;
    const len = Math.min(Number(ctx.url.searchParams.get('length')) || CHUNK, CHUNK);
    const fd = fs.openSync(file, 'r');
    try {
      const buf = Buffer.alloc(Math.max(0, Math.min(len, size - Number(offset))));
      fs.readSync(fd, buf, 0, buf.length, Number(offset));
      return { rev, offset: Number(offset), size, data: buf.toString('base64') };
    } finally {
      fs.closeSync(fd);
    }
  });

  // Upload a new package in chunks. The last chunk makes it the new revision,
  // which needs: the first package (by whoever created the project), or the
  // sync turn + a base revision equal to the current one.
  route('POST', '/api/projects/:pid/revit/central/chunk', async (ctx) => {
    requireRole(ctx.user, 'admin', 'editor');
    const p = hostedProject(ctx.params.pid);
    const b = await readJson(ctx.req);
    const index = Number(b.index);
    const buf = Buffer.from(String(b.data || ''), 'base64');
    if (buf.length > CHUNK) throw httpError(413, 'chunk_too_large');
    fs.mkdirSync(root(p.id), { recursive: true });
    let up = uploads.get(b.upload_id);
    if (index === 0) {
      const id = crypto.randomBytes(9).toString('hex');
      up = { pid: p.id, user: ctx.user.id, file: path.join(root(p.id), `upload_${id}.part`), next: 0 };
      fs.writeFileSync(up.file, Buffer.alloc(0));
      uploads.set(id, up);
      b.upload_id = id;
    }
    if (!up || up.pid !== p.id || up.user !== ctx.user.id) throw httpError(400, 'unknown_upload');
    if (index !== up.next) throw httpError(409, 'wrong_chunk', { expected: up.next });
    fs.appendFileSync(up.file, buf);
    up.next += 1;
    // A big upload keeps the turn alive.
    const turn = syncTurns.get(p.id);
    if (turn && turn.user.id === ctx.user.id) turn.until = now() + SYNC_LEASE_MS;
    if (!b.final) return { ok: true, upload_id: b.upload_id, next: up.next };

    uploads.delete(b.upload_id);
    const m = meta(p.id);
    const first = m.rev === 0;
    if (first) {
      if (ctx.user.role !== 'admin' && ctx.user.id !== p.created_by) { fs.rmSync(up.file, { force: true }); throw httpError(403, 'only_creator'); }
    } else {
      if (!turn || turn.user.id !== ctx.user.id) { fs.rmSync(up.file, { force: true }); throw httpError(423, 'no_turn'); }
      if (Number(b.base_rev) !== m.rev) { fs.rmSync(up.file, { force: true }); throw httpError(409, 'stale_central', { rev: m.rev }); }
    }
    const rev = m.rev + 1;
    fs.renameSync(up.file, pkgPath(p.id, rev));
    const next = { rev, file: String(b.file || m.file || `${p.name}.rvt`).slice(0, 200), updated_at: now(), updated_by: ctx.user.id };
    fs.writeFileSync(metaPath(p.id), JSON.stringify(next));
    for (let r = rev - REVIT_KEEP; r > 0; r--) {
      const old = pkgPath(p.id, r);
      if (!fs.existsSync(old)) break;
      fs.rmSync(old, { force: true });
    }
    return { ok: true, done: true, central: info(p) };
  });

  // Revisions = backups (admin).
  route('GET', '/api/projects/:pid/revit/central/revisions', (ctx) => {
    requireRole(ctx.user, 'admin');
    const p = hostedProject(ctx.params.pid);
    const dir = root(p.id);
    const revs = fs.existsSync(dir) ? fs.readdirSync(dir).filter((f) => /^\d+\.zip$/.test(f))
      .map((f) => ({ rev: Number(f.replace('.zip', '')), size: fs.statSync(path.join(dir, f)).size, at: fs.statSync(path.join(dir, f)).mtimeMs }))
      .sort((a, b) => b.rev - a.rev) : [];
    return { revisions: revs, folder: dir };
  });

  return { info: (p) => info(p) };
}

module.exports = { install };
