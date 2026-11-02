# Checks re-importing a Rhino export as an update (AxisVM plugin\Checks\RhinoUpdateLiveChecks.cs): import, the same
# export again as an update, then with a beam moved and a curve deleted - all in a fresh AxisVM started here through
# COM and closed again; then the live link round trip (Send to Rhino / Load from Rhino) in another fresh AxisVM.
# Each run starts its own AxisVM and never touches one that is already open (the user's or another agent's).
# Usage: scripts\Check-RhinoUpdate.ps1 <AxExport.json> [-Meters]
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Json, [switch]$Meters)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interop = Join-Path $repo 'AxisVM plugin\lib\Interop.AxisVM.FW4.dll'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('nextra-update-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null

$core = Join-Path $repo 'AxisVM plugin\AxisVM plugin'
$sources = @('Core\AxisModelBuilder.cs', 'Core\AxisModelBuilder.Elements.cs', 'Core\AxisModelBuilder.Surfaces.cs', 'Core\AxisModelBuilder.Loads.cs',
    'Core\AxisModelBuilder.Helpers.cs', 'Core\AxisModelBuilder.Link.cs', 'Core\LinkFile.cs', 'Core\LinkState.cs', 'Core\LinkLibrary.cs', 'Core\LinkLoads.cs', 'Core\RhinoLinkSender.cs', 'Core\SpringParamResolver.cs', 'Core\AttributeParser.cs', 'Core\CatalogResolver.cs',
    'Core\CatalogExport.cs', 'Core\Json.cs', 'Core\RhinoModelDto.cs', 'Core\AxisVMHelper.cs', 'Functions\OneWayPanelBuilder.cs',
    'Functions\EdgeChainBuilder.cs') | ForEach-Object { Join-Path $core $_ }
$exe = Join-Path $temp 'RhinoUpdateLiveChecks.exe'
Copy-Item $interop $temp   # a few interop types (struct arrays) cannot be embedded
& $csc /nologo /target:exe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "/link:$interop" "/out:$exe" (Join-Path $repo 'AxisVM plugin\Checks\RhinoUpdateLiveChecks.cs') (Join-Path $repo 'AxisVM plugin\Checks\AxisVmDialogGuard.cs') @sources
if ($LASTEXITCODE) { throw 'Compilation failed.' }

$units = if ($Meters) { 'm' } else { 'mm' }
Write-Host '== Update on re-import'
& $exe (Resolve-Path $Json) $units; $updateExit = $LASTEXITCODE
Write-Host '== Live link round trip'
& $exe (Resolve-Path $Json) $units link; $linkExit = $LASTEXITCODE
exit ([int]($updateExit -or $linkExit))
