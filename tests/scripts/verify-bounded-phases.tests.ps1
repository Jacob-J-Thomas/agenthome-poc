Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$tempScriptPath = Join-Path $repoRoot "scripts\verification-temp.ps1"
$artifactScriptPath = Join-Path $repoRoot "scripts\verification-artifacts.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$watchdogScriptPath = Join-Path $repoRoot "scripts\verify-with-watchdog.ps1"
$coverageScriptPath = Join-Path $repoRoot "scripts\verify-coverage.ps1"
$coverageEvidenceScriptPath = Join-Path $repoRoot "scripts\verification-coverage-evidence.ps1"
$verifyWorkflowPath = Join-Path $repoRoot ".github\workflows\verify.yml"
$qualificationWorkflowPath = Join-Path $repoRoot ".github\workflows\qualification.yml"
$browserWorkflowPath = Join-Path $repoRoot ".github\workflows\browser-e2e.yml"
$promotionCancellationWorkflowPath = Join-Path $repoRoot ".github\workflows\promotion-cancellation.yaml"
$codeqlWorkflowPath = Join-Path $repoRoot ".github\workflows\codeql.yml"
$dependencyReviewWorkflowPath = Join-Path $repoRoot ".github\workflows\dependency-review.yml"
$stressWorkflowPath = Join-Path $repoRoot ".github\workflows\verification-stress.yml"
$pullRequestSettingsPath = Join-Path $repoRoot "tests\verification-pull-request.runsettings"
$stressSettingsPath = Join-Path $repoRoot "tests\verification-stress.runsettings"
$gitIgnorePath = Join-Path $repoRoot ".gitignore"
$readmePath = Join-Path $repoRoot "README.md"
$verificationDocumentationPath = Join-Path $repoRoot "docs\VERIFICATION.md"
$maximumTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\CustomLoopRunArtifactMaximumShapeTests.cs"
$retentionTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\CustomLoopTraceRetentionStoreTests.cs"
$coverageChildProcessPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Verification\CoverageChildProcessAssembly.cs"
$cancellationHostProcessPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Verification\CancellationHostProcess.cs"
$persistenceTestProjectPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\EmbodySense.Core.Persistence.Tests.csproj"
$cancellationHostProjectPath = Join-Path $repoRoot "tests\EmbodySense.CancellationHost\EmbodySense.CancellationHost.csproj"
$cancellationHostProgramPath = Join-Path $repoRoot "tests\EmbodySense.CancellationHost\Program.cs"
$scheduleStoreTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Triggers\Schedules\ScheduleStoreTests.cs"
$scheduleStoreHostPath = Join-Path $repoRoot "tests\Shared\ScheduleStoreCrossProcessHost.cs"
$humanReviewOrderedReleaseTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\HumanReview\HumanReviewOrderedReleasePersistenceTests.cs"
$humanReviewOrderedReleaseHostPath = Join-Path $repoRoot "tests\EmbodySense.CancellationHost\Persistence\HumanReviewOrderedReleaseProcessHost.cs"
$humanReviewOrderedReleaseAuthorityPath = Join-Path $repoRoot "tests\EmbodySense.CancellationHost\Persistence\HumanReviewOrderedReleaseProcessAuthority.cs"
$humanReviewOrderedReleaseRaceGateStorePath = Join-Path $repoRoot "tests\EmbodySense.CancellationHost\Persistence\HumanReviewOrderedReleaseRaceGateStore.cs"
$reconciliationProbeProcessTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\Execution\Reconciliation\GovernedLoopEffectReconciliationProbeProcessTests.cs"
$admissionStoreTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\Admission\GovernedLoopAdmissionStoreTests.cs"
$admissionStoreFixturePath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\Admission\GovernedLoopAdmissionStoreTestFixture.cs"
$admissionStoreHostTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\Admission\GovernedLoopAdmissionStoreCrossProcessHostTests.cs"
$admissionWriterHostPath = Join-Path $repoRoot "tests\Shared\GovernedLoopAdmissionCrossProcessWriterHost.cs"
$persistenceEnvironmentCollectionPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Verification\ProcessEnvironmentCollection.cs"
$persistenceCapabilityCatalogTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Capabilities\FileCapabilityCatalogTrustProviderTests.cs"
$startupRuntimeCollectionPath = Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Loops\Execution\LoopRuntimeIntegrationCollection.cs"
$startupNestedProcessTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Runtime\AgentRuntimeFactoryNestedProcessTests.cs"
$startupFactoryTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Runtime\AgentRuntimeFactoryTests.cs"
$startupFactoryEffectReconciliationTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Runtime\AgentRuntimeReconciliationFactoryTests.cs"
$startupFactoryEffectReconciliationCoverageTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Runtime\AgentRuntimeReconciliationFactoryTests.Coverage.cs"
$startupFactoryHumanReviewTestPaths = @(
    "AgentRuntimeHumanReviewTests.cs",
    "AgentRuntimeHumanReviewTests.Authority.cs",
    "AgentRuntimeHumanReviewTests.AuthorityEdges.cs",
    "AgentRuntimeHumanReviewTests.FacadeCoverage.cs",
    "AgentRuntimeHumanReviewTests.FacadeDeletionEquivalence.cs",
    "AgentRuntimeHumanReviewTests.FacadeEffectEvidence.cs",
    "AgentRuntimeHumanReviewTests.FacadePublicCoverage.cs",
    "AgentRuntimeHumanReviewTests.HostRecovery.cs",
    "AgentRuntimeHumanReviewTests.HostRecoveryCoverage.cs",
    "AgentRuntimeHumanReviewTests.PublicRecoveryEquivalence.cs",
    "AgentRuntimeHumanReviewTests.Readiness.cs",
    "AgentRuntimeHumanReviewTests.RecoveryCoverage.cs"
) | ForEach-Object { Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Runtime\$_" }
$powerShellExecutable = (Get-Process -Id $PID).Path
$functionalChildTimeoutSeconds = 30
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:assertionCount++
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)
    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

function Invoke-ExpectedFailure {
    param([scriptblock]$Action, [string]$ExpectedMessage)
    $failureMessage = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $failureMessage = $_.Exception.Message
    }
    if ($null -eq $failureMessage) { throw "Expected the action to fail, but it completed successfully." }
    Assert-Contains -Actual $failureMessage -Expected $ExpectedMessage -Message "Failure diagnostic mismatch."
    return $failureMessage
}

$noOpWasRejected = $false
try { $null = Invoke-ExpectedFailure -ExpectedMessage "never emitted" -Action { } } catch { $noOpWasRejected = $_.Exception.Message -ceq "Expected the action to fail, but it completed successfully." }
Assert-True -Condition $noOpWasRejected -Message "The negative-test helper must reject a successful action instead of catching its own sentinel."
Assert-True -Condition ($functionalChildTimeoutSeconds -eq 30) -Message "Functional child probes must retain Windows startup headroom without weakening the independent one-second timeout proof."

. $phaseScriptPath
. $tempScriptPath
. $artifactScriptPath
Reset-VerificationPhaseState

$contextLine = Write-VerificationContext -RepositoryRoot $repoRoot -Configuration Debug -VerificationTier PullRequest
Assert-Contains -Actual $contextLine -Expected "VERIFY_CONTEXT_JSON=" -Message "Verifier context must be machine readable."
$context = $contextLine.Substring("VERIFY_CONTEXT_JSON=".Length) | ConvertFrom-Json
Assert-True -Condition ($context.schemaVersion -eq 1) -Message "Verifier context schema must remain version 1."
Assert-True -Condition ($context.verificationTier -eq "PullRequest") -Message "Verifier context must identify its tier."
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($context.repositoryHead)) -Message "Verifier context must identify the exact head or an explicit marker."
Assert-True -Condition ($context.processorCount -ge 1) -Message "Verifier context must identify processor count."

$systemTempProbe = Join-Path $repoRoot ("embodysense-system-temp-probe-" + [Guid]::NewGuid().ToString("N"))
$runnerTempProbe = Join-Path $repoRoot ("embodysense-runner-temp-probe-" + [Guid]::NewGuid().ToString("N"))
Assert-True -Condition ((Resolve-VerificationPhysicalTempRoot -RunnerTemp $runnerTempProbe -SystemTempPath $systemTempProbe) -ceq ([IO.Path]::GetFullPath($runnerTempProbe))) -Message "Hosted verification must prefer the runner-owned ephemeral temporary root."
Assert-True -Condition ((Resolve-VerificationPhysicalTempRoot -RunnerTemp "" -SystemTempPath $systemTempProbe) -ceq ([IO.Path]::GetFullPath($systemTempProbe))) -Message "Local verification must retain a fully-qualified system-temp fallback."
if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
    Assert-True -Condition ((Resolve-VerificationPhysicalTempRoot -RunnerTemp "/tmp/embodysense-verification" -SystemTempPath $systemTempProbe) -ceq "/private/tmp/embodysense-verification") -Message "macOS verification must resolve the /tmp symlink before capability path guards inspect lane fixtures."
}
$null = Invoke-ExpectedFailure -ExpectedMessage "fully qualified path" -Action {
    Resolve-VerificationPhysicalTempRoot -RunnerTemp "relative-temp" -SystemTempPath $systemTempProbe
}
$laneFixturePath = Get-VerificationLaneFixturePath -PhysicalTempRoot ([IO.Path]::GetTempPath()) -RunIdentity "run-a" -LaneIdentity "project-lane-a"
$sameLaneFixturePath = Get-VerificationLaneFixturePath -PhysicalTempRoot ([IO.Path]::GetTempPath()) -RunIdentity "run-a" -LaneIdentity "project-lane-a"
$differentLaneFixturePath = Get-VerificationLaneFixturePath -PhysicalTempRoot ([IO.Path]::GetTempPath()) -RunIdentity "run-a" -LaneIdentity "project-lane-b"
Assert-True -Condition ($laneFixturePath -ceq $sameLaneFixturePath) -Message "A run/lane identity must derive one stable temporary path."
Assert-True -Condition ($laneFixturePath -cne $differentLaneFixturePath) -Message "Distinct lanes must derive disjoint temporary paths."
Assert-True -Condition ((Split-Path -Parent $laneFixturePath) -ceq ([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar))) -Message "Lane fixtures must remain on the selected physical temporary volume."
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    Assert-True -Condition ([Text.Encoding]::UTF8.GetByteCount($laneFixturePath) -le 72) -Message "Unix lane fixtures must reserve the CoreFxPipe endpoint suffix below macOS's 104-byte limit."
}
$null = Invoke-ExpectedFailure -ExpectedMessage "fully qualified root" -Action {
    Get-VerificationLaneFixturePath -PhysicalTempRoot "relative-temp" -RunIdentity "run-a" -LaneIdentity "project-lane-a"
}

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-bounded-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $manifestProbeRoot = Join-Path $scenarioRoot "immutable-pristine"
    New-Item -ItemType Directory -Path $manifestProbeRoot | Out-Null
    $manifestProbePath = Join-Path $manifestProbeRoot "assembly.dll"
    [IO.File]::WriteAllBytes($manifestProbePath, [byte[]](1, 2, 3, 4))
    $manifestProbe = @(Get-VerificationDirectoryManifest -Directory $manifestProbeRoot)
    Assert-VerificationDirectoryManifest -Expected $manifestProbe -Directory $manifestProbeRoot -Description "Unchanged pristine probe"
    [IO.File]::WriteAllBytes($manifestProbePath, [byte[]](4, 3, 2, 1))
    $null = Invoke-ExpectedFailure -ExpectedMessage "failed immutable artifact verification" -Action {
        Assert-VerificationDirectoryManifest -Expected $manifestProbe -Directory $manifestProbeRoot -Description "Mutated pristine probe"
    }

    $argumentProbePath = Join-Path $scenarioRoot "argument probe.ps1"
    @'
