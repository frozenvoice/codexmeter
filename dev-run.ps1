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

# Mirrors ProMeter.Core's ChromiumExtensionId.TryCompute: SHA-256 of the manifest's
# base64 "key" (DER SubjectPublicKeyInfo), first 16 bytes, each nibble mapped
# 0-9a-f -> a-p. Read-only: this never writes a key, it only derives the ID an
# unpacked load of this manifest will get, regardless of which folder it's loaded
# from.
function Get-DeterministicCompanionExtensionId {
    param([Parameter(Mandatory)][string]$ManifestPath)

    if (-not (Test-Path $ManifestPath)) {
        return $null
    }

    try {
        $manifest = Get-Content -Path $ManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }

    $keyB64 = $manifest.key
    if ([string]::IsNullOrWhiteSpace($keyB64)) {
        return $null
    }

    try {
        $keyBytes = [Convert]::FromBase64String($keyB64.Trim())
    }
    catch {
        return $null
    }

    $hash = [System.Security.Cryptography.SHA256]::HashData($keyBytes)
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt 16; $i++) {
        $b = $hash[$i]
        [void]$sb.Append([char](97 + ($b -shr 4)))
        [void]$sb.Append([char](97 + ($b -band 0xF)))
    }
    return $sb.ToString()
}

# Read-only inspection of ProMeter's own native-host manifest and HKCU
# registration. dev-run.ps1 never writes the registry or the manifest itself -
# ProMeter owns registration; this only verifies and reports.
function Get-CompanionRegistrationState {
    param([Parameter(Mandatory)][string]$ExpectedExtensionId)

    $manifestPath = Join-Path $env:LOCALAPPDATA 'ProMeter\com.prometer.bridge.json'
    $expectedOrigin = "chrome-extension://$ExpectedExtensionId/"

    $state = [ordered]@{
        ManifestPath      = $manifestPath
        ManifestExists    = $false
        ManifestHasOrigin = $false
        ChromeRegistered  = $false
        EdgeRegistered    = $false
    }

    if (Test-Path $manifestPath) {
        $state.ManifestExists = $true
        try {
            $manifestJson = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
            if ($manifestJson.allowed_origins -contains $expectedOrigin) {
                $state.ManifestHasOrigin = $true
            }
        }
        catch {
            # Leave ManifestHasOrigin false; malformed manifest is reported as a mismatch.
        }
    }

    $chromeKey = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.prometer.bridge'
    $chromeItem = Get-Item -Path $chromeKey -ErrorAction SilentlyContinue
    if ($chromeItem -and $chromeItem.GetValue('') -eq $manifestPath) {
        $state.ChromeRegistered = $true
    }

    $edgeKey = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.prometer.bridge'
    $edgeItem = Get-Item -Path $edgeKey -ErrorAction SilentlyContinue
    if ($edgeItem -and $edgeItem.GetValue('') -eq $manifestPath) {
        $state.EdgeRegistered = $true
    }

    return $state
}

function Test-CompanionRegistrationMatches {
    param($State)
    return $State.ManifestExists -and $State.ManifestHasOrigin -and ($State.ChromeRegistered -or $State.EdgeRegistered)
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

$ExpectedExtensionId = Get-DeterministicCompanionExtensionId -ManifestPath (Join-Path $RepoRoot 'extension\manifest.json')
if ($ExpectedExtensionId) {
    Write-Host "Companion extension ID : $ExpectedExtensionId"
}
else {
    Write-Host "Companion extension ID : unavailable (extension\manifest.json has no 'key')" -ForegroundColor Yellow
}

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

    if ($ExpectedExtensionId) {
        Write-Host ""
        Write-Host "Browser Companion registration (informational, -NoLaunch changes nothing):"
        $state = Get-CompanionRegistrationState -ExpectedExtensionId $ExpectedExtensionId
        if (Test-CompanionRegistrationMatches $state) {
            Write-Host "  OK - existing registration already matches extension ID $ExpectedExtensionId"
        }
        elseif ($state.ManifestExists) {
            Write-Host "  Mismatch - $($state.ManifestPath) does not currently include chrome-extension://$ExpectedExtensionId/"
        }
        else {
            Write-Host "  Not registered yet - $($state.ManifestPath) does not exist (expected once Browser Companion is opted in and ProMeter has started)"
        }
    }

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

if ($ExpectedExtensionId) {
    # Bounded wait for ProMeter's own startup registration (EnsureCompanionHostRegistration)
    # to run; dev-run.ps1 never writes the manifest or registry itself.
    $deadline = (Get-Date).AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 500
        $state = Get-CompanionRegistrationState -ExpectedExtensionId $ExpectedExtensionId
    } while (-not (Test-CompanionRegistrationMatches $state) -and (Get-Date) -lt $deadline)

    Write-Host ""
    if (Test-CompanionRegistrationMatches $state) {
        Write-Host "Browser Companion registration: OK" -ForegroundColor Green
        Write-Host "Extension ID: $ExpectedExtensionId"
    }
    else {
        Write-Host "Browser Companion registration mismatch" -ForegroundColor Yellow
        Write-Host "Expected extension ID: $ExpectedExtensionId"
        Write-Host "Restart ProMeter or re-register Browser Companion."
    }
}

Write-Host ""
Write-Host "ProMeter local build running"
Write-Host "HEAD: $HeadSha"
Write-Host "EXE : $ExePath"
Write-Host ""
Write-Host "If extension/* changed, reload the unpacked browser extension."
