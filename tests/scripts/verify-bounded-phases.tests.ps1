Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$verifyWorkflowPath = Join-Path $repoRoot ".github\workflows\verify.yml"
$stressWorkflowPath = Join-Path $repoRoot ".github\workflows\verification-stress.yml"
$pullRequestSettingsPath = Join-Path $repoRoot "tests\verification-pull-request.runsettings"
$stressSettingsPath = Join-Path $repoRoot "tests\verification-stress.runsettings"
$maximumTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\CustomLoopRunArtifactMaximumShapeTests.cs"
$retentionTestPath = Join-Path $repoRoot "tests\EmbodySense.Core.Persistence.Tests\Loops\CustomLoopTraceRetentionStoreTests.cs"
$powerShellExecutable = (Get-Process -Id $PID).Path
$assertionCount = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Contains {
    param(
        [string]$Actual,
        [string]$Expected,
        [string]$Message
    )

    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

function Invoke-ExpectedFailure {
    param(
        [scriptblock]$Action,
        [string]$ExpectedMessage
    )

    try {
        & $Action | Out-Null
        throw "Expected the action to fail with '$ExpectedMessage'."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected $ExpectedMessage -Message "Failure diagnostic mismatch."
        return $_.Exception.Message
    }
}

. $phaseScriptPath
Reset-VerificationPhaseState

$contextLine = Write-VerificationContext -RepositoryRoot $repoRoot -Configuration Debug -VerificationTier PullRequest
Assert-Contains -Actual $contextLine -Expected "VERIFY_CONTEXT_JSON=" -Message "Verifier context must be machine readable."
$context = $contextLine.Substring("VERIFY_CONTEXT_JSON=".Length) | ConvertFrom-Json
Assert-True -Condition ($context.schemaVersion -eq 1) -Message "Verifier context schema must remain version 1."
Assert-True -Condition ($context.verificationTier -eq "PullRequest") -Message "Verifier context must identify its tier."
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($context.repositoryHead)) -Message "Verifier context must identify the repository head or its explicit unavailable marker."
Assert-True -Condition ($context.processorCount -ge 1) -Message "Verifier context must identify processor count."

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-bounded-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $argumentProbePath = Join-Path $scenarioRoot "argument probe.ps1"
    @'
param(
    [string]$First,
    [string]$Second,
    [string]$Third
)

if ($First -cne "value with spaces" -or $Second -cne 'quote"value' -or $Third -cne 'trailing\') {
    exit 19
}
'@ | Set-Content -LiteralPath $argumentProbePath -Encoding UTF8

    $successArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $successArguments += @("-ExecutionPolicy", "Bypass")
    }

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
    if (Test-Path $scenarioRoot) {
        Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
    }
}

$verifyScript = Get-Content -Raw $verifyScriptPath
$phaseScript = Get-Content -Raw $phaseScriptPath
$verifyWorkflow = Get-Content -Raw $verifyWorkflowPath
$stressWorkflow = Get-Content -Raw $stressWorkflowPath
$pullRequestSettings = Get-Content -Raw $pullRequestSettingsPath
$stressSettings = Get-Content -Raw $stressSettingsPath
$maximumTest = Get-Content -Raw $maximumTestPath
$retentionTest = Get-Content -Raw $retentionTestPath