param([string]$First, [string]$Second, [string]$Third)
if ($First -cne "value with spaces" -or $Second -cne 'quote"value' -or $Third -cne 'trailing\') { exit 19 }
'@ | Set-Content -LiteralPath $argumentProbePath -Encoding UTF8

    $successArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { $successArguments += @("-ExecutionPolicy", "Bypass") }
    $successArguments += @("-File", $argumentProbePath, "value with spaces", 'quote"value', 'trailing\')
    # Hosted Windows may take more than ten seconds to start a fresh PowerShell child while the required build overlaps this contract.
    # The outer 90-second contract bound still contains both functional probes plus the independent one-second timeout proof.
    $successOutput = @(Invoke-VerificationPhase -Name "argument-integrity" -FileName $powerShellExecutable -Arguments $successArguments -TimeoutSeconds $functionalChildTimeoutSeconds -WorkingDirectory $repoRoot) -join [Environment]::NewLine
    Assert-Contains -Actual $successOutput -Expected "VERIFY_PHASE_START name=argument-integrity" -Message "Successful phases must announce their start."
    Assert-Contains -Actual $successOutput -Expected "VERIFY_PHASE_COMPLETE name=argument-integrity" -Message "Successful phases must announce elapsed completion."

    $failureMessage = Invoke-ExpectedFailure -ExpectedMessage "exited with code 23" -Action {
        Invoke-VerificationPhase -Name "nonzero-exit" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-Command", "exit 23") -TimeoutSeconds $functionalChildTimeoutSeconds -WorkingDirectory $repoRoot
    }
    Assert-Contains -Actual $failureMessage -Expected "Last completed phase: 'argument-integrity'" -Message "Nonzero diagnostics must preserve the last completed phase."

    $timeoutStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $timeoutMessage = Invoke-ExpectedFailure -ExpectedMessage "timed out after 1 seconds" -Action {
        Invoke-VerificationPhase -Name "bounded-timeout" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-Command", "Start-Sleep -Seconds 5") -TimeoutSeconds 1 -WorkingDirectory $repoRoot
    }
    $timeoutStopwatch.Stop()
    Assert-True -Condition ($timeoutStopwatch.Elapsed -lt [TimeSpan]::FromSeconds(10)) -Message "The timeout harness must terminate promptly."
    Assert-Contains -Actual $timeoutMessage -Expected "Last completed phase: 'argument-integrity'" -Message "Timeout diagnostics must preserve the last completed phase."
}
finally {
    if (Test-Path -LiteralPath $scenarioRoot) { Remove-Item -LiteralPath $scenarioRoot -Recurse -Force }
}

$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$watchdogScript = Get-Content -LiteralPath $watchdogScriptPath -Raw
$phaseScript = Get-Content -LiteralPath $phaseScriptPath -Raw
$parallelScript = Get-Content -LiteralPath $parallelScriptPath -Raw
$scheduleScript = Get-Content -LiteralPath $scheduleScriptPath -Raw
$laneScript = Get-Content -LiteralPath (Join-Path $repoRoot "scripts\verification-test-lanes.ps1") -Raw
$coverageScript = Get-Content -LiteralPath $coverageScriptPath -Raw
$coverageEvidenceScript = Get-Content -LiteralPath $coverageEvidenceScriptPath -Raw
$verifyWorkflow = Get-Content -LiteralPath $verifyWorkflowPath -Raw
$qualificationWorkflow = Get-Content -LiteralPath $qualificationWorkflowPath -Raw
$browserWorkflow = Get-Content -LiteralPath $browserWorkflowPath -Raw
$promotionCancellationWorkflow = Get-Content -LiteralPath $promotionCancellationWorkflowPath -Raw
$codeqlWorkflow = Get-Content -LiteralPath $codeqlWorkflowPath -Raw
$dependencyReviewWorkflow = Get-Content -LiteralPath $dependencyReviewWorkflowPath -Raw
$stressWorkflow = Get-Content -LiteralPath $stressWorkflowPath -Raw
$pullRequestSettings = Get-Content -LiteralPath $pullRequestSettingsPath -Raw
$stressSettings = Get-Content -LiteralPath $stressSettingsPath -Raw
$gitIgnore = Get-Content -LiteralPath $gitIgnorePath -Raw
$readme = Get-Content -LiteralPath $readmePath -Raw
$verificationDocumentation = Get-Content -LiteralPath $verificationDocumentationPath -Raw
$maximumTest = Get-Content -LiteralPath $maximumTestPath -Raw
$retentionTest = Get-Content -LiteralPath $retentionTestPath -Raw
$coverageChildProcess = Get-Content -LiteralPath $coverageChildProcessPath -Raw
$cancellationHostProcess = Get-Content -LiteralPath $cancellationHostProcessPath -Raw
$persistenceTestProject = Get-Content -LiteralPath $persistenceTestProjectPath -Raw
$cancellationHostProject = Get-Content -LiteralPath $cancellationHostProjectPath -Raw
$cancellationHostProgram = Get-Content -LiteralPath $cancellationHostProgramPath -Raw
$scheduleStoreTest = Get-Content -LiteralPath $scheduleStoreTestPath -Raw
$scheduleStoreHost = Get-Content -LiteralPath $scheduleStoreHostPath -Raw
$humanReviewOrderedReleaseTest = Get-Content -LiteralPath $humanReviewOrderedReleaseTestPath -Raw
$humanReviewOrderedReleaseHost = Get-Content -LiteralPath $humanReviewOrderedReleaseHostPath -Raw
$humanReviewOrderedReleaseAuthority = Get-Content -LiteralPath $humanReviewOrderedReleaseAuthorityPath -Raw
$humanReviewOrderedReleaseRaceGateStore = Get-Content -LiteralPath $humanReviewOrderedReleaseRaceGateStorePath -Raw
$reconciliationProbeProcessTest = Get-Content -LiteralPath $reconciliationProbeProcessTestPath -Raw
$admissionStoreTest = Get-Content -LiteralPath $admissionStoreTestPath -Raw
$admissionStoreFixture = Get-Content -LiteralPath $admissionStoreFixturePath -Raw
$admissionStoreHostTest = Get-Content -LiteralPath $admissionStoreHostTestPath -Raw
$admissionWriterHost = Get-Content -LiteralPath $admissionWriterHostPath -Raw
$persistenceEnvironmentCollection = Get-Content -LiteralPath $persistenceEnvironmentCollectionPath -Raw
$persistenceCapabilityCatalogTest = Get-Content -LiteralPath $persistenceCapabilityCatalogTestPath -Raw
$startupRuntimeCollection = Get-Content -LiteralPath $startupRuntimeCollectionPath -Raw
$startupNestedProcessTest = Get-Content -LiteralPath $startupNestedProcessTestPath -Raw
$startupFactoryTest = Get-Content -LiteralPath $startupFactoryTestPath -Raw
$startupFactoryEffectReconciliationTest = Get-Content -LiteralPath $startupFactoryEffectReconciliationTestPath -Raw
$startupFactoryEffectReconciliationCoverageTest = Get-Content -LiteralPath $startupFactoryEffectReconciliationCoverageTestPath -Raw
$startupFactoryHumanReviewTests = @($startupFactoryHumanReviewTestPaths | ForEach-Object { Get-Content -LiteralPath $_ -Raw })

Assert-Contains -Actual $verifyScript -Expected '[ValidateSet("PullRequest", "Stress")]' -Message "The verifier must expose only the two owned tiers."
Assert-Contains -Actual $verifyScript -Expected '[string]$Configuration = "Release"' -Message "The canonical verifier must default to Release."
Assert-Contains -Actual $verifyScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))' -Message "The required gate must request bounded logical concurrency above the physical processor count."
Assert-Contains -Actual $watchdogScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))' -Message "The external watchdog must preserve the bounded logical worker request."
Assert-Contains -Actual $watchdogScript -Expected '[ValidateSet("Full", "Solution", "StaticContracts", "NestedProcess")]' -Message "The external watchdog must expose explicit full, solution, static, and nested-process component modes."
Assert-Contains -Actual $watchdogScript -Expected '"-VerificationComponent", $VerificationComponent' -Message "The watchdog must forward the selected component to the canonical verifier."
Assert-Contains -Actual $phaseScript -Expected 'if ($null -ne $commandScriptPath) {' -Message "Windows batch phases must preserve cmd.exe quoting."
Assert-Contains -Actual $phaseScript -Expected 'elseif ($null -ne $startInfo.PSObject.Properties["ArgumentList"]) {' -Message "Non-batch phases must use ArgumentList when available."
Assert-Contains -Actual $phaseScript -Expected 'VERIFY_CHILD_TIMEOUT name=$Name' -Message "Sequential timeouts must emit structured watchdog evidence."
Assert-Contains -Actual $parallelScript -Expected 'Sort-Object -Property @{ Expression = "SchedulingPrioritySeconds"; Descending = $true }, @{ Expression = "EstimatedDurationSeconds"; Descending = $true }, @{ Expression = "Name"; Descending = $false }' -Message "Parallel phases must prioritize singleton-class backlog before deterministic longest-processing-time and exact-name ties."
Assert-Contains -Actual $verifyScript -Expected '$hardwareProcessorCount = [Math]::Max(1, [Environment]::ProcessorCount)' -Message "The verifier must normalize the host processor count before deriving bounded concurrency."
Assert-Contains -Actual $verifyScript -Expected '$hardwareBoundedResourceCapacity = [Math]::Min($MaximumTestWorkers, $hardwareProcessorCount)' -Message "Non-required parallel phases must retain hardware-bounded resource capacity."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-VerificationParallelPhases -MaximumWorkers $hardwareBoundedResourceCapacity -MaximumResourceCapacity $hardwareBoundedResourceCapacity | Out-Null' -Message "Preflight and discovery must remain bounded to physical hosted-runner capacity even when required gates request logical concurrency."
Assert-Contains -Actual $parallelScript -Expected 'cannot schedule phases beyond logical resource capacity' -Message "Declared phase weight must fail closed instead of adapting down to available capacity."
Assert-Contains -Actual $parallelScript -Expected 'resource classes are underweighted' -Message "CPU-bound and process-heavy phases must fail closed when their declared weight is too small."
Assert-Contains -Actual $parallelScript -Expected '$phase.EffectiveWeight = $phase.Weight' -Message "Scheduler evidence must preserve declared weights exactly."
Assert-Contains -Actual $parallelScript -Expected 'scheduling_priority_seconds=$($phase.SchedulingPrioritySeconds)' -Message "Scheduler start evidence must expose the static priority used for deterministic ordering."
Assert-Contains -Actual $parallelScript -Expected 'Select-VerificationParallelPhase -Pending $pending -AvailableCapacity $availableCapacity' -Message "The scheduler must select a fitting phase instead of blocking behind the queue head."
Assert-Contains -Actual $parallelScript -Expected '-AvailableResourceClassSlots $availableResourceClassSlots' -Message "The scheduler must apply explicit resource-class concurrency limits while selecting fitting phases."
Assert-Contains -Actual $parallelScript -Expected 'resource-class limits cannot exceed the maximum worker count' -Message "Invalid resource-class concurrency limits must fail closed."
Assert-Contains -Actual $parallelScript -Expected '$Pending[$index].SchedulingDeferrals -ge 1' -Message "Backfill must reserve a later fitting opportunity for bypassed phases."
Assert-Contains -Actual $parallelScript -Expected 'VERIFY_CHILD_TIMEOUT name=$($result.Name)' -Message "Parallel timeouts must emit structured watchdog evidence."
Assert-Contains -Actual $verifyScript -Expected '-TimeoutSeconds $profile.TimeoutSeconds' -Message "Every required lane must execute only with its checked-in profile-owned timeout."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMinimumTimeoutHeadroomSeconds = 120' -Message "Required test lanes must retain explicit measured timeout headroom."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateExtendedTestTimeoutSeconds = 720' -Message "Startup must retain the shared bounded extended child timeout."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGatePersistenceTestTimeoutSeconds = 840' -Message "Persistence must retain its explicit bounded child-timeout maximum."
Assert-Contains -Actual $scheduleScript -Expected '$profile.Name -ceq $script:VerificationRequiredGatePersistenceTestName' -Message "Persistence must use a dedicated timeout-policy branch rather than widening the shared Startup ceiling."
Assert-Contains -Actual $verifyScript -Expected 'Get-ProjectCoverageIsolation' -Message "Every test project must execute from isolated exact-build copies."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot $lane.Name) -Configuration $Configuration -TargetFramework $targetFramework' -Message "Every lane must preserve its bin/<Configuration>/<TargetFramework> AppContext suffix."
Assert-Contains -Actual $verifyScript -Expected 'Copy-VerifiedDirectoryFromManifest -SourceDirectory $pristineDirectory -SourceManifest $pristineManifest -DestinationDirectory $laneDirectory' -Message "Every lane copy must use and verify the already authenticated pristine manifest."
Assert-Contains -Actual $verifyScript -Expected '$coverageChildProcessTestProjects = @(' -Message "External-child coverage projects must remain a statically inspectable inventory."
Assert-Contains -Actual $verifyScript -Expected 'if (-not $SkipCoverage -and $coverageChildProcessTestProjects.Contains($TestProject.Name)) {' -Message "Every declared external-child coverage project must receive its immutable pristine source."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory' -Message "External-child coverage must receive a process-scoped immutable source."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationDirectoryManifest -Expected $isolation.PristineManifest -Directory $isolation.PristineDirectory' -Message "Every verifier run must re-hash the immutable pristine source after all child processes exit."
Assert-Contains -Actual $coverageChildProcess -Expected 'AddExpectedTerminationVstestArguments' -Message "Intentional process-loss cases must retain an exact VSTest testhost path instead of a custom executable helper."
Assert-Contains -Actual $coverageChildProcess -Expected 'startInfo.ArgumentList.Add(isolatedPath);' -Message "Expected-termination VSTest must read the immutable pristine test assembly directly."
Assert-Contains -Actual $admissionStoreTest -Expected '[Collection(Verification.ProcessEnvironmentCollection.Name)]' -Message "Admission-store qualification must retain the class-wide process fence while its coverage-bearing writer children execute."
Assert-Contains -Actual $admissionStoreFixture -Expected '"crash-proof" or "crash-primary" or "crash-trust" => true' -Message "Only the three admitted abrupt-loss modes may omit an impossible child coverage report."
Assert-Contains -Actual $admissionStoreFixture -Expected '"writer" => false' -Message "Successful cross-process writers must remain distinct from intentional crash workers."
Assert-Contains -Actual $admissionStoreFixture -Expected 'if (mode == "writer")' -Message "Successful cross-process writers must use the direct apphost route."
Assert-Contains -Actual $admissionStoreFixture -Expected '"governed-loop-admission-writer"' -Message "Successful cross-process writers must invoke the shared apphost operation."
Assert-Contains -Actual $admissionStoreFixture -Expected 'AddExpectedTerminationVstestArguments(startInfo, typeof(GovernedLoopAdmissionStoreCrossProcessHostTests).Assembly.Location, CrossProcessHostTestName)' -Message "The crash-only route must execute the exact isolated xUnit worker identity."
Assert-Contains -Actual $admissionStoreHostTest -Expected 'public Task Cross_process_admission_store_host() => RunCrossProcessHostAsync();' -Message "The isolated child worker must remain discoverable in canonical inventory."
Assert-Contains -Actual $admissionWriterHost -Expected 'await File.WriteAllTextAsync(ready, "ready");' -Message "The apphost writer must preserve the existing readiness handshake."
Assert-Contains -Actual $persistenceTestProject -Expected '<Import Project="..\EmbodySense.CancellationHost\CancellationHostTestFixture.targets" />' -Message "Persistence qualification must carry the authenticated cancellation-host bundle into every isolated lane."
Assert-Contains -Actual $cancellationHostProcess -Expected 'var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "CancellationHost");' -Message "Cancellation children must execute only from the authenticated isolated fixture bundle."
Assert-Contains -Actual $cancellationHostProcess -Expected '"EmbodySense.CancellationHost.runtimeconfig.json"' -Message "Cancellation-host launch must fail closed when any runtime bundle component is missing."
Assert-Contains -Actual $cancellationHostProcess -Expected 'internal static Process StartAppHost' -Message "The successful admission writer must have one explicit authenticated apphost route."
Assert-Contains -Actual $cancellationHostProcess -Expected 'var startInfo = CreateStartInfo("dotnet")' -Message "Existing cancellation and process-loss fixtures must retain their reviewed dotnet exec semantics."
Assert-Contains -Actual $cancellationHostProcess -Expected 'startInfo.ArgumentList.Add("exec")' -Message "Existing cancellation and process-loss fixtures must invoke the dedicated host through dotnet exec."
Assert-Contains -Actual $admissionStoreFixture -Expected 'CancellationHostProcess.StartAppHost(' -Message "Only the successful admission writer may use the explicit apphost optimization."
Assert-Contains -Actual $scheduleStoreTest -Expected 'public async Task Cross_process_schedule_create_host()' -Message "The canonical schedule worker Fact must remain discoverable in the Persistence inventory."
Assert-Contains -Actual $scheduleStoreTest -Expected 'CancellationHostProcess.StartAppHostOwned(' -Message "Successful ScheduleStore workers must use the job-owned direct apphost route."
Assert-Contains -Actual $scheduleStoreTest -Expected 'if (crashBoundary is null)' -Message "ScheduleStore apphost optimization must be limited to successful workers."
Assert-Contains -Actual $scheduleStoreTest -Expected 'AddExpectedTerminationVstestArguments(startInfo, typeof(ScheduleStoreTests).Assembly.Location, CrossProcessHostTestName)' -Message "ScheduleStore crash-boundary workers must retain the exact expected-termination VSTest route."
Assert-Contains -Actual $scheduleStoreHost -Expected 'internal static async Task<int> RunAsync(' -Message "ScheduleStore cross-process logic must live in the named shared fixture."
Assert-Contains -Actual $scheduleStoreHost -Expected 'operation is not ("create" or "compare-exchange" or "compare-exchange-current")' -Message "The shared schedule worker must validate its bounded operation vocabulary."
Assert-Contains -Actual $cancellationHostProgram -Expected '["schedule-store", var scheduleWorkspaceRoot' -Message "The cancellation host must expose the explicit schedule-store apphost operation."
Assert-Contains -Actual $cancellationHostProject -Expected '..\Shared\ScheduleStoreCrossProcessHost.cs' -Message "The cancellation host must compile the shared schedule worker."
Assert-Contains -Actual $persistenceTestProject -Expected '..\Shared\ScheduleStoreCrossProcessHost.cs' -Message "Persistence tests must compile the same shared schedule worker."
Assert-Contains -Actual $cancellationHostProcess -Expected 'internal static CrossProcessProcess StartAppHostOwned' -Message "Direct schedule workers must be job-owned for bounded cleanup."
Assert-True -Condition ([regex]::Matches($humanReviewOrderedReleaseTest, 'CancellationHostProcess\.StartAppHostOwned\("human-review-ordered-effect-race"').Count -eq 2) -Message "The approved Human Review effect race must use exactly two owned apphost workers."
Assert-True -Condition ($humanReviewOrderedReleaseTest.IndexOf('CancellationHostProcess.Start("human-review-ordered-effect-race"', [StringComparison]::Ordinal) -lt 0) -Message "The approved Human Review effect race must not use the unowned dotnet-exec route."
Assert-Contains -Actual $humanReviewOrderedReleaseTest -Expected 'new CrossProcessReadinessChild("first"' -Message "The Human Review effect race must expose the first child through bounded readiness diagnostics."
Assert-Contains -Actual $humanReviewOrderedReleaseTest -Expected 'CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("human-review-ordered-effect-race"' -Message "The Human Review effect race must wait for both children through shared readiness diagnostics."
Assert-Contains -Actual $humanReviewOrderedReleaseTest -Expected 'await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));' -Message "The Human Review effect race must preserve its bounded release/completion wait."
Assert-Contains -Actual $humanReviewOrderedReleaseHost -Expected 'new HumanReviewOrderedReleaseRaceGateStore(store, readyPath, releasePath)' -Message "The Human Review effect race must synchronize at its test-only whole-run compare-exchange store."
Assert-Contains -Actual $humanReviewOrderedReleaseHost -Expected 'releaseStore ?? store' -Message "Only the Human Review effect-race release service may receive the synchronization wrapper."
Assert-True -Condition ($humanReviewOrderedReleaseAuthority.IndexOf('readyPath', [StringComparison]::Ordinal) -lt 0 -and $humanReviewOrderedReleaseAuthority.IndexOf('releasePath', [StringComparison]::Ordinal) -lt 0) -Message "Human Review effect-race readiness must not be misclassified as authority-source entry."
Assert-Contains -Actual $humanReviewOrderedReleaseRaceGateStore -Expected 'public async Task<CustomLoopRunStoreResult> UpdateAsync(' -Message "The Human Review effect race must synchronize at the existing whole-run compare-exchange call."
Assert-Contains -Actual $humanReviewOrderedReleaseRaceGateStore -Expected 'Interlocked.Exchange(ref _barrierEntered, 1) == 0' -Message "Each Human Review race worker must enter the compare-exchange barrier exactly once."
Assert-Contains -Actual $humanReviewOrderedReleaseRaceGateStore -Expected 'GetAsync(string runId, CancellationToken cancellationToken = default) => inner.GetAsync(runId, cancellationToken);' -Message "Human Review race readiness must not be emitted from a pre-compare-exchange canonical read."
Assert-Contains -Actual $humanReviewOrderedReleaseRaceGateStore -Expected 'await File.WriteAllTextAsync(readyPath, "ready", cancellationToken);' -Message "A Human Review race worker may report ready only when it reaches the compare-exchange barrier."
Assert-Contains -Actual $humanReviewOrderedReleaseRaceGateStore -Expected 'return await inner.UpdateAsync(run, expectedLifecycleVersion, cancellationToken);' -Message "The Human Review race barrier must preserve the canonical persistence compare-exchange."
Assert-Contains -Actual $humanReviewOrderedReleaseRaceGateStore -Expected 'WaitForFileAsync(releasePath, TimeSpan.FromSeconds(30), cancellationToken)' -Message "The Human Review race barrier must preserve the bounded functional-child release wait."
Assert-Contains -Actual $humanReviewOrderedReleaseTest -Expected 'await AssertExpectedHostLossAsync(' -Message "Crash-boundary Human Review workers must retain their existing process-loss assertion helper."
Assert-Contains -Actual $humanReviewOrderedReleaseTest -Expected '"human-review-ordered-effect-process-loss"' -Message "Crash-boundary Human Review workers must retain their existing process-loss mode."
Assert-Contains -Actual $humanReviewOrderedReleaseTest -Expected 'using var process = CancellationHostProcess.Start([command, .. arguments]);' -Message "Unchanged Human Review crash/restart helpers must retain the existing raw cancellation-host route."
Assert-Contains -Actual $reconciliationProbeProcessTest -Expected 'public async Task Cross_process_probe_worker()' -Message "The reconciliation probe worker Fact must remain discoverable in the Persistence inventory."
Assert-Contains -Actual $reconciliationProbeProcessTest -Expected 'CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(startInfo, assemblyPath, testName);' -Message "Reconciliation probe crash workers must retain the exact expected-termination VSTest route."
Assert-Contains -Actual $reconciliationProbeProcessTest -Expected 'CoverageChildProcessAssembly.AddCoordinationOnlyVstestArguments(startInfo, assemblyPath, testName);' -Message "Successful reconciliation probe workers must remain report-free coordination children whose production paths are covered by the parent lane."
Assert-Contains -Actual $verifyScript -Expected 'Resolve-VerificationPhysicalTempRoot -RunnerTemp $env:RUNNER_TEMP -SystemTempPath ([IO.Path]::GetTempPath())' -Message "Hosted verification must select the runner-owned ephemeral volume with a local fallback."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationLaneFixturePath -PhysicalTempRoot $verificationPhysicalTempRoot' -Message "Lane fixture isolation must remain short, disjoint, and outside retained repository artifacts."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"' -Message "Every project lane must receive a disjoint process-scoped catalog trust root."

