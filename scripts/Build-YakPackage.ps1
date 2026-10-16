# Builds the Rhino Package Manager (Yak) package for the AxisBridge Rhino plug-in.
# Output: Rhino\yak\out\*.yak   (publish with: yak push <file>)
[CmdletBinding()]
param(
    [string]$Yak = 'C:\Program Files\Rhino 8\System\Yak.exe',
    [string]$Version = '1.0.1'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
dotnet build (Join-Path $root 'Rhino\AxisBridge\AxisBridge.csproj') -c Release | Out-Host
if ($LASTEXITCODE) { throw 'AxisBridge build failed.' }
$bin = Join-Path $root 'Rhino\AxisBridge\bin\Release'
$stage = Join-Path $root 'Rhino\yak\out'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $bin 'AxisBridge.rhp'), (Join-Path $bin 'Newtonsoft.Json.dll') $stage
Copy-Item (Join-Path $root 'Rhino\yak\manifest.yml') $stage
Copy-Item (Join-Path $root 'Rhino\yak\README.md') $stage
Push-Location $stage
try { & $Yak build --platform win | Out-Host } finally { Pop-Location }
Get-ChildItem $stage -Filter *.yak | ForEach-Object { Write-Output "Built $($_.FullName)" }
