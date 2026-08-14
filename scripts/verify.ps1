param(
    [switch]$SkipCoverage,
    [switch]$SkipRestore,
    [switch]$RunBrowserE2E,
    [switch]$BrowserE2EOnly,
    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5))),
    [ValidateSet("PullRequest", "Stress")]
    [string]$VerificationTier = "PullRequest",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("Standard", "UnfilteredEvidence", "FilteredEvidence")]
    [string]$CoverageOwnershipMode = "Standard"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$verificationStopwatch = [Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"
$e2eProjectPath = Join-Path $testsPath "EmbodySense.E2ETests\EmbodySense.E2ETests.csproj"
$persistenceTestProjectPath = Join-Path $testsPath "EmbodySense.Core.Persistence.Tests\EmbodySense.Core.Persistence.Tests.csproj"
$pullRequestRunSettingsPath = Join-Path $testsPath "verification-pull-request.runsettings"
$coverageOwnershipManifestPath = Join-Path $testsPath "verification-coverage-ownership.json"
$stressRunSettingsPath = Join-Path $testsPath "verification-stress.runsettings"
$stressResultsPath = Join-Path $testsPath "EmbodySense.Core.Persistence.Tests\TestResults\VerificationStress"
$verificationResultsPath = Join-Path $testsPath "VerificationResults"
$verificationLogsPath = Join-Path $verificationResultsPath "Logs"
$canonicalInventoryRoot = Join-Path $verificationResultsPath "Inventory\Canonical"
$verificationInventoryPath = Join-Path $verificationResultsPath "required-execution-tests.json"
$verificationPartitionReportPath = Join-Path $verificationResultsPath "required-test-partition.json"
$verificationTestPreparationPlanPath = Join-Path $verificationResultsPath "required-test-preparation.json"
$verificationInventoryReportPath = Join-Path $verificationResultsPath "required-test-report.json"
$coverageIsolationRoot = Join-Path $verificationResultsPath "CoverageIsolation"
$standardTestResultsRoot = Join-Path $verificationResultsPath "StandardTests"
$coverageManifestPath = Join-Path $verificationResultsPath "coverage-manifest.json"
$coverageSummaryPath = Join-Path $verificationResultsPath "coverage-summary.json"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$maximumArtifactStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunArtifactMaximumShapeTests.Adversarial_maximum_transition_reservations_and_canonical_order_checks_remain_bounded"
$deletionCapacityStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTraceRetentionStoreTests.Rejected_operation_capacity_preserves_reserved_tombstone_deletions_and_remains_visible"
$testLaneTimeoutSeconds = 480

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-parallel.ps1")
. (Join-Path $PSScriptRoot "verification-artifacts.ps1")
. (Join-Path $PSScriptRoot "verification-coverage-evidence.ps1")
. (Join-Path $PSScriptRoot "verification-coverage-manifest.ps1")
. (Join-Path $PSScriptRoot "verification-temp.ps1")
. (Join-Path $PSScriptRoot "verification-test-lanes.ps1")
. (Join-Path $PSScriptRoot "verification-test-plan.ps1")
. (Join-Path $PSScriptRoot "verification-schedule.ps1")
$hardwareProcessorCount = [Math]::Max(1, [Environment]::ProcessorCount)
$hardwareBoundedResourceCapacity = [Math]::Min($MaximumTestWorkers, $hardwareProcessorCount)
$requiredGateResourceCapacity = Get-VerificationRequiredGateResourceCapacity
$requiredGateMaximumProcessHeavyWorkers = Get-VerificationRequiredGateMaximumProcessHeavyWorkers
$requiredGateMaximumCpuBoundWorkers = Get-VerificationRequiredGateMaximumCpuBoundWorkers
$requiredGateMaximumWorkers = Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers $MaximumTestWorkers -HardwareProcessorCount $hardwareProcessorCount
$effectiveRequiredGateMaximumProcessHeavyWorkers = [Math]::Min($requiredGateMaximumProcessHeavyWorkers, $requiredGateMaximumWorkers)
$effectiveRequiredGateMaximumCpuBoundWorkers = [Math]::Min($requiredGateMaximumCpuBoundWorkers, $requiredGateMaximumWorkers)
$verificationPhysicalTempRoot = Resolve-VerificationPhysicalTempRoot -RunnerTemp $env:RUNNER_TEMP -SystemTempPath ([IO.Path]::GetTempPath())
$verificationFixtureRunIdentity = [Guid]::NewGuid().ToString("N")
$verificationLaneFixtureRoots = [Collections.Generic.List[string]]::new()
Reset-VerificationPhaseState
Reset-VerificationParallelPhaseState

if ($BrowserE2EOnly -and -not $RunBrowserE2E) {
    throw "-BrowserE2EOnly requires -RunBrowserE2E."
}

if ($VerificationTier -eq "Stress" -and ($RunBrowserE2E -or $BrowserE2EOnly)) {
    throw "The Stress verification tier cannot be combined with browser E2E switches."
}

if ($CoverageOwnershipMode -cne "Standard") {
    if ($VerificationTier -cne "PullRequest" -or $SkipCoverage -or $RunBrowserE2E -or $BrowserE2EOnly -or $Configuration -cne "Release") {
        throw "Coverage ownership evidence collection requires the exact Release pull-request verifier with coverage and without browser-only modes."
    }
    $gitStatus = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $gitStatus.Count -ne 0) {
        throw "Coverage ownership evidence collection requires a clean committed worktree."
    }
}

