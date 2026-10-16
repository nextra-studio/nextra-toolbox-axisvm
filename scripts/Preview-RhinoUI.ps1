# Renders the AxisBridge (Rhino) dialogs to PNG with synthetic data; no Rhino needed.
[CmdletBinding()]
param([string]$OutputDir)
$ErrorActionPreference = 'Stop'
if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'AxisVM plugin\docs\images\rhino' }
$repo = Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $repo 'Rhino\AxisBridge\AxisBridge.csproj') -c Release -v:q
if ($LASTEXITCODE) { throw 'AxisBridge build failed.' }
$bin = Join-Path $repo 'Rhino\AxisBridge\bin\Release'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('nextra-rhino-preview-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
Copy-Item "$bin\AxisBridge.rhp", "$bin\Newtonsoft.Json.dll" $temp
Copy-Item "$bin\AxisBridge.rhp" (Join-Path $temp 'AxisBridge.dll')  # so the runtime can resolve the assembly by name
$rc = Join-Path $env:USERPROFILE '.nuget\packages\rhinocommon\8.6.24101.5001\lib\net48'
Copy-Item "$rc\RhinoCommon.dll", "$rc\Rhino.UI.dll", "$rc\Eto.dll" $temp
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $temp 'RhinoUiPreview.exe'
& $csc /nologo /target:exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "/r:$temp\AxisBridge.dll" "/r:$temp\RhinoCommon.dll" "/r:$temp\Newtonsoft.Json.dll" "/out:$exe" (Join-Path $repo 'Rhino\Checks\RhinoUiPreview.cs')
if ($LASTEXITCODE) { throw 'Fixture compilation failed.' }
& $exe (Join-Path $temp "AxisBridge.dll") $OutputDir
if ($LASTEXITCODE) { throw 'Fixture failed.' }
