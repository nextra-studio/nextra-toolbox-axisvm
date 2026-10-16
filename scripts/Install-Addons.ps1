[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourceDir,
    [string]$AddOnsDir = 'C:\AXISVM X8\AddOns',
    [string]$BackupRoot = (Join-Path $env:LOCALAPPDATA 'NextraToolbox\backups')
)
$ErrorActionPreference = 'Stop'
if (-not $SourceDir) {
    $packaged = Join-Path $PSScriptRoot 'AddOns'
    $SourceDir = if (Test-Path -LiteralPath $packaged) { $packaged } else {
        Join-Path $PSScriptRoot '..\AxisVM plugin\AxisVM plugin\bin\Release'
    }
}
# The tool list comes from tools.json (next to this script in the release ZIP, one folder up in the repo),
# so a newly registered tool is installed without editing this script.
$registry = @((Join-Path $PSScriptRoot 'tools.json'), (Join-Path $PSScriptRoot '..	ools.json')) |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $registry) { throw 'tools.json not found next to Install-Addons.ps1.' }
$names = @((Get-Content -LiteralPath $registry -Raw | ConvertFrom-Json).tools | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.dll) })
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
$AddOnsDir = [System.IO.Path]::GetFullPath($AddOnsDir)
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "$name.dll"))) { throw "Missing build: $name.dll" }
}
if (Get-Process -Name 'AxisVM*' -ErrorAction SilentlyContinue) { throw 'Close AxisVM before replacing its add-ons.' }
if ($SourceDir.TrimEnd('\') -eq $AddOnsDir.TrimEnd('\')) { throw 'Source and destination must differ.' }
if (-not $PSCmdlet.ShouldProcess($AddOnsDir, "Install the $($names.Count) Nextra add-on DLLs, backing up existing versions")) { return }
if (-not (Test-Path -LiteralPath $AddOnsDir)) {
    # A missing folder usually means a different AxisVM location; do not silently create one.
    if ([Environment]::UserInteractive) {
        $answer = Read-Host "AxisVM AddOns folder not found: $AddOnsDir`nType the correct AddOns folder (or press Enter to cancel)"
        if (-not $answer) { throw 'Cancelled. Re-run with -AddOnsDir ''<your AxisVM>\AddOns''.' }
        $AddOnsDir = [System.IO.Path]::GetFullPath($answer.Trim('"'))
        if (-not (Test-Path -LiteralPath $AddOnsDir)) { throw "Folder not found: $AddOnsDir" }
    } else { throw "AxisVM AddOns folder not found: $AddOnsDir. Use -AddOnsDir." }
}
$backup = Join-Path $BackupRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
foreach ($name in $names) {
    $destination = Join-Path $AddOnsDir "$name.dll"
    if (Test-Path -LiteralPath $destination) {
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Copy-Item -LiteralPath $destination -Destination (Join-Path $backup "$name.dll")
    }
}
foreach ($name in $names) {
    $source = Join-Path $SourceDir "$name.dll"
    $destination = Join-Path $AddOnsDir "$name.dll"
    Copy-Item -LiteralPath $source -Destination $destination -Force
    Unblock-File -LiteralPath $destination  # drop the downloaded-from-internet mark so AxisVM loads it
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        throw "Installed hash does not match: $name.dll. Previous versions: $backup"
    }
    Write-Output "Installed $name.dll"
}
if (Test-Path -LiteralPath $backup) { Write-Output "Previous versions: $backup" }
Write-Output 'Restart AxisVM to load the updated dialogs and icons.'
