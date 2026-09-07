# Shared by dev-run and the isolated installer regression tests.
function Invoke-InstallRetry([scriptblock]$Action, [int]$Attempts = 30, [int]$DelayMilliseconds = 500) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try { & $Action; return }
        catch {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Install-StagedApp {
    param(
        [string]$StagingDir, [string]$LocalDir, [string]$BackupDir,
        [scriptblock]$Validate,
        [scriptblock]$Move = { param($Source, $Destination) Move-Item -LiteralPath $Source -Destination $Destination },
        [scriptblock]$Start = {
            param($Directory)
            $exe = Join-Path $Directory 'CodexMeter.exe'
            if (!(Test-Path -LiteralPath $exe)) { $exe = Join-Path $Directory 'prometer.exe' }
            $running = Start-Process -FilePath $exe -ArgumentList @('--show') -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
            Start-Sleep -Seconds 2
            if ($running.HasExited) { throw "CodexMeter exited at startup: $($running.ExitCode)" }
            Write-Host "CodexMeter running: $exe (PID $($running.Id))"
        },
        [int]$Attempts = 30, [int]$DelayMilliseconds = 500
    )
    $oldMoved = $false
    $newMoved = $false
    $hadLocal = Test-Path -LiteralPath $LocalDir
    try {
        foreach ($target in @($StagingDir, $LocalDir, $BackupDir)) { & $Validate $target }
        if (Test-Path -LiteralPath $BackupDir) {
            Invoke-InstallRetry { & $Validate $BackupDir; Remove-Item -LiteralPath $BackupDir -Recurse -Force } $Attempts $DelayMilliseconds
        }
        if ($hadLocal) {
            Invoke-InstallRetry { & $Validate $LocalDir; & $Move $LocalDir $BackupDir } $Attempts $DelayMilliseconds
            $oldMoved = $true
        }
        Invoke-InstallRetry { & $Validate $StagingDir; & $Move $StagingDir $LocalDir } $Attempts $DelayMilliseconds
        $newMoved = $true
        & $Start $LocalDir
    }
    catch {
        $failure = $_
        # Never restore a stale backup if moving the current installation failed.
        if ($newMoved) {
            Invoke-InstallRetry { & $Validate $LocalDir; & $Move $LocalDir $StagingDir } $Attempts $DelayMilliseconds
        }
        if ($oldMoved) {
            Invoke-InstallRetry { & $Validate $BackupDir; & $Move $BackupDir $LocalDir } $Attempts $DelayMilliseconds
        }
        if ($hadLocal) { & $Start $LocalDir }
        throw $failure
    }
}