$expectedCoverageChildProcessProjects = @(
    "EmbodySense.Core.Persistence.Tests.csproj",
    "EmbodySense.Core.Startup.Tests.csproj"
)
$coverageChildProjectDeclaration = [regex]::Match($verifyScript, '(?ms)^\$coverageChildProcessTestProjects = @\(\r?\n(?<body>.*?)^\)')
Assert-True -Condition $coverageChildProjectDeclaration.Success -Message "The external-child coverage project inventory must remain statically inspectable."
$declaredCoverageChildProjects = @([regex]::Matches($coverageChildProjectDeclaration.Groups["body"].Value, '^\s+"(?<name>[^"]+)",?\r?$', [Text.RegularExpressions.RegexOptions]::Multiline))
Assert-True -Condition ($declaredCoverageChildProjects.Count -eq $expectedCoverageChildProcessProjects.Count) -Message "External-child coverage must retain exactly the Persistence and Startup test projects."
for ($index = 0; $index -lt $expectedCoverageChildProcessProjects.Count; $index++) {
    Assert-True -Condition ($declaredCoverageChildProjects[$index].Groups["name"].Value -ceq $expectedCoverageChildProcessProjects[$index]) -Message "External-child coverage project order and names must remain deterministic."
}

foreach ($tempVariable in @("TEMP", "TMP", "TMPDIR")) {
    Assert-Contains -Actual $verifyScript -Expected "$tempVariable = `$laneFixtureRoot" -Message "Every lane and descendant must use the fast isolated '$tempVariable' fixture root."
}
Assert-Contains -Actual $verifyScript -Expected 'Remove-Item -LiteralPath $laneFixtureRoot -Recurse -Force' -Message "Lane fixture roots must be cleaned after ordinary verifier completion."
Assert-Contains -Actual $verifyScript -Expected '"vstest", $Lane.AssemblyPath' -Message "Test lanes must execute isolated assemblies."
Assert-Contains -Actual $laneScript -Expected 'if ($TestProject.Name -eq "EmbodySense.Core.Startup.Tests.csproj") {' -Message "Only Startup may declare the approved two-lane partition."
Assert-True -Condition ([regex]::Matches($laneScript, 'New-VerificationTestLane -Name "all"').Count -eq 1 -and [regex]::Matches($laneScript, 'New-VerificationTestLane -Name "remainder"').Count -eq 2 -and [regex]::Matches($laneScript, 'New-VerificationTestLane -Name "nested-process"').Count -eq 2) -Message "The verifier must retain one general lane declaration, the default two Startup lanes, and one exact owner for each hosted Startup component lane."
Assert-Contains -Actual $laneScript -Expected '[switch]$NestedProcessOnly' -Message "The nested-process component must select its exact Startup fixture lane through an explicit lane contract."
Assert-Contains -Actual $laneScript -Expected '[switch]$SolutionCoreOnly' -Message "The solution component must select the disjoint Startup remainder through an explicit lane contract."
Assert-Contains -Actual $verifyScript -Expected '-NestedProcessOnly:($VerificationComponent -eq "NestedProcess") -SolutionCoreOnly:($VerificationComponent -eq "Solution")' -Message "Hosted components must pass disjoint Startup lane ownership into canonical discovery and execution."
foreach ($parallelAssemblyInfoPath in @(
    "tests\EmbodySense.Core.Persistence.Tests\AssemblyInfo.cs",
    "tests\EmbodySense.Core.Startup.Tests\AssemblyInfo.cs",
    "tests\EmbodySense.IntegrationTests\AssemblyInfo.cs",
    "tests\EmbodySense.Web.Tests\AssemblyInfo.cs"
)) {
    $parallelAssemblyInfo = Get-Content -LiteralPath (Join-Path $repoRoot $parallelAssemblyInfoPath) -Raw
    Assert-Contains -Actual $parallelAssemblyInfo -Expected '[assembly: CollectionBehavior(MaxParallelThreads = 2)]' -Message "Assembly-wide lane '$parallelAssemblyInfoPath' must retain the explicit two-thread xUnit ceiling."
}
Assert-Contains -Actual $startupRuntimeCollection -Expected '[CollectionDefinition(Name)]' -Message "Startup runtime wrappers must retain one shared serial xUnit collection."
foreach ($startupRuntimeWrapper in @(
    "CustomLoopRuntimeTestsAdmissionAndContext.cs",
    "CustomLoopRuntimeTestsDurabilityAndRecovery.cs",
    "CustomLoopRuntimeTestsPublicationAndConcurrency.cs",
    "GovernedLoopRuntimeTestsAdmissionAndBinding.cs",
    "GovernedLoopRuntimeTestsCompletionConstraints.cs",
    "GovernedLoopRuntimeTestsResumeAndAuthority.cs"
)) {
    $startupRuntimeWrapperSource = Get-Content -LiteralPath (Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Loops\Execution\$startupRuntimeWrapper") -Raw
    Assert-Contains -Actual $startupRuntimeWrapperSource -Expected '[Collection(LoopRuntimeIntegrationCollection.Name)]' -Message "Startup runtime wrapper '$startupRuntimeWrapper' must serialize shared file-backed runtime state."
}
Assert-Contains -Actual $startupNestedProcessTest -Expected '[Collection(LoopRuntimeIntegrationCollection.Name)]' -Message "The held runtime-authoring nested-process test must serialize with the restart fixtures."
Assert-Contains -Actual $startupFactoryTest -Expected 'public sealed partial class AgentRuntimeFactoryTests' -Message "The general AgentRuntime factory tests must remain one independently schedulable xUnit class."
foreach ($effectReconciliationFactoryTest in @($startupFactoryEffectReconciliationTest, $startupFactoryEffectReconciliationCoverageTest)) {
    Assert-Contains -Actual $effectReconciliationFactoryTest -Expected 'public sealed partial class AgentRuntimeReconciliationFactoryTests' -Message "Effect Reconciliation factory tests must remain a second independently schedulable xUnit class."
    Assert-True -Condition ($effectReconciliationFactoryTest.IndexOf('public sealed partial class AgentRuntimeFactoryTests', [StringComparison]::Ordinal) -lt 0) -Message "Effect Reconciliation factory tests must not rejoin the serialized general factory class."
}
Assert-True -Condition ($startupFactoryHumanReviewTests.Count -eq 12) -Message "Human Review and recovery factory tests must remain in the complete independently schedulable file group."
foreach ($humanReviewFactoryTest in $startupFactoryHumanReviewTests) {
    Assert-Contains -Actual $humanReviewFactoryTest -Expected 'public sealed partial class AgentRuntimeHumanReviewTests' -Message "Human Review and recovery factory tests must remain a third independently schedulable xUnit class."
    Assert-True -Condition ($humanReviewFactoryTest.IndexOf('public sealed partial class AgentRuntimeFactoryTests', [StringComparison]::Ordinal) -lt 0) -Message "Human Review and recovery factory tests must not rejoin the serialized general factory class."
}
Assert-Contains -Actual $persistenceEnvironmentCollection -Expected '[CollectionDefinition(Name, DisableParallelization = true)]' -Message "Persistence process-environment mutation must remain exclusive of all assembly tests."
Assert-Contains -Actual $persistenceCapabilityCatalogTest -Expected '[Collection(Verification.ProcessEnvironmentCollection.Name)]' -Message "Capability-catalog trust-root mutation must retain process-environment serialization."
Assert-Contains -Actual $admissionStoreHostTest -Expected '[Collection(ProcessEnvironmentCollection.Name)]' -Message "Coverage child-directory mutation must retain process-environment serialization."
foreach ($webSharedRuntimeTest in @(
    "CapabilityApiControllerTests.cs",
    "LoopApiControllerTests.cs",
    "LoopRunApiControllerTests.cs",
    "WebAgentRuntimeHostTests.cs",
    "WebApiControllerTests.cs",
    "WebSessionHubTests.cs"
)) {
    $webSharedRuntimeTestSource = Get-Content -LiteralPath (Join-Path $repoRoot "tests\EmbodySense.Web.Tests\$webSharedRuntimeTest") -Raw
    Assert-Contains -Actual $webSharedRuntimeTestSource -Expected '[Collection(EphemeralPortApiCollection.Name)]' -Message "Web runtime/API test '$webSharedRuntimeTest' must serialize shared default trust and host state inside the assembly-wide lane."
}
foreach ($assemblyProfile in @(
    'Name = "tests-EmbodySense.Core.Persistence.Tests-all"; EstimatedDurationSeconds = 720; TimeoutSeconds = 840; Weight = 6; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Core.Startup.Tests-remainder"; EstimatedDurationSeconds = 560; TimeoutSeconds = 720; Weight = 6; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Core.Startup.Tests-nested-process"; EstimatedDurationSeconds = 180; TimeoutSeconds = 600; Weight = 12; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Web.Tests-all"; EstimatedDurationSeconds = 210; TimeoutSeconds = 600; Weight = 3; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.IntegrationTests-all"; EstimatedDurationSeconds = 180; TimeoutSeconds = 600; Weight = 3; ResourceClass = "ProcessHeavy"'
)) {
    Assert-Contains -Actual $scheduleScript -Expected $assemblyProfile -Message "Internally parallel assembly gates must retain exact conservative process-heavy scheduling profiles."
}
foreach ($assemblyName in @("EmbodySense.Cli.Command.Tests", "EmbodySense.Core.Application.Tests", "EmbodySense.Core.Clients.Tests", "EmbodySense.Core.Common.Tests", "EmbodySense.Core.Persistence.Tests", "EmbodySense.E2ETests", "EmbodySense.IntegrationTests", "EmbodySense.Web.Tests")) {
    Assert-Contains -Actual $scheduleScript -Expected "Name = `"tests-$assemblyName-all`";" -Message "Every production test assembly must have exactly one checked-in required-gate profile."
}
Assert-True -Condition ($scheduleScript.IndexOf('Name = "tests-EmbodySense.Core.Startup.Tests-all"', [StringComparison]::Ordinal) -lt 0) -Message "Startup must not retain its stale all-lane profile."
foreach ($retiredLane in @("loop-execution-custom-runtime", "loop-execution-governed-runtime", "contextual-roles", "codex-app-server", "runtime-host", "remainder-triggers")) {
    Assert-True -Condition ($laneScript.IndexOf("New-VerificationTestLane -Name `"$retiredLane`"", [StringComparison]::Ordinal) -lt 0) -Message "Assembly-wide execution must not retain report-amplifying lane '$retiredLane'."
}
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateResourceCapacity = 12' -Message "Required gates must retain twelve logical resource units independently of the three-process host ceiling."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMaximumProcessHeavyWorkers = 2' -Message "Required gates must enforce an explicit two-process-heavy concurrency ceiling."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMaximumCpuBoundWorkers = 1' -Message "Required gates must enforce an explicit one-CPU-bound concurrency ceiling."
Assert-Contains -Actual $scheduleScript -Expected 'Weight = 3; ResourceClass = "ProcessHeavy"' -Message "Process-heavy required gates must retain their evidence-backed logical weight."
Assert-Contains -Actual $scheduleScript -Expected '"ProcessHeavy" { 3; break }' -Message "Required-gate profile validation must reject underweighted process-heavy gates."
Assert-Contains -Actual $scheduleScript -Expected 'Name = "format-whitespace"; EstimatedDurationSeconds = 35; TimeoutSeconds = 240; Weight = 2; ResourceClass = "CpuBound"' -Message "Whitespace formatting must retain one checked-in CPU-bound required-gate profile."
Assert-Contains -Actual $scheduleScript -Expected 'Name = "format-naming-style"; EstimatedDurationSeconds = 65; TimeoutSeconds = 240; Weight = 2; ResourceClass = "CpuBound"' -Message "Naming/style formatting must retain one checked-in CPU-bound required-gate profile."
Assert-Contains -Actual $verifyScript -Expected 'Add-ProfiledRequiredGatePhase -Name "format-whitespace"' -Message "Whitespace formatting must overlap only immutable required-gate test execution."
Assert-Contains -Actual $verifyScript -Expected 'Add-ProfiledRequiredGatePhase -Name "format-naming-style"' -Message "Naming/style formatting must overlap only immutable required-gate test execution."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationRequiredGateScheduleProfile -Name $Name' -Message "Every required gate must obtain checked-in duration and resource metadata by exact name."
Assert-Contains -Actual $verifyScript -Expected '-EstimatedDurationSeconds $profile.EstimatedDurationSeconds -Weight $profile.Weight -ResourceClass $profile.ResourceClass' -Message "Every required gate must pass its exact checked-in scheduler profile."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases)' -Message "The complete required gate plan must fail closed before execution when a profile is missing or mismatched."
Assert-Contains -Actual $scheduleScript -Expected '$actualProcessCeiling = [Math]::Min(3, [Math]::Min($script:VerificationRequiredGateResourceCapacity, $HardwareProcessorCount))' -Message "Required gates must separate twelve logical resource units from the hard three-process execution ceiling."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min($MaximumTestWorkers, $actualProcessCeiling)' -Message "Required gates must preserve lower explicit worker requests without bypassing the three-process ceiling."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers $MaximumTestWorkers -HardwareProcessorCount $hardwareProcessorCount' -Message "Required gate execution must use the behavior-tested worker derivation."
Assert-Contains -Actual $verifyScript -Expected '$effectiveRequiredGateMaximumProcessHeavyWorkers = [Math]::Min($requiredGateMaximumProcessHeavyWorkers, $requiredGateMaximumWorkers)' -Message "Low-core execution must cap the process-heavy limit at the effective worker ceiling."
Assert-Contains -Actual $verifyScript -Expected '$effectiveRequiredGateMaximumCpuBoundWorkers = [Math]::Min($requiredGateMaximumCpuBoundWorkers, $requiredGateMaximumWorkers)' -Message "Low-core execution must cap the CPU-bound limit at the effective worker ceiling."
Assert-Contains -Actual $verifyScript -Expected 'maximum_process_heavy=$effectiveRequiredGateMaximumProcessHeavyWorkers maximum_cpu_bound=$effectiveRequiredGateMaximumCpuBoundWorkers scheduling=singleton-class-backlog-priority-lpt' -Message "The required-gate plan must report effective limits and singleton-class backlog-priority scheduling."
Assert-Contains -Actual $verifyScript -Expected '-MaximumProcessHeavyWorkers $effectiveRequiredGateMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers $effectiveRequiredGateMaximumCpuBoundWorkers' -Message "Required gate execution must apply both effective fail-closed resource-class limits."
Assert-Contains -Actual $parallelScript -Expected '$running.Count -lt $MaximumWorkers' -Message "Logical resource capacity cannot bypass the explicit child-process ceiling."
Assert-Contains -Actual $verifyScript -Expected 'identity=TestCase.Id partition_identity=XunitTestCaseUniqueID' -Message "Stable inventory identities must remain explicit."
Assert-Contains -Actual $verifyScript -Expected 'verify-test-partition.ps1' -Message "Canonical discovery and declarative lane selection must be reconciled."
Assert-Contains -Actual $verifyScript -Expected 'Write-CoverageManifest' -Message "Coverage must be bound to an exact fresh report manifest."
Assert-Contains -Actual $verifyScript -Expected 'kind=reconciliation' -Message "Inventory and coverage aggregation must overlap safely."
Assert-Contains -Actual $verifyScript -Expected '-Name "git-diff-check"' -Message "The canonical verifier must retain git diff validation."
Assert-Contains -Actual $verifyScript -Expected '-Name "frontend-preflight"' -Message "The canonical verifier must retain frontend validation exactly once behind its npm install dependency."
Assert-Contains -Actual $verifyScript -Expected 'VERIFY_COMPLETE schema_version=1 status=passed' -Message "A successful standard run must emit exact terminal evidence."
Assert-Contains -Actual $verifyScript -Expected '$normalPullRequestVerification = $VerificationComponent -eq "Full" -and $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly' -Message "The default Full component must retain the canonical preflight path."
Assert-Contains -Actual $verifyScript -Expected 'function Invoke-StaticVerificationContracts {' -Message "The static component must have one bounded execution owner."
Assert-True -Condition ($verifyScript.IndexOf(' -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") | Out-Null', [StringComparison]::Ordinal) -lt 0) -Message "Static contract phase completions must remain in the watchdog evidence stream for fan-in authentication."
Assert-Contains -Actual $verifyScript -Expected 'Write-Output "VERIFY_COMPLETE schema_version=1 component=static-contracts status=passed elapsed_seconds=$elapsedText"' -Message "The static component must emit identity-bearing terminal evidence."
Assert-Contains -Actual $verifyScript -Expected 'Write-Output "VERIFY_COMPLETE schema_version=1 component=solution status=passed elapsed_seconds=$elapsedText"' -Message "The solution component must emit identity-bearing terminal evidence."
Assert-Contains -Actual $verifyScript -Expected '"Solution" {' -Message "The solution component must own its exact nine-lane required-gate profile set."
Assert-Contains -Actual $verifyScript -Expected '"NestedProcess" {' -Message "The nested-process component must own only its exact one-lane required-gate profile set."
Assert-Contains -Actual $verifyScript -Expected '"tests-EmbodySense.Core.Startup.Tests-nested-process", "git-diff-check", "format-whitespace", "format-naming-style"' -Message "Solution scheduling must exclude only the nested lane and static profiles."
Assert-Contains -Actual $verifyScript -Expected '"tests-EmbodySense.Core.Persistence.Tests-all", "tests-EmbodySense.Core.Startup.Tests-remainder"' -Message "Nested scheduling must exclude all non-nested test profiles rather than silently running the full suite."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases) -ExcludedNames $excludedRequiredGateNames' -Message "Component scheduling must be validated against the reduced but exact required-gate profile set."
Assert-Contains -Actual $verifyScript -Expected 'if ($VerificationComponent -eq "Solution" -or $VerificationComponent -eq "NestedProcess")' -Message "Both partial components must collect reports without applying the global coverage floor before fan-in."
Assert-True -Condition ($phaseScript.IndexOf('$process.WaitForExit()', [StringComparison]::Ordinal) -lt 0) -Message "Sequential phase execution must not reintroduce an unbounded process wait after timeout or normal exit."
Assert-Contains -Actual $phaseScript -Expected '$processExitedAfterStop = $process.HasExited -or $process.WaitForExit(5000)' -Message "Timeout cleanup must confirm process exit only through a bounded post-kill wait."
Assert-Contains -Actual $phaseScript -Expected '[Threading.Tasks.Task]::WaitAll($captureTasks, $TimeoutMilliseconds)' -Message "Redirected output drain must remain bounded even when descendants retain inherited pipe handles."

$phaseBehaviorRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-phase-output-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $phaseBehaviorRoot | Out-Null
try {
    $blockedOutput = [Threading.Tasks.TaskCompletionSource[string]]::new()
    $blockedError = [Threading.Tasks.TaskCompletionSource[string]]::new()
    $blockedCaptureLog = Join-Path $phaseBehaviorRoot "blocked-capture.log"
    Assert-True -Condition (-not (Write-VerificationPhaseCapturedOutput -OutputPath $blockedCaptureLog -StandardOutputTask $blockedOutput.Task -StandardErrorTask $blockedError.Task -TimeoutMilliseconds 50)) -Message "Sequential phase output capture must return within its bounded drain window when inherited pipe handles remain open."
    Assert-Contains -Actual (Get-Content -LiteralPath $blockedCaptureLog -Raw) -Expected "did not close within 50 milliseconds" -Message "A bounded output-drain failure must retain actionable diagnostics."

    $outputScript = Join-Path $phaseBehaviorRoot "output.ps1"
    [IO.File]::WriteAllText($outputScript, "Write-Output 'stdout-evidence'; [Console]::Error.WriteLine('stderr-evidence')", [Text.UTF8Encoding]::new($false))
    $outputLog = Join-Path $phaseBehaviorRoot "output.log"
    Invoke-VerificationPhase -Name "phase-output" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-File", $outputScript) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $outputLog
    $outputText = Get-Content -LiteralPath $outputLog -Raw
    Assert-Contains -Actual $outputText -Expected "stdout-evidence" -Message "Sequential phase output capture must retain stdout."
    Assert-Contains -Actual $outputText -Expected "stderr-evidence" -Message "Sequential phase output capture must retain stderr."

    $silentScript = Join-Path $phaseBehaviorRoot "silent.ps1"
    [IO.File]::WriteAllText($silentScript, "exit 0", [Text.UTF8Encoding]::new($false))
    $silentLog = Join-Path $phaseBehaviorRoot "silent.log"
    Invoke-VerificationPhase -Name "phase-silent" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-File", $silentScript) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $silentLog
    Assert-True -Condition ((Get-Item -LiteralPath $silentLog).Length -eq 0) -Message "A successful silent sequential phase must retain a zero-byte diagnostic log."

    $failedScript = Join-Path $phaseBehaviorRoot "failed.ps1"
    [IO.File]::WriteAllText($failedScript, "Write-Output 'failure-evidence'; exit 7", [Text.UTF8Encoding]::new($false))
    $failedLog = Join-Path $phaseBehaviorRoot "failed.log"
    try {
        Invoke-VerificationPhase -Name "phase-failed" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-File", $failedScript) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $failedLog
        throw "Expected nonzero phase failure."
    }
    catch {
        Assert-Contains -Actual (Get-Content -LiteralPath $failedLog -Raw) -Expected "failure-evidence" -Message "A failed sequential phase must retain available diagnostics."
    }

    $redirectedTimeoutChildScript = Join-Path $phaseBehaviorRoot "redirected-timeout-child.ps1"
    $redirectedTimeoutRunnerScript = Join-Path $phaseBehaviorRoot "redirected-timeout-runner.ps1"
    $redirectedTimeoutReadyMarker = Join-Path $phaseBehaviorRoot "redirected-timeout-ready.marker"
    $redirectedTimeoutCompletedMarker = Join-Path $phaseBehaviorRoot "redirected-timeout-completed.marker"
    $redirectedTimeoutLog = Join-Path $phaseBehaviorRoot "redirected-timeout.log"
    $redirectedTimeoutReadinessSeconds = $functionalChildTimeoutSeconds - 5
    Assert-True -Condition ($redirectedTimeoutReadinessSeconds -eq 25) -Message "The redirected-output timeout runner must retain Windows startup headroom inside the functional child bound."
    @'
param([Parameter(Mandatory = $true)] [string]$ReadyMarker)

[Console]::Out.WriteLine("redirected-timeout-evidence")
[Console]::Out.Flush()
[IO.File]::WriteAllText($ReadyMarker, "ready", [Text.UTF8Encoding]::new($false))
while ($true) {
    Start-Sleep -Seconds 1
}
'@ | Set-Content -LiteralPath $redirectedTimeoutChildScript -Encoding utf8NoBOM
    @'
param(
    [Parameter(Mandatory = $true)] [string]$PhaseScriptPath,
    [Parameter(Mandatory = $true)] [string]$PowerShellExecutable,
    [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)] [string]$ChildScriptPath,
    [Parameter(Mandatory = $true)] [string]$ReadyMarker,
    [Parameter(Mandatory = $true)] [string]$TimeoutLog,
    [Parameter(Mandatory = $true)] [string]$CompletedMarker,
    [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. $PhaseScriptPath
try {
    Invoke-VerificationPhase -Name "phase-redirected-timeout" -FileName $PowerShellExecutable -Arguments @("-NoProfile", "-File", $ChildScriptPath, "-ReadyMarker", $ReadyMarker) -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $RepositoryRoot -OutputPath $TimeoutLog
    throw "Expected redirected-output phase timeout."
}
catch {
    if ($_.Exception.Message.IndexOf("timed out after $TimeoutSeconds seconds", [StringComparison]::Ordinal) -lt 0) {
        throw
    }
}

if (-not (Test-Path -LiteralPath $TimeoutLog -PathType Leaf)) {
    throw "The timed-out phase did not retain its redirected output log."
}
if ((Get-Content -LiteralPath $TimeoutLog -Raw).IndexOf("redirected-timeout-evidence", [StringComparison]::Ordinal) -lt 0) {
    throw "The timed-out phase log did not retain flushed redirected output."
}
[IO.File]::WriteAllText($CompletedMarker, "passed", [Text.UTF8Encoding]::new($false))
'@ | Set-Content -LiteralPath $redirectedTimeoutRunnerScript -Encoding utf8NoBOM
    $redirectedTimeoutRunnerArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { $redirectedTimeoutRunnerArguments += @("-ExecutionPolicy", "Bypass") }
    $redirectedTimeoutRunnerArguments += @("-File", $redirectedTimeoutRunnerScript, "-PhaseScriptPath", $phaseScriptPath, "-PowerShellExecutable", $powerShellExecutable, "-RepositoryRoot", $repoRoot, "-ChildScriptPath", $redirectedTimeoutChildScript, "-ReadyMarker", $redirectedTimeoutReadyMarker, "-TimeoutLog", $redirectedTimeoutLog, "-CompletedMarker", $redirectedTimeoutCompletedMarker, "-TimeoutSeconds", $functionalChildTimeoutSeconds)
    $redirectedTimeoutRunner = [Diagnostics.Process]::new()
    $redirectedTimeoutRunnerStarted = $false
    try {
        $redirectedTimeoutRunner.StartInfo = New-VerificationProcessStartInfo -FileName $powerShellExecutable -Arguments $redirectedTimeoutRunnerArguments -WorkingDirectory $repoRoot
        $redirectedTimeoutStopwatch = [Diagnostics.Stopwatch]::StartNew()
        $redirectedTimeoutRunnerStarted = $redirectedTimeoutRunner.Start()
        Assert-True -Condition $redirectedTimeoutRunnerStarted -Message "The redirected-output timeout runner must start."
        $readinessStopwatch = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $redirectedTimeoutReadyMarker -PathType Leaf) -and $readinessStopwatch.Elapsed -lt [TimeSpan]::FromSeconds($redirectedTimeoutReadinessSeconds)) {
            Start-Sleep -Milliseconds 100
        }
        $readinessStopwatch.Stop()
        Assert-True -Condition (Test-Path -LiteralPath $redirectedTimeoutReadyMarker -PathType Leaf) -Message "The redirected-output timeout child must publish its ready marker before the bounded readiness deadline."
        Assert-True -Condition $redirectedTimeoutRunner.WaitForExit(45000) -Message "The redirected-output timeout runner must complete within its bounded parent wait."
        $redirectedTimeoutStopwatch.Stop()
        Assert-True -Condition ($redirectedTimeoutRunner.ExitCode -eq 0) -Message "The redirected-output timeout runner must authenticate the expected phase timeout."
        Assert-True -Condition (Test-Path -LiteralPath $redirectedTimeoutCompletedMarker -PathType Leaf) -Message "The redirected-output timeout runner must publish completion only after validating retained phase evidence."
        Assert-Contains -Actual (Get-Content -LiteralPath $redirectedTimeoutLog -Raw) -Expected "redirected-timeout-evidence" -Message "A functional timed-out sequential phase must retain flushed redirected diagnostics."
        Assert-True -Condition ($redirectedTimeoutStopwatch.Elapsed -ge [TimeSpan]::FromSeconds($functionalChildTimeoutSeconds) -and $redirectedTimeoutStopwatch.Elapsed -lt [TimeSpan]::FromSeconds(45)) -Message "The redirected-output timeout proof must consume its functional bound but remain inside its honest 45-second qualification estimate. Actual: $([Math]::Round($redirectedTimeoutStopwatch.Elapsed.TotalSeconds, 3))."
    }
    finally {
        if ($redirectedTimeoutRunnerStarted -and -not $redirectedTimeoutRunner.HasExited) {
            Stop-VerificationProcessTree $redirectedTimeoutRunner
            [void]$redirectedTimeoutRunner.WaitForExit(5000)
        }
        $redirectedTimeoutRunner.Dispose()
    }
}
finally {
    Reset-VerificationPhaseState
    Remove-Item -LiteralPath $phaseBehaviorRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Assert-Contains -Actual $gitIgnore -Expected 'tests/VerificationResults/' -Message "Generated verifier diagnostics must remain uploadable without dirtying a local worktree."
Assert-Contains -Actual $gitIgnore -Expected 'tests/QualificationResults/' -Message "Generated qualification diagnostics must remain uploadable without dirtying a local worktree."
Assert-Contains -Actual $coverageEvidenceScript -Expected 'if (!fileLines.TryGetValue(line.Key, out existingHits) || line.Value > existingHits)' -Message "Split coverage must merge duplicate source lines by maximum hits in the authenticated reduction owner."
Assert-Contains -Actual $coverageScript -Expected 'Coverage report manifest contains duplicate report paths.' -Message "Duplicate report evidence must fail closed."
Assert-Contains -Actual $coverageScript -Expected 'missing, stale, or unexpected reports' -Message "Coverage manifest reconciliation must reject extra or missing files."
Assert-Contains -Actual $coverageScript -Expected '[switch]$CollectOnly' -Message "Partial verification coverage must expose an explicit collect-only mode."
Assert-Contains -Actual $verifyScript -Expected '$coverageArguments += "-CollectOnly"' -Message "Partial verification components must pass collect-only coverage through their child verifier."
Assert-Contains -Actual $pullRequestSettings -Expected '<TreatNoTestsAsError>true</TreatNoTestsAsError>' -Message "Required verification cannot accept an empty test selection."
Assert-Contains -Actual $stressSettings -Expected '<TreatNoTestsAsError>true</TreatNoTestsAsError>' -Message "Stress verification cannot accept an empty test selection."
Assert-Contains -Actual $stressSettings -Expected '<TestSessionTimeout>1500000</TestSessionTimeout>' -Message "Stress sessions must remain bounded."
Assert-Contains -Actual $maximumTest -Expected '[Trait(VerificationTier.TraitName, VerificationTier.Stress)]' -Message "The adversarial maximum test must remain in the stress tier."
Assert-Contains -Actual $maximumTest -Expected "Public_artifact_contract_round_trips_the_maximum_bounded_shape_below_fifteen_mebibytes" -Message "A required maximum-contract proof must remain visible."
Assert-Contains -Actual $retentionTest -Expected '[Trait(VerificationTier.TraitName, VerificationTier.Stress)]' -Message "The 10,000-operation case must remain in the stress tier."
Assert-Contains -Actual $stressWorkflow -Expected "schedule:" -Message "The stress tier must retain its scheduled owner."
Assert-Contains -Actual $stressWorkflow -Expected "-VerificationTier Stress" -Message "The scheduled workflow must invoke the stress tier."
Assert-Contains -Actual $stressWorkflow -Expected "if: always()" -Message "Stress diagnostics must be retained on failure."
Assert-Contains -Actual $verifyWorkflow -Expected "./scripts/verify-with-watchdog.ps1 -Configuration Release" -Message "Standard CI must enter through the external watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "github.event.pull_request.draft == false" -Message "Full verification must be a promotion gate for merge candidates and main."
Assert-Contains -Actual $verifyWorkflow -Expected "types: [opened, synchronize, reopened, ready_for_review, edited]" -Message "Every non-draft metadata edit must rerun substantive verification under the protected context."
Assert-Contains -Actual $verifyWorkflow -Expected "name: verify" -Message "Verification must always emit the exact protected context name."
Assert-Contains -Actual $verifyWorkflow -Expected 'group: verify-solution-${{ github.event.pull_request.number || github.ref }}' -Message "A newer promotion edge must cancel superseded solution verification work."
Assert-Contains -Actual $verifyWorkflow -Expected 'group: verify-contracts-${{ github.event.pull_request.number || github.ref }}' -Message "A newer promotion edge must cancel superseded contract verification work."
Assert-Contains -Actual $verifyWorkflow -Expected 'group: verify-nested-process-${{ github.event.pull_request.number || github.ref }}' -Message "A newer promotion edge must cancel superseded nested-process verification work."
Assert-Contains -Actual $verifyWorkflow -Expected "cancel-in-progress: true" -Message "Full verification must release its Windows runner when superseded."
$solutionJobIndex = $verifyWorkflow.IndexOf("  verify-solution:", [StringComparison]::Ordinal)
$solutionJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $solutionJobIndex, [StringComparison]::Ordinal)
$solutionJobConcurrencyIndex = $verifyWorkflow.IndexOf("    concurrency:", $solutionJobIndex, [StringComparison]::Ordinal)
$nestedJobIndex = $verifyWorkflow.IndexOf("  verify-nested-process:", [StringComparison]::Ordinal)
$nestedJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $nestedJobIndex, [StringComparison]::Ordinal)
$nestedJobConcurrencyIndex = $verifyWorkflow.IndexOf("    concurrency:", $nestedJobIndex, [StringComparison]::Ordinal)
$contractJobIndex = $verifyWorkflow.IndexOf("  verify-contracts:", [StringComparison]::Ordinal)
$contractJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $contractJobIndex, [StringComparison]::Ordinal)
$contractJobConcurrencyIndex = $verifyWorkflow.IndexOf("    concurrency:", $contractJobIndex, [StringComparison]::Ordinal)
$fanInJobIndex = $verifyWorkflow.IndexOf("  verify:", [StringComparison]::Ordinal)
$fanInJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $fanInJobIndex, [StringComparison]::Ordinal)
$fanInJobRunsOnIndex = $verifyWorkflow.IndexOf("    runs-on: windows-latest", $fanInJobIndex, [StringComparison]::Ordinal)
$fanInNeedsIndex = $verifyWorkflow.IndexOf("    needs: [verify-solution, verify-nested-process, verify-contracts]", $fanInJobIndex, [StringComparison]::Ordinal)
Assert-True -Condition ($solutionJobIndex -ge 0 -and $solutionJobConditionIndex -gt $solutionJobIndex -and $solutionJobConcurrencyIndex -gt $solutionJobConditionIndex -and $nestedJobIndex -gt $solutionJobIndex -and $nestedJobConditionIndex -gt $nestedJobIndex -and $nestedJobConcurrencyIndex -gt $nestedJobConditionIndex -and $contractJobIndex -gt $nestedJobIndex -and $contractJobConditionIndex -gt $contractJobIndex -and $contractJobConcurrencyIndex -gt $contractJobConditionIndex -and $fanInJobIndex -gt $contractJobIndex -and $fanInJobConditionIndex -gt $fanInJobIndex -and $fanInJobRunsOnIndex -gt $fanInJobIndex -and $fanInNeedsIndex -gt $fanInJobConditionIndex) -Message "Solution, nested-process, and contract cancellation must remain job-scoped behind non-draft eligibility, with a Windows final fan-in after all three children."
Assert-True -Condition ($verifyWorkflow.IndexOf("`nconcurrency:", [StringComparison]::Ordinal) -lt 0) -Message "Full verification must not use workflow-scoped cancellation for ineligible metadata edits."
Assert-True -Condition ($verifyWorkflow.IndexOf("-SkipCoverage", [StringComparison]::Ordinal) -lt 0) -Message "Promotion verification must retain exact coverage collection and reduction."
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 1500 -VerificationComponent Solution" -Message "The solution child must own build, lanes, inventory, and coverage behind the evidence-backed 1500-second watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 600 -VerificationComponent NestedProcess" -Message "The nested-process child must own the five Startup fixtures behind its bounded 600-second watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 600 -VerificationComponent StaticContracts" -Message "The static child must own all static contracts behind a bounded 600-second watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "uses: actions/download-artifact@v7" -Message "The protected fan-in must transport child artifacts explicitly."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-solution-diagnostics-${{ github.run_attempt }}' -Message "The solution evidence artifact must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-contract-diagnostics-${{ github.run_attempt }}' -Message "The static evidence artifact must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'cache: "npm"' -Message "Hosted static verification must restore the lockfile-keyed npm package cache."
Assert-Contains -Actual $verifyWorkflow -Expected 'cache-dependency-path: package-lock.json' -Message "Hosted npm cache identity must remain bound to the canonical dependency lockfile."
Assert-Contains -Actual $verifyWorkflow -Expected 'package-manager-cache: false' -Message "Explicit npm cache configuration must not be replaced by package metadata inference."
Assert-True -Condition ([regex]::Matches($verifyWorkflow, [regex]::Escape('cache: "npm"')).Count -eq 1) -Message "The npm cache must have exactly one hosted verification owner."
Assert-True -Condition ([regex]::Matches($verifyWorkflow, [regex]::Escape('cache-dependency-path: package-lock.json')).Count -eq 1) -Message "The canonical lockfile must be the sole hosted npm cache dependency path."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-solution-receipt-${{ github.run_attempt }}' -Message "The protected solution receipt must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-nested-process-receipt-${{ github.run_attempt }}' -Message "The protected nested-process receipt must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-contract-receipt-${{ github.run_attempt }}' -Message "The protected static receipt must bind to the current workflow attempt."
foreach ($solutionReceiptPath in @("verification-component-evidence.json", "verification-component-manifest.json", "verification-watchdog-evidence.json", "watchdog.log", "required-test-lanes.json", "required-test-partition.json", "required-execution-tests.json", "required-test-report.json", "coverage-manifest.json", "coverage-summary.json", "**/*.trx")) {
    Assert-Contains -Actual $verifyWorkflow -Expected "tests/VerificationResults/$solutionReceiptPath" -Message "The solution receipt must transport '$solutionReceiptPath'."
}
foreach ($nestedReceiptPath in @("verification-component-evidence.json", "verification-component-manifest.json", "verification-watchdog-evidence.json", "watchdog.log", "required-test-lanes.json", "required-test-partition.json", "required-execution-tests.json", "required-test-report.json", "coverage-manifest.json", "coverage-summary.json", "**/*.cobertura.xml", "**/*.trx")) {
    Assert-Contains -Actual $verifyWorkflow -Expected "tests/VerificationResults/$nestedReceiptPath" -Message "The nested-process receipt must transport '$nestedReceiptPath'."
}
foreach ($staticReceiptPath in @("verify-sdk-diagnostics.tests.ps1.log", "verify-preflight-overlap.tests.ps1.log", "verify-coverage.tests.ps1.log", "verify-bounded-phases.tests.ps1.log", "verify-parallel.tests.ps1.log", "verify-test-inventory.tests.ps1.log", "verify-watchdog.tests.ps1.log", "verify-promotion-fan-in.tests.ps1.log", "frontend-preflight.log", "restore-static.log", "format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) {
    Assert-Contains -Actual $verifyWorkflow -Expected "tests/VerificationResults/Logs/$staticReceiptPath" -Message "The static receipt must transport '$staticReceiptPath'."
}
Assert-Contains -Actual $verifyWorkflow -Expected "scripts/verify-promotion-fan-in.ps1" -Message "The protected fan-in must delegate evidence authentication to the repository verifier contract."
Assert-Contains -Actual $verifyWorkflow -Expected '-ExpectedRunId ''${{ github.run_id }}'' -ExpectedRunAttempt ''${{ github.run_attempt }}'' -SolutionResult ''${{ needs.verify-solution.result }}'' -NestedResult ''${{ needs.verify-nested-process.result }}'' -StaticResult ''${{ needs.verify-contracts.result }}''' -Message "The protected fan-in must authenticate the current run, attempt, and all three child results."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-solution-receipt-${{ github.run_attempt }}' -Message "The protected fan-in must download the small solution receipt rather than the full diagnostics artifact."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-contract-receipt-${{ github.run_attempt }}' -Message "The protected fan-in must download the small static receipt rather than the full diagnostics artifact."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-nested-process-receipt-${{ github.run_attempt }}' -Message "The protected fan-in must download the small nested-process receipt rather than the full diagnostics artifact."
Assert-Contains -Actual $verifyWorkflow -Expected 'ref: ${{ github.sha }}' -Message "The protected fan-in must check out the exact reviewed SHA before running its verifier."
Assert-Contains -Actual $browserWorkflow -Expected "github.event.pull_request.draft == false" -Message "Installed-browser verification must be a promotion gate for merge candidates and main."
Assert-Contains -Actual $browserWorkflow -Expected "types: [opened, synchronize, reopened, ready_for_review, edited]" -Message "Every non-draft metadata edit must rerun installed-browser verification under the protected context."
Assert-Contains -Actual $browserWorkflow -Expected "name: browser-e2e" -Message "Installed-browser verification must always emit the exact protected context name."
Assert-Contains -Actual $browserWorkflow -Expected 'group: browser-e2e-${{ github.event.pull_request.number || github.ref }}' -Message "A newer promotion edge must cancel superseded installed-browser work."
Assert-Contains -Actual $browserWorkflow -Expected "cancel-in-progress: true" -Message "Installed-browser verification must release its Windows runner when superseded."
$browserJobIndex = $browserWorkflow.IndexOf("  browser-e2e:", [StringComparison]::Ordinal)
$browserJobConditionIndex = $browserWorkflow.IndexOf("    if:", $browserJobIndex, [StringComparison]::Ordinal)
$browserJobConcurrencyIndex = $browserWorkflow.IndexOf("    concurrency:", $browserJobIndex, [StringComparison]::Ordinal)
Assert-True -Condition ($browserJobIndex -ge 0 -and $browserJobConditionIndex -gt $browserJobIndex -and $browserJobConcurrencyIndex -gt $browserJobConditionIndex) -Message "Browser cancellation must remain job-scoped behind non-draft eligibility."
Assert-True -Condition ($browserWorkflow.IndexOf("`nconcurrency:", [StringComparison]::Ordinal) -lt 0) -Message "Browser verification must not use workflow-scoped cancellation for ineligible metadata edits."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "types: [converted_to_draft]" -Message "Returning a pull request to draft must trigger cancellation of obsolete promotion work."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "name: cancel-obsolete-promotion" -Message "Draft demotion must emit one distinct non-required cancellation context."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "actions: write" -Message "Draft demotion requires narrowly scoped authority to cancel obsolete workflow runs."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "pull-requests: read" -Message "Draft demotion must re-read live pull-request eligibility before cancellation."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "uses: actions/github-script@v9" -Message "Draft demotion must use the bounded GitHub API cancellation path."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "github.rest.pulls.get" -Message "Draft demotion must re-read live pull-request state."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "if (!live.data.draft)" -Message "A stale demotion event must retain every newer promotion after the pull request is ready again."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected '["verify.yml", "browser-e2e.yml"]' -Message "Draft demotion may cancel only the two exhaustive promotion workflows."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "run.id < context.runId" -Message "Draft demotion must never cancel a workflow run newer than its own event run."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected 'run.status !== "completed"' -Message "Draft demotion must not rewrite terminal workflow evidence."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "pull.number === pullNumber" -Message "Draft demotion cancellation must remain scoped to the exact pull request."
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "github.rest.actions.cancelWorkflowRun" -Message "Draft demotion must cancel each authenticated older promotion run explicitly."
Assert-True -Condition ($promotionCancellationWorkflow.IndexOf("concurrency:", [StringComparison]::Ordinal) -lt 0 -and $promotionCancellationWorkflow.IndexOf("cancel-in-progress:", [StringComparison]::Ordinal) -lt 0) -Message "Draft demotion must not use unordered cross-workflow concurrency cancellation."
Assert-True -Condition ($promotionCancellationWorkflow.IndexOf("actions/checkout", [StringComparison]::Ordinal) -lt 0 -and $promotionCancellationWorkflow.IndexOf("actions/setup-", [StringComparison]::Ordinal) -lt 0) -Message "Draft demotion cancellation must not consume checkout or tool-setup time."
Assert-True -Condition ($promotionCancellationWorkflow.IndexOf("`n    name: verify`n", [StringComparison]::Ordinal) -lt 0 -and $promotionCancellationWorkflow.IndexOf("`n    name: browser-e2e`n", [StringComparison]::Ordinal) -lt 0) -Message "Draft demotion must not publish either required promotion context."
Assert-True -Condition ($verifyWorkflow.IndexOf('ref: ${{ github.event.pull_request.head.sha }}', [StringComparison]::Ordinal) -lt 0) -Message "Promotion verification must retain the generated merge-ref checkout it documents."
Assert-True -Condition ($browserWorkflow.IndexOf('ref: ${{ github.event.pull_request.head.sha }}', [StringComparison]::Ordinal) -lt 0) -Message "Installed-browser promotion must retain the generated merge-ref checkout it documents."
Assert-Contains -Actual $readme -Expected "GitHub's generated merge ref for the current reviewed head/base pair" -Message "README promotion authority must match the workflow checkout."
Assert-Contains -Actual $verificationDocumentation -Expected "generated merge-ref checkout" -Message "Verification documentation must distinguish exact-head qualification from merge-ref promotion."
Assert-Contains -Actual $qualificationWorkflow -Expected "workflow_dispatch:" -Message "Hosted qualification must be an explicit diagnostic action."
Assert-True -Condition ($qualificationWorkflow.IndexOf("pull_request:", [StringComparison]::Ordinal) -lt 0 -and $qualificationWorkflow.IndexOf("push:", [StringComparison]::Ordinal) -lt 0) -Message "Draft pushes must not consume hosted qualification capacity."
Assert-Contains -Actual $qualificationWorkflow -Expected "name: hosted-qualification" -Message "Hosted diagnostics must not impersonate a protected or automatic context."
Assert-Contains -Actual $qualificationWorkflow -Expected "cancel-in-progress: false" -Message "An explicitly dispatched diagnostic must not be cancelled by unrelated repository activity."
$qualificationJobIndex = $qualificationWorkflow.IndexOf("  qualification:", [StringComparison]::Ordinal)
$qualificationJobConcurrencyIndex = $qualificationWorkflow.IndexOf("    concurrency:", $qualificationJobIndex, [StringComparison]::Ordinal)
Assert-True -Condition ($qualificationJobIndex -ge 0 -and $qualificationJobConcurrencyIndex -gt $qualificationJobIndex) -Message "Hosted diagnostic serialization must remain job-scoped."
Assert-True -Condition ($qualificationWorkflow.IndexOf("`nconcurrency:", [StringComparison]::Ordinal) -lt 0) -Message "Hosted diagnostics must not introduce workflow-scoped cancellation."
Assert-Contains -Actual $qualificationWorkflow -Expected "git merge-base --is-ancestor `$env:BASE_SHA `$env:HEAD_SHA" -Message "Hosted qualification must authenticate its exact edge before execution."
Assert-Contains -Actual $qualificationWorkflow -Expected '-Qualification -BaseCommit ''${{ inputs.base_sha }}'' -HeadCommit ''${{ inputs.head_sha }}'' -Configuration Release -DeadlineSeconds 1680' -Message "Qualification must bind the dispatched exact edge under one 1680-second watchdog."
Assert-Contains -Actual $qualificationWorkflow -Expected "    timeout-minutes: 30" -Message "Hosted qualification must leave at least two minutes of setup and diagnostic-upload margin around its 1680-second child watchdog."
Assert-True -Condition ($qualificationWorkflow.IndexOf("run: ./scripts/verify.ps1", [StringComparison]::Ordinal) -lt 0) -Message "Qualification cannot bypass the watchdog."
Assert-True -Condition ($qualificationWorkflow.IndexOf("coverage.cobertura.xml", [StringComparison]::Ordinal) -lt 0) -Message "Qualification must not claim or upload absent coverage evidence."
Assert-Contains -Actual $codeqlWorkflow -Expected "types: [opened, synchronize, reopened, edited]" -Message "CodeQL must observe a retargeted pull request edge."
Assert-Contains -Actual $codeqlWorkflow -Expected 'name: Analyze ${{ matrix.language }}' -Message "Every metadata edit must rerun CodeQL under its stable analysis names."
Assert-Contains -Actual $dependencyReviewWorkflow -Expected "types: [opened, synchronize, reopened, edited]" -Message "Dependency review must observe a retargeted pull request edge."
Assert-Contains -Actual $dependencyReviewWorkflow -Expected "name: dependency-review" -Message "Every metadata edit must rerun dependency review under its protected context name."
foreach ($workflowText in @($verifyWorkflow, $browserWorkflow, $qualificationWorkflow, $codeqlWorkflow, $dependencyReviewWorkflow)) {
    Assert-True -Condition ($workflowText.IndexOf("metadata-edit", [StringComparison]::Ordinal) -lt 0) -Message "No workflow may replace a protected context with an unevaluated skipped metadata name."
}
Assert-True -Condition ($verifyWorkflow.IndexOf("run: ./scripts/verify.ps1", [StringComparison]::Ordinal) -lt 0) -Message "Standard CI cannot bypass the watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 1500" -Message "Promotion must have one explicit bounded twenty-five-minute certification window for the complete solution child."
Assert-Contains -Actual $verifyWorkflow -Expected "timeout-minutes: 30" -Message "Workflow setup and diagnostic upload must retain bounded margin outside the measured 1500-second promotion child."
Assert-Contains -Actual $verifyWorkflow -Expected "timeout-minutes: 15" -Message "The static child job must leave bounded setup and receipt-upload margin around its 600-second verifier."
Assert-True -Condition ($verifyWorkflow.IndexOf("run: ./tests/scripts/", [StringComparison]::Ordinal) -lt 0) -Message "Repository script tests must execute inside the measured verifier child."
foreach ($contractScript in @("verify-sdk-diagnostics.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1", "verify-promotion-fan-in.tests.ps1")) {
    Assert-Contains -Actual $verifyScript -Expected $contractScript -Message "The measured verifier must own '$contractScript'."
}
Assert-Contains -Actual $stressWorkflow -Expected "./tests/scripts/verify-coverage.tests.ps1" -Message "Scheduled stress verification must retain coverage merger contracts."

Write-Output "Bounded verifier contract tests passed ($assertionCount assertions)."