Assert-Contains -Actual $verifyScript -Expected '[ValidateSet("PullRequest", "Stress")]' -Message "The verifier must expose only the two owned tiers."
Assert-Contains -Actual $phaseScript -Expected 'if ($null -ne $commandScriptPath) {' -Message "Windows batch phases must select cmd.exe command-line quoting before generic ArgumentList handling."
Assert-Contains -Actual $phaseScript -Expected 'elseif ($null -ne $startInfo.PSObject.Properties["ArgumentList"]) {' -Message "Non-batch phases should still use ArgumentList when the runtime provides it."
Assert-Contains -Actual $verifyScript -Expected '[string]$Configuration = "Release"' -Message "The canonical verifier must default to Release."
Assert-Contains -Actual $verifyScript -Expected '[ValidateSet("Debug", "Release")]' -Message "The verifier must retain Debug as an explicit supported configuration."
Assert-Contains -Actual $verifyScript -Expected 'VerificationTier!=Stress' -Message "Required verification must explicitly exclude only the owned stress trait."
Assert-Contains -Actual $verifyScript -Expected "Adversarial_maximum_transition_reservations_and_canonical_order_checks_remain_bounded" -Message "The verifier must own the exact maximum-artifact stress test."
Assert-Contains -Actual $verifyScript -Expected "Rejected_operation_capacity_preserves_reserved_tombstone_deletions_and_remains_visible" -Message "The verifier must own the exact deletion-capacity stress test."
Assert-Contains -Actual $verifyScript -Expected 'exact_test_count=2' -Message "Stress diagnostics must expose the expected exact-test inventory."
Assert-Contains -Actual $pullRequestSettings -Expected '<TreatNoTestsAsError>true</TreatNoTestsAsError>' -Message "Required verification cannot silently accept an empty selection."
Assert-Contains -Actual $pullRequestSettings -Expected '<TestSessionTimeout>1500000</TestSessionTimeout>' -Message "Pull-request test sessions must retain enough bounded time for the Persistence coverage suite."
Assert-Contains -Actual $verifyScript -Expected '$coveragePhaseTimeoutSeconds = if ($_.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") { 1560 } else { 900 }' -Message "Only Persistence coverage receives the extended phase timeout."
Assert-Contains -Actual $verifyScript -Expected '-TimeoutSeconds $coveragePhaseTimeoutSeconds' -Message "Coverage phases must use the project-specific bounded timeout."
Assert-Contains -Actual $stressSettings -Expected '<TreatNoTestsAsError>true</TreatNoTestsAsError>' -Message "Stress verification cannot silently accept an empty selection."
Assert-Contains -Actual $stressSettings -Expected '<TestSessionTimeout>1500000</TestSessionTimeout>' -Message "Stress test sessions must remain bounded."
Assert-Contains -Actual $maximumTest -Expected '[Trait(VerificationTier.TraitName, VerificationTier.Stress)]' -Message "The adversarial maximum test must remain in the stress tier."
Assert-Contains -Actual $maximumTest -Expected "Public_artifact_contract_round_trips_the_maximum_bounded_shape_below_fifteen_mebibytes" -Message "A required maximum-contract proof must remain visible."
Assert-Contains -Actual $retentionTest -Expected '[Trait(VerificationTier.TraitName, VerificationTier.Stress)]' -Message "The 10,000-operation case must remain in the stress tier."
Assert-Contains -Actual $stressWorkflow -Expected "schedule:" -Message "The stress tier must have a schedule owner."
Assert-Contains -Actual $stressWorkflow -Expected "-VerificationTier Stress" -Message "The scheduled workflow must invoke the stress tier explicitly."
Assert-Contains -Actual $stressWorkflow -Expected "-Configuration Release" -Message "The scheduled workflow must explicitly use the canonical Release configuration."
Assert-Contains -Actual $stressWorkflow -Expected "if: always()" -Message "Stress diagnostics must be retained on both success and failure."
Assert-Contains -Actual $verifyWorkflow -Expected "-Configuration Release" -Message "Pull-request verification must explicitly use the canonical Release configuration."
Assert-Contains -Actual $verifyWorkflow -Expected "./tests/scripts/verify-bounded-phases.tests.ps1" -Message "Pull-request verification must execute this contract harness."
Assert-Contains -Actual $verifyWorkflow -Expected "./tests/scripts/verify-coverage.tests.ps1" -Message "Pull-request verification must exercise coverage aggregation contracts."
Assert-Contains -Actual $stressWorkflow -Expected "./tests/scripts/verify-coverage.tests.ps1" -Message "Scheduled stress verification must exercise coverage aggregation contracts."

Write-Output "Bounded verifier contract tests passed ($assertionCount assertions)."
