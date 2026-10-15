[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\AxisVM plugin\docs\images'),
    [switch]$Stress
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $repo 'AxisVM plugin\AxisVM plugin\bin\Release'
dotnet build (Join-Path $repo 'AxisVM plugin\AxisVM plugin.slnx') -c Release -v:q -m:1
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The fixture only creates its own off-screen forms with synthetic inputs; no COM model is opened.
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('nextra-preview-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$exe = Join-Path $temp 'UiPreview.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "/r:$bin\ConnectionLoadExtractorAddon.dll" "/out:$exe" (Join-Path $repo 'AxisVM plugin\Checks\UiPreview.cs')
if ($LASTEXITCODE -ne 0) { throw 'Preview fixture compilation failed.' }
Copy-Item -LiteralPath (Join-Path $bin 'ConnectionLoadExtractorAddon.dll') -Destination $temp
if ($Stress) { & $exe $bin $OutputDir '--stress' } else { & $exe $bin $OutputDir }
if ($LASTEXITCODE -ne 0) { throw 'Preview fixture failed.' }
