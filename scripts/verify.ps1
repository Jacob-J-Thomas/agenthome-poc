param(
    [switch]$SkipCoverage,
    [switch]$SkipRestore,
    [switch]$RunBrowserE2E,
    [switch]$BrowserE2EOnly,
    [ValidateSet("Full", "Solution", "StaticContracts")]
    [string]$VerificationComponent = "Full",
    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5))),
    [ValidateSet("PullRequest", "Stress")]
    [string]$VerificationTier = "PullRequest",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$verificationStopwatch = [Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"
$e2eProjectPath = Join-Path $testsPath "EmbodySense.E2ETests\EmbodySense.E2ETests.csproj"
$persistenceTestProjectPath = Join-Path $testsPath "EmbodySense.Core.Persistence.Tests\EmbodySense.Core.Persistence.Tests.csproj"
$pullRequestRunSettingsPath = Join-Path $testsPath "verification-pull-request.runsettings"
$stressRunSettingsPath = Join-Path $testsPath "verification-stress.runsettings"
$stressResultsPath = Join-Path $testsPath "EmbodySense.Core.Persistence.Tests\TestResults\VerificationStress"
$verificationResultsPath = Join-Path $testsPath "VerificationResults"
$verificationLogsPath = Join-Path $verificationResultsPath "Logs"
$canonicalInventoryRoot = Join-Path $verificationResultsPath "Inventory\Canonical"
$verificationInventoryPath = Join-Path $verificationResultsPath "required-execution-tests.json"
$verificationPartitionReportPath = Join-Path $verificationResultsPath "required-test-partition.json"
$verificationInventoryReportPath = Join-Path $verificationResultsPath "required-test-report.json"
$coverageIsolationRoot = Join-Path $verificationResultsPath "CoverageIsolation"
$standardTestResultsRoot = Join-Path $verificationResultsPath "StandardTests"
$coverageManifestPath = Join-Path $verificationResultsPath "coverage-manifest.json"
$coverageSummaryPath = Join-Path $verificationResultsPath "coverage-summary.json"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$maximumArtifactStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunArtifactMaximumShapeTests.Adversarial_maximum_transition_reservations_and_canonical_order_checks_remain_bounded"
$deletionCapacityStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTraceRetentionStoreTests.Rejected_operation_capacity_preserves_reserved_tombstone_deletions_and_remains_visible"
# https://github.com/Jacob-J-Thomas/agenthome-poc/issues/610 owns the measured per-lane budgets. A required gate cannot select an unprofiled
# timeout, so every child remains bounded and observable without retrying or dropping work.
$coverageChildProcessTestProjects = @(
    "EmbodySense.Core.Persistence.Tests.csproj",
    "EmbodySense.Core.Startup.Tests.csproj"
)

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-parallel.ps1")
. (Join-Path $PSScriptRoot "verification-artifacts.ps1")
. (Join-Path $PSScriptRoot "verification-coverage-evidence.ps1")
. (Join-Path $PSScriptRoot "verification-coverage-manifest.ps1")
. (Join-Path $PSScriptRoot "verification-temp.ps1")
. (Join-Path $PSScriptRoot "verification-test-lanes.ps1")
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

if ($VerificationComponent -ne "Full" -and ($VerificationTier -ne "PullRequest" -or $RunBrowserE2E -or $BrowserE2EOnly)) {
    throw "A non-Full verification component is valid only for the complete PullRequest verification tier."
}

if ($VerificationComponent -eq "StaticContracts" -and -not $runningOnWindows) {
    throw "The StaticContracts verification component is reserved for the hosted Windows verifier."
}

if ($VerificationTier -eq "Stress" -and ($RunBrowserE2E -or $BrowserE2EOnly)) {
    throw "The Stress verification tier cannot be combined with browser E2E switches."
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

function Get-TestProjectFilter {
    param([System.IO.FileInfo]$TestProject)

    if ($TestProject.Name -eq "EmbodySense.E2ETests.csproj") {
        return "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)"
    }

    return "VerificationTier!=Stress"
}

function Get-ProjectCoverageIsolation {
    param(
        [System.IO.FileInfo]$TestProject,
        [object[]]$Lanes
    )

    [xml]$project = Get-Content -LiteralPath $TestProject.FullName -Raw
    $targetFrameworks = @($project.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($targetFrameworks.Count -ne 1) {
        throw "Coverage isolation requires one explicit target framework in $($TestProject.Name)."
    }

    $targetFramework = [string]$targetFrameworks[0]
    $sourceDirectory = Join-Path (Join-Path (Join-Path $TestProject.DirectoryName "bin") $Configuration) $targetFramework
    $testAssemblyName = "$($TestProject.BaseName).dll"
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $testAssemblyName) -PathType Leaf)) {
        throw "Coverage isolation source assembly is missing: $(Join-Path $sourceDirectory $testAssemblyName)"
    }

    $assetsPath = Join-Path $TestProject.DirectoryName "obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Coverage isolation assets are missing: $assetsPath"
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $coverletPackage = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith("coverlet.collector/", [StringComparison]::OrdinalIgnoreCase) })
    if ($coverletPackage.Count -ne 1) {
        throw "Coverage isolation requires one resolved coverlet.collector package for $($TestProject.Name)."
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
        throw "Coverage isolation could not locate coverlet.collector binaries for $($TestProject.Name)."
    }

    $projectRoot = Join-Path $coverageIsolationRoot $TestProject.BaseName
    $pristineDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot "canonical") -Configuration $Configuration -TargetFramework $targetFramework
    $collectorDirectory = Join-Path $projectRoot "Collector"
    $runSettingsPath = Join-Path $projectRoot "verification-pull-request.runsettings"
    $childCoverageRoot = Split-Path -Parent $pristineDirectory
    $childCollectorDirectory = Join-Path $childCoverageRoot "Collector"
    $childRunSettingsPath = Join-Path $childCoverageRoot "verification-pull-request.runsettings"
    $childResultsPath = Join-Path $childCoverageRoot "Results"
    $sourceManifest = @(Get-VerificationDirectoryManifest -Directory $sourceDirectory)
    $pristineManifest = @(Copy-VerifiedDirectory -SourceDirectory $sourceDirectory -DestinationDirectory $pristineDirectory -Description "$($TestProject.BaseName) pristine copy")
    [void](Copy-VerifiedDirectory -SourceDirectory $collectorSource -DestinationDirectory $collectorDirectory -Description "$($TestProject.BaseName) collector copy")
    [void](Copy-VerifiedDirectory -SourceDirectory $collectorSource -DestinationDirectory $childCollectorDirectory -Description "$($TestProject.BaseName) child collector copy")
    Copy-Item -LiteralPath $pullRequestRunSettingsPath -Destination $runSettingsPath
    Copy-Item -LiteralPath $pullRequestRunSettingsPath -Destination $childRunSettingsPath

    $laneCopies = [Collections.Generic.List[object]]::new()
    foreach ($lane in $Lanes) {
        $laneDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot $lane.Name) -Configuration $Configuration -TargetFramework $targetFramework
        $laneManifest = @(Copy-VerifiedDirectoryFromManifest -SourceDirectory $pristineDirectory -SourceManifest $pristineManifest -DestinationDirectory $laneDirectory -Description "$($TestProject.BaseName)/$($lane.Name) lane copy")
        $laneIdentity = "$($TestProject.BaseName)-$($lane.Name)"
        $laneFixtureRoot = Get-VerificationLaneFixturePath -PhysicalTempRoot $verificationPhysicalTempRoot -RunIdentity $verificationFixtureRunIdentity -LaneIdentity $laneIdentity
        if (Test-Path -LiteralPath $laneFixtureRoot) {
            throw "Verification lane temporary path collision for '$laneIdentity': $laneFixtureRoot"
        }
        New-Item -ItemType Directory -Path $laneFixtureRoot | Out-Null
        $verificationLaneFixtureRoots.Add($laneFixtureRoot)
        $laneEnvironment = @{
            EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"
            TEMP = $laneFixtureRoot
            TMP = $laneFixtureRoot
            TMPDIR = $laneFixtureRoot
        }
        if (-not $SkipCoverage -and $coverageChildProcessTestProjects.Contains($TestProject.Name)) {
            $laneEnvironment.EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory
        }
        $laneCopies.Add([pscustomobject]@{
            Name = "$($TestProject.BaseName)-$($lane.Name)"
            ProjectName = $TestProject.BaseName
            ShardName = $lane.Name
            Filter = if ($TestProject.Name -eq "EmbodySense.E2ETests.csproj") { Get-VerificationTestLaneFilter -Lane $lane -AdditionalExclusions @("BrowserFlowTests") } else { Get-VerificationTestLaneFilter -Lane $lane }
            AssemblyPath = Join-Path $laneDirectory $testAssemblyName
            Directory = $laneDirectory
            Manifest = $laneManifest
            ResultsPath = Join-Path $standardTestResultsRoot "$($TestProject.BaseName)-$($lane.Name)"
            Environment = $laneEnvironment
        })
    }

    return [pscustomobject]@{
        Project = $TestProject
        SourceDirectory = $sourceDirectory
        SourceManifest = $sourceManifest
        PristineDirectory = $pristineDirectory
        PristineManifest = $pristineManifest
        CollectorDirectory = $collectorDirectory
        RunSettingsPath = $runSettingsPath
        ChildResultsPath = $childResultsPath
        CanonicalAssemblyPath = Join-Path $pristineDirectory $testAssemblyName
        Lanes = @($laneCopies)
    }
}

