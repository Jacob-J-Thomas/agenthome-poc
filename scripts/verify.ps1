param(
    [switch]$SkipCoverage,
    [switch]$SkipRestore,
    [switch]$RunBrowserE2E,
    [switch]$BrowserE2EOnly,
    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = [Math]::Min(8, [Environment]::ProcessorCount),
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
$laneInventoryRoot = Join-Path $verificationResultsPath "Inventory\Lanes"
$verificationInventoryPath = Join-Path $verificationResultsPath "required-execution-tests.json"
$verificationPartitionReportPath = Join-Path $verificationResultsPath "required-test-partition.json"
$verificationInventoryReportPath = Join-Path $verificationResultsPath "required-test-report.json"
$coverageIsolationRoot = Join-Path $verificationResultsPath "CoverageIsolation"
$standardTestResultsRoot = Join-Path $verificationResultsPath "StandardTests"
$coverageManifestPath = Join-Path $verificationResultsPath "coverage-manifest.json"
$coverageSummaryPath = Join-Path $verificationResultsPath "coverage-summary.json"
$verificationPhysicalTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX) -and ($verificationPhysicalTempRoot -eq "/var" -or $verificationPhysicalTempRoot.StartsWith("/var/", [StringComparison]::Ordinal))) {
    $verificationPhysicalTempRoot = "/private" + $verificationPhysicalTempRoot
}
$verificationLaneTrustRoot = Join-Path $verificationPhysicalTempRoot ("embodysense-verification-trust-" + [Guid]::NewGuid().ToString("N"))
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$maximumArtifactStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunArtifactMaximumShapeTests.Adversarial_maximum_transition_reservations_and_canonical_order_checks_remain_bounded"
$deletionCapacityStressTest = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTraceRetentionStoreTests.Rejected_operation_capacity_preserves_reserved_tombstone_deletions_and_remains_visible"
$testLaneTimeoutSeconds = 480

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-parallel.ps1")
. (Join-Path $PSScriptRoot "verification-artifacts.ps1")
Reset-VerificationPhaseState
Reset-VerificationParallelPhaseState

