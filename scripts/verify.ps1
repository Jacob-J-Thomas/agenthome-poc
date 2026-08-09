param(
    [switch]$SkipCoverage,
    [switch]$SkipRestore,
    [switch]$RunBrowserE2E,
    [switch]$BrowserE2EOnly,
    [ValidateSet("PullRequest", "Stress")]
    [string]$VerificationTier = "PullRequest",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"
$e2eProjectPath = Join-Path $testsPath "EmbodySense.E2ETests\EmbodySense.E2ETests.csproj"
$persistenceTestProjectPath = Join-Path $testsPath "EmbodySense.Core.Persistence.Tests\EmbodySense.Core.Persistence.Tests.csproj"
$pullRequestRunSettingsPath = Join-Path $testsPath "verification-pull-request.runsettings"
$stressRunSettingsPath = Join-Path $testsPath "verification-stress.runsettings"
$stressResultsPath = Join-Path $testsPath "EmbodySense.Core.Persistence.Tests\TestResults\VerificationStress"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$maximumArtifactStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunArtifactMaximumShapeTests.Adversarial_maximum_transition_reservations_and_canonical_order_checks_remain_bounded"
$deletionCapacityStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTraceRetentionStoreTests.Rejected_operation_capacity_preserves_reserved_tombstone_deletions_and_remains_visible"

. (Join-Path $PSScriptRoot "verification-phase.ps1")
Reset-VerificationPhaseState

if ($BrowserE2EOnly -and -not $RunBrowserE2E) {
    throw "-BrowserE2EOnly requires -RunBrowserE2E."
}

if ($VerificationTier -eq "Stress" -and ($RunBrowserE2E -or $BrowserE2EOnly)) {
    throw "The Stress verification tier cannot be combined with browser E2E switches."
}

function Invoke-CheckedNativePhase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    Invoke-VerificationPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $repoRoot
}

function Assert-CoverageReportProduced {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$TestProject,

        [Parameter(Mandatory = $true)]
        [DateTime]$MinimumWriteTimeUtc
    )

    $coverageReport = Get-ChildItem -Path (Join-Path $TestProject.DirectoryName "TestResults") -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $coverageReport) {
        throw "Coverage collection for $($TestProject.BaseName) did not produce a fresh coverage.cobertura.xml report."
    }
}