function Add-TestDiscoveryPhase {
    param([string]$Name, [string]$AssemblyPath, [string]$Filter, [string]$OutputPath)

    $diagnosticPath = [IO.Path]::ChangeExtension($OutputPath, ".diag.log")
    $arguments = @("-NoProfile")
    if ($runningOnWindows) {
        $arguments += @("-ExecutionPolicy", "Bypass")
    }
    $arguments += @("-File", (Join-Path $PSScriptRoot "write-test-inventory.ps1"), "-TestAssemblyPath", $AssemblyPath, "-Filter", $Filter, "-OutputPath", $OutputPath, "-DiagnosticPath", $diagnosticPath, "-WorkingDirectory", $repoRoot)
    Add-VerificationParallelPhase -Name "discover-$Name" -FileName $powerShellExecutable -Arguments $arguments -TimeoutSeconds 180 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "discover-$Name.log")
}

function Add-ProfiledRequiredGatePhase {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [string]$OutputPath,
        [string]$CoverageSearchRoot,
        [string]$TrxPath,
        [hashtable]$Environment
    )

    $profile = Get-VerificationRequiredGateScheduleProfile -Name $Name
    Add-VerificationParallelPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds $profile.TimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $OutputPath -CoverageSearchRoot $CoverageSearchRoot -TrxPath $TrxPath -Environment $Environment -EstimatedDurationSeconds $profile.EstimatedDurationSeconds -Weight $profile.Weight -ResourceClass $profile.ResourceClass
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

    Add-ProfiledRequiredGatePhase -Name "tests-$($Lane.Name)" -FileName "dotnet" -Arguments $arguments -OutputPath (Join-Path $verificationLogsPath "$($Lane.Name).log") -CoverageSearchRoot $(if ($SkipCoverage) { $null } else { $Lane.ResultsPath }) -TrxPath (Join-Path $Lane.ResultsPath $trxName) -Environment $Lane.Environment
}

