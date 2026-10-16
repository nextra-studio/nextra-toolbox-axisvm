# Builds everything and assembles the downloadable toolbox ZIP: dist\nextra-axisvm-toolbox.zip
#   AddOns\*.dll   Rhino\*   Install-Addons.ps1   tools.json   INSTALL.md
# Publish it with:  gh release create v<version> dist\nextra-axisvm-toolbox.zip --title "..." --notes "..."
# (keep the asset name unchanged: the README links to releases/latest/download/nextra-axisvm-toolbox.zip)
[CmdletBinding()]
param([string]$Dist)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Dist) { $Dist = Join-Path $repo 'dist' }
$tools = Get-Content -LiteralPath (Join-Path $repo 'tools.json') -Raw | ConvertFrom-Json

dotnet build (Join-Path $repo 'AxisVM plugin\AxisVM plugin.slnx') -c Release -m:1 | Out-Host
if ($LASTEXITCODE) { throw 'Add-on build failed.' }
dotnet build (Join-Path $repo 'Rhino\AxisBridge\AxisBridge.csproj') -c Release | Out-Host
if ($LASTEXITCODE) { throw 'AxisBridge build failed.' }

$stage = Join-Path $Dist 'nextra-axisvm-toolbox'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path "$stage\AddOns", "$stage\Rhino" -Force | Out-Null
$bin = Join-Path $repo 'AxisVM plugin\AxisVM plugin\bin\Release'
foreach ($t in $tools.tools) {
    $dll = Join-Path $bin $t.dll
    if (-not (Test-Path $dll)) { throw "Build output missing: $($t.dll)" }
    Copy-Item $dll "$stage\AddOns"
}
$rb = Join-Path $repo 'Rhino\AxisBridge\bin\Release'
foreach ($f in $tools.rhino.files) { Copy-Item (Join-Path $rb $f) "$stage\Rhino" }
Copy-Item (Join-Path $repo 'scripts\Install-Addons.ps1'), (Join-Path $repo 'tools.json') $stage
Copy-Item (Join-Path $repo 'AxisVM plugin\docs\INSTALLATION.md') (Join-Path $stage 'INSTALL.md')

$zip = Join-Path $Dist 'nextra-axisvm-toolbox.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Output "Built $zip ($($tools.tools.Count) add-ons + Rhino plug-in)"