function New-CoverageChildProcessAssemblyCopy {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$TestProject,

        [Parameter(Mandatory = $true)]
        [string]$BuildConfiguration
    )

    [xml]$project = Get-Content -LiteralPath $TestProject.FullName -Raw
    $targetFrameworks = @($project.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($targetFrameworks.Count -ne 1) {
        throw "Coverage child-process isolation requires one explicit target framework in $($TestProject.Name)."
    }

    $targetFramework = [string]$targetFrameworks[0]
    $sourceDirectory = Join-Path (Join-Path (Join-Path $TestProject.DirectoryName "bin") $BuildConfiguration) $targetFramework
    if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
        throw "Coverage child-process isolation source does not exist: $sourceDirectory"
    }

    $assetsPath = Join-Path $TestProject.DirectoryName "obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Coverage child-process isolation assets do not exist: $assetsPath"
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $coverletPackage = @($assets.libraries.PSObject.Properties.Name | Where-Object {
        $_.StartsWith("coverlet.collector/", [StringComparison]::OrdinalIgnoreCase)
    })
    if ($coverletPackage.Count -ne 1) {
        throw "Coverage child-process isolation requires one resolved coverlet.collector package."
    }

    $collectorSource = $null
    foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path (Join-Path (Join-Path $packageFolder $coverletPackage[0]) "build") $targetFramework
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $collectorSource = $candidate
            break
        }
    }
    if ($null -eq $collectorSource) {
        throw "Coverage child-process isolation could not locate the resolved coverlet.collector binaries for $targetFramework."
    }

    $isolationRoot = Join-Path $TestProject.DirectoryName "TestResults\CoverageChildProcess"
    $pristineDirectory = Join-Path $isolationRoot "Pristine"
    $collectorDirectory = Join-Path $isolationRoot "Collector"
    New-Item -ItemType Directory -Path $pristineDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $collectorDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDirectory "*") -Destination $pristineDirectory -Recurse -Force
    Copy-Item -Path (Join-Path $collectorSource "*") -Destination $collectorDirectory -Recurse -Force
    Copy-Item -LiteralPath $pullRequestRunSettingsPath -Destination (Join-Path $isolationRoot "verification-pull-request.runsettings") -Force
    return $pristineDirectory
}

Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot "verify-sdk.ps1") -GlobalJsonPath (Join-Path $repoRoot "global.json") -RepositoryRoot $repoRoot
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-VerificationContext -RepositoryRoot $repoRoot -Configuration $Configuration -VerificationTier $VerificationTier
    Write-Output "VERIFY_TIER_SELECTION tier=$VerificationTier stress_owner=.github/workflows/verification-stress.yml"

    $cleanupStarted = [Diagnostics.Stopwatch]::StartNew()
    Write-Output "VERIFY_PHASE_START name=clean-test-results started_at_utc=$([DateTimeOffset]::UtcNow.ToString("O")) timeout_seconds=none last_completed=$script:LastCompletedVerificationPhase"
    Get-ChildItem -Path $testsPath -Directory | ForEach-Object {
        $testResultsPath = Join-Path $_.FullName "TestResults"
        if (Test-Path $testResultsPath) {
            Remove-Item -LiteralPath $testResultsPath -Recurse -Force
        }
    }
    $cleanupStarted.Stop()
    $script:LastCompletedVerificationPhase = "clean-test-results"
    Write-Output "VERIFY_PHASE_COMPLETE name=clean-test-results elapsed_seconds=$([Math]::Round($cleanupStarted.Elapsed.TotalSeconds, 3)) completed_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"

    $buildArguments = @("build")
    if ($SkipRestore) {
        $buildArguments += "--no-restore"
    }
    $buildArguments += if ($VerificationTier -eq "Stress") { $persistenceTestProjectPath } elseif ($BrowserE2EOnly) { $e2eProjectPath } else { "EmbodySense.sln" }
    $buildArguments += "-c"
    $buildArguments += $Configuration
    $buildArguments += "/p:RestoreIgnoreFailedSources=true"

    Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900

    if ($VerificationTier -eq "Stress") {
        Write-Output "VERIFY_STRESS_CONTRACT exact_test_count=2 session_timeout_seconds=1500 max_artifact_process_timeout_seconds=1800 deletion_capacity_process_timeout_seconds=1200"
        $maximumResultsPath = Join-Path $stressResultsPath "MaximumArtifact"
        $maximumFilter = "FullyQualifiedName=$maximumArtifactStressTest&VerificationTier=Stress"
        Invoke-CheckedNativePhase -Name "stress-maximum-artifact" -FileName "dotnet" -Arguments @("test", $persistenceTestProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $stressRunSettingsPath, "--filter", $maximumFilter, "--logger", "trx;LogFileName=maximum-artifact-stress.trx", "--results-directory", $maximumResultsPath, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 1800

        $deletionResultsPath = Join-Path $stressResultsPath "DeletionCapacity"
        $deletionFilter = "FullyQualifiedName=$deletionCapacityStressTest&VerificationTier=Stress"
        Invoke-CheckedNativePhase -Name "stress-deletion-operation-capacity" -FileName "dotnet" -Arguments @("test", $persistenceTestProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $stressRunSettingsPath, "--filter", $deletionFilter, "--logger", "trx;LogFileName=deletion-capacity-stress.trx", "--results-directory", $deletionResultsPath, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 1200
        return
    }

    if (-not $BrowserE2EOnly) {
        Invoke-CheckedNativePhase -Name "format-whitespace" -FileName "dotnet" -Arguments @("format", "whitespace", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--verbosity", "minimal") -TimeoutSeconds 300
        Invoke-CheckedNativePhase -Name "format-naming-style" -FileName "dotnet" -Arguments @("format", "style", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal") -TimeoutSeconds 300

        if ($runningOnWindows) {
            Invoke-CheckedNativePhase -Name "npm-ci" -FileName $env:ComSpec -Arguments @("/d", "/s", "/c", "npm.cmd ci --include=dev") -TimeoutSeconds 600
            Invoke-CheckedNativePhase -Name "frontend-tests" -FileName $env:ComSpec -Arguments @("/d", "/s", "/c", "npm.cmd test") -TimeoutSeconds 600
        }
        else {
            Invoke-CheckedNativePhase -Name "npm-ci" -FileName "npm" -Arguments @("ci", "--include=dev") -TimeoutSeconds 600
            Invoke-CheckedNativePhase -Name "frontend-tests" -FileName "npm" -Arguments @("test") -TimeoutSeconds 600
        }
    }

    if ($RunBrowserE2E) {
        $oldRunBrowserE2E = $env:EMBODYSENSE_RUN_BROWSER_E2E
        $oldBrowserE2EArtifacts = $env:EMBODYSENSE_BROWSER_E2E_ARTIFACTS
        try {
            $env:EMBODYSENSE_RUN_BROWSER_E2E = "1"
            $browserE2ETestResultsPath = Join-Path $testsPath "EmbodySense.E2ETests\TestResults\BrowserE2E"
            $env:EMBODYSENSE_BROWSER_E2E_ARTIFACTS = $browserE2ETestResultsPath
            Invoke-CheckedNativePhase -Name "browser-e2e" -FileName "dotnet" -Arguments @("test", $e2eProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $pullRequestRunSettingsPath, "--filter", "FullyQualifiedName~BrowserFlowTests", "--logger", "trx;LogFileName=browser-e2e.trx", "--results-directory", $browserE2ETestResultsPath, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 1200
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

    Write-Output "VERIFY_REQUIRED_TEST_CONTRACT filter=VerificationTier!=Stress required_maximum_test=EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunArtifactMaximumShapeTests.Public_artifact_contract_round_trips_the_maximum_bounded_shape_below_fifteen_mebibytes"
    $testProjects = Get-ChildItem -Path $testsPath -Recurse -Filter "*.csproj" | Where-Object {
        $_.Name -ne "EmbodySense.CancellationHost.csproj" -and $_.Name -ne "EmbodySense.Tests.Support.csproj"
    } | Sort-Object FullName
    if (-not $SkipCoverage) {
        $coverageStartedUtc = [DateTime]::UtcNow
        $testProjects | ForEach-Object {
            $filter = if ($_.Name -eq "EmbodySense.E2ETests.csproj") { "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)" } else { "VerificationTier!=Stress" }
            $testArguments = @("test", $_.FullName, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $pullRequestRunSettingsPath, "--collect:XPlat Code Coverage", "--filter", $filter, "/p:RestoreIgnoreFailedSources=true")
            if ($_.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
                $testArguments += @("--logger", "console;verbosity=detailed")
            }

            $previousCoverageChildAssemblyDirectory = $env:EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY
            try {
                if ($_.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
                    $env:EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = New-CoverageChildProcessAssemblyCopy -TestProject $_ -BuildConfiguration $Configuration
                }

                Invoke-CheckedNativePhase -Name "coverage-$($_.BaseName)" -FileName "dotnet" -Arguments $testArguments -TimeoutSeconds 900
                Assert-CoverageReportProduced -TestProject $_ -MinimumWriteTimeUtc $coverageStartedUtc
            }
            finally {
                if ($null -eq $previousCoverageChildAssemblyDirectory) {
                    Remove-Item Env:\EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY -ErrorAction SilentlyContinue
                }
                else {
                    $env:EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $previousCoverageChildAssemblyDirectory
                }
            }
        }

        $coverageArguments = @("-NoProfile")
        if ($runningOnWindows) {
            $coverageArguments += @("-ExecutionPolicy", "Bypass")
        }

        $coverageArguments += @("-File", (Join-Path $PSScriptRoot "verify-coverage.ps1"), "-MinimumWriteTimeUtc", $coverageStartedUtc.ToString("O"))
        Invoke-CheckedNativePhase -Name "coverage-thresholds" -FileName $powerShellExecutable -Arguments $coverageArguments -TimeoutSeconds 300
    }
    else {
        $testProjects | ForEach-Object {
            $filter = if ($_.Name -eq "EmbodySense.E2ETests.csproj") { "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)" } else { "VerificationTier!=Stress" }
            $testArguments = @("test", $_.FullName, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $pullRequestRunSettingsPath, "--filter", $filter, "/p:RestoreIgnoreFailedSources=true")
            if ($_.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
                $testArguments += @("--logger", "console;verbosity=detailed")
            }

            Invoke-CheckedNativePhase -Name "tests-$($_.BaseName)" -FileName "dotnet" -Arguments $testArguments -TimeoutSeconds 900
        }
    }
}
finally {
    Pop-Location
}
