# Team Hub for Revit — one-time setup of the shared folder for central models.
# Run on the SERVER PC only:  right-click → "Run with PowerShell" as Administrator.
#
# What it does (all of it changes Windows settings, which is why you run it yourself):
#   1. Creates C:\TeamHub-Revit\Central
#   2. Creates a local Windows account "teamhub" (you choose its password) that the
#      team uses to reach the folder over Radmin VPN
#   3. Shares the folder as \\<this PC>\TeamHubRevit (only Administrators + teamhub)
#   4. Allows Windows file sharing (port 445) from the Radmin VPN network (26.0.0.0/8) only
#   5. Tells the Team Hub server where the share lives, so it can back up central models

#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$folder = 'C:\TeamHub-Revit\Central'
$share = 'TeamHubRevit'
$account = 'teamhub'

Write-Host "`n=== Team Hub — Revit shared folder setup ===`n"

New-Item -ItemType Directory -Force $folder | Out-Null
Write-Host "[1/5] Folder ready: $folder"

if (-not (Get-LocalUser -Name $account -ErrorAction SilentlyContinue)) {
  $pw = Read-Host "Choose a password for the '$account' account (the team will type it once)" -AsSecureString
  New-LocalUser -Name $account -Password $pw -FullName 'Team Hub Revit share' `
    -Description 'Used by teammates to reach the Revit central models' -PasswordNeverExpires | Out-Null
  Write-Host "[2/5] Account '$account' created"
} else {
  Write-Host "[2/5] Account '$account' already exists"
}

$acl = Get-Acl $folder
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($account, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.SetAccessRule($rule)
Set-Acl $folder $acl

if (-not (Get-SmbShare -Name $share -ErrorAction SilentlyContinue)) {
  New-SmbShare -Name $share -Path $folder -FullAccess 'Administrators', $account -CachingMode None | Out-Null
}
Write-Host "[3/5] Shared as \\$env:COMPUTERNAME\$share  (offline caching off — required for Revit central files)"

$ruleName = 'Team Hub Revit share (Radmin VPN)'
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort 445 `
    -RemoteAddress '26.0.0.0/8', 'LocalSubnet' -Action Allow -Profile Any | Out-Null
}
Write-Host "[4/5] Firewall: file sharing allowed from Radmin VPN (26.x.x.x) and the local network"

$config = Join-Path $PSScriptRoot '..\server\config.json'
if (Test-Path $config) {
  $c = Get-Content $config -Raw | ConvertFrom-Json
  $shares = @{ $share = $folder }
  $c | Add-Member -NotePropertyName revitShares -NotePropertyValue $shares -Force
  $c | ConvertTo-Json -Depth 5 | Set-Content $config -Encoding UTF8
  Write-Host "[5/5] Server config updated (backups of central models enabled). Restart start-server.bat."
}

$radmin = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '26.*' } | Select-Object -First 1).IPAddress
Write-Host "`nDone."
if ($radmin) {
  Write-Host "Teammates reach the folder at:  \\$radmin\$share"
  Write-Host "Central model path to use in Team Hub:  \\$radmin\$share\<Project>.rvt"
} else {
  Write-Host "Radmin VPN address not found. Install/connect Radmin VPN, then use \\<your 26.x.x.x>\$share"
}
Write-Host "Teammates log in to the folder with:  user '$account' + the password you chose.`n"
Read-Host 'Press Enter to close'
