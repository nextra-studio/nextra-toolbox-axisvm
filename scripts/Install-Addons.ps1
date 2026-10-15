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
$names = @('RhinoImportAddon', 'OneWayLoadPanelAddon', 'LoadPanelToolsAddon',
    'EndPinAssignAddon', 'ReactionTransferAddon', 'SectionModifierAddon', 'ConnectionLoadExtractorAddon')
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
$AddOnsDir = [System.IO.Path]::GetFullPath($AddOnsDir)
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "$name.dll"))) { throw "Missing build: $name.dll" }
}
if (Get-Process -Name 'AxisVM*' -ErrorAction SilentlyContinue) { throw 'Close AxisVM before replacing its add-ons.' }
if ($SourceDir.TrimEnd('\') -eq $AddOnsDir.TrimEnd('\')) { throw 'Source and destination must differ.' }
if (-not $PSCmdlet.ShouldProcess($AddOnsDir, 'Install the seven Nextra add-on DLLs, backing up existing versions')) { return }
New-Item -ItemType Directory -Path $AddOnsDir -Force | Out-Null
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
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        throw "Installed hash does not match: $name.dll. Previous versions: $backup"
    }
    Write-Output "Installed $name.dll"
}
if (Test-Path -LiteralPath $backup) { Write-Output "Previous versions: $backup" }
Write-Output 'Restart AxisVM to load the updated dialogs and icons.'