function Invoke-StaticVerificationContracts {
    $contractScripts = @(
        "verify-sdk-diagnostics.tests.ps1",
        "verify-preflight-overlap.tests.ps1",
        "verify-coverage.tests.ps1",
        "verify-bounded-phases.tests.ps1",
        "verify-parallel.tests.ps1",
        "verify-test-inventory.tests.ps1",
        "verify-watchdog.tests.ps1",
        "verify-promotion-fan-in.tests.ps1"
    )

    Write-Output "VERIFY_STATIC_CONTRACT_PLAN contracts=$($contractScripts -join ',') execution=serial timeout_seconds=120"
    foreach ($contractScript in $contractScripts) {
        $contractArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $testsPath "scripts\$contractScript"))
        Invoke-VerificationPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log")
    }

    $frontendArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "verify-frontend.ps1"), "-RepositoryRoot", $repoRoot, "-LogsPath", $verificationLogsPath)
    Invoke-VerificationPhase -Name "frontend-preflight" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 590 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-preflight.log")

    Invoke-VerificationPhase -Name "restore-static" -FileName "dotnet" -Arguments @("restore", "EmbodySense.sln", "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "restore-static.log")
    Invoke-VerificationPhase -Name "format-whitespace" -FileName "dotnet" -Arguments @("format", "whitespace", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--verbosity", "minimal") -TimeoutSeconds 240 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "format-whitespace.log")
    Invoke-VerificationPhase -Name "format-naming-style" -FileName "dotnet" -Arguments @("format", "style", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal") -TimeoutSeconds 240 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "format-naming-style.log")
    Invoke-VerificationPhase -Name "git-diff-check" -FileName "git" -Arguments @("diff", "--check") -TimeoutSeconds 60 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "git-diff-check.log")
    $script:LastCompletedVerificationPhase = "static-contracts"
}

function Write-VerificationComponentEvidence {
    param(
        [Parameter(Mandatory = $true)] [ValidateSet("solution", "static-contracts")] [string]$Component,
        [Parameter(Mandatory = $true)] [string[]]$ManifestPaths,
        [int]$LaneCount = 0,
        [bool]$InventoryComplete = $false,
        [bool]$CoverageComplete = $false,
        [int]$StaticContractCount = 0,
        [bool]$FrontendComplete = $false,
        [bool]$FormatComplete = $false,
        [bool]$DiffComplete = $false
    )

    $evidencePath = Join-Path $verificationResultsPath "verification-component-evidence.json"
    $manifestPath = Join-Path $verificationResultsPath "verification-component-manifest.json"
    $rootFullPath = [IO.Path]::GetFullPath($verificationResultsPath)
    $manifestEntries = [Collections.Generic.List[object]]::new()
    foreach ($candidatePath in $ManifestPaths) {
        $fullPath = [IO.Path]::GetFullPath($candidatePath)
        if (-not $fullPath.StartsWith($rootFullPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Component evidence path is missing or outside VerificationResults: $fullPath"
        }
        $file = Get-Item -LiteralPath $fullPath
        $relativePath = [IO.Path]::GetRelativePath($rootFullPath, $fullPath).Replace([IO.Path]::DirectorySeparatorChar, "/")
        if (@($manifestEntries | Where-Object { $_.path -ceq $relativePath }).Count -ne 0) {
            throw "Component evidence manifest contains a duplicate path: $relativePath"
        }
        $manifestEntries.Add([ordered]@{ path = $relativePath; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant() })
    }

    $evidence = [ordered]@{
        schemaVersion = 1
        component = $Component
        repositoryHead = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
        githubRunId = $env:GITHUB_RUN_ID
        githubRunAttempt = $env:GITHUB_RUN_ATTEMPT
        laneCount = $LaneCount
        inventoryComplete = $InventoryComplete
        coverageComplete = $CoverageComplete
        staticContractCount = $StaticContractCount
        frontendComplete = $FrontendComplete
        formatComplete = $FormatComplete
        diffComplete = $DiffComplete
        manifestSha256 = ""
    }
    [IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $manifest = [ordered]@{ schemaVersion = 1; files = @($manifestEntries | Sort-Object path) }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $evidence.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
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

    $buildArguments = @("build")
    if ($SkipRestore) {
        $buildArguments += "--no-restore"
    }
    $buildArguments += if ($VerificationTier -eq "Stress") { $persistenceTestProjectPath } elseif ($BrowserE2EOnly) { $e2eProjectPath } else { "EmbodySense.sln" }
    $buildArguments += @("-c", $Configuration, "/p:RestoreIgnoreFailedSources=true")

    $normalPullRequestVerification = $VerificationComponent -eq "Full" -and $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly
    if ($normalPullRequestVerification) {
        $contractScripts = @(
            "verify-preflight-overlap.tests.ps1",
            "verify-coverage.tests.ps1",
            "verify-bounded-phases.tests.ps1",
            "verify-parallel.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1",
            "verify-promotion-fan-in.tests.ps1"
        )
        if ($runningOnWindows) {
            $contractScripts = @("verify-sdk-diagnostics.tests.ps1") + $contractScripts
        }
        $preflightNestedProcessContractScripts = @(Get-VerificationPreflightNestedProcessContractScripts -RunningOnWindows:$runningOnWindows)
        $preflightBuildOverlapContractScripts = @(
            "verify-bounded-phases.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1",
            "verify-promotion-fan-in.tests.ps1"
        )
        Assert-VerificationPreflightContractClassification -ContractScripts $contractScripts -CoverageContractScript "verify-coverage.tests.ps1" -NestedProcessContractScripts $preflightNestedProcessContractScripts -OrdinaryContractScripts $preflightBuildOverlapContractScripts
        $preflightMaximumWorkers = [Math]::Min(4, $hardwareBoundedResourceCapacity)
        $preflightResourceCapacity = $preflightMaximumWorkers
        $preflightProcessHeavyWeight = [Math]::Max(1, [int][Math]::Ceiling($preflightResourceCapacity / 2.0))
        $preflightMaximumProcessHeavyWorkers = 1
        $preflightCoverageContractWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $preflightResourceCapacity
        $preflightFrontendWeight = Get-VerificationPreflightFrontendWeight -ResourceCapacity $preflightResourceCapacity
        $preflightNestedProcessContractWeight = Get-VerificationPreflightNestedProcessContractWeight -ResourceCapacity $preflightResourceCapacity

        Add-VerificationParallelPhase -Name "build-pullrequest" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "build-pullrequest.log") -EstimatedDurationSeconds 90 -Weight $preflightProcessHeavyWeight -ResourceClass "ProcessHeavy"
        $frontendArguments = @("-NoProfile")
        if ($runningOnWindows) {
            $frontendArguments += @("-ExecutionPolicy", "Bypass")
        }
        $frontendArguments += @("-File", (Join-Path $PSScriptRoot "verify-frontend.ps1"), "-RepositoryRoot", $repoRoot, "-LogsPath", $verificationLogsPath)
        Add-VerificationParallelPhase -Name "frontend-preflight" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 590 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-preflight.log") -EstimatedDurationSeconds 70 -Weight $preflightFrontendWeight -ResourceClass "CpuBound"
        foreach ($contractScript in $preflightBuildOverlapContractScripts) {
            $contractArguments = @("-NoProfile")
            if ($runningOnWindows) {
                $contractArguments += @("-ExecutionPolicy", "Bypass")
            }
            $contractArguments += @("-File", (Join-Path $testsPath "scripts\$contractScript"))
            # https://github.com/Jacob-J-Thomas/agenthome-poc/issues/422 tracks the broader bounded Windows verifier contention; only this source-heavy contract receives the measured ordinary-contract headroom.
            $contractTimeoutSeconds = if ($contractScript -ceq "verify-watchdog.tests.ps1") { 120 } else { 90 }
            Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds $contractTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"
        }
        $coverageContractArguments = @("-NoProfile")
        if ($runningOnWindows) {
            $coverageContractArguments += @("-ExecutionPolicy", "Bypass")
        }
        $coverageContractArguments += @("-File", (Join-Path $testsPath "scripts\verify-coverage.tests.ps1"))
        Add-VerificationParallelPhase -Name "contract-verify-coverage.tests" -FileName $powerShellExecutable -Arguments $coverageContractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "verify-coverage.tests.ps1.log") -EstimatedDurationSeconds 75 -Weight $preflightCoverageContractWeight -ResourceClass "ProcessHeavy"
        Write-Output "VERIFY_PARALLEL_PLAN kind=pull-request-build-overlap phases=$($script:VerificationParallelPhases.Count) requested_workers=$MaximumTestWorkers maximum_workers=$preflightMaximumWorkers maximum_resource_capacity=$preflightResourceCapacity maximum_process_heavy=$preflightMaximumProcessHeavyWorkers maximum_cpu_bound=1 build_weight=$preflightProcessHeavyWeight coverage_contract_weight=$preflightCoverageContractWeight frontend_weight=$preflightFrontendWeight ordinary_contract_weight=1 ordinary_contracts=$($preflightBuildOverlapContractScripts.Count) coverage_after_build_by_singleton_lpt=true configuration=$Configuration"
        Invoke-VerificationParallelPhases -MaximumWorkers $preflightMaximumWorkers -MaximumResourceCapacity $preflightResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers 1 | Out-Null
        Reset-VerificationParallelPhaseState
        $script:LastCompletedVerificationPhase = "pull-request-build-overlap"

        foreach ($contractScript in $contractScripts) {
            if ($preflightBuildOverlapContractScripts -ccontains $contractScript -or $contractScript -ceq "verify-coverage.tests.ps1") {
                continue
            }

            $contractArguments = @("-NoProfile")
            if ($runningOnWindows) {
                $contractArguments += @("-ExecutionPolicy", "Bypass")
            }
            $contractArguments += @("-File", (Join-Path $testsPath "scripts\$contractScript"))
            if ($preflightNestedProcessContractScripts -ccontains $contractScript) {
                Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 60 -Weight $preflightNestedProcessContractWeight -ResourceClass "ProcessHeavy"
            }
            else {
                throw "Preflight script contract '$contractScript' reached execution without a resource classification."
            }
        }
        Write-Output "VERIFY_PARALLEL_PLAN kind=pull-request-post-build-contracts phases=$($script:VerificationParallelPhases.Count) requested_workers=$MaximumTestWorkers maximum_workers=$preflightMaximumWorkers maximum_resource_capacity=$preflightResourceCapacity maximum_process_heavy=$preflightMaximumProcessHeavyWorkers nested_process_contract_weight=$preflightNestedProcessContractWeight nested_process_contracts=$($preflightNestedProcessContractScripts.Count) nested_process_isolation=full-resource-capacity configuration=$Configuration"
        Invoke-VerificationParallelPhases -MaximumWorkers $preflightMaximumWorkers -MaximumResourceCapacity $preflightResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers | Out-Null
        Reset-VerificationParallelPhaseState
        $script:LastCompletedVerificationPhase = "pull-request-preflight"
    }
    elseif ($VerificationComponent -eq "StaticContracts") {
        Invoke-StaticVerificationContracts
        $staticManifestPaths = @(
            (Join-Path $verificationLogsPath "verify-sdk-diagnostics.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-preflight-overlap.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-coverage.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-bounded-phases.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-parallel.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-test-inventory.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-watchdog.tests.ps1.log"),
            (Join-Path $verificationLogsPath "verify-promotion-fan-in.tests.ps1.log"),
            (Join-Path $verificationLogsPath "frontend-preflight.log"),
            (Join-Path $verificationLogsPath "restore-static.log"),
            (Join-Path $verificationLogsPath "format-whitespace.log"),
            (Join-Path $verificationLogsPath "format-naming-style.log"),
            (Join-Path $verificationLogsPath "git-diff-check.log")
        )
        Write-VerificationComponentEvidence -Component "static-contracts" -ManifestPaths $staticManifestPaths -StaticContractCount 8 -FrontendComplete $true -FormatComplete $true -DiffComplete $true
        $verificationStopwatch.Stop()
        $elapsedText = $verificationStopwatch.Elapsed.TotalSeconds.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
        Write-Output "VERIFY_COMPLETE schema_version=1 component=static-contracts status=passed elapsed_seconds=$elapsedText"
        return
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
    $testProjects = @(Get-ChildItem -Path $testsPath -Recurse -Filter "*.csproj" | Where-Object { $_.Name -ne "EmbodySense.CancellationHost.csproj" -and $_.Name -ne "EmbodySense.E2EBrowserHost.csproj" -and $_.Name -ne "EmbodySense.Tests.Support.csproj" } | Sort-Object FullName)
    $isolations = [Collections.Generic.List[object]]::new()
    foreach ($testProject in $testProjects) {
        $isolations.Add((Get-ProjectCoverageIsolation -TestProject $testProject -Lanes @(Get-VerificationTestProjectLanes -TestProject $testProject)))
    }

    foreach ($isolation in $isolations) {
        Add-TestDiscoveryPhase -Name "canonical-$($isolation.Project.BaseName)" -AssemblyPath $isolation.CanonicalAssemblyPath -Filter (Get-TestProjectFilter -TestProject $isolation.Project) -OutputPath (Join-Path $canonicalInventoryRoot "$($isolation.Project.BaseName).json")
    }
    Write-Output "VERIFY_PARALLEL_PLAN kind=discovery phases=$($script:VerificationParallelPhases.Count) requested_workers=$MaximumTestWorkers maximum_workers=$hardwareBoundedResourceCapacity maximum_resource_capacity=$hardwareBoundedResourceCapacity"
    Invoke-VerificationParallelPhases -MaximumWorkers $hardwareBoundedResourceCapacity -MaximumResourceCapacity $hardwareBoundedResourceCapacity | Out-Null
    Reset-VerificationParallelPhaseState

    $laneDefinitions = @($isolations | ForEach-Object {
        $projectName = $_.Project.BaseName
        foreach ($lane in $_.Lanes) {
            [ordered]@{
                name = $lane.Name
                projectName = $projectName
                filter = $lane.Filter
            }
        }
    })
    $laneDefinitionPath = Join-Path $verificationResultsPath "required-test-lanes.json"
    [IO.File]::WriteAllText($laneDefinitionPath, ([ordered]@{ schemaVersion = 1; lanes = $laneDefinitions } | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

    $partitionArguments = @("-NoProfile")
    if ($runningOnWindows) { $partitionArguments += @("-ExecutionPolicy", "Bypass") }
    $partitionArguments += @("-File", (Join-Path $PSScriptRoot "verify-test-partition.ps1"), "-CanonicalInventoryRoot", $canonicalInventoryRoot, "-LaneDefinitionPath", $laneDefinitionPath, "-ExpectedExecutionInventoryPath", $verificationInventoryPath, "-ReportPath", $verificationPartitionReportPath)
    Invoke-CheckedNativePhase -Name "test-partition-reconciliation" -FileName $powerShellExecutable -Arguments $partitionArguments -TimeoutSeconds 120

    $coverageStartedUtc = [DateTime]::UtcNow
    if ($VerificationComponent -ne "Solution") {
        Add-ProfiledRequiredGatePhase -Name "git-diff-check" -FileName "git" -Arguments @("diff", "--check") -OutputPath (Join-Path $verificationLogsPath "git-diff-check.log")
        Add-ProfiledRequiredGatePhase -Name "format-whitespace" -FileName "dotnet" -Arguments @("format", "whitespace", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--verbosity", "minimal") -OutputPath (Join-Path $verificationLogsPath "format-whitespace.log")
        Add-ProfiledRequiredGatePhase -Name "format-naming-style" -FileName "dotnet" -Arguments @("format", "style", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal") -OutputPath (Join-Path $verificationLogsPath "format-naming-style.log")
    }
    foreach ($isolation in $isolations) {
        foreach ($lane in $isolation.Lanes) {
            Add-TestExecutionPhase -Isolation $isolation -Lane $lane
        }
    }

    $excludedRequiredGateNames = if ($VerificationComponent -eq "Solution") { @("git-diff-check", "format-whitespace", "format-naming-style") } else { @() }
    Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases) -ExcludedNames $excludedRequiredGateNames
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

    $inventoryArguments = @("-NoProfile")
    if ($runningOnWindows) { $inventoryArguments += @("-ExecutionPolicy", "Bypass") }
    $inventoryArguments += @("-File", (Join-Path $PSScriptRoot "verify-test-inventory.ps1"), "-ExpectedInventoryPath", $verificationInventoryPath, "-ResultsRoot", $standardTestResultsRoot, "-ReportPath", $verificationInventoryReportPath)
    Add-VerificationParallelPhase -Name "test-inventory-reconciliation" -FileName $powerShellExecutable -Arguments $inventoryArguments -TimeoutSeconds 180 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "test-inventory-reconciliation.log")

    if (-not $SkipCoverage) {
        Write-CoverageManifest -TestResults $testResults -Isolations @($isolations) -MinimumWriteTimeUtc $coverageStartedUtc -VerificationResultsPath $verificationResultsPath -ManifestPath $coverageManifestPath
        $coverageArguments = @("-NoProfile")
        if ($runningOnWindows) { $coverageArguments += @("-ExecutionPolicy", "Bypass") }
        $coverageArguments += @("-File", (Join-Path $PSScriptRoot "verify-coverage.ps1"), "-MinimumWriteTimeUtc", $coverageStartedUtc.ToString("O"), "-ResultsRoot", $verificationResultsPath, "-ManifestPath", $coverageManifestPath, "-ReportPath", $coverageSummaryPath)
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

$solutionManifestPaths = @(
    (Join-Path $verificationResultsPath "required-test-lanes.json"),
    (Join-Path $verificationResultsPath "required-test-partition.json"),
    (Join-Path $verificationResultsPath "required-execution-tests.json"),
    (Join-Path $verificationResultsPath "required-test-report.json"),
    (Join-Path $verificationResultsPath "coverage-manifest.json"),
    (Join-Path $verificationResultsPath "coverage-summary.json")
)
if ($VerificationComponent -eq "Solution") {
    $solutionManifestPaths += @($testResults | ForEach-Object { $_.TrxPath })
    Write-VerificationComponentEvidence -Component "solution" -ManifestPaths $solutionManifestPaths -LaneCount $testResults.Count -InventoryComplete $true -CoverageComplete $true
}

$verificationStopwatch.Stop()
$elapsedText = $verificationStopwatch.Elapsed.TotalSeconds.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
if ($VerificationComponent -eq "Solution") {
    Write-Output "VERIFY_COMPLETE schema_version=1 component=solution status=passed elapsed_seconds=$elapsedText"
}
else {
    Write-Output "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=$elapsedText"
}
