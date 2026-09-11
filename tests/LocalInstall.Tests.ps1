#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/LocalInstall.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CycleArc-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
function Assert-TestDirectory([string]$Target) {
    $absolute = [IO.Path]::GetFullPath($Target)
    if (!$absolute.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test target' }
}
try {
    foreach ($previousFile in @('CycleArc.exe', 'CodexMeter.exe', 'prometer.exe')) {
    foreach ($scenario in @('success', 'transient', 'old-locked', 'staging-failed', 'startup-failed')) {
        $directory = Join-Path $testRoot ($previousFile + '-' + $scenario)
        $local = Join-Path $directory 'local'
        $staging = Join-Path $directory 'staging'
        $backup = Join-Path $directory 'backup'
        foreach ($path in @($local, $staging, $backup)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
        Set-Content -LiteralPath (Join-Path $local $previousFile) -Value 'old'
        Set-Content -LiteralPath (Join-Path $staging 'CycleArc.exe') -Value 'new'
        Set-Content -LiteralPath (Join-Path $backup $previousFile) -Value 'stale'
        $state = @{ Moves = 0; Starts = [Collections.Generic.List[string]]::new() }
        $move = {
            param($Source, $Destination)
            if ($Source -eq $local -and $Destination -eq $backup) {
                $state.Moves++
                if ($scenario -eq 'old-locked' -or ($scenario -eq 'transient' -and $state.Moves -lt 3)) { throw 'Synthetic lock' }
            }
            if ($scenario -eq 'staging-failed' -and $Source -eq $staging) { throw 'Synthetic staging failure' }
            Move-Item -LiteralPath $Source -Destination $Destination
        }
        $start = {
            param($Directory)
            $version = (Get-Content -LiteralPath (Resolve-InstalledExecutable $Directory) -Raw).Trim()
            $state.Starts.Add($version)
            if ($scenario -eq 'startup-failed' -and $version -eq 'new') { throw 'Synthetic startup failure' }
        }
        $failed = $false
        try { Install-StagedApp $staging $local $backup ${function:Assert-TestDirectory} $move $start -Attempts 3 -DelayMilliseconds 1 }
        catch { $failed = $true }
        $expectFailure = $scenario -in @('old-locked', 'staging-failed', 'startup-failed')
        if ($failed -ne $expectFailure) { throw "Unexpected outcome: $scenario" }
        $expected = if ($expectFailure) { 'old' } else { 'new' }
        $expectedFile = if ($expectFailure) { $previousFile } else { 'CycleArc.exe' }
        if ((Get-Content -LiteralPath (Join-Path $local $expectedFile) -Raw).Trim() -ne $expected) { throw "Wrong installed version: $scenario" }
        if ($state.Starts[-1] -ne $expected) { throw "Wrong restarted version: $scenario" }
        if ($scenario -eq 'transient' -and $state.Moves -ne 3) { throw 'Transient lock was not retried' }
        if (!$expectFailure -and (Get-Content -LiteralPath (Join-Path $backup $previousFile) -Raw).Trim() -ne 'old') { throw 'Previous version lost' }
    }
    }
    Write-Host 'PASS: 15 isolated installer deployment/retry/rollback scenarios across CycleArc and both legacy executable names.'
}
finally {
    # All recursive removals are verified against the unique test root.
    foreach ($directory in Get-ChildItem -LiteralPath $testRoot -Directory) {
        Assert-TestDirectory $directory.FullName
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
    Remove-Item -LiteralPath $testRoot
}
