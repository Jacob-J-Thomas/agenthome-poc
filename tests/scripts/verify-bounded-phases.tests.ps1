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
$admissionStoreTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\Admission\GovernedLoopAdmissionStoreTests.cs"
$persistenceEnvironmentCollectionPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Verification\ProcessEnvironmentCollection.cs"
$persistenceCapabilityCatalogTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Capabilities\FileCapabilityCatalogTrustProviderTests.cs"
$startupRuntimeCollectionPath = Join-Path $repoRoot "tests\EmbodySense.Core.Startup.Tests\Loops\Execution\LoopRuntimeIntegrationCollection.cs"
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
$admissionStoreTest = Get-Content -LiteralPath $admissionStoreTestPath -Raw
$persistenceEnvironmentCollection = Get-Content -LiteralPath $persistenceEnvironmentCollectionPath -Raw
$persistenceCapabilityCatalogTest = Get-Content -LiteralPath $persistenceCapabilityCatalogTestPath -Raw
$startupRuntimeCollection = Get-Content -LiteralPath $startupRuntimeCollectionPath -Raw

Assert-Contains -Actual $verifyScript -Expected '[ValidateSet("PullRequest", "Stress")]' -Message "The verifier must expose only the two owned tiers."
Assert-Contains -Actual $verifyScript -Expected '[string]$Configuration = "Release"' -Message "The canonical verifier must default to Release."
Assert-Contains -Actual $verifyScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))' -Message "The required gate must request bounded logical concurrency above the physical processor count."
Assert-Contains -Actual $watchdogScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))' -Message "The external watchdog must preserve the bounded logical worker request."
Assert-Contains -Actual $watchdogScript -Expected '[ValidateSet("Full", "Solution", "StaticContracts")]' -Message "The external watchdog must expose explicit full, solution, and static component modes."
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
Assert-Contains -Actual $verifyScript -Expected '$testLaneTimeoutSeconds = 480' -Message "Every required lane must fit inside the outer budget."
Assert-Contains -Actual $verifyScript -Expected 'Get-ProjectCoverageIsolation' -Message "Every test project must execute from isolated exact-build copies."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot $lane.Name) -Configuration $Configuration -TargetFramework $targetFramework' -Message "Every lane must preserve its bin/<Configuration>/<TargetFramework> AppContext suffix."
Assert-Contains -Actual $verifyScript -Expected 'Copy-VerifiedDirectoryFromManifest -SourceDirectory $pristineDirectory -SourceManifest $pristineManifest -DestinationDirectory $laneDirectory' -Message "Every lane copy must use and verify the already authenticated pristine manifest."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory' -Message "Persistence child-process coverage must receive a process-scoped immutable source."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationDirectoryManifest -Expected $isolation.PristineManifest -Directory $isolation.PristineDirectory' -Message "Every verifier run must re-hash the immutable pristine source after all child processes exit."
Assert-Contains -Actual $coverageChildProcess -Expected 'AddExpectedTerminationVstestArguments' -Message "Intentional process-loss cases must retain an exact VSTest testhost path instead of a custom executable helper."
Assert-Contains -Actual $coverageChildProcess -Expected 'startInfo.ArgumentList.Add(isolatedPath);' -Message "Expected-termination VSTest must read the immutable pristine test assembly directly."
Assert-Contains -Actual $admissionStoreTest -Expected '"crash-proof" or "crash-primary" or "crash-trust" => true' -Message "Only the three admitted abrupt-loss modes may omit an impossible child coverage report."
Assert-Contains -Actual $admissionStoreTest -Expected '"writer" => false' -Message "Successful cross-process writers must retain the report-producing coverage path."
Assert-Contains -Actual $admissionStoreTest -Expected 'AddExpectedTerminationVstestArguments(startInfo, typeof(GovernedLoopAdmissionStoreTests).Assembly.Location, CrossProcessHostTestName)' -Message "The crash-only route must execute the existing exact xUnit worker identity."
Assert-Contains -Actual $admissionStoreTest -Expected 'public async Task Cross_process_admission_store_host()' -Message "The existing child worker test ID must remain in canonical inventory."
Assert-Contains -Actual $verifyScript -Expected 'Resolve-VerificationPhysicalTempRoot -RunnerTemp $env:RUNNER_TEMP -SystemTempPath ([IO.Path]::GetTempPath())' -Message "Hosted verification must select the runner-owned ephemeral volume with a local fallback."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationLaneFixturePath -PhysicalTempRoot $verificationPhysicalTempRoot' -Message "Lane fixture isolation must remain short, disjoint, and outside retained repository artifacts."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"' -Message "Every project lane must receive a disjoint process-scoped catalog trust root."
foreach ($tempVariable in @("TEMP", "TMP", "TMPDIR")) {
    Assert-Contains -Actual $verifyScript -Expected "$tempVariable = `$laneFixtureRoot" -Message "Every lane and descendant must use the fast isolated '$tempVariable' fixture root."
}
Assert-Contains -Actual $verifyScript -Expected 'Remove-Item -LiteralPath $laneFixtureRoot -Recurse -Force' -Message "Lane fixture roots must be cleaned after ordinary verifier completion."
Assert-Contains -Actual $verifyScript -Expected '"vstest", $Lane.AssemblyPath' -Message "Test lanes must execute isolated assemblies."
Assert-Contains -Actual $laneScript -Expected 'return @((New-VerificationTestLane -Name "all"))' -Message "Each test assembly must execute through one exact stable-ID lane."
Assert-True -Condition ($laneScript.IndexOf('$TestProject.', [StringComparison]::Ordinal) -lt 0) -Message "Assembly-wide execution must not inspect project identity or retain project-specific sharding branches."
Assert-True -Condition ([regex]::Matches($laneScript, 'New-VerificationTestLane -Name "all"').Count -eq 1) -Message "The one-lane policy must have exactly one scheduler declaration."
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
Assert-Contains -Actual $persistenceEnvironmentCollection -Expected '[CollectionDefinition(Name, DisableParallelization = true)]' -Message "Persistence process-environment mutation must remain exclusive of all assembly tests."
Assert-Contains -Actual $persistenceCapabilityCatalogTest -Expected '[Collection(Verification.ProcessEnvironmentCollection.Name)]' -Message "Capability-catalog trust-root mutation must retain process-environment serialization."
Assert-Contains -Actual $admissionStoreTest -Expected '[Collection(Verification.ProcessEnvironmentCollection.Name)]' -Message "Coverage child-directory mutation must retain process-environment serialization."
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
    'Name = "tests-EmbodySense.Core.Persistence.Tests-all"; EstimatedDurationSeconds = 300; Weight = 6; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Core.Startup.Tests-all"; EstimatedDurationSeconds = 240; Weight = 6; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Web.Tests-all"; EstimatedDurationSeconds = 210; Weight = 3; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.IntegrationTests-all"; EstimatedDurationSeconds = 180; Weight = 3; ResourceClass = "ProcessHeavy"'
)) {
    Assert-Contains -Actual $scheduleScript -Expected $assemblyProfile -Message "Internally parallel assembly gates must retain exact conservative process-heavy scheduling profiles."
}
foreach ($assemblyName in @("EmbodySense.Cli.Command.Tests", "EmbodySense.Core.Application.Tests", "EmbodySense.Core.Clients.Tests", "EmbodySense.Core.Common.Tests", "EmbodySense.Core.Persistence.Tests", "EmbodySense.Core.Startup.Tests", "EmbodySense.E2ETests", "EmbodySense.IntegrationTests", "EmbodySense.Web.Tests")) {
    Assert-Contains -Actual $scheduleScript -Expected "Name = `"tests-$assemblyName-all`";" -Message "Every production test assembly must have exactly one checked-in required-gate profile."
}
foreach ($retiredLane in @("loop-execution-custom-runtime", "loop-execution-governed-runtime", "contextual-roles", "codex-app-server", "runtime-host", "remainder-triggers")) {
    Assert-True -Condition ($laneScript.IndexOf("New-VerificationTestLane -Name `"$retiredLane`"", [StringComparison]::Ordinal) -lt 0) -Message "Assembly-wide execution must not retain report-amplifying lane '$retiredLane'."
}
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateResourceCapacity = 12' -Message "Required gates must retain twelve logical resource units independently of the three-process host ceiling."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMaximumProcessHeavyWorkers = 2' -Message "Required gates must enforce an explicit two-process-heavy concurrency ceiling."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMaximumCpuBoundWorkers = 1' -Message "Required gates must enforce an explicit one-CPU-bound concurrency ceiling."
Assert-Contains -Actual $scheduleScript -Expected 'Weight = 3; ResourceClass = "ProcessHeavy"' -Message "Process-heavy required gates must retain their evidence-backed logical weight."
Assert-Contains -Actual $scheduleScript -Expected '"ProcessHeavy" { 3; break }' -Message "Required-gate profile validation must reject underweighted process-heavy gates."
Assert-Contains -Actual $scheduleScript -Expected 'Name = "format-whitespace"; EstimatedDurationSeconds = 35; Weight = 2; ResourceClass = "CpuBound"' -Message "Whitespace formatting must retain one checked-in CPU-bound required-gate profile."
Assert-Contains -Actual $scheduleScript -Expected 'Name = "format-naming-style"; EstimatedDurationSeconds = 65; Weight = 2; ResourceClass = "CpuBound"' -Message "Naming/style formatting must retain one checked-in CPU-bound required-gate profile."
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
Assert-Contains -Actual $verifyScript -Expected '$excludedRequiredGateNames = if ($VerificationComponent -eq "Solution") { @("git-diff-check", "format-whitespace", "format-naming-style") } else { @() }' -Message "Only Solution may exclude the three static required-gate profiles; Full and StaticContracts must pass no exclusions."
Assert-Contains -Actual $verifyScript -Expected '$excludedRequiredGateNames = if ($VerificationComponent -eq "Solution")' -Message "The solution component must explicitly exclude only static required-gate profiles."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases) -ExcludedNames $excludedRequiredGateNames' -Message "Solution scheduling must be validated against the reduced but exact required-gate profile set."

$phaseBehaviorRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-phase-output-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $phaseBehaviorRoot | Out-Null
try {
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

    $timeoutScript = Join-Path $phaseBehaviorRoot "timeout.ps1"
    [IO.File]::WriteAllText($timeoutScript, "Write-Output 'timeout-evidence'; Start-Sleep -Seconds 5", [Text.UTF8Encoding]::new($false))
    $timeoutLog = Join-Path $phaseBehaviorRoot "timeout.log"
    try {
        Invoke-VerificationPhase -Name "phase-timeout" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-File", $timeoutScript) -TimeoutSeconds 1 -WorkingDirectory $repoRoot -OutputPath $timeoutLog
        throw "Expected sequential phase timeout."
    }
    catch {
        Assert-Contains -Actual (Get-Content -LiteralPath $timeoutLog -Raw) -Expected "timeout-evidence" -Message "A timed-out sequential phase must retain available diagnostics."
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
Assert-Contains -Actual $verifyWorkflow -Expected "cancel-in-progress: true" -Message "Full verification must release its Windows runner when superseded."
$solutionJobIndex = $verifyWorkflow.IndexOf("  verify-solution:", [StringComparison]::Ordinal)
$solutionJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $solutionJobIndex, [StringComparison]::Ordinal)
$solutionJobConcurrencyIndex = $verifyWorkflow.IndexOf("    concurrency:", $solutionJobIndex, [StringComparison]::Ordinal)
$contractJobIndex = $verifyWorkflow.IndexOf("  verify-contracts:", [StringComparison]::Ordinal)
$contractJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $contractJobIndex, [StringComparison]::Ordinal)
$contractJobConcurrencyIndex = $verifyWorkflow.IndexOf("    concurrency:", $contractJobIndex, [StringComparison]::Ordinal)
$fanInJobIndex = $verifyWorkflow.IndexOf("  verify:", [StringComparison]::Ordinal)
$fanInJobConditionIndex = $verifyWorkflow.IndexOf("    if:", $fanInJobIndex, [StringComparison]::Ordinal)
$fanInNeedsIndex = $verifyWorkflow.IndexOf("    needs: [verify-solution, verify-contracts]", $fanInJobIndex, [StringComparison]::Ordinal)
Assert-True -Condition ($solutionJobIndex -ge 0 -and $solutionJobConditionIndex -gt $solutionJobIndex -and $solutionJobConcurrencyIndex -gt $solutionJobConditionIndex -and $contractJobIndex -gt $solutionJobIndex -and $contractJobConditionIndex -gt $contractJobIndex -and $contractJobConcurrencyIndex -gt $contractJobConditionIndex -and $fanInJobIndex -gt $contractJobIndex -and $fanInJobConditionIndex -gt $fanInJobIndex -and $fanInNeedsIndex -gt $fanInJobConditionIndex) -Message "Solution and contract cancellation must remain job-scoped behind non-draft eligibility, with a final fan-in after both children."
Assert-True -Condition ($verifyWorkflow.IndexOf("`nconcurrency:", [StringComparison]::Ordinal) -lt 0) -Message "Full verification must not use workflow-scoped cancellation for ineligible metadata edits."
Assert-True -Condition ($verifyWorkflow.IndexOf("-SkipCoverage", [StringComparison]::Ordinal) -lt 0) -Message "Promotion verification must retain exact coverage collection and reduction."
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 900 -VerificationComponent Solution" -Message "The solution child must own build, lanes, inventory, and coverage behind the unchanged 900-second watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 600 -VerificationComponent StaticContracts" -Message "The static child must own all static contracts behind a bounded 600-second watchdog."
Assert-Contains -Actual $verifyWorkflow -Expected "uses: actions/download-artifact@v7" -Message "The protected fan-in must transport child artifacts explicitly."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-solution-diagnostics-${{ github.run_attempt }}' -Message "The solution evidence artifact must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-contract-diagnostics-${{ github.run_attempt }}' -Message "The static evidence artifact must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-solution-receipt-${{ github.run_attempt }}' -Message "The protected solution receipt must bind to the current workflow attempt."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-contract-receipt-${{ github.run_attempt }}' -Message "The protected static receipt must bind to the current workflow attempt."
foreach ($solutionReceiptPath in @("verification-component-evidence.json", "verification-component-manifest.json", "verification-watchdog-evidence.json", "watchdog.log", "required-test-lanes.json", "required-test-partition.json", "required-execution-tests.json", "required-test-report.json", "coverage-manifest.json", "coverage-summary.json", "**/*.trx")) {
    Assert-Contains -Actual $verifyWorkflow -Expected "tests/VerificationResults/$solutionReceiptPath" -Message "The solution receipt must transport '$solutionReceiptPath'."
}
foreach ($staticReceiptPath in @("verify-sdk-diagnostics.tests.ps1.log", "verify-preflight-overlap.tests.ps1.log", "verify-coverage.tests.ps1.log", "verify-bounded-phases.tests.ps1.log", "verify-parallel.tests.ps1.log", "verify-test-inventory.tests.ps1.log", "verify-watchdog.tests.ps1.log", "verify-promotion-fan-in.tests.ps1.log", "frontend-preflight.log", "restore-static.log", "format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) {
    Assert-Contains -Actual $verifyWorkflow -Expected "tests/VerificationResults/Logs/$staticReceiptPath" -Message "The static receipt must transport '$staticReceiptPath'."
}
Assert-Contains -Actual $verifyWorkflow -Expected "scripts/verify-promotion-fan-in.ps1" -Message "The protected fan-in must delegate evidence authentication to the repository verifier contract."
Assert-Contains -Actual $verifyWorkflow -Expected '-ExpectedRunId ''${{ github.run_id }}'' -ExpectedRunAttempt ''${{ github.run_attempt }}'' -SolutionResult ''${{ needs.verify-solution.result }}'' -StaticResult ''${{ needs.verify-contracts.result }}''' -Message "The protected fan-in must authenticate the current run, attempt, and both child results."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-solution-receipt-${{ github.run_attempt }}' -Message "The protected fan-in must download the small solution receipt rather than the full diagnostics artifact."
Assert-Contains -Actual $verifyWorkflow -Expected 'name: verification-contract-receipt-${{ github.run_attempt }}' -Message "The protected fan-in must download the small static receipt rather than the full diagnostics artifact."
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
Assert-Contains -Actual $promotionCancellationWorkflow -Expected "uses: actions/github-script@v8" -Message "Draft demotion must use the bounded GitHub API cancellation path."
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
Assert-Contains -Actual $qualificationWorkflow -Expected '-Qualification -BaseCommit ''${{ inputs.base_sha }}'' -HeadCommit ''${{ inputs.head_sha }}'' -Configuration Release -DeadlineSeconds 480' -Message "Qualification must bind the dispatched exact edge under one eight-minute watchdog."
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
Assert-Contains -Actual $verifyWorkflow -Expected "run: ./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 900" -Message "Promotion must have one explicit bounded fifteen-minute certification window for the complete solution child."
Assert-Contains -Actual $verifyWorkflow -Expected "timeout-minutes: 20" -Message "Workflow setup and diagnostic upload must remain bounded outside the measured promotion child."
Assert-Contains -Actual $verifyWorkflow -Expected "timeout-minutes: 15" -Message "The static child job must leave bounded setup and receipt-upload margin around its 600-second verifier."
Assert-True -Condition ($verifyWorkflow.IndexOf("run: ./tests/scripts/", [StringComparison]::Ordinal) -lt 0) -Message "Repository script tests must execute inside the measured verifier child."
foreach ($contractScript in @("verify-sdk-diagnostics.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1", "verify-promotion-fan-in.tests.ps1")) {
    Assert-Contains -Actual $verifyScript -Expected $contractScript -Message "The measured verifier must own '$contractScript'."
}
Assert-Contains -Actual $stressWorkflow -Expected "./tests/scripts/verify-coverage.tests.ps1" -Message "Scheduled stress verification must retain coverage merger contracts."

Write-Output "Bounded verifier contract tests passed ($assertionCount assertions)."
