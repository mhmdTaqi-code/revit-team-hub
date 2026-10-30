'use strict';
// Keeps the plugins this server hands out up to date by itself: every few
// hours it looks at the newest GitHub release of each plugin and, when it's
// newer than what's in server/plugin, downloads it. Every member's plugin then
// updates itself from this server. Turn off with "autoUpdate": false in config.json.

const fs = require('node:fs');
const path = require('node:path');

const EVERY_MS = 6 * 60 * 60 * 1000;
const SOURCES = [
  {
    name: 'Revit',
    repo: 'mhmdTaqi-code/revit-team-hub',
    meta: 'revit-version.json',
    assets: { 'TeamHub-Revit.zip': 'teamhub_revit.zip' },
    extra: { revit: ['2025', '2026'] },
  },
  {
    name: 'SketchUp',
    repo: 'mhmdTaqi-code/sketchup-team-hub',
    meta: 'version.json',
    assets: { 'su_team_hub.rbz': 'su_team_hub.rbz' },
    extra: { min_sketchup: 2022 },
  },
];

function newer(a, b) {
  const pa = String(a || '0').split('.').map(Number);
  const pb = String(b || '0').split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) > (pb[i] || 0);
  return false;
}

async function checkOne(src, pluginDir, log) {
  const res = await fetch(`https://api.github.com/repos/${src.repo}/releases/latest`, {
    headers: { 'User-Agent': 'TeamHub-server', Accept: 'application/vnd.github+json' },
  });
  if (!res.ok) throw new Error(`GitHub ${res.status}`);
  const rel = await res.json();
  const version = String(rel.tag_name || '').replace(/^v/i, '');
  let current = {};
  try { current = JSON.parse(fs.readFileSync(path.join(pluginDir, src.meta), 'utf8')); } catch { /* none */ }
  if (!version || !newer(version, current.version)) return null;
  const got = {};
  for (const [asset, file] of Object.entries(src.assets)) {
    const a = (rel.assets || []).find((x) => x.name === asset);
    if (!a) continue;
    const r = await fetch(a.browser_download_url, { headers: { 'User-Agent': 'TeamHub-server' } });
    if (!r.ok) throw new Error(`${asset}: ${r.status}`);
    got[file] = Buffer.from(await r.arrayBuffer());
  }
  if (!Object.keys(got).length) return null;
  fs.mkdirSync(pluginDir, { recursive: true });
  // Write files first, the version last: a half-finished update is never announced.
  for (const [file, buf] of Object.entries(got)) {
    fs.writeFileSync(path.join(pluginDir, `${file}.part`), buf);
    fs.renameSync(path.join(pluginDir, `${file}.part`), path.join(pluginDir, file));
  }
  const meta = { ...current, ...src.extra, version, built_at: Date.parse(rel.published_at) || Date.now(), source: rel.html_url };
  fs.writeFileSync(path.join(pluginDir, src.meta), JSON.stringify(meta, null, 2));
  log(`${src.name} plugin updated to ${version} from GitHub`);
  return version;
}

function start({ pluginDir, config, log = console.log }) {
  if (config.autoUpdate === false) return;
  const run = async () => {
    for (const src of SOURCES) {
      try { await checkOne(src, pluginDir, log); } catch (e) { log(`${src.name} update check: ${e.message}`); }
    }
  };
  setTimeout(run, 30_000).unref();
  setInterval(run, EVERY_MS).unref();
}

module.exports = { start, newer, checkOne, SOURCES };
