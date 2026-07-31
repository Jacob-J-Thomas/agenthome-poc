param(
    [switch]$SkipCoverage,
    [switch]$SkipRestore,
    [switch]$RunBrowserE2E,
    [switch]$BrowserE2EOnly,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"
$e2eProjectPath = Join-Path $testsPath "EmbodySense.E2ETests\EmbodySense.E2ETests.csproj"

if ($BrowserE2EOnly -and -not $RunBrowserE2E) {
    throw "-BrowserE2EOnly requires -RunBrowserE2E."
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName exited with code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot "verify-sdk.ps1") -GlobalJsonPath (Join-Path $repoRoot "global.json")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Get-ChildItem -Path $testsPath -Directory | ForEach-Object {
        $testResultsPath = Join-Path $_.FullName "TestResults"
        if (Test-Path $testResultsPath) {
            Remove-Item -LiteralPath $testResultsPath -Recurse -Force
        }
    }

    $buildArguments = @("build")
    if ($SkipRestore) {
        $buildArguments += "--no-restore"
    }
    $buildArguments += if ($BrowserE2EOnly) { $e2eProjectPath } else { "EmbodySense.sln" }
    $buildArguments += "-c"
    $buildArguments += $Configuration
    $buildArguments += "/p:RestoreIgnoreFailedSources=true"

    Invoke-CheckedNative "dotnet" $buildArguments

    if (-not $BrowserE2EOnly) {
        Invoke-CheckedNative "dotnet" @("format", "whitespace", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--verbosity", "minimal")
        Invoke-CheckedNative "dotnet" @("format", "style", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal")

        $runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
        $npm = if ($runningOnWindows) { "npm.cmd" } else { "npm" }
        Invoke-CheckedNative $npm @("ci", "--include=dev")
        Invoke-CheckedNative $npm @("test")
    }

    if ($RunBrowserE2E) {
        $oldRunBrowserE2E = $env:EMBODYSENSE_RUN_BROWSER_E2E
        $oldBrowserE2EArtifacts = $env:EMBODYSENSE_BROWSER_E2E_ARTIFACTS
        try {
            $env:EMBODYSENSE_RUN_BROWSER_E2E = "1"
            $browserE2ETestResultsPath = Join-Path $testsPath "EmbodySense.E2ETests\TestResults\BrowserE2E"
            $env:EMBODYSENSE_BROWSER_E2E_ARTIFACTS = $browserE2ETestResultsPath
            Invoke-CheckedNative "dotnet" @("test", $e2eProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--filter", "FullyQualifiedName~BrowserFlowTests", "--logger", "trx;LogFileName=browser-e2e.trx", "--results-directory", $browserE2ETestResultsPath, "/p:RestoreIgnoreFailedSources=true")
        }
        finally {
            if ($null -eq $oldRunBrowserE2E) {
                Remove-Item Env:\EMBODYSENSE_RUN_BROWSER_E2E -ErrorAction SilentlyContinue
            }
            else {
                $env:EMBODYSENSE_RUN_BROWSER_E2E = $oldRunBrowserE2E
            }

            if ($null -eq $oldBrowserE2EArtifacts) {
                Remove-Item Env:\EMBODYSENSE_BROWSER_E2E_ARTIFACTS -ErrorAction SilentlyContinue
            }
            else {
                $env:EMBODYSENSE_BROWSER_E2E_ARTIFACTS = $oldBrowserE2EArtifacts
            }
        }
    }

    if ($BrowserE2EOnly) {
        return
    }

    if (-not $SkipCoverage) {
        $coverageStartedUtc = [DateTime]::UtcNow
        Get-ChildItem -Path $testsPath -Recurse -Filter "*.csproj" | Where-Object {
            $_.Name -ne "EmbodySense.Tests.Support.csproj"
        } | Sort-Object FullName | ForEach-Object {
            if ($_.Name -eq "EmbodySense.E2ETests.csproj") {
                Invoke-CheckedNative "dotnet" @("test", $_.FullName, "-c", $Configuration, "--no-build", "--no-restore", "--collect:XPlat Code Coverage", "--filter", "FullyQualifiedName!~BrowserFlowTests", "/p:RestoreIgnoreFailedSources=true")
            }
            else {
                Invoke-CheckedNative "dotnet" @("test", $_.FullName, "-c", $Configuration, "--no-build", "--no-restore", "--collect:XPlat Code Coverage", "/p:RestoreIgnoreFailedSources=true")
            }
        }

        & (Join-Path $PSScriptRoot "verify-coverage.ps1") -MinimumWriteTimeUtc $coverageStartedUtc
    }
    else {
        Get-ChildItem -Path $testsPath -Recurse -Filter "*.csproj" | Where-Object {
            $_.Name -ne "EmbodySense.Tests.Support.csproj"
        } | Sort-Object FullName | ForEach-Object {
            Invoke-CheckedNative "dotnet" @("test", $_.FullName, "-c", $Configuration, "--no-build", "--no-restore", "/p:RestoreIgnoreFailedSources=true")
        }
    }
}
finally {
    Pop-Location
}
