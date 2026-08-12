Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$coverageScriptPath = Join-Path $repoRoot "scripts\verify-coverage.ps1"
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
$coverageScript = Get-Content -Raw $coverageScriptPath
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
Assert-Contains -Actual $verifyScript -Expected '$persistenceCoveragePhaseTimeoutSeconds = 1560' -Message "Persistence coverage shards must retain the existing extended phase timeout."
Assert-Contains -Actual $verifyScript -Expected '-TimeoutSeconds $persistenceCoveragePhaseTimeoutSeconds' -Message "Every Persistence coverage shard must use the existing bounded timeout."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "coverage-$($_.BaseName)" -FileName "dotnet" -Arguments $testArguments -TimeoutSeconds 900' -Message "All other coverage projects must retain the existing bounded timeout."
Assert-Contains -Actual $verifyScript -Expected 'foreach ($shard in $persistenceCoverageShards)' -Message "Persistence coverage must execute each declared shard in a fresh test process."
Assert-Contains -Actual $verifyScript -Expected '"--collect:XPlat Code Coverage", "--filter", $shard.Filter, "--logger", "console;verbosity=detailed", "--results-directory", $shardResultsPath' -Message "Each Persistence shard must retain coverage collection, its exact filter, detailed logging, and an isolated result root."
Assert-Contains -Actual $verifyScript -Expected 'Assert-CoverageReportProduced -TestProject $_ -MinimumWriteTimeUtc $shardStartedUtc -SearchRoot $shardResultsPath' -Message "Each Persistence shard must prove that its own invocation produced fresh coverage."
Assert-Contains -Actual $verifyScript -Expected '$coverageArguments += @("-File", (Join-Path $PSScriptRoot "verify-coverage.ps1"), "-MinimumWriteTimeUtc", $coverageStartedUtc.ToString("O"))' -Message "All shard reports must continue through the canonical coverage merger."
Assert-Contains -Actual $coverageScript -Expected 'if (-not $fileLines.ContainsKey($lineNumber) -or $hits -gt $fileLines[$lineNumber]) {' -Message "Split reports must continue to merge duplicate source lines by maximum hit count."

$expectedPersistenceCoverageShards = @(
    [pscustomobject]@{
        Name = "graph-authoring"
        Filter = "(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring)&(VerificationTier!=Stress)"
    }
    [pscustomobject]@{
        Name = "governance"
        Filter = "((FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Audit)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Authority)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Capabilities)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.ContextualRoles)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Credentials)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.HumanInput)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.ToolResults))&(VerificationTier!=Stress)"
    }
    [pscustomobject]@{
        Name = "loops-triggers"
        Filter = "((FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Loops)|(FullyQualifiedName~EmbodySense.Core.Persistence.Tests.Triggers))&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring)&(VerificationTier!=Stress)"
    }
    [pscustomobject]@{
        Name = "remainder"
        Filter = "(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Loops)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Triggers)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Audit)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Authority)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Capabilities)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.ContextualRoles)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.Credentials)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.HumanInput)&(FullyQualifiedName!~EmbodySense.Core.Persistence.Tests.ToolResults)&(VerificationTier!=Stress)"
    }
)
$shardDeclaration = [regex]::Match($verifyScript, '(?ms)^\$persistenceCoverageShards = @\(\r?\n(?<body>.*?)^\)')
Assert-True -Condition $shardDeclaration.Success -Message "The Persistence coverage shard inventory must remain statically inspectable."
$declaredShards = @([regex]::Matches($shardDeclaration.Groups["body"].Value, '(?ms)^\s+\[pscustomobject\]@\{\r?\n\s+Name = "(?<name>[^"]+)"\r?\n\s+Filter = "(?<filter>[^"]+)"\r?\n\s+\}'))
Assert-True -Condition ($declaredShards.Count -eq $expectedPersistenceCoverageShards.Count) -Message "Persistence coverage must retain exactly four shards."
for ($index = 0; $index -lt $expectedPersistenceCoverageShards.Count; $index++) {
    Assert-True -Condition ($declaredShards[$index].Groups["name"].Value -ceq $expectedPersistenceCoverageShards[$index].Name) -Message "Persistence coverage shard order and names must remain deterministic."
    Assert-True -Condition ($declaredShards[$index].Groups["filter"].Value -ceq $expectedPersistenceCoverageShards[$index].Filter) -Message "Persistence coverage shard filters must remain mutually exclusive, exhaustive, and stress-free."
}

$governancePrefixes = @("Audit", "Authority", "Capabilities", "ContextualRoles", "Credentials", "HumanInput", "ToolResults") | ForEach-Object { "EmbodySense.Core.Persistence.Tests.$_" }
$coveragePartitionProbes = @(
    "EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring.GraphContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Loops.Admission.AdmissionContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Triggers.TriggerContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Audit.AuditContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Authority.AuthorityContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.ContextualRoles.RoleContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Credentials.CredentialContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.ToolResults.ToolResultContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Memory.MemoryContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Verification.VerificationContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.Workspace.WorkspaceContractTests.Probe",
    "EmbodySense.Core.Persistence.Tests.FutureNamespace.FutureContractTests.Probe"
)
foreach ($fullyQualifiedName in $coveragePartitionProbes) {
    $isGraphAuthoring = $fullyQualifiedName.StartsWith("EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring", [StringComparison]::Ordinal)
    $isGovernance = @($governancePrefixes | Where-Object { $fullyQualifiedName.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
    $isLoopsOrTriggers = (-not $isGraphAuthoring) -and ($fullyQualifiedName.StartsWith("EmbodySense.Core.Persistence.Tests.Loops", [StringComparison]::Ordinal) -or $fullyQualifiedName.StartsWith("EmbodySense.Core.Persistence.Tests.Triggers", [StringComparison]::Ordinal))
    $isRemainder = -not ($isGraphAuthoring -or $isGovernance -or $isLoopsOrTriggers)
    $partitionCount = @(@($isGraphAuthoring, $isGovernance, $isLoopsOrTriggers, $isRemainder) | Where-Object { $_ }).Count
    Assert-True -Condition ($partitionCount -eq 1) -Message "Persistence coverage partition must select '$fullyQualifiedName' exactly once."
}
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