function Invoke-CheckedNativePhase {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
    )

    Invoke-VerificationPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $repoRoot
}

function Add-ProfiledRequiredGatePhase {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)] [string]$OutputPath,
        [string]$CoverageSearchRoot,
        [string]$TrxPath,
        [hashtable]$Environment
    )

    $profile = Get-VerificationRequiredGateScheduleProfile -Name $Name
    Add-VerificationParallelPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $OutputPath -CoverageSearchRoot $CoverageSearchRoot -TrxPath $TrxPath -Environment $Environment -EstimatedDurationSeconds $profile.EstimatedDurationSeconds -Weight $profile.Weight -ResourceClass $profile.ResourceClass
}

function Add-TestExecutionPhase {
    param([object]$Isolation, [object]$Lane)

    $trxName = "$($Lane.Name).trx"
    $arguments = @(
        "vstest", $Lane.AssemblyPath,
        "--Settings:$(if ($SkipCoverage) { $stressRunSettingsPath } else { $Isolation.RunSettingsPath })",
        "--TestAdapterPath:$($Isolation.CollectorDirectory)",
        "--TestCaseFilter:$($Lane.Filter)",
        "--Logger:trx;LogFileName=$trxName",
        "--Logger:console;verbosity=minimal",
        "--ResultsDirectory:$($Lane.ResultsPath)"
    )
    if (-not $SkipCoverage) {
        $arguments += "--Collect:XPlat Code Coverage"
    }

    Add-ProfiledRequiredGatePhase -Name "tests-$($Lane.Name)" -FileName "dotnet" -Arguments $arguments -TimeoutSeconds $testLaneTimeoutSeconds -OutputPath (Join-Path $verificationLogsPath "$($Lane.Name).log") -CoverageSearchRoot $(if ($SkipCoverage) { $null } else { $Lane.ResultsPath }) -TrxPath (Join-Path $Lane.ResultsPath $trxName) -Environment $Lane.Environment
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
    if (Test-Path -LiteralPath $verificationResultsPath) {
        Remove-Item -LiteralPath $verificationResultsPath -Recurse -Force
    }
    Get-ChildItem -Path $testsPath -Directory | ForEach-Object {
        $testResultsPath = Join-Path $_.FullName "TestResults"
        if (Test-Path -LiteralPath $testResultsPath) {
            Remove-Item -LiteralPath $testResultsPath -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $verificationLogsPath -Force | Out-Null
    $cleanupStarted.Stop()
    $script:LastCompletedVerificationPhase = "clean-test-results"
    Write-Output "VERIFY_PHASE_COMPLETE name=clean-test-results elapsed_seconds=$([Math]::Round($cleanupStarted.Elapsed.TotalSeconds, 3)) completed_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"

    $normalPullRequestVerification = $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly
    $testProjects = @()
    if ($normalPullRequestVerification) {
        $testProjects = @(Get-VerificationCanonicalTestProjects -RepositoryRoot $repoRoot)
        $fixturePathComparer = if ($runningOnWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
        $reservedFixtureRoots = [Collections.Generic.HashSet[string]]::new($fixturePathComparer)
        foreach ($testProject in $testProjects) {
            foreach ($lane in @(Get-VerificationTestProjectLanes -TestProject $testProject)) {
                $laneIdentity = "$($testProject.BaseName)-$($lane.Name)"
                $laneFixtureRoot = Get-VerificationLaneFixturePath -PhysicalTempRoot $verificationPhysicalTempRoot -RunIdentity $verificationFixtureRunIdentity -LaneIdentity $laneIdentity
                if (-not $reservedFixtureRoots.Add($laneFixtureRoot) -or (Test-Path -LiteralPath $laneFixtureRoot)) {
                    throw "Verification lane temporary path collision for '$laneIdentity': $laneFixtureRoot"
                }
                New-Item -ItemType Directory -Path $laneFixtureRoot | Out-Null
                $verificationLaneFixtureRoots.Add($laneFixtureRoot)
            }
        }
    }

    $buildArguments = @("build")
    if ($SkipRestore) {
        $buildArguments += "--no-restore"
    }
    $buildArguments += if ($VerificationTier -eq "Stress") { $persistenceTestProjectPath } elseif ($BrowserE2EOnly) { $e2eProjectPath } else { "EmbodySense.sln" }
    $buildArguments += @("-c", $Configuration, "/p:RestoreIgnoreFailedSources=true")

    if ($normalPullRequestVerification) {
        $contractScripts = @(
            "verify-preflight-overlap.tests.ps1",
            "verify-coverage.tests.ps1",
            "verify-bounded-phases.tests.ps1",
            "verify-parallel.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1"
        )
        if ($runningOnWindows) {
            $contractScripts = @("verify-sdk-diagnostics.tests.ps1") + $contractScripts
        }
        $preflightNestedProcessContractScripts = @(
            "verify-preflight-overlap.tests.ps1",
            "verify-parallel.tests.ps1"
        )
        if ($runningOnWindows) {
            $preflightNestedProcessContractScripts = @("verify-sdk-diagnostics.tests.ps1") + $preflightNestedProcessContractScripts
        }
        $preflightOrdinaryContractScripts = @(
            "verify-bounded-phases.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1"
        )
        Assert-VerificationPreflightContractClassification -ContractScripts $contractScripts -CoverageContractScript "verify-coverage.tests.ps1" -NestedProcessContractScripts $preflightNestedProcessContractScripts -OrdinaryContractScripts $preflightOrdinaryContractScripts
        $preflightMaximumWorkers = [Math]::Min(4, $hardwareBoundedResourceCapacity)
        $preflightResourceCapacity = [Math]::Min(8, [Math]::Max(1, $preflightMaximumWorkers * 2))
        $preflightProcessHeavyWeight = [Math]::Min(3, [Math]::Max(1, [int][Math]::Ceiling($preflightResourceCapacity / 2.0)))
        $preflightMaximumProcessHeavyWorkers = [Math]::Min(2, $preflightMaximumWorkers)
        $preflightCoverageContractWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $preflightResourceCapacity
        $preflightFrontendWeight = Get-VerificationPreflightFrontendWeight -ResourceCapacity $preflightResourceCapacity
        $preflightNestedProcessContractWeight = Get-VerificationPreflightNestedProcessContractWeight -ResourceCapacity $preflightResourceCapacity
        $preflightTestPlanWeight = Get-VerificationPreflightTestPlanWeight -ResourceCapacity $preflightResourceCapacity

        Add-VerificationParallelPhase -Name "build-pullrequest" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "build-pullrequest.log") -EstimatedDurationSeconds 90 -Weight $preflightProcessHeavyWeight -ResourceClass "ProcessHeavy"
        $testPlanArguments = @("-NoProfile")
        if ($runningOnWindows) {
            $testPlanArguments += @("-ExecutionPolicy", "Bypass")
        }
        $testPlanArguments += @("-File", (Join-Path $PSScriptRoot "prepare-verification-test-plan.ps1"), "-RepositoryRoot", $repoRoot, "-VerificationResultsPath", $verificationResultsPath, "-VerificationPhysicalTempRoot", $verificationPhysicalTempRoot, "-FixtureRunIdentity", $verificationFixtureRunIdentity, "-Configuration", $Configuration, "-CoverageOwnershipMode", $CoverageOwnershipMode)
        if ($SkipCoverage) { $testPlanArguments += "-SkipCoverage" }
        Add-VerificationParallelPhase -Name "prepare-test-plan" -FileName $powerShellExecutable -Arguments $testPlanArguments -TimeoutSeconds 240 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "prepare-test-plan.log") -DependsOn @("build-pullrequest") -EstimatedDurationSeconds 60 -Weight $preflightTestPlanWeight -ResourceClass "ProcessHeavy"
        $frontendArguments = @("-NoProfile")
        if ($runningOnWindows) {
            $frontendArguments += @("-ExecutionPolicy", "Bypass")
        }
        $frontendArguments += @("-File", (Join-Path $PSScriptRoot "verify-frontend.ps1"), "-RepositoryRoot", $repoRoot, "-LogsPath", $verificationLogsPath)
        Add-VerificationParallelPhase -Name "frontend-preflight" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 590 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-preflight.log") -EstimatedDurationSeconds 70 -Weight $preflightFrontendWeight -ResourceClass "CpuBound"
        foreach ($contractScript in $contractScripts) {
            $contractArguments = @("-NoProfile")
            if ($runningOnWindows) {
                $contractArguments += @("-ExecutionPolicy", "Bypass")
            }
            $contractArguments += @("-File", (Join-Path $testsPath "scripts\$contractScript"))
            if ($preflightOrdinaryContractScripts -ccontains $contractScript) {
                Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 90 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"
            }
            elseif ($contractScript -ceq "verify-coverage.tests.ps1") {
                Add-VerificationParallelPhase -Name "contract-verify-coverage.tests" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "verify-coverage.tests.ps1.log") -DependsOn @("build-pullrequest") -EstimatedDurationSeconds 75 -Weight $preflightCoverageContractWeight -ResourceClass "ProcessHeavy"
            }
            elseif ($preflightNestedProcessContractScripts -ccontains $contractScript) {
                Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 60 -Weight $preflightNestedProcessContractWeight -ResourceClass "ProcessHeavy"
            }
            else {
                throw "Preflight script contract '$contractScript' reached execution without a resource classification."
            }
        }
        Write-Output "VERIFY_PARALLEL_PLAN kind=pull-request-preflight-dag phases=$($script:VerificationParallelPhases.Count) requested_workers=$MaximumTestWorkers maximum_workers=$preflightMaximumWorkers maximum_resource_capacity=$preflightResourceCapacity maximum_process_heavy=$preflightMaximumProcessHeavyWorkers maximum_cpu_bound=1 build_weight=$preflightProcessHeavyWeight coverage_contract_weight=$preflightCoverageContractWeight frontend_weight=$preflightFrontendWeight nested_process_contract_weight=$preflightNestedProcessContractWeight test_plan_weight=$preflightTestPlanWeight nested_process_contracts=$($preflightNestedProcessContractScripts.Count) ordinary_contracts=$($preflightOrdinaryContractScripts.Count) coverage_dependency=build-pullrequest test_plan_dependency=build-pullrequest configuration=$Configuration"
        Invoke-VerificationParallelPhases -MaximumWorkers $preflightMaximumWorkers -MaximumResourceCapacity $preflightResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers 1 | Out-Null
        Reset-VerificationParallelPhaseState
        $script:LastCompletedVerificationPhase = "pull-request-preflight"
    }
    else {
        Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900
    }

    if ($VerificationTier -eq "Stress") {
        Write-Output "VERIFY_STRESS_CONTRACT exact_test_count=2 session_timeout_seconds=1500 max_artifact_process_timeout_seconds=1800 deletion_capacity_process_timeout_seconds=1200"
        $maximumResultsPath = Join-Path $stressResultsPath "MaximumArtifact"
        Invoke-CheckedNativePhase -Name "stress-maximum-artifact" -FileName "dotnet" -Arguments @("test", $persistenceTestProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $stressRunSettingsPath, "--filter", "FullyQualifiedName=$maximumArtifactStressTest&VerificationTier=Stress", "--logger", "trx;LogFileName=maximum-artifact-stress.trx", "--results-directory", $maximumResultsPath, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 1800
        $deletionResultsPath = Join-Path $stressResultsPath "DeletionCapacity"
        Invoke-CheckedNativePhase -Name "stress-deletion-operation-capacity" -FileName "dotnet" -Arguments @("test", $persistenceTestProjectPath, "-c", $Configuration, "--no-build", "--no-restore", "--settings", $stressRunSettingsPath, "--filter", "FullyQualifiedName=$deletionCapacityStressTest&VerificationTier=Stress", "--logger", "trx;LogFileName=deletion-capacity-stress.trx", "--results-directory", $deletionResultsPath, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 1200
        return
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
            if ($null -eq $oldRunBrowserE2E) { Remove-Item Env:\EMBODYSENSE_RUN_BROWSER_E2E -ErrorAction SilentlyContinue } else { $env:EMBODYSENSE_RUN_BROWSER_E2E = $oldRunBrowserE2E }
            if ($null -eq $oldBrowserE2EArtifacts) { Remove-Item Env:\EMBODYSENSE_BROWSER_E2E_ARTIFACTS -ErrorAction SilentlyContinue } else { $env:EMBODYSENSE_BROWSER_E2E_ARTIFACTS = $oldBrowserE2EArtifacts }
        }
    }

    if ($BrowserE2EOnly) {
        return
    }

    Write-Output "VERIFY_REQUIRED_TEST_CONTRACT identity=TestCase.Id partition_identity=XunitTestCaseUniqueID filter=VerificationTier!=Stress"
    $coverageOwnership = Read-VerificationCoverageOwnership -ManifestPath $coverageOwnershipManifestPath -RepositoryRoot $repoRoot -TestProjects $testProjects
    Write-Output "VERIFY_COVERAGE_OWNERSHIP schema_version=1 ownership_sha256=$($coverageOwnership.OwnershipSha256) collector_version=$($coverageOwnership.CollectorVersion) source_files=$($coverageOwnership.ProductionFiles.Count) test_projects=$($testProjects.Count)"
    if ($CoverageOwnershipMode -cne "Standard") {
        $headSha = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $headSha -cnotmatch '^[0-9a-f]{40}$') {
            throw "Coverage ownership evidence collection could not bind one exact Git head."
        }
        $evidenceContextPath = Join-Path $verificationResultsPath "coverage-ownership-evidence-context.json"
        $evidenceContext = [ordered]@{
            schemaVersion = 1
            mode = $CoverageOwnershipMode
            headSha = $headSha
            platform = if ($runningOnWindows) { "windows" } else { "nonWindows" }
            collectorVersion = $coverageOwnership.CollectorVersion
            ownershipSha256 = $coverageOwnership.OwnershipSha256
            runSettingsSha256 = $coverageOwnership.RunSettingsSha256
        }
        [IO.File]::WriteAllText($evidenceContextPath, ($evidenceContext | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
        Write-Output "VERIFY_COVERAGE_OWNERSHIP_EVIDENCE_CONTEXT mode=$CoverageOwnershipMode head_sha=$headSha platform=$($evidenceContext.platform) path=$evidenceContextPath"
    }
    $isolations = @(Read-VerificationTestPreparationPlan -PlanPath $verificationTestPreparationPlanPath -RepositoryRoot $repoRoot -VerificationResultsPath $verificationResultsPath -CoverageIsolationRoot $coverageIsolationRoot -StandardTestResultsRoot $standardTestResultsRoot -VerificationPhysicalTempRoot $verificationPhysicalTempRoot -FixtureRunIdentity $verificationFixtureRunIdentity -Configuration $Configuration -SkipCoverage ([bool]$SkipCoverage) -CoverageOwnershipMode $CoverageOwnershipMode -CoverageOwnership $coverageOwnership -TestProjects $testProjects)
    foreach ($isolation in $isolations) {
        $testProject = $isolation.Project
        Write-Output "VERIFY_COVERAGE_SELECTION project=$($testProject.BaseName) selected_files=$($isolation.CoverageSelection.SelectedFiles.Count) excluded_files=$($isolation.CoverageSelection.ExcludedFiles.Count) primary_roots=$($isolation.CoverageSelection.PrimaryRoots.Count)"
    }

    $coverageStartedUtc = [DateTime]::UtcNow
    Add-ProfiledRequiredGatePhase -Name "git-diff-check" -FileName "git" -Arguments @("diff", "--check") -TimeoutSeconds 60 -OutputPath (Join-Path $verificationLogsPath "git-diff-check.log")
    # Omitting a formatter subcommand makes one workspace load run whitespace plus the
    # explicitly selected IDE1006 style analyzer; --verify-no-changes keeps both fail-closed.
    Add-ProfiledRequiredGatePhase -Name "format-csharp" -FileName "dotnet" -Arguments @("format", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal") -TimeoutSeconds 240 -OutputPath (Join-Path $verificationLogsPath "format-csharp.log")
    foreach ($isolation in $isolations) {
        foreach ($lane in $isolation.Lanes) {
            Add-TestExecutionPhase -Isolation $isolation -Lane $lane
        }
    }

    Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases)
    Write-Output "VERIFY_PARALLEL_PLAN kind=required-gates phases=$($script:VerificationParallelPhases.Count) maximum_workers=$requiredGateMaximumWorkers maximum_resource_capacity=$requiredGateResourceCapacity maximum_process_heavy=$effectiveRequiredGateMaximumProcessHeavyWorkers maximum_cpu_bound=$effectiveRequiredGateMaximumCpuBoundWorkers scheduling=singleton-class-backlog-priority-lpt coverage=$(-not $SkipCoverage)"
    $gateResults = @(Invoke-VerificationParallelPhases -MaximumWorkers $requiredGateMaximumWorkers -MaximumResourceCapacity $requiredGateResourceCapacity -MaximumProcessHeavyWorkers $effectiveRequiredGateMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers $effectiveRequiredGateMaximumCpuBoundWorkers)
    $testResults = @($gateResults | Where-Object { $_.Name.StartsWith("tests-", [StringComparison]::Ordinal) })
    Reset-VerificationParallelPhaseState

    foreach ($isolation in $isolations) {
        Assert-VerificationDirectoryManifest -Expected $isolation.SourceManifest -Directory $isolation.SourceDirectory -Description "$($isolation.Project.BaseName) build output"
        Assert-VerificationDirectoryManifest -Expected $isolation.PristineManifest -Directory $isolation.PristineDirectory -Description "$($isolation.Project.BaseName) pristine output"
        foreach ($lane in $isolation.Lanes) {
            Assert-VerificationDirectoryManifest -Expected $lane.Manifest -Directory $lane.Directory -Description "$($lane.Name) execution output"
        }
    }
    Write-Output "VERIFY_ARTIFACT_ISOLATION_COMPLETE projects=$($isolations.Count) lanes=$($testResults.Count)"

    if ($CoverageOwnershipMode -cne "Standard") {
        $binaryManifestProjects = [Collections.Generic.List[object]]::new()
        foreach ($isolation in @($isolations | Sort-Object { $_.Project.BaseName })) {
            $canonicalBinaries = @($isolation.PristineManifest | Where-Object {
                [IO.Path]::GetExtension([string]$_.RelativePath) -cin @(".dll", ".pdb")
            } | Sort-Object RelativePath | ForEach-Object {
                [ordered]@{ path = [string]$_.RelativePath; length = [long]$_.Length; sha256 = [string]$_.Sha256 }
            })
            if ($canonicalBinaries.Count -eq 0) {
                throw "Coverage ownership evidence has no canonical DLL/PDB inventory for '$($isolation.Project.BaseName)'."
            }
            $invocations = [Collections.Generic.List[object]]::new()
            if (Test-Path -LiteralPath $isolation.ChildInvocationsRoot -PathType Container) {
                foreach ($invocationDirectory in @(Get-ChildItem -LiteralPath $isolation.ChildInvocationsRoot -Directory -Force | Sort-Object FullName)) {
                    $invocationId = [Guid]::Empty
                    if (-not [Guid]::TryParseExact($invocationDirectory.Name, "N", [ref]$invocationId)) {
                        throw "Coverage ownership evidence child invocation has an unsafe identity: $($invocationDirectory.FullName)"
                    }
                    $binaryEntries = @(Get-VerificationDirectoryManifest -Directory $invocationDirectory.FullName | Where-Object {
                        [IO.Path]::GetExtension([string]$_.RelativePath) -cin @(".dll", ".pdb")
                    } | Sort-Object RelativePath | ForEach-Object {
                        [ordered]@{ path = [string]$_.RelativePath; length = [long]$_.Length; sha256 = [string]$_.Sha256 }
                    })
                    if ($binaryEntries.Count -eq 0) {
                        throw "Coverage ownership evidence child invocation is missing its DLL/PDB inventory: $($invocationDirectory.FullName)"
                    }
                    $invocationRecords = @($binaryEntries | ForEach-Object { "$($_.path)" + [char]0 + "$($_.length)" + [char]0 + "$($_.sha256)" })
                    $invocations.Add([ordered]@{
                        relativeRoot = [IO.Path]::GetRelativePath($verificationResultsPath, $invocationDirectory.FullName).Replace('\', '/')
                        binarySha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records $invocationRecords
                        binaries = $binaryEntries
                    })
                }
            }
            $parentSettingsHash = (Get-FileHash -LiteralPath $isolation.RunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $childSettingsHash = (Get-FileHash -LiteralPath $isolation.ChildRunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($parentSettingsHash -cne $childSettingsHash) {
                throw "Coverage ownership evidence child settings do not byte-match parent settings for '$($isolation.Project.BaseName)'."
            }
            $binaryManifestProjects.Add([ordered]@{
                project = $isolation.Project.BaseName
                canonicalRoot = [IO.Path]::GetRelativePath($verificationResultsPath, $isolation.PristineDirectory).Replace('\', '/')
                canonicalBinaries = $canonicalBinaries
                parentSettingsPath = [IO.Path]::GetRelativePath($verificationResultsPath, $isolation.RunSettingsPath).Replace('\', '/')
                childSettingsPath = [IO.Path]::GetRelativePath($verificationResultsPath, $isolation.ChildRunSettingsPath).Replace('\', '/')
                settingsSha256 = $parentSettingsHash
                childInvocations = @($invocations)
            })
        }
        $binaryManifestPath = Join-Path $verificationResultsPath "coverage-ownership-binary-manifest.json"
        $binaryManifest = [ordered]@{
            schemaVersion = 1
            mode = $CoverageOwnershipMode
            headSha = $headSha
            projects = @($binaryManifestProjects)
        }
        [IO.File]::WriteAllText($binaryManifestPath, ($binaryManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Write-Output "VERIFY_COVERAGE_OWNERSHIP_BINARY_MANIFEST mode=$CoverageOwnershipMode projects=$($binaryManifestProjects.Count) path=$binaryManifestPath"
    }

    $inventoryArguments = @("-NoProfile")
    if ($runningOnWindows) { $inventoryArguments += @("-ExecutionPolicy", "Bypass") }
    $inventoryArguments += @("-File", (Join-Path $PSScriptRoot "verify-test-inventory.ps1"), "-ExpectedInventoryPath", $verificationInventoryPath, "-ResultsRoot", $standardTestResultsRoot, "-ReportPath", $verificationInventoryReportPath)
    Add-VerificationParallelPhase -Name "test-inventory-reconciliation" -FileName $powerShellExecutable -Arguments $inventoryArguments -TimeoutSeconds 180 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "test-inventory-reconciliation.log")

    if (-not $SkipCoverage) {
        Write-CoverageManifest -TestResults $testResults -Isolations @($isolations) -MinimumWriteTimeUtc $coverageStartedUtc -VerificationResultsPath $verificationResultsPath -ManifestPath $coverageManifestPath
        $coverageArguments = @("-NoProfile")
        if ($runningOnWindows) { $coverageArguments += @("-ExecutionPolicy", "Bypass") }
        $coverageArguments += @("-File", (Join-Path $PSScriptRoot "verify-coverage.ps1"), "-MinimumWriteTimeUtc", $coverageStartedUtc.ToString("O"), "-ResultsRoot", $verificationResultsPath, "-ManifestPath", $coverageManifestPath, "-ReportPath", $coverageSummaryPath, "-CoverageOwnershipMode", $CoverageOwnershipMode)
        Add-VerificationParallelPhase -Name "coverage-thresholds" -FileName $powerShellExecutable -Arguments $coverageArguments -TimeoutSeconds 180 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "coverage-thresholds.log")
    }
    Write-Output "VERIFY_PARALLEL_PLAN kind=reconciliation phases=$($script:VerificationParallelPhases.Count) maximum_resource_capacity=$([Math]::Min(2, $hardwareBoundedResourceCapacity))"
    Invoke-VerificationParallelPhases -MaximumWorkers ([Math]::Min(2, $MaximumTestWorkers)) -MaximumResourceCapacity ([Math]::Min(2, $hardwareBoundedResourceCapacity)) | Out-Null
}
finally {
    Pop-Location
    foreach ($laneFixtureRoot in $verificationLaneFixtureRoots) {
        if (Test-Path -LiteralPath $laneFixtureRoot) {
            Remove-Item -LiteralPath $laneFixtureRoot -Recurse -Force
        }
    }
}

$verificationStopwatch.Stop()
$elapsedText = $verificationStopwatch.Elapsed.TotalSeconds.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
if ($CoverageOwnershipMode -cne "Standard") {
    Write-Output "VERIFY_COVERAGE_OWNERSHIP_EVIDENCE_COMPLETE mode=$CoverageOwnershipMode status=collection-only elapsed_seconds=$elapsedText"
}
Write-Output "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=$elapsedText"
