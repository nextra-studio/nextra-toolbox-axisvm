# Checks the AxisVM catalog -> Rhino library -> AxisVM import round trip without Rhino:
#   1. AxisVM: "Export catalog for Rhino" (Core\CatalogExport.cs) against a fresh AxisVM, compared name by name
#      with the catalog read straight from AxisVM; the model must stay untouched.
#   2. Rhino (Rhino\Checks\CatalogLibraryChecks.cs): reads those CSVs, drives the picker and library dialogs and
#      writes an AxExport-style JSON.
#   3. AxisVM (AxisVM plugin\Checks\RhinoImportCatalogLiveChecks.cs): imports that JSON into another fresh AxisVM
#      with the real AxisModelBuilder and checks every material and cross-section.
# Every AxisVM used is started here through COM and closed again; an AxisVM that is already open is never touched.
# The CSVs go to the output folder, not to %APPDATA%\AxisBridge, so a catalog you exported yourself is left alone.
# -Full <export.json> [-SaveModelAs x.axs]: import a real AxExport file (every element type) into a fresh AxisVM and check all of it.
# -Offline: only the offline part of step 2. -ImportOnly: step 3 on the last JSON; -Subset sec|mat|sec:FROM:TO narrows it.
[CmdletBinding()]
param([string]$OutputDir, [switch]$Offline, [switch]$ImportOnly, [string]$Subset, [string]$Full, [string]$SaveModelAs)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path ([IO.Path]::GetTempPath()) 'nextra-catalog-checks' }
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$axisDir = if ($env:AXISVM_DIR) { $env:AXISVM_DIR } else { 'C:\AXISVM X8' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interop = Join-Path $repo 'AxisVM plugin\lib\Interop.AxisVM.FW4.dll'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('nextra-catalog-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null

# ---- AxisVM-side checker (export + import modes) ---------------------------------------------------------
$core = Join-Path $repo 'AxisVM plugin\AxisVM plugin'
$sources = @('Core\AxisModelBuilder.cs', 'Core\AxisModelBuilder.Elements.cs', 'Core\AxisModelBuilder.Surfaces.cs', 'Core\AxisModelBuilder.Loads.cs',
    'Core\AxisModelBuilder.Helpers.cs', 'Core\AxisModelBuilder.Link.cs', 'Core\LinkFile.cs', 'Core\LinkState.cs', 'Core\LinkLibrary.cs', 'Core\LinkLoads.cs', 'Core\RhinoLinkSender.cs', 'Core\SpringParamResolver.cs', 'Core\AttributeParser.cs', 'Core\CatalogResolver.cs', 'Core\CatalogExport.cs',
    'Core\Json.cs', 'Core\RhinoModelDto.cs', 'Core\AxisVMHelper.cs', 'Functions\OneWayPanelBuilder.cs', 'Functions\EdgeChainBuilder.cs') | ForEach-Object { Join-Path $core $_ }
$axisExe = Join-Path $temp 'RhinoImportCatalogLiveChecks.exe'
Copy-Item $interop $temp   # a few interop types (struct arrays) cannot be embedded
& $csc /nologo /target:exe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "/link:$interop" "/out:$axisExe" (Join-Path $repo 'AxisVM plugin\Checks\RhinoImportCatalogLiveChecks.cs') (Join-Path $repo 'AxisVM plugin\Checks\AxisVmDialogGuard.cs') @sources
if ($LASTEXITCODE) { throw 'AxisVM check compilation failed.' }

if ($Full) { & $axisExe full $Full $SaveModelAs; exit $LASTEXITCODE }

# ---- Rhino-side checker ------------------------------------------------------------------------------------
# Built into its own folder: a running Rhino keeps bin\Release\AxisBridge.rhp locked.
$bin = Join-Path $temp 'rhp\'
dotnet build (Join-Path $repo 'Rhino\AxisBridge\AxisBridge.csproj') -c Release -v:q -nologo "-p:OutputPath=$bin" "-p:BaseIntermediateOutputPath=$temp\obj\"
if ($LASTEXITCODE) { throw 'AxisBridge build failed.' }
Copy-Item "$bin\Newtonsoft.Json.dll" $temp
Copy-Item "$bin\AxisBridge.rhp" (Join-Path $temp 'AxisBridge.dll')
$rc = Join-Path $env:USERPROFILE '.nuget\packages\rhinocommon\8.6.24101.5001\lib\net48'
Copy-Item "$rc\RhinoCommon.dll", "$rc\Rhino.UI.dll", "$rc\Eto.dll" $temp
$rhinoExe = Join-Path $temp 'CatalogLibraryChecks.exe'
& $csc /nologo /target:exe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "/r:$temp\AxisBridge.dll" "/r:$temp\RhinoCommon.dll" "/r:$temp\Newtonsoft.Json.dll" "/out:$rhinoExe" (Join-Path $repo 'Rhino\Checks\CatalogLibraryChecks.cs')
if ($LASTEXITCODE) { throw 'Rhino check compilation failed.' }

if ($Offline) { & $rhinoExe $OutputDir offline; exit $LASTEXITCODE }

$catalog = Join-Path $OutputDir 'catalog'
$json = Join-Path $OutputDir 'catalog-export.json'
$exportExit = 0; $rhinoExit = 0
if (-not $ImportOnly) {
    Write-Host '== 1. AxisVM: export catalog for Rhino'
    & $axisExe export $catalog; $exportExit = $LASTEXITCODE
    Write-Host '== 2. Rhino: read catalog, picker, library, export JSON'
    & $rhinoExe $OutputDir $catalog; $rhinoExit = $LASTEXITCODE
}
if (-not (Test-Path $json)) { throw 'The Rhino checks wrote no export JSON.' }
Write-Host '== 3. AxisVM: import the JSON'
if ($Subset) { & $axisExe $json $axisDir $Subset } else { & $axisExe $json $axisDir }
$importExit = $LASTEXITCODE

Write-Host ''
$s = { param($c) if ($c) { 'FAILED' } else { 'passed' } }
Write-Host ("Export: " + (& $s $exportExit) + "   Rhino: " + (& $s $rhinoExit) + "   Import: " + (& $s $importExit) + "   output: $OutputDir")
exit ([int]($exportExit -or $rhinoExit -or $importExit))
