# Team Hub for Revit, as a pyRevit extension (Revit 2025 and 2026).
# pyRevit loads it itself: no .addin file, no "unsigned add-in" question, and
# updating is just running this again (works even while Revit is open; the new
# version starts the next time Revit opens). No admin rights needed.
$ErrorActionPreference = 'Stop'
$src = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'TeamHub.extension'
$root = Join-Path $env:APPDATA 'pyRevit'
if (-not (Test-Path $root)) {
  Write-Host "pyRevit is not installed. Install it first from https://github.com/pyrevitlabs/pyRevit/releases , then run this again."
  exit 1
}
$dest = Join-Path $root 'Extensions\TeamHub.extension'
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item $src $dest -Recurse
Write-Host "Team Hub installed as a pyRevit extension: $dest"

# The old stand-alone add-in would load a second copy: switch it off.
foreach ($v in 2025, 2026) {
  $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v\TeamHub.addin"
  if (Test-Path $addin) {
    Move-Item $addin "$addin.disabled" -Force
    Write-Host "Old Team Hub add-in for Revit $v switched off (pyRevit loads Team Hub now)."
  }
}
Write-Host "Open Revit (or pyRevit -> Reload). The 'Team Hub' tab appears."
