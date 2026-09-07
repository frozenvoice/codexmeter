#Requires -Version 7.0
<#
.SYNOPSIS
    One-command local build/test/publish/launch runner for ProMeter.

.DESCRIPTION
    Release build -> full tests -> local publish -> stop the running ProMeter ->
    launch the freshly published build. Mirrors the GitHub Actions Windows
    workflow's publish options so a local run and CI produce the same artifact
    shape. GitHub Actions remains the final CI verification; this script is
    for iterating locally without downloading/unzipping CI artifacts.

.PARAMETER Fast
    Skip the full test run. Build and publish still run in full. Never the
    default.

.PARAMETER NoLaunch
    Build, test (unless -Fast), publish, and validate the artifact, but never
    stop the running ProMeter and never deploy/launch a new one. Prints the
    staged publish path only.

.EXAMPLE
    .\dev-run.ps1

.EXAMPLE
    .\dev-run.ps1 -Fast

.EXAMPLE
    .\dev-run.ps1 -NoLaunch
#>
[CmdletBinding()]
param(
    [switch]$Fast,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$Description
    )

    Write-Host "  > $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit code $LASTEXITCODE)."
    }
}

function Write-StepHeader {
    param([int]$Index, [int]$Total, [string]$Name)
    Write-Host ""
    Write-Host "[$Index/$Total] $Name" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# Preliminaries: repo root, dotnet availability, current HEAD.
# ---------------------------------------------------------------------------

$RepoRoot = $PSScriptRoot
Set-Location -Path $RepoRoot

if (-not (Test-Path (Join-Path $RepoRoot 'ProMeter.sln'))) {
    throw "ProMeter.sln not found next to dev-run.ps1 ($RepoRoot). Run this script from its own location in the repo."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found on PATH. Install the .NET 8 SDK before running dev-run.ps1."
}

$HeadSha = (& git rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($HeadSha)) {
    throw "git rev-parse --short HEAD failed. Is this a git checkout?"
}
$HeadSha = $HeadSha.Trim()

Write-Host "Repo root : $RepoRoot"
Write-Host "HEAD      : $HeadSha"
if ($Fast) {
    Write-Host ""
    Write-Host "FAST MODE: full tests skipped" -ForegroundColor Yellow
}

$TotalSteps = 5
$StagingDir = Join-Path $RepoRoot 'publish\.dev-staging'
$LocalDir = Join-Path $RepoRoot 'publish\local'
$BackupDir = Join-Path $RepoRoot 'publish\local.previous'

# The staging directory is never the active running location, so it is safe
# to clear it unconditionally on every run.
if (Test-Path $StagingDir) {
    Remove-Item -Path $StagingDir -Recurse -Force
}

# ---------------------------------------------------------------------------
# [1/5] Build
# ---------------------------------------------------------------------------

Write-StepHeader -Index 1 -Total $TotalSteps -Name 'Build'
Invoke-Checked -FilePath 'dotnet' -ArgumentList @('restore') -Description 'dotnet restore'
Invoke-Checked -FilePath 'dotnet' -ArgumentList @('build', 'ProMeter.sln', '-c', 'Release') -Description 'Release build'

# ---------------------------------------------------------------------------
# [2/5] Test
# ---------------------------------------------------------------------------

Write-StepHeader -Index 2 -Total $TotalSteps -Name 'Test'
if ($Fast) {
    Write-Host "  FAST MODE: full tests skipped" -ForegroundColor Yellow
}
else {
    Invoke-Checked -FilePath 'dotnet' -ArgumentList @('test', 'ProMeter.sln', '-c', 'Release', '--no-build') -Description 'Release tests'
}

# ---------------------------------------------------------------------------
# [3/5] Publish (staged, never published directly on top of a running build)
# ---------------------------------------------------------------------------

Write-StepHeader -Index 3 -Total $TotalSteps -Name 'Publish'
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

Invoke-Checked -FilePath 'dotnet' -ArgumentList @(
    'publish', 'src/ProMeter/ProMeter.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $StagingDir
) -Description 'dotnet publish'

# Same artifact assertion as the GitHub Actions workflow.
$RequiredArtifacts = @(
    'prometer.exe',
    'prometer-companion-host.exe',
    'extension\manifest.json',
    'extension\background.js',
    'extension\page-executor.js',
    'extension\page-tab.js',
    'extension\companion-reconnect.js'
)
foreach ($relative in $RequiredArtifacts) {
    $full = Join-Path $StagingDir $relative
    if (-not (Test-Path $full)) {
        throw "Publish validation failed: missing '$relative' in $StagingDir"
    }
}
Write-Host "  Publish artifacts verified in $StagingDir"

if ($NoLaunch) {
    Write-Host ""
    Write-Host "[4/$TotalSteps] Deploy" -ForegroundColor Cyan
    Write-Host "  Skipped (-NoLaunch): existing ProMeter left untouched, publish\local not replaced."
    Write-Host ""
    Write-Host "[5/$TotalSteps] Launch" -ForegroundColor Cyan
    Write-Host "  Skipped (-NoLaunch): no new instance started."
    Write-Host ""
    Write-Host "ProMeter local build ready (not launched)"
    Write-Host "HEAD    : $HeadSha"
    Write-Host "Publish : $StagingDir"
    Write-Host ""
    Write-Host "If extension/* changed, reload the unpacked browser extension."
    exit 0
}

# ---------------------------------------------------------------------------
# [4/5] Deploy: stop the running ProMeter, then swap staging into publish\local
# ---------------------------------------------------------------------------

Write-StepHeader -Index 4 -Total $TotalSteps -Name 'Deploy'

# Match by exact process name only. This never touches
# prometer-companion-host, which Chrome/Edge Native Messaging starts and
# stops on its own lifecycle.
$running = @(Get-Process -Name 'prometer' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    foreach ($proc in $running) {
        Write-Host "  Found running ProMeter process: PID $($proc.Id)"
    }
    foreach ($proc in $running) {
        try {
            Stop-Process -Id $proc.Id -ErrorAction Stop
        }
        catch {
            throw "Failed to stop existing ProMeter process (PID $($proc.Id)): $($_.Exception.Message)"
        }
    }

    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $stillRunning = @($running | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
    } while ($stillRunning.Count -gt 0 -and (Get-Date) -lt $deadline)

    if ($stillRunning.Count -gt 0) {
        throw "Existing ProMeter process(es) did not exit within 10s: $(($stillRunning | ForEach-Object { $_.Id }) -join ', '). Not launching a new instance."
    }

    Write-Host "  Existing ProMeter process(es) stopped."
}
else {
    Write-Host "  No running ProMeter process found."
}

# Preserve the previous publish\local until the moment it is actually
# replaced, and restore it if the swap fails partway through.
if (Test-Path $BackupDir) {
    Remove-Item -Path $BackupDir -Recurse -Force
}
if (Test-Path $LocalDir) {
    Rename-Item -Path $LocalDir -NewName 'local.previous'
}

try {
    Move-Item -Path $StagingDir -Destination $LocalDir -Force
}
catch {
    Write-Host "  Deploy failed while replacing publish\local: $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $BackupDir) {
        if (Test-Path $LocalDir) {
            Remove-Item -Path $LocalDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        Rename-Item -Path $BackupDir -NewName 'local'
        Write-Host "  Restored the previous publish\local." -ForegroundColor Yellow
    }
    throw "Deploy failed: publish\local was not replaced. The previous build (if any) was restored; no new instance will be launched."
}

if (Test-Path $BackupDir) {
    Remove-Item -Path $BackupDir -Recurse -Force
}

Write-Host "  Deployed to $LocalDir"

# ---------------------------------------------------------------------------
# [5/5] Launch
# ---------------------------------------------------------------------------

Write-StepHeader -Index 5 -Total $TotalSteps -Name 'Launch'
$ExePath = Join-Path $LocalDir 'prometer.exe'
if (-not (Test-Path $ExePath)) {
    throw "Launch failed: $ExePath does not exist after deploy."
}

Start-Process -FilePath $ExePath | Out-Null
Write-Host "  Started $ExePath"

Write-Host ""
Write-Host "ProMeter local build running"
Write-Host "HEAD: $HeadSha"
Write-Host "EXE : $ExePath"
Write-Host ""
Write-Host "If extension/* changed, reload the unpacked browser extension."
