param(
    [switch]$SkipCoverage,
    [switch]$SkipRestore,
    [switch]$RunBrowserE2E,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"

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
    $buildArguments += "EmbodySense.sln"
    $buildArguments += "-c"
    $buildArguments += $Configuration
    $buildArguments += "/p:RestoreIgnoreFailedSources=true"

    Invoke-CheckedNative "dotnet" $buildArguments

    $isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $npm = if ($isWindows) { "npm.cmd" } else { "npm" }
    Invoke-CheckedNative $npm @("test")

    if ($RunBrowserE2E) {
        $oldRunBrowserE2E = $env:EMBODYSENSE_RUN_BROWSER_E2E
        try {
            $env:EMBODYSENSE_RUN_BROWSER_E2E = "1"
            $e2eProjectPath = Join-Path $testsPath "EmbodySense.E2ETests\EmbodySense.E2ETests.csproj"
            Invoke-CheckedNative "dotnet" @("test", $e2eProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--filter", "FullyQualifiedName~BrowserFlowTests", "/p:RestoreIgnoreFailedSources=true")
        }
        finally {
            if ($null -eq $oldRunBrowserE2E) {
                Remove-Item Env:\EMBODYSENSE_RUN_BROWSER_E2E -ErrorAction SilentlyContinue
            }
            else {
                $env:EMBODYSENSE_RUN_BROWSER_E2E = $oldRunBrowserE2E
            }
        }
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
