Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$tempScriptPath = Join-Path $repoRoot "scripts\verification-temp.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$watchdogScriptPath = Join-Path $repoRoot "scripts\verify-with-watchdog.ps1"
$coverageScriptPath = Join-Path $repoRoot "scripts\verify-coverage.ps1"
$coverageEvidenceScriptPath = Join-Path $repoRoot "scripts\verification-coverage-evidence.ps1"
$verifyWorkflowPath = Join-Path $repoRoot ".github\workflows\verify.yml"
$stressWorkflowPath = Join-Path $repoRoot ".github\workflows\verification-stress.yml"
$pullRequestSettingsPath = Join-Path $repoRoot "tests\verification-pull-request.runsettings"
$stressSettingsPath = Join-Path $repoRoot "tests\verification-stress.runsettings"
$gitIgnorePath = Join-Path $repoRoot ".gitignore"
$maximumTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\CustomLoopRunArtifactMaximumShapeTests.cs"
$retentionTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\CustomLoopTraceRetentionStoreTests.cs"
$powerShellExecutable = (Get-Process -Id $PID).Path
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

. $phaseScriptPath
. $tempScriptPath
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
    $argumentProbePath = Join-Path $scenarioRoot "argument probe.ps1"
    @'
param([string]$First, [string]$Second, [string]$Third)
if ($First -cne "value with spaces" -or $Second -cne 'quote"value' -or $Third -cne 'trailing\') { exit 19 }
'@ | Set-Content -LiteralPath $argumentProbePath -Encoding UTF8

    $successArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { $successArguments += @("-ExecutionPolicy", "Bypass") }
    $successArguments += @("-File", $argumentProbePath, "value with spaces", 'quote"value', 'trailing\')
    $successOutput = @(Invoke-VerificationPhase -Name "argument-integrity" -FileName $powerShellExecutable -Arguments $successArguments -TimeoutSeconds 10 -WorkingDirectory $repoRoot) -join [Environment]::NewLine
    Assert-Contains -Actual $successOutput -Expected "VERIFY_PHASE_START name=argument-integrity" -Message "Successful phases must announce their start."
    Assert-Contains -Actual $successOutput -Expected "VERIFY_PHASE_COMPLETE name=argument-integrity" -Message "Successful phases must announce elapsed completion."

    $failureMessage = Invoke-ExpectedFailure -ExpectedMessage "exited with code 23" -Action {
        Invoke-VerificationPhase -Name "nonzero-exit" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-Command", "exit 23") -TimeoutSeconds 10 -WorkingDirectory $repoRoot
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
$stressWorkflow = Get-Content -LiteralPath $stressWorkflowPath -Raw
$pullRequestSettings = Get-Content -LiteralPath $pullRequestSettingsPath -Raw
$stressSettings = Get-Content -LiteralPath $stressSettingsPath -Raw
$gitIgnore = Get-Content -LiteralPath $gitIgnorePath -Raw
$maximumTest = Get-Content -LiteralPath $maximumTestPath -Raw
$retentionTest = Get-Content -LiteralPath $retentionTestPath -Raw

Assert-Contains -Actual $verifyScript -Expected '[ValidateSet("PullRequest", "Stress")]' -Message "The verifier must expose only the two owned tiers."
Assert-Contains -Actual $verifyScript -Expected '[string]$Configuration = "Release"' -Message "The canonical verifier must default to Release."
Assert-Contains -Actual $verifyScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))' -Message "The required gate must request bounded logical concurrency above the physical processor count."
Assert-Contains -Actual $watchdogScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))' -Message "The external watchdog must preserve the bounded logical worker request."
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
Assert-Contains -Actual $verifyScript -Expected 'Copy-VerifiedDirectory -SourceDirectory $pristineDirectory -DestinationDirectory $laneDirectory' -Message "Every lane copy must be verified before use."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory' -Message "Persistence child-process coverage must receive a process-scoped immutable source."
Assert-Contains -Actual $verifyScript -Expected 'Resolve-VerificationPhysicalTempRoot -RunnerTemp $env:RUNNER_TEMP -SystemTempPath ([IO.Path]::GetTempPath())' -Message "Hosted verification must select the runner-owned ephemeral volume with a local fallback."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationLaneFixturePath -PhysicalTempRoot $verificationPhysicalTempRoot' -Message "Lane fixture isolation must remain short, disjoint, and outside retained repository artifacts."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"' -Message "Every project lane must receive a disjoint process-scoped catalog trust root."
foreach ($tempVariable in @("TEMP", "TMP", "TMPDIR")) {
    Assert-Contains -Actual $verifyScript -Expected "$tempVariable = `$laneFixtureRoot" -Message "Every lane and descendant must use the fast isolated '$tempVariable' fixture root."
}
Assert-Contains -Actual $verifyScript -Expected 'Remove-Item -LiteralPath $laneFixtureRoot -Recurse -Force' -Message "Lane fixture roots must be cleaned after ordinary verifier completion."
Assert-Contains -Actual $verifyScript -Expected '"vstest", $Lane.AssemblyPath' -Message "Test lanes must execute isolated assemblies."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-execution-custom-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests")' -Message "Custom loop runtime tests must retain their independently scheduled Startup lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-execution-governed-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests")' -Message "Governed loop runtime tests must retain their independently scheduled Startup lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-execution-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution") -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests", "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests")' -Message "The Startup execution remainder must explicitly exclude both dedicated runtime lanes."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "remainder-capabilities" -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops", "EmbodySense.Core.Startup.Tests.Runtime", "EmbodySense.Core.Startup.Tests.Triggers")' -Message "The Startup remainder must absorb capabilities without overlapping the dedicated loop and runtime lanes."
Assert-True -Condition ($verifyScript.IndexOf('[pscustomobject]@{ Name = "loop-execution";', [StringComparison]::Ordinal) -lt 0) -Message "The oversized serial Startup execution lane must not be restored."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "contextual-roles" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ContextualRoles")' -Message "Contextual-role persistence tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "authority" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Authority")' -Message "Purpose-built external hosts must allow authority persistence coverage to return to one report-producing lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "credentials" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Credentials")' -Message "Purpose-built external hosts must allow credential persistence coverage to return to one report-producing lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "tool-results-audit" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ToolResults", "EmbodySense.Core.Persistence.Tests.Audit")' -Message "Short-lived audit hosts must not force a duplicate coverage lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "human-input" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest", "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse")' -Message "Human Input request and response tests must share one exact report-producing lane after nested-VSTest removal."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "default-conversation" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn")' -Message "Default-conversation recovery must return to its complete family after nested-VSTest removal."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "graph-lifecycle" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring", "EmbodySense.Core.Persistence.Tests.Loops.Admission", "EmbodySense.Core.Persistence.Tests.Loops.Revisions")' -Message "Graph and lifecycle persistence tests must share a coverage-report-aware lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "remainder-triggers" -ExcludeFullyQualifiedName @(' -Message "Trigger coverage must be absorbed by the exact persistence remainder after nested-VSTest removal."
Assert-True -Condition ($laneScript.IndexOf('New-VerificationTestLane -Name "authority-context"', [StringComparison]::Ordinal) -lt 0) -Message "The oversized authority-context lane must not be restored."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "governance" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.Core.Governance")' -Message "Integration governance tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "cli" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.Cli")' -Message "CLI integration tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "codex-app-server" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.CodexAppServer")' -Message "Codex app-server integration tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.IntegrationTests.Core.Governance", "EmbodySense.IntegrationTests.Cli", "EmbodySense.IntegrationTests.CodexAppServer")' -Message "The Integration remainder must explicitly exclude every dedicated lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "runtime-host" -IncludeFullyQualifiedName @("EmbodySense.Web.Tests.WebAgentRuntimeHostTests")' -Message "Web runtime-host tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-api-run" -IncludeFullyQualifiedName @("EmbodySense.Web.Tests.LoopApiControllerTests", "EmbodySense.Web.Tests.LoopRunApiControllerTests")' -Message "Loop definition and run API tests must share an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Web.Tests.WebAgentRuntimeHostTests", "EmbodySense.Web.Tests.LoopApiControllerTests", "EmbodySense.Web.Tests.LoopRunApiControllerTests")' -Message "The Web remainder must explicitly exclude every dedicated lane."
foreach ($heavyLane in @("EmbodySense.Core.Startup.Tests-loop-execution-custom-runtime", "EmbodySense.Core.Startup.Tests-loop-execution-governed-runtime")) {
    Assert-Contains -Actual $scheduleScript -Expected "Name = `"tests-$heavyLane`";" -Message "Measured process-heavy lane '$heavyLane' must have a checked-in scheduling profile."
}
foreach ($nestedProcessProfile in @(
    'Name = "tests-EmbodySense.Core.Persistence.Tests-custom-run-trace"; EstimatedDurationSeconds = 140; Weight = 3; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Core.Persistence.Tests-default-conversation"; EstimatedDurationSeconds = 65; Weight = 3; ResourceClass = "ProcessHeavy"'
    'Name = "tests-EmbodySense.Core.Persistence.Tests-graph-lifecycle"; EstimatedDurationSeconds = 55; Weight = 3; ResourceClass = "ProcessHeavy"'
)) {
    Assert-Contains -Actual $scheduleScript -Expected $nestedProcessProfile -Message "Nested-process persistence lanes must retain exact process-heavy scheduling profiles."
}
foreach ($retiredMicroLane in @("authority-grants-process", "credentials-external-process", "default-conversation-recovery", "effect-authority-process", "sequential-evidence-process")) {
    Assert-True -Condition ($laneScript.IndexOf("New-VerificationTestLane -Name `"$retiredMicroLane`"", [StringComparison]::Ordinal) -lt 0) -Message "Purpose-built hosts must retire report-amplifying lane '$retiredMicroLane'."
}
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateResourceCapacity = 8' -Message "Required gates must use the explicit eight-unit logical resource capacity."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMaximumProcessHeavyWorkers = 2' -Message "Required gates must enforce an explicit two-process-heavy concurrency ceiling."
Assert-Contains -Actual $scheduleScript -Expected '$script:VerificationRequiredGateMaximumCpuBoundWorkers = 1' -Message "Required gates must enforce an explicit one-CPU-bound concurrency ceiling."
Assert-Contains -Actual $scheduleScript -Expected 'Weight = 3; ResourceClass = "ProcessHeavy"' -Message "Process-heavy required gates must retain their evidence-backed logical weight."
Assert-Contains -Actual $scheduleScript -Expected 'Weight = 2; ResourceClass = "CpuBound"' -Message "CPU-bound non-test gates must consume multiple logical resource units."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationRequiredGateScheduleProfile -Name $Name' -Message "Every required gate must obtain checked-in duration and resource metadata by exact name."
Assert-Contains -Actual $verifyScript -Expected '-EstimatedDurationSeconds $profile.EstimatedDurationSeconds -Weight $profile.Weight -ResourceClass $profile.ResourceClass' -Message "Every required gate must pass its exact checked-in scheduler profile."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases)' -Message "The complete required gate plan must fail closed before execution when a profile is missing or mismatched."
Assert-Contains -Actual $scheduleScript -Expected '$logicalLaneWorkerCeiling = [Math]::Min(6,' -Message "Required gates must derive a six-process ceiling even when logical capacity is eight."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min($MaximumTestWorkers, $logicalLaneWorkerCeiling)' -Message "Required gates must preserve lower explicit worker requests without bypassing the derived ceiling."
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
Assert-Contains -Actual $verifyScript -Expected 'VERIFY_COMPLETE schema_version=1 status=passed' -Message "A successful standard run must emit exact terminal evidence."
Assert-Contains -Actual $gitIgnore -Expected 'tests/VerificationResults/' -Message "Generated verifier diagnostics must remain uploadable without dirtying a local worktree."
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
Assert-True -Condition ($verifyWorkflow.IndexOf("run: ./scripts/verify.ps1", [StringComparison]::Ordinal) -lt 0) -Message "Standard CI cannot bypass the watchdog."
Assert-True -Condition ($verifyWorkflow.IndexOf("run: ./tests/scripts/", [StringComparison]::Ordinal) -lt 0) -Message "Repository script tests must execute inside the measured verifier child."
foreach ($contractScript in @("verify-sdk-diagnostics.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1")) {
    Assert-Contains -Actual $verifyScript -Expected $contractScript -Message "The measured verifier must own '$contractScript'."
}
Assert-Contains -Actual $stressWorkflow -Expected "./tests/scripts/verify-coverage.tests.ps1" -Message "Scheduled stress verification must retain coverage merger contracts."

Write-Output "Bounded verifier contract tests passed ($assertionCount assertions)."
