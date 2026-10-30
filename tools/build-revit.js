'use strict';
// Builds the Revit add-in (Revit 2025 + 2026, .NET 8) and publishes it:
//   dist/TeamHub-Revit.zip            — hand out manually
//   server/plugin/teamhub_revit.zip   — served at /download/teamhub_revit.zip
// Usage: node tools/build-revit.js   (needs the .NET 8 SDK and Revit 2025 installed)

const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { zip } = require('./zip');

const ROOT = path.resolve(__dirname, '..');
const PROJ = path.join(ROOT, 'revit', 'TeamHub.Revit');
const INSTALLER = path.join(ROOT, 'revit', 'installer');

execFileSync('dotnet', ['build', '-c', 'Release', '--nologo', '-v', 'q', '-p:NoWarn=MSB3277'], { cwd: PROJ, stdio: 'inherit' });
const dll = path.join(PROJ, 'bin', 'Release', 'net8.0-windows', 'TeamHub.Revit.dll');
// Sign with the Team Hub certificate when this PC has it (its private key never
// leaves the server PC): a trusted signature means Revit loads it without asking.
const SUBJECT = 'CN=Team Hub Revit Add-in (mhmdTaqi-code)';
try {
  const status = execFileSync('powershell', ['-NoProfile', '-Command',
    `$c = Get-ChildItem Cert:\\CurrentUser\\My -CodeSigningCert | Where-Object { $_.Subject -eq '${SUBJECT}' } | Select-Object -First 1; ` +
    `if ($c) { (Set-AuthenticodeSignature -FilePath '${dll}' -Certificate $c -HashAlgorithm SHA256).Status } else { 'no certificate' }`],
    { encoding: 'utf8' }).trim();
  console.log(`signature: ${status}`);
} catch (e) {
  console.log(`signature: failed (${e.message})`);
}
const csproj = fs.readFileSync(path.join(PROJ, 'TeamHub.Revit.csproj'), 'utf8');
const version = (csproj.match(/<Version>([^<]+)<\/Version>/) || [])[1] || '0.0.0';

const entries = [
  { name: 'TeamHub-Revit/TeamHub.Revit.dll', data: fs.readFileSync(dll) },
  ...['install.cmd', 'install.ps1', 'uninstall.cmd', 'trust-certificate.cmd', 'trust-certificate.ps1', 'TeamHub-Revit.cer'].map((f) => ({
    name: `TeamHub-Revit/${f}`, data: fs.readFileSync(path.join(INSTALLER, f)),
  })),
];
const out = zip(entries);
for (const [dir, name] of [[path.join(ROOT, 'dist'), 'TeamHub-Revit.zip'], [path.join(ROOT, 'server', 'plugin'), 'teamhub_revit.zip']]) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, name), out);
}
fs.writeFileSync(path.join(ROOT, 'server', 'plugin', 'revit-version.json'),
  JSON.stringify({ version, built_at: Date.now(), revit: ['2025', '2026'] }, null, 2));
console.log(`Team Hub for Revit v${version}: ${out.length} bytes -> dist/TeamHub-Revit.zip and server/plugin/`);