if ($BrowserE2EOnly -and -not $RunBrowserE2E) {
    throw "-BrowserE2EOnly requires -RunBrowserE2E."
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

function Get-TestProjectLanes {
    param([System.IO.FileInfo]$TestProject)

    $nonStress = "(VerificationTier!=Stress)"
    if ($TestProject.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
        return @(
            [pscustomobject]@{ Name = "graph-authoring"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring)&$nonStress" }
            [pscustomobject]@{ Name = "capabilities"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Capabilities)&$nonStress" }
            [pscustomobject]@{ Name = "authority-context"; Filter = "((FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Audit)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Authority)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.ContextualRoles)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.ToolResults))&$nonStress" }
            [pscustomobject]@{ Name = "credentials"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Credentials)&$nonStress" }
            [pscustomobject]@{ Name = "human-input-requests"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest)&$nonStress" }
            [pscustomobject]@{ Name = "human-input-responses"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse)&$nonStress" }
            [pscustomobject]@{ Name = "default-conversation"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn)&$nonStress" }
            [pscustomobject]@{ Name = "custom-definition-control"; Filter = "((FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinition)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControl)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocation))&$nonStress" }
            [pscustomobject]@{ Name = "custom-run-trace"; Filter = "((FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRun)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTrace)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspace)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverage))&$nonStress" }
            [pscustomobject]@{ Name = "governed-lifecycle"; Filter = "((FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.Admission)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.Revisions))&$nonStress" }
            [pscustomobject]@{ Name = "triggers"; Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Triggers)&$nonStress" }
            [pscustomobject]@{ Name = "remainder"; Filter = "(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Capabilities)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Audit)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Authority)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.ContextualRoles)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.ToolResults)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Credentials)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinition)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControl)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocation)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRun)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTrace)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspace)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverage)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.Admission)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.Revisions)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Triggers)&$nonStress" }
        )
    }

    if ($TestProject.Name -eq "EmbodySense.Core.Startup.Tests.csproj") {
        return @(
            [pscustomobject]@{ Name = "capabilities"; Filter = "(FullyQualifiedName~EmbodySense.Core.Startup.Tests.Capabilities)&$nonStress" }
            [pscustomobject]@{ Name = "loop-execution"; Filter = "(FullyQualifiedName~EmbodySense.Core.Startup.Tests.Loops.Execution)&$nonStress" }
            [pscustomobject]@{ Name = "loops-other"; Filter = "(FullyQualifiedName~EmbodySense.Core.Startup.Tests.Loops)&(FullyQualifiedName!~EmbodySense.Core.Startup.Tests.Loops.Execution)&$nonStress" }
            [pscustomobject]@{ Name = "runtime-triggers"; Filter = "((FullyQualifiedName~EmbodySense.Core.Startup.Tests.Runtime)|(FullyQualifiedName~EmbodySense.Core.Startup.Tests.Triggers))&$nonStress" }
            [pscustomobject]@{ Name = "remainder"; Filter = "(FullyQualifiedName!~EmbodySense.Core.Startup.Tests.Capabilities)&(FullyQualifiedName!~EmbodySense.Core.Startup.Tests.Loops)&(FullyQualifiedName!~EmbodySense.Core.Startup.Tests.Runtime)&(FullyQualifiedName!~EmbodySense.Core.Startup.Tests.Triggers)&$nonStress" }
        )
    }

    return @([pscustomobject]@{ Name = "all"; Filter = Get-TestProjectFilter -TestProject $TestProject })
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
        $laneManifest = @(Copy-VerifiedDirectory -SourceDirectory $pristineDirectory -DestinationDirectory $laneDirectory -Description "$($TestProject.BaseName)/$($lane.Name) lane copy")
        $laneEnvironment = @{
            EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $verificationLaneTrustRoot "$($TestProject.BaseName)-$($lane.Name)"
        }
        if (-not $SkipCoverage -and $TestProject.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
            $laneEnvironment.EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory
        }
        $laneCopies.Add([pscustomobject]@{
            Name = "$($TestProject.BaseName)-$($lane.Name)"
            ProjectName = $TestProject.BaseName
            ShardName = $lane.Name
            Filter = $lane.Filter
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

    $priority = switch -Wildcard ($Lane.Name) {
        "EmbodySense.Core.Persistence.Tests-human-input-responses" { 2200; break }
        "EmbodySense.Core.Persistence.Tests-triggers" { 2100; break }
        "EmbodySense.Core.Persistence.Tests-custom-run-trace" { 2050; break }
        "EmbodySense.Core.Startup.Tests-*" { 2000; break }
        "EmbodySense.IntegrationTests-*" { 1950; break }
        "EmbodySense.Web.Tests-*" { 1900; break }
        "EmbodySense.Core.Persistence.Tests-*" { 1800; break }
        "EmbodySense.Core.Application.Tests-*" { 1700; break }
        "EmbodySense.Core.Clients.Tests-*" { 1600; break }
        default { 1500; break }
    }
    Add-VerificationParallelPhase -Name "tests-$($Lane.Name)" -FileName "dotnet" -Arguments $arguments -TimeoutSeconds $testLaneTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$($Lane.Name).log") -CoverageSearchRoot $(if ($SkipCoverage) { $null } else { $Lane.ResultsPath }) -TrxPath (Join-Path $Lane.ResultsPath $trxName) -Environment $Lane.Environment -Priority $priority
}

function Write-CoverageManifest {
    param([object[]]$TestResults, [object[]]$Isolations, [DateTime]$MinimumWriteTimeUtc)

    $laneReports = [Collections.Generic.List[object]]::new()
    foreach ($result in $TestResults) {
        $reports = @(Get-ChildItem -LiteralPath $result.CoverageSearchRoot -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc })
        if ($reports.Count -ne 1) {
            throw "Coverage lane '$($result.Name)' produced $($reports.Count) fresh reports; exactly one is required."
        }
        $laneReports.Add($reports[0])
    }

    $childReports = @($Isolations | ForEach-Object {
        if (Test-Path -LiteralPath $_.ChildResultsPath) {
            Get-ChildItem -LiteralPath $_.ChildResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc }
        }
    })
    $allReports = @($laneReports) + @($childReports)
    $allCoverageFiles = @(Get-ChildItem -LiteralPath $verificationResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)
    $expectedPaths = @($allReports.FullName | Sort-Object -Unique)
    $actualPaths = @($allCoverageFiles.FullName | Sort-Object -Unique)
    if (@(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $actualPaths -CaseSensitive).Count -ne 0) {
        throw "Coverage results contain stale or unexpected reports outside the successful lane/child report inventory."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        resultsRoot = [IO.Path]::GetFullPath($verificationResultsPath)
        minimumWriteTimeUtc = $MinimumWriteTimeUtc.ToString("O")
        laneReportCount = $laneReports.Count
        childReportCount = $childReports.Count
        reports = @($allCoverageFiles | ForEach-Object {
            [ordered]@{
                path = $_.FullName
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    }
    [IO.File]::WriteAllText($coverageManifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output "VERIFY_COVERAGE_MANIFEST_COMPLETE lanes=$($laneReports.Count) child_reports=$($childReports.Count) reports=$($allCoverageFiles.Count) path=$coverageManifestPath"
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

    if ($VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly) {
        $contractScripts = @(
            "verify-coverage.tests.ps1",
            "verify-bounded-phases.tests.ps1",
            "verify-parallel.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1"
        )
        if ($runningOnWindows) {
            $contractScripts = @("verify-sdk-diagnostics.tests.ps1") + $contractScripts
        }
        foreach ($contractScript in $contractScripts) {
            $contractArguments = @("-NoProfile")
            if ($runningOnWindows) {
                $contractArguments += @("-ExecutionPolicy", "Bypass")
            }
            $contractArguments += @("-File", (Join-Path $testsPath "scripts\$contractScript"))
            Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 90 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log")
        }
        Write-Output "VERIFY_PARALLEL_PLAN kind=script-contracts phases=$($contractScripts.Count) maximum_workers=$([Math]::Min(4, $MaximumTestWorkers))"
        Invoke-VerificationParallelPhases -MaximumWorkers ([Math]::Min(4, $MaximumTestWorkers)) | Out-Null
        Reset-VerificationParallelPhaseState
    }

    $buildArguments = @("build")
    if ($SkipRestore) {
        $buildArguments += "--no-restore"
    }
    $buildArguments += if ($VerificationTier -eq "Stress") { $persistenceTestProjectPath } elseif ($BrowserE2EOnly) { $e2eProjectPath } else { "EmbodySense.sln" }
    $buildArguments += @("-c", $Configuration, "/p:RestoreIgnoreFailedSources=true")
    Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900

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

    if ($runningOnWindows) {
        Invoke-CheckedNativePhase -Name "npm-ci" -FileName $env:ComSpec -Arguments @("/d", "/s", "/c", "npm.cmd ci --include=dev") -TimeoutSeconds 300
    }
    else {
        Invoke-CheckedNativePhase -Name "npm-ci" -FileName "npm" -Arguments @("ci", "--include=dev") -TimeoutSeconds 300
    }

    Write-Output "VERIFY_REQUIRED_TEST_CONTRACT identity=TestCase.Id partition_identity=XunitTestCaseUniqueID filter=VerificationTier!=Stress"
    $testProjects = @(Get-ChildItem -Path $testsPath -Recurse -Filter "*.csproj" | Where-Object { $_.Name -ne "EmbodySense.CancellationHost.csproj" -and $_.Name -ne "EmbodySense.Tests.Support.csproj" } | Sort-Object FullName)
    $isolations = [Collections.Generic.List[object]]::new()
    foreach ($testProject in $testProjects) {
        $isolations.Add((Get-ProjectCoverageIsolation -TestProject $testProject -Lanes @(Get-TestProjectLanes -TestProject $testProject)))
    }

    foreach ($isolation in $isolations) {
        Add-TestDiscoveryPhase -Name "canonical-$($isolation.Project.BaseName)" -AssemblyPath $isolation.CanonicalAssemblyPath -Filter (Get-TestProjectFilter -TestProject $isolation.Project) -OutputPath (Join-Path $canonicalInventoryRoot "$($isolation.Project.BaseName).json")
        foreach ($lane in $isolation.Lanes) {
            Add-TestDiscoveryPhase -Name "lane-$($lane.Name)" -AssemblyPath $lane.AssemblyPath -Filter $lane.Filter -OutputPath (Join-Path $laneInventoryRoot "$($lane.Name).json")
        }
    }
    Write-Output "VERIFY_PARALLEL_PLAN kind=discovery phases=$($script:VerificationParallelPhases.Count) maximum_workers=$MaximumTestWorkers"
    Invoke-VerificationParallelPhases -MaximumWorkers $MaximumTestWorkers | Out-Null
    Reset-VerificationParallelPhaseState

    $partitionArguments = @("-NoProfile")
    if ($runningOnWindows) { $partitionArguments += @("-ExecutionPolicy", "Bypass") }
    $partitionArguments += @("-File", (Join-Path $PSScriptRoot "verify-test-partition.ps1"), "-CanonicalInventoryRoot", $canonicalInventoryRoot, "-LaneInventoryRoot", $laneInventoryRoot, "-ExpectedExecutionInventoryPath", $verificationInventoryPath, "-ReportPath", $verificationPartitionReportPath)
    Invoke-CheckedNativePhase -Name "test-partition-reconciliation" -FileName $powerShellExecutable -Arguments $partitionArguments -TimeoutSeconds 120

    $coverageStartedUtc = [DateTime]::UtcNow
    Add-VerificationParallelPhase -Name "format-whitespace" -FileName "dotnet" -Arguments @("format", "whitespace", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--verbosity", "minimal") -TimeoutSeconds 240 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "format-whitespace.log") -Priority 1400
    Add-VerificationParallelPhase -Name "format-naming-style" -FileName "dotnet" -Arguments @("format", "style", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal") -TimeoutSeconds 240 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "format-naming-style.log") -Priority 1400
    Add-VerificationParallelPhase -Name "git-diff-check" -FileName "git" -Arguments @("diff", "--check") -TimeoutSeconds 60 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "git-diff-check.log") -Priority 1400
    if ($runningOnWindows) {
        Add-VerificationParallelPhase -Name "frontend-tests" -FileName $env:ComSpec -Arguments @("/d", "/s", "/c", "npm.cmd test") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-tests.log") -Priority 1450
    }
    else {
        Add-VerificationParallelPhase -Name "frontend-tests" -FileName "npm" -Arguments @("test") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-tests.log") -Priority 1450
    }
    foreach ($isolation in $isolations) {
        foreach ($lane in $isolation.Lanes) {
            Add-TestExecutionPhase -Isolation $isolation -Lane $lane
        }
    }

    Write-Output "VERIFY_PARALLEL_PLAN kind=required-gates phases=$($script:VerificationParallelPhases.Count) maximum_workers=$MaximumTestWorkers coverage=$(-not $SkipCoverage)"
    $gateResults = @(Invoke-VerificationParallelPhases -MaximumWorkers $MaximumTestWorkers)
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
        Write-CoverageManifest -TestResults $testResults -Isolations @($isolations) -MinimumWriteTimeUtc $coverageStartedUtc
        $coverageArguments = @("-NoProfile")
        if ($runningOnWindows) { $coverageArguments += @("-ExecutionPolicy", "Bypass") }
        $coverageArguments += @("-File", (Join-Path $PSScriptRoot "verify-coverage.ps1"), "-MinimumWriteTimeUtc", $coverageStartedUtc.ToString("O"), "-ResultsRoot", $verificationResultsPath, "-ManifestPath", $coverageManifestPath, "-ReportPath", $coverageSummaryPath)
        Add-VerificationParallelPhase -Name "coverage-thresholds" -FileName $powerShellExecutable -Arguments $coverageArguments -TimeoutSeconds 180 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "coverage-thresholds.log")
    }
    Write-Output "VERIFY_PARALLEL_PLAN kind=reconciliation phases=$($script:VerificationParallelPhases.Count) maximum_workers=$([Math]::Min(2, $MaximumTestWorkers))"
    Invoke-VerificationParallelPhases -MaximumWorkers ([Math]::Min(2, $MaximumTestWorkers)) | Out-Null
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $verificationLaneTrustRoot) {
        Remove-Item -LiteralPath $verificationLaneTrustRoot -Recurse -Force
    }
}

$verificationStopwatch.Stop()
$elapsedText = $verificationStopwatch.Elapsed.TotalSeconds.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
Write-Output "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=$elapsedText"
