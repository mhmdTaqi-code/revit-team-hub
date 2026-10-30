'use strict';
// Photoshop projects: everyone paints on the same document, even the same layer.
//
// A layer's pixels travel as 128×128 tiles (raw 8-bit RGB/RGBA, deflated).
// Every tile has a revision; a client sends a tile together with the revision
// it started from. If someone else changed that tile in the meantime the
// server merges pixel by pixel: whatever this client changed relative to its
// base wins, everything else keeps the newer version. So two people painting
// in the same area keep both strokes; only the very same pixel painted by
// both at once goes to whoever arrived last.
//
// Layer properties (name, visibility, opacity, blend mode, parent) and the
// layer order are small "last writer wins" records.

const zlib = require('node:zlib');

const TILE = 128;
const HISTORY_KEEP = 64; // tile revisions kept as merge bases

function install({ db, route, tx, emit, getProject, requireRole, readJson, httpError, now }) {
  db.exec(`
    CREATE TABLE IF NOT EXISTS ps_doc (
      project_id INTEGER PRIMARY KEY,
      meta TEXT NOT NULL,
      order_rev INTEGER NOT NULL DEFAULT 0,
      layer_order TEXT NOT NULL DEFAULT '[]',
      updated_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS ps_layers (
      project_id INTEGER NOT NULL,
      uid TEXT NOT NULL,
      rev INTEGER NOT NULL,
      props TEXT NOT NULL,
      deleted INTEGER NOT NULL DEFAULT 0,
      seq INTEGER NOT NULL DEFAULT 0,
      updated_by INTEGER,
      updated_at INTEGER NOT NULL,
      PRIMARY KEY (project_id, uid)
    );
    CREATE TABLE IF NOT EXISTS ps_tiles (
      project_id INTEGER NOT NULL,
      uid TEXT NOT NULL,
      tx INTEGER NOT NULL,
      ty INTEGER NOT NULL,
      rev INTEGER NOT NULL,
      w INTEGER NOT NULL,
      h INTEGER NOT NULL,
      c INTEGER NOT NULL,
      data BLOB NOT NULL,
      seq INTEGER NOT NULL DEFAULT 0,
      updated_by INTEGER,
      updated_at INTEGER NOT NULL,
      PRIMARY KEY (project_id, uid, tx, ty)
    );
    CREATE INDEX IF NOT EXISTS ps_tiles_seq ON ps_tiles (project_id, seq);
    CREATE TABLE IF NOT EXISTS ps_tile_hist (
      project_id INTEGER NOT NULL,
      uid TEXT NOT NULL,
      tx INTEGER NOT NULL,
      ty INTEGER NOT NULL,
      rev INTEGER NOT NULL,
      data BLOB NOT NULL,
      PRIMARY KEY (project_id, uid, tx, ty, rev)
    );
  `);

  const q = {
    doc: db.prepare('SELECT * FROM ps_doc WHERE project_id = ?'),
    upsertDoc: db.prepare(`INSERT INTO ps_doc (project_id, meta, updated_at) VALUES (?, ?, ?)
                           ON CONFLICT (project_id) DO UPDATE SET meta = excluded.meta, updated_at = excluded.updated_at`),
    setOrder: db.prepare('UPDATE ps_doc SET order_rev = order_rev + 1, layer_order = ?, updated_at = ? WHERE project_id = ? RETURNING order_rev'),
    layer: db.prepare('SELECT * FROM ps_layers WHERE project_id = ? AND uid = ?'),
    layers: db.prepare('SELECT * FROM ps_layers WHERE project_id = ? ORDER BY uid'),
    upsertLayer: db.prepare(`INSERT INTO ps_layers (project_id, uid, rev, props, deleted, seq, updated_by, updated_at)
                             VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                             ON CONFLICT (project_id, uid) DO UPDATE SET rev = excluded.rev, props = excluded.props,
                               deleted = excluded.deleted, seq = excluded.seq, updated_by = excluded.updated_by,
                               updated_at = excluded.updated_at`),
    tile: db.prepare('SELECT * FROM ps_tiles WHERE project_id = ? AND uid = ? AND tx = ? AND ty = ?'),
    tileIndex: db.prepare('SELECT uid, tx, ty, rev, seq FROM ps_tiles WHERE project_id = ?'),
    tilesSince: db.prepare('SELECT * FROM ps_tiles WHERE project_id = ? AND seq > ? ORDER BY seq LIMIT ?'),
    upsertTile: db.prepare(`INSERT INTO ps_tiles (project_id, uid, tx, ty, rev, w, h, c, data, seq, updated_by, updated_at)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                            ON CONFLICT (project_id, uid, tx, ty) DO UPDATE SET rev = excluded.rev, w = excluded.w,
                              h = excluded.h, c = excluded.c, data = excluded.data, seq = excluded.seq,
                              updated_by = excluded.updated_by, updated_at = excluded.updated_at`),
    hist: db.prepare('SELECT data FROM ps_tile_hist WHERE project_id = ? AND uid = ? AND tx = ? AND ty = ? AND rev = ?'),
    insertHist: db.prepare('INSERT OR REPLACE INTO ps_tile_hist (project_id, uid, tx, ty, rev, data) VALUES (?, ?, ?, ?, ?, ?)'),
    pruneHist: db.prepare('DELETE FROM ps_tile_hist WHERE project_id = ? AND uid = ? AND tx = ? AND ty = ? AND rev <= ?'),
    dropLayerTiles: db.prepare('DELETE FROM ps_tiles WHERE project_id = ? AND uid = ?'),
    dropLayerHist: db.prepare('DELETE FROM ps_tile_hist WHERE project_id = ? AND uid = ?'),
  };

  const psProject = (pid) => {
    const p = getProject(pid);
    if (p.kind !== 'photoshop') throw httpError(400, 'not_a_photoshop_project');
    return p;
  };
  const layerOut = (l) => ({ uid: l.uid, rev: l.rev, props: JSON.parse(l.props), deleted: !!l.deleted, seq: l.seq });
  const tileOut = (t, withData) => ({ uid: t.uid, tx: t.tx, ty: t.ty, rev: t.rev, w: t.w, h: t.h, c: t.c, seq: t.seq,
    ...(withData ? { data: Buffer.from(t.data).toString('base64') } : {}) });

  // Pixel-level three-way merge: take every pixel the client changed against
  // its base, keep the server's current pixel everywhere else.
  function merge3(cur, base, inc, c) {
    const out = Buffer.from(cur);
    for (let i = 0; i < inc.length; i += c) {
      let changed = false;
      for (let k = 0; k < c; k++) if (inc[i + k] !== base[i + k]) { changed = true; break; }
      if (changed) inc.copy(out, i, i, i + c);
    }
    return out;
  }

  // ------------------------------------------------------------ document

  route('PUT', '/api/projects/:pid/ps/doc', async (ctx) => {
    requireRole(ctx.user, 'admin', 'editor');
    const p = psProject(ctx.params.pid);
    const b = await readJson(ctx.req);
    const meta = { width: Number(b.width) | 0, height: Number(b.height) | 0, mode: String(b.mode || 'RGB'),
      depth: Number(b.depth) || 8, resolution: Number(b.resolution) || 72, name: String(b.name || p.name).slice(0, 200) };
    if (!(meta.width > 0 && meta.height > 0)) throw httpError(400, 'size_required');
    q.upsertDoc.run(p.id, JSON.stringify(meta), now());
    return { doc: meta };
  });

  route('GET', '/api/projects/:pid/ps/state', (ctx) => {
    requireRole(ctx.user, 'admin', 'editor', 'viewer');
    const p = psProject(ctx.params.pid);
    const d = q.doc.get(p.id);
    return {
      seq: getProject(p.id).seq,
      tile: TILE,
      doc: d ? JSON.parse(d.meta) : null,
      order: d ? JSON.parse(d.layer_order) : [],
      order_rev: d ? d.order_rev : 0,
      layers: q.layers.all(p.id).map(layerOut),
      tiles: q.tileIndex.all(p.id),
    };
  });

  // ------------------------------------------------------------ layers

  route('POST', '/api/projects/:pid/ps/layers', async (ctx) => {
    requireRole(ctx.user, 'admin', 'editor');
    const p = psProject(ctx.params.pid);
    const b = await readJson(ctx.req);
    const changes = Array.isArray(b.changes) ? b.changes.slice(0, 500) : [];
    const out = tx(() => changes.map((ch) => {
      const uid = String(ch.uid || '').slice(0, 80);
      if (!uid) return null;
      const cur = q.layer.get(p.id, uid);
      // Per-property last writer wins: a rename and a visibility toggle from
      // two people at once both survive.
      const props = { ...(cur ? JSON.parse(cur.props) : {}), ...(ch.props || {}) };
      const deleted = ch.deleted ? 1 : 0;
      const rev = (cur ? cur.rev : 0) + 1;
      const seq = emit(p.id, 'ps_layer', { uid, rev, props, deleted: !!deleted }, ctx.user.id);
      q.upsertLayer.run(p.id, uid, rev, JSON.stringify(props), deleted, seq, ctx.user.id, now());
      if (deleted) { q.dropLayerTiles.run(p.id, uid); q.dropLayerHist.run(p.id, uid); }
      return { uid, rev, props, deleted: !!deleted };
    }).filter(Boolean));
    return { layers: out };
  });

  route('POST', '/api/projects/:pid/ps/order', async (ctx) => {
    requireRole(ctx.user, 'admin', 'editor');
    const p = psProject(ctx.params.pid);
    const b = await readJson(ctx.req);
    const order = (Array.isArray(b.order) ? b.order : []).slice(0, 5000)
      .map((o) => ({ uid: String(o.uid || '').slice(0, 80), parent: o.parent ? String(o.parent).slice(0, 80) : null }))
      .filter((o) => o.uid);
    if (!q.doc.get(p.id)) q.upsertDoc.run(p.id, JSON.stringify({ name: p.name }), now());
    const res = tx(() => {
      const rev = q.setOrder.get(JSON.stringify(order), now(), p.id).order_rev;
      emit(p.id, 'ps_order', { rev, order }, ctx.user.id);
      return { rev, order };
    });
    return res;
  });

  // ------------------------------------------------------------ tiles

  route('POST', '/api/projects/:pid/ps/tiles', async (ctx) => {
    requireRole(ctx.user, 'admin', 'editor');
    const p = psProject(ctx.params.pid);
    const b = await readJson(ctx.req);
    const tiles = Array.isArray(b.tiles) ? b.tiles.slice(0, 400) : [];
    const results = [];
    tx(() => {
      for (const t of tiles) {
        const uid = String(t.uid || '').slice(0, 80);
        const tx_ = Number(t.tx) | 0;
        const ty_ = Number(t.ty) | 0;
        const w = Number(t.w) | 0;
        const h = Number(t.h) | 0;
        const c = Number(t.c) | 0;
        if (!uid || w <= 0 || h <= 0 || w > TILE || h > TILE || (c !== 3 && c !== 4)) {
          results.push({ uid, tx: tx_, ty: ty_, ok: false, error: 'bad_tile' });
          continue;
        }
        let inc;
        try { inc = zlib.inflateRawSync(Buffer.from(String(t.data || ''), 'base64')); } catch { inc = null; }
        if (!inc || inc.length !== w * h * c) {
          results.push({ uid, tx: tx_, ty: ty_, ok: false, error: 'bad_data' });
          continue;
        }
        const cur = q.tile.get(p.id, uid, tx_, ty_);
        const baseRev = Number(t.base_rev) || 0;
        let pixels = inc;
        let merged = false;
        if (cur && cur.rev !== baseRev) {
          if (cur.w !== w || cur.h !== h || cur.c !== c) {
            results.push({ uid, tx: tx_, ty: ty_, ok: false, error: 'tile_shape_changed', rev: cur.rev });
            continue;
          }
          const curPx = zlib.inflateRawSync(Buffer.from(cur.data));
          const baseRow = baseRev > 0 ? q.hist.get(p.id, uid, tx_, ty_, baseRev) : null;
          // No base (first upload by this client, or pruned): the base is
          // "nothing", so only pixels that aren't transparent/empty count as changed.
          const basePx = baseRow ? zlib.inflateRawSync(Buffer.from(baseRow.data)) : Buffer.alloc(inc.length);
          pixels = merge3(curPx, basePx, inc, c);
          merged = true;
        }
        const data = merged ? zlib.deflateRawSync(pixels) : Buffer.from(String(t.data), 'base64');
        const rev = (cur ? cur.rev : 0) + 1;
        const meta = { uid, tx: tx_, ty: ty_, rev, w, h, c, merged };
        const seq = emit(p.id, 'ps_tile', meta, ctx.user.id, { data: data.toString('base64') });
        q.upsertTile.run(p.id, uid, tx_, ty_, rev, w, h, c, data, seq, ctx.user.id, now());
        q.insertHist.run(p.id, uid, tx_, ty_, rev, data);
        if (rev > HISTORY_KEEP) q.pruneHist.run(p.id, uid, tx_, ty_, rev - HISTORY_KEEP);
        results.push({ ...meta, ok: true, seq, ...(merged ? { data: data.toString('base64') } : {}) });
      }
    });
    return { tiles: results };
  });

  // Current pixels of some tiles (after a reconnect, or for a new member).
  route('POST', '/api/projects/:pid/ps/tiles/get', async (ctx) => {
    requireRole(ctx.user, 'admin', 'editor', 'viewer');
    const p = psProject(ctx.params.pid);
    const b = await readJson(ctx.req);
    const keys = Array.isArray(b.keys) ? b.keys.slice(0, 300) : [];
    const tiles = [];
    for (const k of keys) {
      const t = q.tile.get(p.id, String(k.uid || ''), Number(k.tx) | 0, Number(k.ty) | 0);
      if (t) tiles.push(tileOut(t, true));
    }
    return { tiles };
  });

  // Everything that changed after `since` (layers + tiles, with pixels), paged.
  route('GET', '/api/projects/:pid/ps/changes', (ctx) => {
    requireRole(ctx.user, 'admin', 'editor', 'viewer');
    const p = psProject(ctx.params.pid);
    const since = Number(ctx.url.searchParams.get('since')) || 0;
    const limit = Math.min(Number(ctx.url.searchParams.get('limit')) || 200, 500);
    const tiles = q.tilesSince.all(p.id, since, limit).map((t) => tileOut(t, true));
    return { tiles, more: tiles.length === limit, seq: getProject(p.id).seq };
  });

  return { TILE };
}

module.exports = { install, TILE };
