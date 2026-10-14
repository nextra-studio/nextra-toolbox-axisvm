param(
    [Parameter(Mandatory=$true)][string]$ResultsPath,
    [string]$DllPath = 'C:\AXISVM X8\AddOns\ConnectionLoadExtractorAddon.dll'
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $DllPath
$cases = Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json
$summary = foreach ($case in $cases) {
    $r = $case.matrix_rows
    $frame = [AxisVM_plugin.Core.LocalFrame]::FromMatrix(
        $r[0][0], $r[0][1], $r[0][2], $r[1][0], $r[1][1], $r[1][2],
        $r[2][0], $r[2][1], $r[2][2], $true)
    $nodeEnd = if ($case.node_end) { $case.node_end } else { $case.fixed_end }
    $isStart = $nodeEnd -eq 'start'
    $end = if ($isStart) { $case.ends[0] } else { $case.ends[1] }
    $local = [AxisVM_plugin.Core.ConnectionLoadMath]::RawToNodeLocal([double[]]$end.raw, $isStart)
    $global = [AxisVM_plugin.Core.ConnectionLoadMath]::LocalToGlobal($local, $frame)
    $expectedLocal = [AxisVM_plugin.Core.ConnectionLoadMath]::GlobalToLocal([double[]]$case.expected_on_fixed_node, $frame)
    $roundtripLocal = [AxisVM_plugin.Core.ConnectionLoadMath]::GlobalToLocal($global, $frame)
    $errorValue = 0.0
    $decompositionError = 0.0
    $roundtripError = 0.0
    for ($i = 0; $i -lt 6; $i++) {
        $errorValue = [Math]::Max($errorValue, [Math]::Abs($global[$i] - $case.expected_on_fixed_node[$i]))
        $decompositionError = [Math]::Max($decompositionError, [Math]::Abs($local[$i] - $expectedLocal[$i]))
        $roundtripError = [Math]::Max($roundtripError, [Math]::Abs($local[$i] - $roundtripLocal[$i]))
    }
    [pscustomobject]@{
        Specimen = $case.specimen
        Combination = $case.combination
        End = $nodeEnd
        MaxError = $errorValue
        DecompositionError = $decompositionError
        RoundtripError = $roundtripError
        Passed = $errorValue -lt 0.001 -and $decompositionError -lt 0.001 -and $roundtripError -lt 1e-10
    }
}
$summary | Export-Csv -LiteralPath (Join-Path (Split-Path $ResultsPath) 'installed-dll-checks.csv') -NoTypeInformation
$summary | Group-Object Specimen | ForEach-Object {
    [pscustomobject]@{
        Specimen = $_.Name
        Passed = @($_.Group | Where-Object Passed).Count
        Total = $_.Count
        MaxError = ($_.Group | Measure-Object MaxError -Maximum).Maximum
    }
} | Format-Table -AutoSize
if (@($summary | Where-Object { -not $_.Passed }).Count -gt 0) { exit 1 }
