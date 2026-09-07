#Requires -Version 7.0
<#
.SYNOPSIS
Build, test, publish and run the single-file CodexMeter desktop app.
.PARAMETER Fast
Skip tests only after they have already been run for these changes.
.PARAMETER NoLaunch
Validate the staged artifact without stopping or replacing the installed local build.
#>
[CmdletBinding()]
param([switch]$Fast, [switch]$NoLaunch)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
Set-Location -LiteralPath $RepoRoot
$StagingDir = Join-Path $RepoRoot 'publish/.dev-staging'
$LocalDir = Join-Path $RepoRoot 'publish/local'
$BackupDir = Join-Path $RepoRoot 'publish/local.previous'

function Assert-OwnedDirectory([string]$Target) {
    $absolute = [IO.Path]::GetFullPath($Target)
    if (!$absolute.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Directory outside repository: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) {
        if ((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse target' }
        if (Get-ChildItem -LiteralPath $absolute -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'Reparse descendant'
        }
    }
}
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments[0])" }
}
foreach ($target in @($StagingDir, $LocalDir, $BackupDir)) { Assert-OwnedDirectory $target }
if (Test-Path -LiteralPath $StagingDir) { Remove-Item -LiteralPath $StagingDir -Recurse -Force }
Invoke-Dotnet -Arguments @('restore')
Invoke-Dotnet -Arguments @('build', 'ProMeter.sln', '-c', 'Release')
if (!$Fast) { Invoke-Dotnet -Arguments @('test', 'ProMeter.sln', '-c', 'Release', '--no-build') }
Invoke-Dotnet -Arguments @('publish', 'src/ProMeter/ProMeter.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $StagingDir)
$files = @(Get-ChildItem -LiteralPath $StagingDir -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'CodexMeter.exe') { throw 'Publish must contain exactly CodexMeter.exe' }
Write-Host 'Publish artifacts verified: CodexMeter.exe only'
if ($NoLaunch) { Write-Host "Staged: $StagingDir"; exit 0 }

# Stop only this workspace's existing installation, including its former executable name.
$knownExecutables = @((Join-Path $LocalDir 'prometer.exe'), (Join-Path $LocalDir 'CodexMeter.exe'))
foreach ($process in @(Get-Process -Name 'prometer', 'CodexMeter' -ErrorAction SilentlyContinue)) {
    if ($process.Path -and $knownExecutables -contains $process.Path) {
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
}
foreach ($target in @($StagingDir, $LocalDir, $BackupDir)) { Assert-OwnedDirectory $target }
if (Test-Path -LiteralPath $BackupDir) { Remove-Item -LiteralPath $BackupDir -Recurse -Force }
if (Test-Path -LiteralPath $LocalDir) { Rename-Item -LiteralPath $LocalDir -NewName (Split-Path $BackupDir -Leaf) }
try { Move-Item -LiteralPath $StagingDir -Destination $LocalDir }
catch {
    if (Test-Path -LiteralPath $BackupDir) { Move-Item -LiteralPath $BackupDir -Destination $LocalDir }
    throw
}
$exe = Join-Path $LocalDir 'CodexMeter.exe'
$running = Start-Process -FilePath $exe -ArgumentList @('--show') -WorkingDirectory $LocalDir -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
if ($running.HasExited) { throw "CodexMeter exited at startup: $($running.ExitCode)" }
Write-Host "CodexMeter running: $exe (PID $($running.Id))"