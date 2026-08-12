Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$tempScriptPath = Join-Path $repoRoot "scripts\verification-temp.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$watchdogScriptPath = Join-Path $repoRoot "scripts\verify-with-watchdog.ps1"
$coverageScriptPath = Join-Path $repoRoot "scripts\verify-coverage.ps1"
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
$laneScript = Get-Content -LiteralPath (Join-Path $repoRoot "scripts\verification-test-lanes.ps1") -Raw
$coverageScript = Get-Content -LiteralPath $coverageScriptPath -Raw
$verifyWorkflow = Get-Content -LiteralPath $verifyWorkflowPath -Raw
$stressWorkflow = Get-Content -LiteralPath $stressWorkflowPath -Raw
$pullRequestSettings = Get-Content -LiteralPath $pullRequestSettingsPath -Raw
$stressSettings = Get-Content -LiteralPath $stressSettingsPath -Raw
$gitIgnore = Get-Content -LiteralPath $gitIgnorePath -Raw
$maximumTest = Get-Content -LiteralPath $maximumTestPath -Raw
$retentionTest = Get-Content -LiteralPath $retentionTestPath -Raw

Assert-Contains -Actual $verifyScript -Expected '[ValidateSet("PullRequest", "Stress")]' -Message "The verifier must expose only the two owned tiers."
Assert-Contains -Actual $verifyScript -Expected '[string]$Configuration = "Release"' -Message "The canonical verifier must default to Release."
Assert-Contains -Actual $verifyScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Environment]::ProcessorCount)' -Message "The required gate must bound parallel workers to available processors and an absolute ceiling."
Assert-Contains -Actual $watchdogScript -Expected '[int]$MaximumTestWorkers = [Math]::Min(8, [Environment]::ProcessorCount)' -Message "The external watchdog must preserve the hardware-aware worker ceiling."
Assert-Contains -Actual $phaseScript -Expected 'if ($null -ne $commandScriptPath) {' -Message "Windows batch phases must preserve cmd.exe quoting."
Assert-Contains -Actual $phaseScript -Expected 'elseif ($null -ne $startInfo.PSObject.Properties["ArgumentList"]) {' -Message "Non-batch phases must use ArgumentList when available."
Assert-Contains -Actual $phaseScript -Expected 'VERIFY_CHILD_TIMEOUT name=$Name' -Message "Sequential timeouts must emit structured watchdog evidence."
Assert-Contains -Actual $parallelScript -Expected 'Sort-Object -Property @{ Expression = "Priority"; Descending = $true }' -Message "Parallel phase priority must be deterministic and longest-first capable."
Assert-Contains -Actual $parallelScript -Expected '[Math]::Min($MaximumWorkers, [Math]::Max(1, [Environment]::ProcessorCount))' -Message "Parallel resource capacity must never exceed the available processor count."
Assert-Contains -Actual $parallelScript -Expected '$phase.EffectiveWeight = [Math]::Min($phase.Weight, $maximumResourceCapacity)' -Message "Declared phase weight must adapt to constrained hosts instead of making verification unschedulable."
Assert-Contains -Actual $parallelScript -Expected 'Select-VerificationParallelPhase -Pending $pending -AvailableCapacity $availableCapacity' -Message "The scheduler must select a fitting phase instead of blocking behind the queue head."
Assert-Contains -Actual $parallelScript -Expected 'if ($Pending[$index].SchedulingDeferrals -ge 1)' -Message "Backfill must reserve a later fitting opportunity for bypassed phases."
Assert-Contains -Actual $parallelScript -Expected 'VERIFY_CHILD_TIMEOUT name=$($result.Name)' -Message "Parallel timeouts must emit structured watchdog evidence."
Assert-Contains -Actual $verifyScript -Expected '$testLaneTimeoutSeconds = 480' -Message "Every required lane must fit inside the outer budget."
Assert-Contains -Actual $verifyScript -Expected 'Get-ProjectCoverageIsolation' -Message "Every test project must execute from isolated exact-build copies."
Assert-Contains -Actual $verifyScript -Expected 'Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot $lane.Name) -Configuration $Configuration -TargetFramework $targetFramework' -Message "Every lane must preserve its bin/<Configuration>/<TargetFramework> AppContext suffix."
Assert-Contains -Actual $verifyScript -Expected 'Copy-VerifiedDirectory -SourceDirectory $pristineDirectory -DestinationDirectory $laneDirectory' -Message "Every lane copy must be verified before use."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory' -Message "Persistence child-process coverage must receive a process-scoped immutable source."
Assert-Contains -Actual $verifyScript -Expected 'Resolve-VerificationPhysicalTempRoot -RunnerTemp $env:RUNNER_TEMP -SystemTempPath ([IO.Path]::GetTempPath())' -Message "Hosted verification must select the runner-owned ephemeral volume with a local fallback."
Assert-Contains -Actual $verifyScript -Expected '$verificationLaneFixtureRoot = Join-Path $verificationPhysicalTempRoot' -Message "Lane fixture isolation must remain outside retained repository artifacts."
Assert-Contains -Actual $verifyScript -Expected 'EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"' -Message "Every project lane must receive a disjoint process-scoped catalog trust root."
foreach ($tempVariable in @("TEMP", "TMP", "TMPDIR")) {
    Assert-Contains -Actual $verifyScript -Expected "$tempVariable = `$laneFixtureRoot" -Message "Every lane and descendant must use the fast isolated '$tempVariable' fixture root."
}
Assert-Contains -Actual $verifyScript -Expected 'Remove-Item -LiteralPath $verificationLaneFixtureRoot -Recurse -Force' -Message "Lane fixture roots must be cleaned after ordinary verifier completion."
Assert-Contains -Actual $verifyScript -Expected '"vstest", $Lane.AssemblyPath' -Message "Test lanes must execute isolated assemblies."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-execution-custom-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests")' -Message "Custom loop runtime tests must retain their independently scheduled Startup lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-execution-governed-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests")' -Message "Governed loop runtime tests must retain their independently scheduled Startup lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-execution-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution") -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests", "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests")' -Message "The Startup execution remainder must explicitly exclude both dedicated runtime lanes."
Assert-True -Condition ($verifyScript.IndexOf('[pscustomobject]@{ Name = "loop-execution";', [StringComparison]::Ordinal) -lt 0) -Message "The oversized serial Startup execution lane must not be restored."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "contextual-roles" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ContextualRoles")' -Message "Contextual-role persistence tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "authority" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Authority")' -Message "Authority persistence tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "tool-results-audit" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ToolResults", "EmbodySense.Core.Persistence.Tests.Audit")' -Message "Tool-result and audit persistence tests must share a bounded lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "default-conversation-recovery" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests")' -Message "Default-conversation recovery tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "default-conversation-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn") -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests")' -Message "The default-conversation remainder must explicitly exclude recovery tests."
Assert-True -Condition ($laneScript.IndexOf('New-VerificationTestLane -Name "authority-context"', [StringComparison]::Ordinal) -lt 0) -Message "The oversized authority-context lane must not be restored."
Assert-True -Condition ($laneScript.IndexOf('New-VerificationTestLane -Name "default-conversation"', [StringComparison]::Ordinal) -lt 0) -Message "The oversized default-conversation lane must not be restored."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "governance" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.Core.Governance")' -Message "Integration governance tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "cli" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.Cli")' -Message "CLI integration tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "codex-app-server" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.CodexAppServer")' -Message "Codex app-server integration tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.IntegrationTests.Core.Governance", "EmbodySense.IntegrationTests.Cli", "EmbodySense.IntegrationTests.CodexAppServer")' -Message "The Integration remainder must explicitly exclude every dedicated lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "runtime-host" -IncludeFullyQualifiedName @("EmbodySense.Web.Tests.WebAgentRuntimeHostTests")' -Message "Web runtime-host tests must have an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "loop-api-run" -IncludeFullyQualifiedName @("EmbodySense.Web.Tests.LoopApiControllerTests", "EmbodySense.Web.Tests.LoopRunApiControllerTests")' -Message "Loop definition and run API tests must share an independently scheduled lane."
Assert-Contains -Actual $laneScript -Expected 'New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Web.Tests.WebAgentRuntimeHostTests", "EmbodySense.Web.Tests.LoopApiControllerTests", "EmbodySense.Web.Tests.LoopRunApiControllerTests")' -Message "The Web remainder must explicitly exclude every dedicated lane."
Assert-Contains -Actual $verifyScript -Expected '"EmbodySense.Core.Startup.Tests-loop-execution-governed-runtime" { 2150; break }' -Message "The longest measured Startup execution lane must enter the worker queue early."
Assert-Contains -Actual $verifyScript -Expected '"EmbodySense.Core.Startup.Tests-loop-execution-custom-runtime" { 2075; break }' -Message "The Windows-dependent custom runtime lane must enter the worker queue before general Startup work."
foreach ($heavyLane in @("EmbodySense.Core.Persistence.Tests-human-input-responses", "EmbodySense.Core.Startup.Tests-loop-execution-governed-runtime", "EmbodySense.Core.Persistence.Tests-triggers", "EmbodySense.Core.Startup.Tests-loop-execution-custom-runtime")) {
    Assert-Contains -Actual $verifyScript -Expected "`"$heavyLane`" { `$true; break }" -Message "Measured process-heavy lane '$heavyLane' must consume two resource units."
}
Assert-Contains -Actual $verifyScript -Expected '-Weight $weight -ResourceClass $resourceClass' -Message "Every required test lane must declare explicit scheduler resource metadata."
Assert-Contains -Actual $verifyScript -Expected 'kind=required-gates phases=$($script:VerificationParallelPhases.Count) maximum_resource_capacity=$MaximumTestWorkers' -Message "The required-gate plan must report capacity semantics instead of implying each child consumes one worker."
Assert-Contains -Actual $verifyScript -Expected 'identity=TestCase.Id partition_identity=XunitTestCaseUniqueID' -Message "Stable inventory identities must remain explicit."
Assert-Contains -Actual $verifyScript -Expected 'verify-test-partition.ps1' -Message "Canonical discovery and declarative lane selection must be reconciled."
Assert-Contains -Actual $verifyScript -Expected 'Write-CoverageManifest' -Message "Coverage must be bound to an exact fresh report manifest."
Assert-Contains -Actual $verifyScript -Expected 'kind=reconciliation' -Message "Inventory and coverage aggregation must overlap safely."
Assert-Contains -Actual $verifyScript -Expected '-Name "git-diff-check"' -Message "The canonical verifier must retain git diff validation."
Assert-Contains -Actual $verifyScript -Expected 'VERIFY_COMPLETE schema_version=1 status=passed' -Message "A successful standard run must emit exact terminal evidence."
Assert-Contains -Actual $gitIgnore -Expected 'tests/VerificationResults/' -Message "Generated verifier diagnostics must remain uploadable without dirtying a local worktree."
Assert-Contains -Actual $coverageScript -Expected 'if (-not $fileLines.ContainsKey($lineNumber) -or $hits -gt $fileLines[$lineNumber]) {' -Message "Split coverage must merge duplicate source lines by maximum hits."
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
foreach ($contractScript in @("verify-sdk-diagnostics.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1")) {
    Assert-Contains -Actual $verifyScript -Expected $contractScript -Message "The measured verifier must own '$contractScript'."
}
Assert-Contains -Actual $stressWorkflow -Expected "./tests/scripts/verify-coverage.tests.ps1" -Message "Scheduled stress verification must retain coverage merger contracts."

Write-Output "Bounded verifier contract tests passed ($assertionCount assertions)."
