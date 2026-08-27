Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$deadlineScriptPath = Join-Path $repoRoot "scripts\verification-deadline.ps1"
$watchdogPolicyScriptPath = Join-Path $repoRoot "scripts\verification-watchdog-policy.ps1"
$qualificationPlanScriptPath = Join-Path $repoRoot "scripts\qualification-plan.ps1"
$qualificationScheduleScriptPath = Join-Path $repoRoot "scripts\qualification-schedule.ps1"
$qualificationScriptPath = Join-Path $repoRoot "scripts\qualify.ps1"
$watchdogScriptPath = Join-Path $repoRoot "scripts\verify-with-watchdog.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$verifyWorkflowPath = Join-Path $repoRoot ".github\workflows\verify.yml"
$qualificationWorkflowPath = Join-Path $repoRoot ".github\workflows\qualification.yml"
$trustedLocalQualificationWorkflowPath = Join-Path $repoRoot ".github\workflows\trusted-local-qualification.yml"
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)

    Assert-True -Condition ($Actual -ceq $Expected) -Message "$Message Expected '$Expected'. Actual '$Actual'."
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)

    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'."
}

function Get-QualificationEstimatedMakespanSeconds {
    param(
        [Parameter(Mandatory = $true)] [object[]]$Phases,
        [Parameter(Mandatory = $true)] [int]$MaximumWorkers,
        [Parameter(Mandatory = $true)] [int]$MaximumResourceCapacity,
        [Parameter(Mandatory = $true)] [int]$MaximumProcessHeavyWorkers,
        [Parameter(Mandatory = $true)] [int]$MaximumCpuBoundWorkers
    )

    $pending = [Collections.Generic.List[object]]::new()
    foreach ($phase in @(Get-VerificationParallelPhaseSchedulingOrder -Phases $Phases -MaximumProcessHeavyWorkers $MaximumProcessHeavyWorkers -MaximumCpuBoundWorkers $MaximumCpuBoundWorkers)) {
        if ($null -eq $phase.PSObject.Properties["EffectiveWeight"]) {
            $phase | Add-Member -NotePropertyName EffectiveWeight -NotePropertyValue $phase.Weight
        }
        else {
            $phase.EffectiveWeight = $phase.Weight
        }
        if ($null -eq $phase.PSObject.Properties["SchedulingDeferrals"]) {
            $phase | Add-Member -NotePropertyName SchedulingDeferrals -NotePropertyValue 0
        }
        else {
            $phase.SchedulingDeferrals = 0
        }
        $pending.Add($phase)
    }

    $running = [Collections.Generic.List[object]]::new()
    $activeResourceCapacity = 0
    $activeResourceClassCounts = @{ Ordinary = 0; CpuBound = 0; ProcessHeavy = 0; ProcessLight = 0 }
    $elapsedSeconds = 0
    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        while ($pending.Count -gt 0 -and $running.Count -lt $MaximumWorkers -and $activeResourceCapacity -lt $MaximumResourceCapacity) {
            $availableResourceClassSlots = @{ Ordinary = $MaximumWorkers; CpuBound = $MaximumCpuBoundWorkers - $activeResourceClassCounts.CpuBound; ProcessHeavy = $MaximumProcessHeavyWorkers - $activeResourceClassCounts.ProcessHeavy; ProcessLight = $MaximumWorkers - $activeResourceClassCounts.ProcessLight }
            $phase = Select-VerificationParallelPhase -Pending $pending -AvailableCapacity ($MaximumResourceCapacity - $activeResourceCapacity) -AvailableResourceClassSlots $availableResourceClassSlots
            if ($null -eq $phase) { break }

            $running.Add([pscustomobject]@{ Phase = $phase; CompletesAtSeconds = $elapsedSeconds + $phase.EstimatedDurationSeconds })
            $activeResourceCapacity += $phase.EffectiveWeight
            $activeResourceClassCounts[$phase.ResourceClass]++
        }

        if ($running.Count -eq 0) { throw "Qualification schedule simulation made no progress." }
        $elapsedSeconds = (@($running | Measure-Object -Property CompletesAtSeconds -Minimum).Minimum)
        foreach ($completed in @($running | Where-Object { $_.CompletesAtSeconds -eq $elapsedSeconds })) {
            $activeResourceCapacity -= $completed.Phase.EffectiveWeight
            $activeResourceClassCounts[$completed.Phase.ResourceClass]--
            [void]$running.Remove($completed)
        }
    }

    return $elapsedSeconds
}

. $deadlineScriptPath
. $watchdogPolicyScriptPath
. $qualificationPlanScriptPath
. $qualificationScheduleScriptPath
. (Join-Path $repoRoot "scripts\verification-parallel.ps1")

$expectedQualificationProjects = @(
    "EmbodySense.Cli.Command.Tests"
    "EmbodySense.Core.Application.Tests"
    "EmbodySense.Core.Clients.Tests"
    "EmbodySense.Core.Common.Tests"
    "EmbodySense.Core.Persistence.Tests"
    "EmbodySense.Core.Startup.Tests"
    "EmbodySense.E2ETests"
    "EmbodySense.IntegrationTests"
    "EmbodySense.Web.Tests"
)
Assert-Equal -Actual (@($script:QualificationTestScheduleProfiles.ProjectName | Sort-Object) -join "|") -Expected ($expectedQualificationProjects -join "|") -Message "Qualification scheduling profiles must equal the canonical nine-project inventory."
$persistenceScheduleProfile = Get-QualificationTestScheduleProfile -ProjectName "EmbodySense.Core.Persistence.Tests"
$startupScheduleProfile = Get-QualificationTestScheduleProfile -ProjectName "EmbodySense.Core.Startup.Tests"
Assert-True -Condition ($persistenceScheduleProfile.EstimatedDurationSeconds -eq 260 -and $startupScheduleProfile.EstimatedDurationSeconds -eq 600) -Message "The protected Persistence and monolithic Startup profiles must retain their evidence-based estimates."
Assert-True -Condition ($persistenceScheduleProfile.ExclusiveOrder -eq 1 -and $startupScheduleProfile.ExclusiveOrder -eq 2) -Message "Persistence must execute before Startup even though Startup has the longer estimate."
Assert-True -Condition ($startupScheduleProfile.EstimatedDurationSeconds -gt (Get-QualificationTestScheduleProfile -ProjectName "EmbodySense.Web.Tests").EstimatedDurationSeconds) -Message "Startup must remain the longest protected qualification suite."
Assert-True -Condition ($persistenceScheduleProfile.TimeoutSeconds -eq 270 -and $startupScheduleProfile.TimeoutSeconds -eq 720) -Message "The two Windows-dominant suites must retain evidence-backed bounded child headroom beneath the global watchdog."
Assert-True -Condition ($persistenceScheduleProfile.Weight -eq 6 -and $startupScheduleProfile.Weight -eq 3 -and $persistenceScheduleProfile.ResourceClass -ceq "ProcessHeavy" -and $startupScheduleProfile.ResourceClass -ceq "ProcessHeavy" -and $persistenceScheduleProfile.Isolation -ceq "Exclusive" -and $startupScheduleProfile.Isolation -ceq "Exclusive") -Message "Persistence and Startup must retain their measured protected posture in separate qualification waves."
foreach ($sharedQualificationProfileExpectation in @(
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Application.Tests"; EstimatedDurationSeconds = 360; TimeoutSeconds = 480; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Web.Tests"; EstimatedDurationSeconds = 210; TimeoutSeconds = 300; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.IntegrationTests"; EstimatedDurationSeconds = 120; TimeoutSeconds = 180; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Clients.Tests"; EstimatedDurationSeconds = 45; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Cli.Command.Tests"; EstimatedDurationSeconds = 35; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Common.Tests"; EstimatedDurationSeconds = 25; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight" }
    [pscustomobject]@{ ProjectName = "EmbodySense.E2ETests"; EstimatedDurationSeconds = 45; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight" }
)) {
    $sharedQualificationProfile = Get-QualificationTestScheduleProfile -ProjectName $sharedQualificationProfileExpectation.ProjectName
    Assert-True -Condition ($sharedQualificationProfile.EstimatedDurationSeconds -eq $sharedQualificationProfileExpectation.EstimatedDurationSeconds -and $sharedQualificationProfile.TimeoutSeconds -eq $sharedQualificationProfileExpectation.TimeoutSeconds -and $sharedQualificationProfile.Weight -eq $sharedQualificationProfileExpectation.Weight -and $sharedQualificationProfile.ResourceClass -ceq $sharedQualificationProfileExpectation.ResourceClass -and $sharedQualificationProfile.Isolation -ceq "Shared") -Message "$($sharedQualificationProfileExpectation.ProjectName) must retain its calibrated shared qualification profile."
}
Assert-True -Condition (@($script:QualificationTestScheduleProfiles | Where-Object { $_.EstimatedDurationSeconds -le 20 -and ($_.Weight -ne 1 -or $_.ResourceClass -cne "ProcessLight") }).Count -eq 0) -Message "Short qualification suites must retain one-unit process-light backfill posture."
$expectedQualificationContracts = @("verify-bounded-phases.tests.ps1", "verify-coverage.tests.ps1", "verify-parallel.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-promotion-fan-in.tests.ps1", "verify-sdk-diagnostics.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1")
Assert-Equal -Actual (@($script:QualificationContractScheduleProfiles.ScriptName | Sort-Object) -join "|") -Expected ($expectedQualificationContracts -join "|") -Message "Qualification contract scheduling profiles must equal the canonical Windows contract inventory."
$preflightScheduleProfile = Get-QualificationContractScheduleProfile -ScriptName "verify-preflight-overlap.tests.ps1"
Assert-True -Condition ($preflightScheduleProfile.Weight -eq 3 -and $preflightScheduleProfile.ResourceClass -ceq "ProcessHeavy" -and $preflightScheduleProfile.Isolation -ceq "Exclusive") -Message "The descendant-heavy preflight contract must retain protected process posture in an exclusive qualification wave."
$watchdogScheduleProfile = Get-QualificationContractScheduleProfile -ScriptName "verify-watchdog.tests.ps1"
Assert-Equal -Actual $watchdogScheduleProfile.TimeoutSeconds -Expected 120 -Message "The source-heavy watchdog contract must retain bounded Windows scan headroom."
$fanInScheduleProfile = Get-QualificationContractScheduleProfile -ScriptName "verify-promotion-fan-in.tests.ps1"
Assert-True -Condition ($fanInScheduleProfile.EstimatedDurationSeconds -eq 20 -and $fanInScheduleProfile.TimeoutSeconds -eq 90 -and $fanInScheduleProfile.Weight -eq 1 -and $fanInScheduleProfile.ResourceClass -ceq "ProcessLight" -and $fanInScheduleProfile.Isolation -ceq "Shared") -Message "The promotion fan-in contract must retain its small shared process-light qualification profile."
$parallelScheduleProfile = Get-QualificationContractScheduleProfile -ScriptName "verify-parallel.tests.ps1"
Assert-True -Condition ($parallelScheduleProfile.EstimatedDurationSeconds -eq 20 -and $parallelScheduleProfile.TimeoutSeconds -eq 90 -and $parallelScheduleProfile.Weight -eq 3 -and $parallelScheduleProfile.ResourceClass -ceq "ProcessHeavy" -and $parallelScheduleProfile.Isolation -ceq "Exclusive") -Message "The descendant-heavy parallel scheduler proof must retain its evidence-based estimate, child bound, and exclusive qualification wave."
Assert-True -Condition ($preflightScheduleProfile.EstimatedDurationSeconds -eq 20 -and $preflightScheduleProfile.TimeoutSeconds -eq 90) -Message "The nested preflight proof must retain its evidence-based estimate and unchanged child bound."
Assert-True -Condition (@($script:QualificationContractScheduleProfiles | Where-Object { $_.ScriptName -cnotin @("verify-parallel.tests.ps1", "verify-preflight-overlap.tests.ps1") -and ($_.Weight -ne 1 -or $_.ResourceClass -cne "ProcessLight" -or $_.Isolation -cne "Shared") }).Count -eq 0) -Message "Measured source/temp-only verifier contracts must retain one-unit shared process-light posture."
Assert-Equal -Actual (@($script:QualificationContractScheduleProfiles | Where-Object { $_.Isolation -ceq "Exclusive" } | Select-Object -ExpandProperty ScriptName | Sort-Object) -join "|") -Expected "verify-parallel.tests.ps1|verify-preflight-overlap.tests.ps1" -Message "Qualification must isolate every contract that recursively schedules its own child process topology."

foreach ($maximumWorkers in 1..4) {
    $qualificationWorkerCount = Get-QualificationWorkerCount -MaximumWorkers $maximumWorkers -HardwareProcessorCount 4
    $qualificationResourceCapacity = Get-QualificationResourceCapacity -WorkerCount $qualificationWorkerCount
    $capacityAwarePersistenceProfile = Get-QualificationTestScheduleProfile -ProjectName "EmbodySense.Core.Persistence.Tests" -ResourceCapacity $qualificationResourceCapacity
    $capacityAwareStartupProfile = Get-QualificationTestScheduleProfile -ProjectName "EmbodySense.Core.Startup.Tests" -ResourceCapacity $qualificationResourceCapacity
    Assert-Equal -Actual $qualificationWorkerCount -Expected $maximumWorkers -Message "Qualification must retain its supported $maximumWorkers-worker posture."
    Assert-Equal -Actual $capacityAwarePersistenceProfile.Weight -Expected ([Math]::Min($persistenceScheduleProfile.Weight, $qualificationResourceCapacity)) -Message "Persistence must reserve a valid protected weight for the $maximumWorkers-worker qualification posture."
    Assert-Equal -Actual $capacityAwareStartupProfile.Weight -Expected ([Math]::Min($startupScheduleProfile.Weight, $qualificationResourceCapacity)) -Message "Startup must reserve a valid protected weight for the $maximumWorkers-worker qualification posture."
    Assert-True -Condition ($capacityAwarePersistenceProfile.Isolation -ceq "Exclusive" -and $capacityAwareStartupProfile.Isolation -ceq "Exclusive" -and $capacityAwarePersistenceProfile.ExclusiveOrder -eq 1 -and $capacityAwareStartupProfile.ExclusiveOrder -eq 2) -Message "Every supported worker posture must retain the ordered protected Persistence and Startup waves."
}
try {
    Get-QualificationWorkerCount -MaximumWorkers 5 -HardwareProcessorCount 4 | Out-Null
    throw "Expected unsupported qualification worker count failure."
}
catch {
    Assert-True -Condition ($_.Exception.Message.IndexOf("cannot validate argument on parameter 'MaximumWorkers'", [StringComparison]::OrdinalIgnoreCase) -ge 0) -Message "Qualification worker policy must reject widening beyond four workers."
}
try {
    Get-QualificationContractScheduleProfile -ScriptName "verify-unmapped.tests.ps1" | Out-Null
    throw "Expected an unmapped qualification contract scheduling failure."
}
catch {
    Assert-True -Condition ($_.Exception.Message.IndexOf("must have exactly one checked-in scheduling profile", [StringComparison]::Ordinal) -ge 0) -Message "A new qualification contract without a profile must fail closed."
}
try {
    Get-QualificationTestScheduleProfile -ProjectName "EmbodySense.Unmapped.Tests" | Out-Null
    throw "Expected an unmapped qualification scheduling failure."
}
catch {
    Assert-True -Condition ($_.Exception.Message.IndexOf("must have exactly one checked-in scheduling profile", [StringComparison]::Ordinal) -ge 0) -Message "A new qualification project without a profile must fail closed."
}

$docsPlan = Get-QualificationPlan -ChangedPaths @("README.md", "docs/VERIFICATION.md")
Assert-True -Condition (-not $docsPlan.RequiresBuild -and -not $docsPlan.RequiresFrontend -and $docsPlan.TestProjects.Count -eq 0) -Message "Documentation-only changes must not trigger unrelated compilation or tests."

$applicationTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/RunnerTests.cs"
$applicationTestNamespaces = @{ $applicationTestPath = "EmbodySense.Core.Application.Tests.Loops" }
$applicationTestClasses = @{ $applicationTestPath = "EmbodySense.Core.Application.Tests.Loops.RunnerTests" }
$applicationPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Application/Loops/Runner.cs", $applicationTestPath) -TestClassesByPath $applicationTestClasses
Assert-True -Condition ($applicationPlan.RequiresBuild -and $applicationPlan.RequiresArchitecture -and $applicationPlan.RequiresCSharpFormat) -Message "Application C# changes must compile, format, and retain architecture validation."
$expectedApplicationConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($applicationPlan.TestProjects -join "|") -Expected ($expectedApplicationConsumers -join "|") -Message "Application production changes must execute every direct test-project consumer."
Assert-True -Condition (@($applicationPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Application production consumers must run as complete suites even when the same test class also changed."
$broadApplicationVerifierPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Application/Loops/Runner.cs", "src/EmbodySense.Core.Persistence/HumanReview/HumanReviewContinuationRecoveryStore.cs", "scripts/qualify.ps1")
Assert-True -Condition ($broadApplicationVerifierPlan.RequiresBuild -and $broadApplicationVerifierPlan.RequiresVerifierContracts -and $broadApplicationVerifierPlan.RequiresArchitecture -and $broadApplicationVerifierPlan.RequiresCSharpFormat) -Message "The broad Application-plus-verifier edge must model every prerequisite, shared verifier contract, format, and architecture phase."
$expectedBroadApplicationVerifierProjects = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj"
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj"
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj"
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
Assert-Equal -Actual ($broadApplicationVerifierPlan.TestProjects -join "|") -Expected ($expectedBroadApplicationVerifierProjects -join "|") -Message "The broad Application-plus-verifier edge must retain every selected suite observed on the hosted Windows stress edge."
$broadApplicationVerifierSharedWavePhases = @()
foreach ($testProject in $broadApplicationVerifierPlan.TestProjects) {
    $testProfile = Get-QualificationTestScheduleProfile -ProjectName ([IO.Path]::GetFileNameWithoutExtension($testProject)) -ResourceCapacity 8
    if ($testProfile.Isolation -ceq "Shared") {
        $broadApplicationVerifierSharedWavePhases += [pscustomobject]@{ Name = "tests-$($testProfile.ProjectName)"; EstimatedDurationSeconds = $testProfile.EstimatedDurationSeconds; Weight = $testProfile.Weight; ResourceClass = $testProfile.ResourceClass }
    }
}
$broadApplicationVerifierSharedWavePhases += @(
    [pscustomobject]@{ Name = "tests-architecture"; EstimatedDurationSeconds = 20; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "format-changed"; EstimatedDurationSeconds = 45; Weight = 3; ResourceClass = "CpuBound" }
    [pscustomobject]@{ Name = "git-diff-check"; EstimatedDurationSeconds = 5; Weight = 1; ResourceClass = "Ordinary" }
)
foreach ($sharedContractProfile in @($script:QualificationContractScheduleProfiles | Where-Object { $_.Isolation -ceq "Shared" })) {
    $broadApplicationVerifierSharedWavePhases += [pscustomobject]@{ Name = "contract-$([IO.Path]::GetFileNameWithoutExtension($sharedContractProfile.ScriptName))"; EstimatedDurationSeconds = $sharedContractProfile.EstimatedDurationSeconds; Weight = $sharedContractProfile.Weight; ResourceClass = $sharedContractProfile.ResourceClass }
}
Assert-True -Condition (@($broadApplicationVerifierSharedWavePhases.Name | Where-Object { $_ -in @("tests-EmbodySense.Core.Persistence.Tests", "tests-EmbodySense.Core.Startup.Tests") }).Count -eq 0) -Message "The shared wave must exclude the proven-unsafe Persistence and Startup pair."
$broadApplicationVerifierSharedWaveSeconds = Get-QualificationEstimatedMakespanSeconds -Phases $broadApplicationVerifierSharedWavePhases -MaximumWorkers 4 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1
$broadApplicationVerifierProtectedTestSeconds = (@($broadApplicationVerifierPlan.TestProjects | ForEach-Object { Get-QualificationTestScheduleProfile -ProjectName ([IO.Path]::GetFileNameWithoutExtension($_)) -ResourceCapacity 8 } | Where-Object { $_.Isolation -ceq "Exclusive" } | Sort-Object -Property EstimatedDurationSeconds -Descending | Measure-Object -Property EstimatedDurationSeconds -Sum).Sum)
$broadApplicationVerifierProtectedOrder = @($broadApplicationVerifierPlan.TestProjects | ForEach-Object { Get-QualificationTestScheduleProfile -ProjectName ([IO.Path]::GetFileNameWithoutExtension($_)) -ResourceCapacity 8 } | Where-Object { $_.Isolation -ceq "Exclusive" } | Sort-Object -Property ExclusiveOrder | Select-Object -ExpandProperty ProjectName)
$broadApplicationVerifierExclusiveContractSeconds = (@($script:QualificationContractScheduleProfiles | Where-Object { $_.Isolation -ceq "Exclusive" } | Measure-Object -Property EstimatedDurationSeconds -Sum).Sum)
$broadApplicationVerifierCriticalPathSeconds = 150 + $broadApplicationVerifierSharedWaveSeconds + $broadApplicationVerifierProtectedTestSeconds + $broadApplicationVerifierExclusiveContractSeconds
Assert-Equal -Actual $broadApplicationVerifierSharedWaveSeconds -Expected 425 -Message "The complete broad Application-plus-verifier shared wave must retain its calibrated scheduler critical path."
Assert-Equal -Actual $broadApplicationVerifierProtectedTestSeconds -Expected 860 -Message "The separately bounded Persistence and monolithic Startup waves must retain their evidence-based total estimate."
Assert-Equal -Actual ($broadApplicationVerifierProtectedOrder -join "|") -Expected "EmbodySense.Core.Persistence.Tests|EmbodySense.Core.Startup.Tests" -Message "The broad Application-plus-verifier model must execute Persistence before Startup in protected waves."
Assert-Equal -Actual $broadApplicationVerifierExclusiveContractSeconds -Expected 40 -Message "The separate evidence-based exclusive verifier waves must retain their bounded total estimate."
Assert-True -Condition ($broadApplicationVerifierCriticalPathSeconds -eq 1475 -and $broadApplicationVerifierCriticalPathSeconds -le 1620 -and $broadApplicationVerifierCriticalPathSeconds -lt 1680) -Message "The complete broad Application-plus-verifier model must retain at least one minute of headroom beneath the exact outer qualification bound."
Assert-True -Condition ((150 + $persistenceScheduleProfile.TimeoutSeconds + $startupScheduleProfile.TimeoutSeconds + $broadApplicationVerifierSharedWaveSeconds + $broadApplicationVerifierExclusiveContractSeconds) -eq 1605 -and (150 + $persistenceScheduleProfile.TimeoutSeconds + $startupScheduleProfile.TimeoutSeconds + $broadApplicationVerifierSharedWaveSeconds + $broadApplicationVerifierExclusiveContractSeconds) -le 1620 -and (150 + $persistenceScheduleProfile.TimeoutSeconds + $startupScheduleProfile.TimeoutSeconds + $broadApplicationVerifierSharedWaveSeconds + $broadApplicationVerifierExclusiveContractSeconds) -lt 1680) -Message "The protected child ceilings and calibrated shared wave must leave at least one minute for qualification watchdog overhead."

$cliCommandPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Cli.Command/RunCommand.cs")
$expectedCliCommandConsumers = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($cliCommandPlan.TestProjects -join "|") -Expected ($expectedCliCommandConsumers -join "|") -Message "CLI Command production changes must execute the owning suite and real-process Integration behavior."
Assert-True -Condition (@($cliCommandPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "CLI Command production consumers must run as complete suites."

$clientsPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs")
$expectedClientsConsumers = @(
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($clientsPlan.TestProjects -join "|") -Expected ($expectedClientsConsumers -join "|") -Message "Clients production changes must execute the owning suite, Startup composition, and app-server Integration behavior."
Assert-True -Condition (@($clientsPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Clients production consumers must run as complete suites."

$developerInstructionsPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Common/Governance/Tools/EmbodySenseDeveloperInstructions.cs")
$expectedDeveloperInstructionsConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($developerInstructionsPlan.TestProjects -join "|") -Expected ($expectedDeveloperInstructionsConsumers -join "|") -Message "Shared developer-instruction changes must execute every behavioral consumer suite."
Assert-True -Condition (@($developerInstructionsPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Shared developer-instruction consumers must run as complete suites."

$commonPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Common/Governance/Tools/ToolResultRetentionLimits.cs")
$expectedCommonConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($commonPlan.TestProjects -join "|") -Expected ($expectedCommonConsumers -join "|") -Message "General Common changes must execute every direct test-project consumer."
Assert-True -Condition (@($commonPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Common production consumers must run as complete suites."

$persistencePlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Persistence/Capabilities/CapabilityCatalogStore.cs")
$expectedPersistenceConsumers = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
Assert-Equal -Actual ($persistencePlan.TestProjects -join "|") -Expected ($expectedPersistenceConsumers -join "|") -Message "Persistence production changes must execute the owning suite, CLI initialization behavior, Startup composition, hosted Web behavior, non-browser E2E, and direct Integration behavior."
Assert-True -Condition (@($persistencePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Persistence production consumers must run as complete suites."

$focusedImplementationPath = "src/EmbodySense.Core.Persistence/Loops/CustomLoopAttemptCancellationHost.cs"
$focusedImplementationTestPath = "tests/EmbodySense.Core.Persistence.Tests/Loops/CustomLoopWorkspaceExecutionGateTests.cs"
$focusedImplementationTestClass = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspaceExecutionGateTests"
$focusedImplementationPlan = Get-QualificationPlan -ChangedPaths @($focusedImplementationPath)
Assert-Equal -Actual ($focusedImplementationPlan.TestProjects -join "|") -Expected "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj" -Message "A reviewed internal implementation must select only its checked public-boundary test project."
Assert-Equal -Actual @($focusedImplementationPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A reviewed internal implementation must not broaden to a namespace filter."
Assert-Equal -Actual ($focusedImplementationPlan.TestSelections[0].Classes -join "|") -Expected $focusedImplementationTestClass -Message "A reviewed internal implementation must select its exact checked public-boundary test class."
Assert-True -Condition ($focusedImplementationPlan.RequiresBuild -and $focusedImplementationPlan.RequiresArchitecture -and $focusedImplementationPlan.RequiresCSharpFormat -and $focusedImplementationPlan.RequiresVerifierContracts) -Message "A focused implementation change must retain compilation, architecture, formatting, and mapping-contract validation."

$focusedImplementationAndTestPlan = Get-QualificationPlan -ChangedPaths @($focusedImplementationPath, $focusedImplementationTestPath) -TestClassesByPath @{ $focusedImplementationTestPath = $focusedImplementationTestClass }
Assert-Equal -Actual @($focusedImplementationAndTestPlan.TestSelections).Count -Expected 1 -Message "A focused implementation and its directly changed boundary test must retain one owning project."
Assert-Equal -Actual ($focusedImplementationAndTestPlan.TestSelections[0].Classes -join "|") -Expected $focusedImplementationTestClass -Message "A focused implementation and its directly changed boundary test must deduplicate to one exact class."
$focusedImplementationTestOnlyPlan = Get-QualificationPlan -ChangedPaths @($focusedImplementationTestPath) -TestClassesByPath @{ $focusedImplementationTestPath = $focusedImplementationTestClass }
Assert-True -Condition $focusedImplementationTestOnlyPlan.RequiresVerifierContracts -Message "Changing a mapped public-boundary test must revalidate its focused implementation mapping."

$focusedImplementationSource = Get-Content -LiteralPath (Join-Path $repoRoot $focusedImplementationPath) -Raw
Assert-True -Condition (Test-QualificationFocusedImplementationSource -Content $focusedImplementationSource) -Message "The reviewed cancellation host must remain one top-level internal sealed non-partial implementation type."
Assert-True -Condition (-not (Test-QualificationFocusedImplementationSource -Content "public sealed class Candidate {}")) -Message "A public implementation must not use focused qualification."
Assert-True -Condition (-not (Test-QualificationFocusedImplementationSource -Content "internal partial class Candidate {}")) -Message "A partial implementation must not use focused qualification."
Assert-True -Condition (-not (Test-QualificationFocusedImplementationSource -Content "internal class Candidate {}")) -Message "A non-sealed implementation must not use focused qualification."
Assert-True -Condition (-not (Test-QualificationFocusedImplementationSource -Content "internal sealed class First {}`ninternal sealed class Second {}")) -Message "Multiple top-level implementations must not use one focused mapping."
Assert-True -Condition (-not (Test-QualificationFocusedImplementationSource -Content "internal sealed class Candidate {")) -Message "A syntax-invalid implementation must not use focused qualification."

$focusedPrivateMethodPath = "src/EmbodySense.Core.Application/Loops/Execution/Custom/CustomLoopLifecycleService.cs"
$focusedPrivateMethodTestClass = "EmbodySense.Core.Application.Tests.Loops.Execution.Custom.CustomLoopLifecycleServiceTests"
$focusedPrivateMethodPlan = Get-QualificationPlan -ChangedPaths @($focusedPrivateMethodPath)
Assert-Equal -Actual ($focusedPrivateMethodPlan.TestProjects -join "|") -Expected "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj" -Message "A reviewed private-method change must select only its checked behavioral project."
Assert-Equal -Actual ($focusedPrivateMethodPlan.TestSelections[0].Classes -join "|") -Expected $focusedPrivateMethodTestClass -Message "A reviewed private-method change must select its exact checked behavioral class."
$focusedPrivateMethodFallbackPlan = Get-QualificationPlan -ChangedPaths @($focusedPrivateMethodPath) -FocusedImplementationFallbackPaths @($focusedPrivateMethodPath)
Assert-Equal -Actual ($focusedPrivateMethodFallbackPlan.TestProjects -join "|") -Expected ($expectedApplicationConsumers -join "|") -Message "A private-method mapping that does not apply to the exact edge must restore every ordinary Application consumer."
Assert-True -Condition (@($focusedPrivateMethodFallbackPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "A focused implementation fallback must run each owning and consumer project unfiltered."
Assert-Equal -Actual ($focusedPrivateMethodFallbackPlan.FocusedImplementationFallbackPaths -join "|") -Expected $focusedPrivateMethodPath -Message "The qualification plan must retain exact fallback evidence."
Assert-True -Condition $focusedPrivateMethodFallbackPlan.RequiresVerifierContracts -Message "A focused implementation fallback must still run mapping-contract validation."

$privateMethodBase = @'
public sealed class Candidate
{
    public void Visible() { }

    private int Handle()
    {
        return 1;
    }
}
'@
$privateMethodHead = $privateMethodBase.Replace("return 1;", "return 2;")
Assert-True -Condition (Test-QualificationFocusedPrivateMethodEdge -BaseContent $privateMethodBase -HeadContent $privateMethodHead -TypeName "Candidate" -MemberName "Handle") -Message "A body-only private method change must remain eligible for focused qualification."
Assert-True -Condition (-not (Test-QualificationFocusedPrivateMethodEdge -BaseContent $privateMethodBase -HeadContent $privateMethodHead.Replace("public void Visible() { }", "public void Visible() { Console.WriteLine(); }") -TypeName "Candidate" -MemberName "Handle")) -Message "A second changed member must invalidate focused private-method qualification."
Assert-True -Condition (-not (Test-QualificationFocusedPrivateMethodEdge -BaseContent $privateMethodBase -HeadContent $privateMethodHead.Replace("private int Handle()", "internal int Handle()") -TypeName "Candidate" -MemberName "Handle")) -Message "A changed private method signature must invalidate focused qualification."
Assert-True -Condition (-not (Test-QualificationFocusedPrivateMethodEdge -BaseContent $privateMethodBase -HeadContent $privateMethodHead.Replace("public sealed class Candidate", "public sealed partial class Candidate") -TypeName "Candidate" -MemberName "Handle")) -Message "A partial public type must invalidate focused private-method qualification."

$focusedContractPath = "src/EmbodySense.Core.Common/Loops/Execution/CustomLoopAttemptCancellationContractLimits.cs"
$focusedContractPlan = Get-QualificationPlan -ChangedPaths @($focusedContractPath)
$expectedFocusedContractProjects = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
)
Assert-Equal -Actual ($focusedContractPlan.TestProjects -join "|") -Expected ($expectedFocusedContractProjects -join "|") -Message "A reviewed one-member contract must select only its complete checked behavioral boundary."
Assert-Equal -Actual ($focusedContractPlan.TestSelections[0].Classes -join "|") -Expected $focusedPrivateMethodTestClass -Message "The cancellation contract must retain its lifecycle behavior class."
Assert-Equal -Actual ($focusedContractPlan.TestSelections[1].Classes -join "|") -Expected $focusedImplementationTestClass -Message "The cancellation contract must retain its remote-host behavior class."
Assert-Equal -Actual @(Get-QualificationFocusedImplementationMappingsForPath -Path $focusedPrivateMethodPath).Count -Expected 2 -Message "Changing a known contract consumer must revalidate both its private-method edge and the shared contract reference map."

$constantContractSource = "public static class DeadlineContract { public const int Seconds = 10; }"
Assert-True -Condition (Test-QualificationPublicConstantContractSource -Content $constantContractSource -TypeName "DeadlineContract" -MemberName "Seconds") -Message "One bounded public integer constant must remain eligible for reviewed contract qualification."
Assert-True -Condition (-not (Test-QualificationPublicConstantContractSource -Content "public static class DeadlineContract { public const int Seconds = 10; public const int Other = 1; }" -TypeName "DeadlineContract" -MemberName "Seconds")) -Message "An added contract member must invalidate focused qualification."
Assert-True -Condition (-not (Test-QualificationPublicConstantContractSource -Content "public static class DeadlineContract { public static int Seconds => 10; }" -TypeName "DeadlineContract" -MemberName "Seconds")) -Message "Executable public contract behavior must invalidate constant-only qualification."
Assert-True -Condition (-not (Test-QualificationPublicConstantContractSource -Content "public static class DeadlineContract { public const int Seconds = 0; }" -TypeName "DeadlineContract" -MemberName "Seconds")) -Message "An unbounded contract value must invalidate focused qualification."

$startupPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Startup/Runtime/AgentRuntime.cs")
$expectedStartupConsumers = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
Assert-Equal -Actual ($startupPlan.TestProjects -join "|") -Expected ($expectedStartupConsumers -join "|") -Message "Startup production changes must execute every direct interface consumer suite."
Assert-True -Condition (@($startupPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Startup production consumers must run as complete suites."

$testOnlyPlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestClassesByPath $applicationTestClasses
Assert-Equal -Actual $testOnlyPlan.TestSelections.Count -Expected 1 -Message "A test-only edit must select exactly its owning project."
Assert-Equal -Actual @($testOnlyPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A direct test edit must not broaden to its containing namespace."
Assert-Equal -Actual @($testOnlyPlan.TestSelections[0].Classes).Count -Expected 1 -Message "A test-only edit must not expand to its entire large test assembly."
Assert-Equal -Actual $testOnlyPlan.TestSelections[0].Classes[0] -Expected "EmbodySense.Core.Application.Tests.Loops.RunnerTests" -Message "A test-only edit must retain its exact filename-matching class as the fail-closed test filter."
Assert-True -Condition (-not $testOnlyPlan.RequiresVerifierContracts) -Message "An unrelated test-only edit must not pay the verifier-contract wave."

$testProjectPlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj")
Assert-True -Condition ($testProjectPlan.RequiresBuild -and $testProjectPlan.RequiresArchitecture) -Message "A changed test project must compile and execute the architecture boundary lane."
Assert-Equal -Actual ($testProjectPlan.TestProjects -join "|") -Expected "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj" -Message "A changed test project must retain its complete owning suite."
Assert-True -Condition (@($testProjectPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "A changed test project must run unfiltered."

$deletedTestSourcePlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestNamespacesByPath @{ $applicationTestPath = [string[]]::new(0) }
Assert-Equal -Actual $deletedTestSourcePlan.TestSelections.Count -Expected 1 -Message "A deleted test source must retain its surviving owning project."
Assert-Equal -Actual @($deletedTestSourcePlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "Deleting the final test in a namespace must run the remaining project unfiltered instead of scheduling an empty namespace."
Assert-Equal -Actual @($deletedTestSourcePlan.TestSelections[0].Classes).Count -Expected 0 -Message "Deleting a test source must not leave a stale class filter."

$helperConsumerPlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestClassesByPath $applicationTestClasses -FocusedHelperRelevantPaths @($applicationTestPath)
Assert-True -Condition $helperConsumerPlan.RequiresVerifierContracts -Message "A syntax-proven focused-helper consumer change must revalidate the checked helper map."

$unchangedHelperConsumerRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestClassesByPath $applicationTestClasses -FocusedHelperRelevantPaths @("tests/EmbodySense.Core.Application.Tests/Loops/UnchangedTests.cs") | Out-Null
}
catch {
    $unchangedHelperConsumerRejected = $_.Exception.Message.Contains("unchanged path", [StringComparison]::Ordinal)
}
Assert-True -Condition $unchangedHelperConsumerRejected -Message "Focused-helper relevance must be bound to the exact changed-path inventory."

$secondApplicationTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/OtherRunnerTests.cs"
$sameNamespacePlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath, $secondApplicationTestPath) -TestClassesByPath @{ $applicationTestPath = "EmbodySense.Core.Application.Tests.Loops.RunnerTests"; $secondApplicationTestPath = "EmbodySense.Core.Application.Tests.Loops.OtherRunnerTests" }
Assert-Equal -Actual @($sameNamespacePlan.TestSelections[0].Classes).Count -Expected 2 -Message "Changed direct tests in one namespace must retain both exact classes."

$helperTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/RunnerFixture.cs"
$helperTestPlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath, $helperTestPath) -TestNamespacesByPath @{ $helperTestPath = "" } -TestClassesByPath $applicationTestClasses
Assert-Equal -Actual @($helperTestPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A helper edit must restore the full owning project even when a direct test in the same namespace also changed."
Assert-Equal -Actual @($helperTestPlan.TestSelections[0].Classes).Count -Expected 0 -Message "A helper edit must clear direct class filters when it restores the full owning project."

foreach ($helperModelPath in @(
    "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseBudget.cs",
    "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseClassification.cs"
)) {
    $helperModelMapping = Get-QualificationFocusedHelperMapping -Path $helperModelPath
    $helperModelPlan = Get-QualificationPlan -ChangedPaths @($helperModelPath) -TestNamespacesByPath @{ $helperModelPath = @($helperModelMapping.ConsumerNamespaces) }
    Assert-Equal -Actual @($helperModelPlan.TestSelections).Count -Expected 1 -Message "A helper model edit must retain its owning test project."
    Assert-Equal -Actual @($helperModelPlan.TestSelections[0].Namespaces).Count -Expected @($helperModelMapping.ConsumerNamespaces).Count -Message "A reviewed helper model must select every checked consumer namespace without expanding to the full project."
}

$crossNamespaceHelperPath = "tests/EmbodySense.Core.Application.Tests/Capabilities/CapabilityArtifactTestData.cs"
$crossNamespaceHelperSource = Get-Content -LiteralPath (Join-Path $repoRoot $crossNamespaceHelperPath) -Raw
Assert-True -Condition (-not (Test-QualificationContainsDirectXunitTest -Content $crossNamespaceHelperSource)) -Message "The reviewed CapabilityArtifactTestData helper must be identified from syntax as a non-test input."
$crossNamespaceHelperMapping = Get-QualificationFocusedHelperMapping -Path $crossNamespaceHelperPath
$crossNamespaceHelperPlan = Get-QualificationPlan -ChangedPaths @($crossNamespaceHelperPath) -TestNamespacesByPath @{ $crossNamespaceHelperPath = @($crossNamespaceHelperMapping.ConsumerNamespaces) }
Assert-Equal -Actual @($crossNamespaceHelperPlan.TestSelections[0].Namespaces).Count -Expected 2 -Message "A reviewed cross-namespace helper must select every checked consumer namespace."
Assert-True -Condition ($crossNamespaceHelperPlan.TestSelections[0].Namespaces -ccontains "EmbodySense.Core.Application.Tests.Credentials") -Message "The CapabilityArtifactTestData mapping must retain its Credentials consumer."

$integrationHelperPath = "tests/EmbodySense.IntegrationTests/Core/Governance/Tools/ImmediateToolResultRetentionStore.cs"
$integrationHelperMapping = Get-QualificationFocusedHelperMapping -Path $integrationHelperPath
$integrationHelperPlan = Get-QualificationPlan -ChangedPaths @($integrationHelperPath) -TestClassesByPath @{ $integrationHelperPath = @($integrationHelperMapping.ConsumerClasses) }
Assert-Equal -Actual @($integrationHelperPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A reviewed single-class helper must not broaden to its containing namespace."
Assert-Equal -Actual @($integrationHelperPlan.TestSelections[0].Classes).Count -Expected 1 -Message "A reviewed single-class helper must remain focused."
Assert-Equal -Actual $integrationHelperPlan.TestSelections[0].Classes[0] -Expected "EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerTests" -Message "The result-retention helper must select its exact ToolBroker consumer class."
$integrationConsumerPath = "tests/EmbodySense.IntegrationTests/Core/Governance/Tools/ToolBrokerTests.cs"
$integrationHelperAndConsumerPlan = Get-QualificationPlan -ChangedPaths @($integrationHelperPath, $integrationConsumerPath) -TestClassesByPath @{ $integrationHelperPath = @($integrationHelperMapping.ConsumerClasses); $integrationConsumerPath = "EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerTests" }
Assert-Equal -Actual @($integrationHelperAndConsumerPlan.TestSelections[0].Classes).Count -Expected 1 -Message "A helper and its directly changed consumer must deduplicate to one exact class."

$missingSelectionRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @($applicationTestPath) | Out-Null
}
catch {
    $missingSelectionRejected = $_.Exception.Message.Contains("exactly one authenticated namespace or class selection", [StringComparison]::Ordinal)
}
Assert-True -Condition $missingSelectionRejected -Message "A changed test source without authenticated class or namespace ownership must fail closed."

$parsedNamespace = Get-QualificationDeclaredTestNamespace -Path $applicationTestPath -Content "namespace EmbodySense.Core.Application.Tests.Loops;`npublic sealed class RunnerTests {}"
Assert-Equal -Actual $parsedNamespace -Expected "EmbodySense.Core.Application.Tests.Loops" -Message "File-scoped test namespaces must be parsed exactly."

$syntaxAwareSource = @'
namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class SourceLayoutTests
{
    private const string Example = """
namespace Example;
""";

    // namespace Commented.Example;
}
'@
$syntaxAwareNamespace = Get-QualificationDeclaredTestNamespace -Path $applicationTestPath -Content $syntaxAwareSource
Assert-Equal -Actual $syntaxAwareNamespace -Expected "EmbodySense.Core.Application.Tests.Loops" -Message "Namespace selection must use the C# syntax tree and ignore namespace-shaped text in raw strings and comments."

$directTestSource = @'
namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class RunnerTests
{
    [Fact]
    public void Runs() {}

    private const string Example = """
[Theory]
""";
}
'@
Assert-True -Condition (Test-QualificationContainsDirectXunitTest -Content $directTestSource) -Message "A real xUnit method attribute must permit direct-test namespace selection."
Assert-True -Condition (-not (Test-QualificationContainsDirectXunitTest -Content $syntaxAwareSource)) -Message "Test-shaped text in strings or comments must not make a helper namespace filterable."
$directTestClasses = @(Get-QualificationDirectXunitTestClasses -Path $applicationTestPath -Content $directTestSource)
Assert-Equal -Actual ($directTestClasses -join "|") -Expected "EmbodySense.Core.Application.Tests.Loops.RunnerTests" -Message "A direct xUnit source must produce its exact filename-matching class filter."
Assert-True -Condition (-not (Test-QualificationContainsIdentifierReference -Content 'private const string Example = "RunnerTests";' -Identifier "RunnerTests")) -Message "Test-class consumer discovery must ignore class-shaped string content."

$partialTestSource = $directTestSource.Replace("public sealed class RunnerTests", "public sealed partial class RunnerTests")
$partialTestClasses = @(Get-QualificationDirectXunitTestClasses -Path "tests/EmbodySense.Core.Application.Tests/Loops/RunnerTests.Wait.cs" -Content $partialTestSource)
Assert-Equal -Actual ($partialTestClasses -join "|") -Expected "EmbodySense.Core.Application.Tests.Loops.RunnerTests" -Message "A dotted partial xUnit fragment must select its filename-prefix-matching canonical class."
$nonPartialFragmentRejected = $false
try {
    Get-QualificationDirectXunitTestClasses -Path "tests/EmbodySense.Core.Application.Tests/Loops/RunnerTests.Wait.cs" -Content $directTestSource | Out-Null
}
catch {
    $nonPartialFragmentRejected = $_.Exception.Message.Contains("partial class", [StringComparison]::Ordinal)
}
Assert-True -Condition $nonPartialFragmentRejected -Message "A dotted xUnit fragment must fail closed unless its canonical class is partial."

$sharedDirectTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialRunMaterializerTests.cs"
$sharedDirectTestClass = "EmbodySense.Core.Application.Tests.Loops.Sequential.GovernedLoopSequentialRunMaterializerTests"
$currentCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-True -Condition ($LASTEXITCODE -eq 0 -and $currentCommit -match '^[0-9a-f]{40}$') -Message "The test-class consumer contract must bind one exact repository commit."
$sharedDirectTestConsumers = @(Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $currentCommit -Path $sharedDirectTestPath -TestClass $sharedDirectTestClass)
$expectedSharedDirectTestConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/HumanReview/HumanReviewAdmissionServiceTests.cs",
    "tests/EmbodySense.Core.Application.Tests/HumanReview/HumanReviewContinuationConsumerTests.cs",
    "tests/EmbodySense.Core.Application.Tests/HumanReview/HumanReviewDecisionTestData.cs",
    "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialBindingResolverTests.cs",
    "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialFrontierMachineTests.cs",
    "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialInvocationCoordinatorTests.cs"
)
Assert-Equal -Actual ($sharedDirectTestConsumers -join "|") -Expected ($expectedSharedDirectTestConsumers -join "|") -Message "A direct xUnit class used as cross-file test infrastructure must expose every exact-head consumer and force full-project qualification."

$customFactSource = @'
namespace EmbodySense.E2ETests.Web;

public sealed class BrowserFlowTests
{
    [InstalledBrowserFact]
    public void Runs() {}

    private sealed class InstalledBrowserFactAttribute : FactAttribute {}
}
'@
Assert-True -Condition (Test-QualificationContainsDirectXunitTest -Content $customFactSource) -Message "A file-local FactAttribute subtype must retain its direct-test namespace selection."
$customFactClasses = @(Get-QualificationDirectXunitTestClasses -Path "tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs" -Content $customFactSource)
Assert-Equal -Actual ($customFactClasses -join "|") -Expected "EmbodySense.E2ETests.Web.BrowserFlowTests" -Message "Custom FactAttribute methods must retain the exact declaring test class."
$browserQualificationFilter = Get-QualificationTestFilter -ProjectName "EmbodySense.E2ETests" -Namespaces @() -Classes $customFactClasses
Assert-Equal -Actual $browserQualificationFilter -Expected "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)" -Message "An installed-browser test edit must qualify the non-browser E2E slice without selecting and excluding the same class."
$nonBrowserQualificationFilter = Get-QualificationTestFilter -ProjectName "EmbodySense.E2ETests" -Namespaces @() -Classes @("EmbodySense.E2ETests.Web.WebClientFlowTests")
Assert-Equal -Actual $nonBrowserQualificationFilter -Expected "(FullyQualifiedName~EmbodySense.E2ETests.Web.WebClientFlowTests.)&(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)" -Message "A non-browser E2E test edit must retain its exact class while installed-browser tests remain promotion-owned."
$browserTestPlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs") -TestClassesByPath @{ "tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs" = $customFactClasses }
Assert-Equal -Actual ($browserTestPlan.TestProjects -join "|") -Expected "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj" -Message "An installed-browser test edit must retain its owning E2E project in qualification."
Assert-True -Condition ($browserTestPlan.TestSelections.Count -eq 1 -and @($browserTestPlan.TestSelections[0].Namespaces).Count -eq 0 -and @($browserTestPlan.TestSelections[0].Classes).Count -eq 0) -Message "Installed-browser source changes must be represented as a full non-browser E2E qualification selection while promotion owns the changed class."

$mismatchedClassRejected = $false
try {
    Get-QualificationDirectXunitTestClasses -Path $applicationTestPath -Content $customFactSource | Out-Null
}
catch {
    $mismatchedClassRejected = $_.Exception.Message.Contains("does not belong to owning project", [StringComparison]::Ordinal) -or $_.Exception.Message.Contains("filename-matching", [StringComparison]::Ordinal)
}
Assert-True -Condition $mismatchedClassRejected -Message "A direct test class that cannot be bound to its project path and filename must fail closed."

$crossProjectRenamePlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Application/Loops/OldRunner.cs", "src/EmbodySense.Core.Common/Loops/NewRunner.cs")
Assert-Equal -Actual $crossProjectRenamePlan.TestProjects.Count -Expected 6 -Message "A cross-project rename into Common must select both owners, the former owner's downstream boundary, and every direct Common consumer."
Assert-True -Condition ($crossProjectRenamePlan.TestProjects -ccontains "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj") -Message "A cross-project rename must retain the former owner."
Assert-True -Condition ($crossProjectRenamePlan.TestProjects -ccontains "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj") -Message "A cross-project rename must select the destination owner."
Assert-True -Condition ($crossProjectRenamePlan.TestProjects -ccontains "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj") -Message "A cross-project rename must retain the Application owner's downstream integration boundary."

$webPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Web/wwwroot/js/governed.js")
Assert-True -Condition ($webPlan.RequiresBuild -and $webPlan.RequiresFrontend) -Message "Web assets must retain both their owning Web build/tests and frontend checks."
$expectedWebConsumers = @(
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
Assert-Equal -Actual ($webPlan.TestProjects -join "|") -Expected ($expectedWebConsumers -join "|") -Message "Web changes must execute the owning suite and non-browser hosted E2E behavior."
Assert-True -Condition (@($webPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Web production consumers must run as complete suites; the E2E runner separately excludes installed-browser tests."

$verifierPlan = Get-QualificationPlan -ChangedPaths @("scripts/qualify.ps1", ".github/workflows/qualification.yml")
Assert-True -Condition ($verifierPlan.RequiresVerifierContracts -and -not $verifierPlan.RequiresBuild -and $verifierPlan.TestProjects.Count -eq 0) -Message "Verifier-only changes must run verifier contracts without an unrelated solution build."
Assert-True -Condition ($verifierPlan.RequiresFrontend -and $verifierPlan.RequiresWorkflowValidation) -Message "Workflow changes must install the pinned frontend toolchain and parse every workflow through Prettier."
$dependabotPlan = Get-QualificationPlan -ChangedPaths @(".github/dependabot.yml")
Assert-True -Condition ($dependabotPlan.RequiresFrontend -and $dependabotPlan.RequiresWorkflowValidation -and $dependabotPlan.RequiresVerifierContracts) -Message "Dependabot configuration changes must install the pinned parser and validate GitHub YAML syntax."
Assert-True -Condition (-not $dependabotPlan.RequiresBuild -and $dependabotPlan.TestProjects.Count -eq 0) -Message "Dependabot syntax validation must not trigger unrelated compilation or test suites."

$sharedTestPlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Tests.Support/TestWorkspace.cs")
Assert-Equal -Actual $sharedTestPlan.TestProjects.Count -Expected 9 -Message "Shared test infrastructure must conservatively select every production test project."

$linkedSharedSourcePlan = Get-QualificationPlan -ChangedPaths @("tests/Shared/TestCapabilityAdmissionFactory.cs")
Assert-Equal -Actual $linkedSharedSourcePlan.TestProjects.Count -Expected 9 -Message "Linked shared test sources must conservatively select every production test project."

$linkedCommonFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Common.Tests/Authority/Grants/AuthorityGrantTestFixture.cs")
Assert-Equal -Actual $linkedCommonFixturePlan.TestProjects.Count -Expected 2 -Message "A linked Common fixture must select both the Common and Persistence consumers."
Assert-True -Condition ($linkedCommonFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj") -Message "A linked Common fixture must retain its source project."
Assert-True -Condition ($linkedCommonFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj") -Message "A linked Common fixture must execute its Persistence consumer."
Assert-True -Condition (@($linkedCommonFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Linked test inputs must run every consuming suite without focused filtering."

$linkedGraphFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Common.Tests/GovernedLoopGraphTestFixture.cs")
Assert-Equal -Actual ($linkedGraphFixturePlan.TestProjects -join "|") -Expected "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj|tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj" -Message "The linked graph fixture must select its Common owner and Persistence consumer."
Assert-True -Condition (@($linkedGraphFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "The linked graph fixture must run both consuming suites without focused filtering."

$linkedApplicationFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Application.Tests/Governance/Authority/Grants/AuthorityGrantApplicationTestFixture.cs")
Assert-Equal -Actual $linkedApplicationFixturePlan.TestProjects.Count -Expected 2 -Message "A linked Application fixture must select both the Application and Startup consumers."
Assert-True -Condition ($linkedApplicationFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj") -Message "A linked Application fixture must retain its source project."
Assert-True -Condition ($linkedApplicationFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj") -Message "A linked Application fixture must execute its Startup consumer."
Assert-True -Condition (@($linkedApplicationFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Linked Application test inputs must run every consuming suite without focused filtering."

$linkedModelProfileFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Application.Tests/GovernedModelProfileApplicationTestFixture.cs")
Assert-Equal -Actual ($linkedModelProfileFixturePlan.TestProjects -join "|") -Expected "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj|tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj" -Message "The linked model-profile fixture must select its Application owner and Startup consumer."
Assert-True -Condition (@($linkedModelProfileFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "The linked model-profile fixture must run both consuming suites without focused filtering."

$linkedEffectFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Application.Tests/Loops/Execution/Effects/GovernedLoopEffectAttemptTestFixture.cs")
Assert-Equal -Actual $linkedEffectFixturePlan.TestProjects.Count -Expected 2 -Message "A linked effect-attempt fixture must select both the Application and Startup consumers."
Assert-True -Condition ($linkedEffectFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj") -Message "A linked effect-attempt fixture must retain its source project."
Assert-True -Condition ($linkedEffectFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj") -Message "A linked effect-attempt fixture must execute its Startup consumer."
Assert-True -Condition (@($linkedEffectFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Linked effect-attempt inputs must run every consuming suite without focused filtering."

$linkedCommandActionFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Application.Tests/CommandActions/CommandActionApplicationTestData.cs")
Assert-Equal -Actual ($linkedCommandActionFixturePlan.TestProjects -join "|") -Expected "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj|tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj" -Message "The linked command-Action fixture must select its Application owner and Startup consumer."
Assert-True -Condition (@($linkedCommandActionFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "The linked command-Action fixture must run both consuming suites without focused filtering."

$linkedSequentialFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialApplicationTestFixture.cs")
Assert-Equal -Actual ($linkedSequentialFixturePlan.TestProjects -join "|") -Expected "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj|tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj" -Message "The linked sequential fixture must select its Application owner and Startup consumer."
Assert-True -Condition (@($linkedSequentialFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "The linked sequential fixture must run both consuming suites without focused filtering."

$browserHostPlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.E2EBrowserHost/Program.cs", "tests/EmbodySense.E2EBrowserHost/EmbodySense.E2EBrowserHost.csproj")
Assert-True -Condition $browserHostPlan.RequiresBuild -Message "The external browser host must compile during qualification."
Assert-Equal -Actual ($browserHostPlan.TestProjects -join "|") -Expected "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj" -Message "The external browser host must execute its owning E2E consumer without becoming a separate test lane."
Assert-True -Condition (@($browserHostPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "External browser-host edits must run the complete E2E consumer suite."

$frontendConfigurationPlan = Get-QualificationPlan -ChangedPaths @("eslint.config.js", ".prettierignore")
Assert-True -Condition ($frontendConfigurationPlan.RequiresFrontend -and $frontendConfigurationPlan.TestProjects.Count -eq 0) -Message "Tracked lint and formatting configuration must run frontend verification without unrelated .NET tests."

$runSettingsPlan = Get-QualificationPlan -ChangedPaths @("tests/verification-pull-request.runsettings", "tests/verification-stress.runsettings")
Assert-True -Condition ($runSettingsPlan.RequiresBuild -and $runSettingsPlan.RequiresVerifierContracts) -Message "Changed runsettings must compile and verify their orchestration contracts."
Assert-Equal -Actual $runSettingsPlan.TestProjects.Count -Expected 9 -Message "Changed runsettings must conservatively execute every affected full test project."
Assert-True -Condition (@($runSettingsPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Runsettings changes cannot retain focused test selections."

$attributesPlan = Get-QualificationPlan -ChangedPaths @(".gitattributes")
Assert-True -Condition ($attributesPlan.RequiresBuild -and $attributesPlan.RequiresArchitecture) -Message "Repository attribute changes must retain build and architecture validation."
Assert-Equal -Actual $attributesPlan.TestProjects.Count -Expected 9 -Message "Repository attribute changes must conservatively execute every full test project."

$deletedTestProject = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"
$survivingTestProjects = @($script:QualificationTestProjects | Where-Object { $_ -cne $deletedTestProject })
$deletedProjectPlan = Get-QualificationPlan -ChangedPaths @($deletedTestProject, "EmbodySense.sln") -AvailableTestProjects $survivingTestProjects
Assert-Equal -Actual $deletedProjectPlan.TestProjects.Count -Expected 8 -Message "A project deletion must retain every surviving suite selected by the changed solution."
Assert-True -Condition ($deletedProjectPlan.TestProjects -cnotcontains $deletedTestProject) -Message "Qualification must never schedule a test project absent from the exact head."
$noTestProjectsPlan = Get-QualificationPlan -ChangedPaths @("EmbodySense.sln") -AvailableTestProjects @()
Assert-Equal -Actual $noTestProjectsPlan.TestProjects.Count -Expected 0 -Message "An explicitly empty exact-head test inventory must not fall back to deleted canonical paths."

$unknownAvailableProjectRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @("README.md") -AvailableTestProjects @("tests/Unknown.Tests/Unknown.Tests.csproj") | Out-Null
}
catch {
    $unknownAvailableProjectRejected = $_.Exception.Message.Contains("unknown available test project", [StringComparison]::Ordinal)
}
Assert-True -Condition $unknownAvailableProjectRejected -Message "Available-project evidence must remain inside the canonical test-project inventory."

$unclassifiedRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @("unexpected-root/file.bin") | Out-Null
}
catch {
    $unclassifiedRejected = $_.Exception.Message.Contains("unclassified changed paths", [StringComparison]::Ordinal)
}
Assert-True -Condition $unclassifiedRejected -Message "Unknown paths must fail closed until the ownership map is updated."

$trackedPaths = @(& git -C $repoRoot ls-files)
Assert-True -Condition ($LASTEXITCODE -eq 0 -and $trackedPaths.Count -gt 0) -Message "The qualification ownership contract must enumerate the tracked repository."
$trackedTestNamespaces = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$trackedTestClasses = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($trackedPath in $trackedPaths) {
    if (Test-QualificationFilterableTestSource -Path $trackedPath) {
        $trackedSource = Get-Content -LiteralPath (Join-Path $repoRoot $trackedPath) -Raw
        $trackedNamespace = Get-QualificationDeclaredTestNamespace -Path $trackedPath -Content $trackedSource
        if (Test-QualificationContainsDirectXunitTest -Content $trackedSource) {
            $trackedTestClasses.Add($trackedPath, @(Get-QualificationDirectXunitTestClasses -Path $trackedPath -Content $trackedSource))
        }
        else {
            $focusedHelperMapping = Get-QualificationFocusedHelperMapping -Path $trackedPath
            if ($null -eq $focusedHelperMapping) {
                $trackedTestNamespaces.Add($trackedPath, [string[]]::new(0))
            }
            elseif (@($focusedHelperMapping.ConsumerClasses).Count -gt 0) {
                $trackedTestClasses.Add($trackedPath, [string[]]@($focusedHelperMapping.ConsumerClasses))
            }
            else {
                $trackedTestNamespaces.Add($trackedPath, [string[]]@($focusedHelperMapping.ConsumerNamespaces))
            }
        }
    }
}
$trackedPlan = Get-QualificationPlan -ChangedPaths $trackedPaths -TestNamespacesByPath $trackedTestNamespaces -TestClassesByPath $trackedTestClasses
Assert-Equal -Actual $trackedPlan.ChangedPaths.Count -Expected $trackedPaths.Count -Message "Every currently tracked path must have explicit qualification ownership."

$mappedHelperPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($mapping in $script:QualificationFocusedHelperMappings) {
    Assert-True -Condition $mappedHelperPaths.Add($mapping.Path) -Message "Focused helper mappings must have unique paths."
    Assert-True -Condition ($trackedPaths -ccontains $mapping.Path) -Message "Focused helper '$($mapping.Path)' must be tracked."
    $helperSource = Get-Content -LiteralPath (Join-Path $repoRoot $mapping.Path) -Raw
    Assert-True -Condition (-not (Test-QualificationContainsDirectXunitTest -Content $helperSource)) -Message "Focused helper '$($mapping.Path)' must not directly declare an xUnit test."
    $usesNamespaceMap = @($mapping.ConsumerNamespaces).Count -gt 0
    $usesClassMap = @($mapping.ConsumerClasses).Count -gt 0
    Assert-True -Condition ($usesNamespaceMap -ne $usesClassMap) -Message "Focused helper '$($mapping.Path)' must use exactly one namespace or class consumer map."
    $helperIdentifier = [IO.Path]::GetFileNameWithoutExtension($mapping.Path)
    $actualConsumerNamespaces = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $actualConsumerClasses = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($candidatePath in $trackedPaths) {
        if ($candidatePath -ceq $mapping.Path -or -not $candidatePath.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) -or $null -eq (Get-QualificationTestProject -Path $candidatePath)) {
            continue
        }

        $candidateSource = Get-Content -LiteralPath (Join-Path $repoRoot $candidatePath) -Raw
        if (-not (Test-QualificationContainsFocusedHelperReference -Content $candidateSource -HelperIdentifiers @($helperIdentifier))) {
            continue
        }

        if ($usesClassMap) {
            $candidateClasses = @(Get-QualificationDirectXunitTestClasses -Path $candidatePath -Content $candidateSource)
            Assert-True -Condition ($candidateClasses.Count -gt 0) -Message "Class-focused helper '$($mapping.Path)' has a non-test consumer '$candidatePath'."
            foreach ($candidateClass in $candidateClasses) {
                [void]$actualConsumerClasses.Add($candidateClass)
            }
        }
        else {
            [void]$actualConsumerNamespaces.Add((Get-QualificationDeclaredTestNamespace -Path $candidatePath -Content $candidateSource))
        }
    }

    Assert-Equal -Actual (@($actualConsumerNamespaces | Sort-Object) -join "|") -Expected (@($mapping.ConsumerNamespaces | Sort-Object) -join "|") -Message "Focused helper '$($mapping.Path)' must enumerate every syntax-proven consumer namespace."
    Assert-Equal -Actual (@($actualConsumerClasses | Sort-Object) -join "|") -Expected (@($mapping.ConsumerClasses | Sort-Object) -join "|") -Message "Focused helper '$($mapping.Path)' must enumerate every syntax-proven consumer class."
}

$mappedImplementationPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$mappedImplementationTests = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($mapping in $script:QualificationFocusedImplementationMappings) {
    Assert-True -Condition $mappedImplementationPaths.Add($mapping.Path) -Message "Focused implementation mappings must have unique production paths."
    Assert-True -Condition ($trackedPaths -ccontains $mapping.Path) -Message "Focused implementation '$($mapping.Path)' must be tracked."
    $implementationSource = Get-Content -LiteralPath (Join-Path $repoRoot $mapping.Path) -Raw
    switch ($mapping.Kind) {
        "InternalSealed" {
            Assert-True -Condition (Test-QualificationFocusedImplementationSource -Content $implementationSource) -Message "Focused implementation '$($mapping.Path)' must remain one top-level internal sealed non-partial type."
        }
        "PrivateMethod" {
            Assert-True -Condition (Test-QualificationFocusedPrivateMethodEdge -BaseContent $implementationSource -HeadContent $implementationSource -TypeName $mapping.TypeName -MemberName $mapping.MemberName) -Message "Focused private-method implementation '$($mapping.Path)' must retain its exact public type and private method shape."
            Assert-Equal -Actual @($mapping.ReferencePaths).Count -Expected 0 -Message "A private-method mapping must not declare public-contract reference paths."
        }
        "PublicConstantContract" {
            Assert-True -Condition (Test-QualificationPublicConstantContractSource -Content $implementationSource -TypeName $mapping.TypeName -MemberName $mapping.MemberName) -Message "Focused public contract '$($mapping.Path)' must remain one bounded integer constant."
            $actualReferencePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($identifier in @($mapping.TypeName, $mapping.MemberName)) {
                foreach ($referencePath in @(Get-QualificationExactIdentifierReferencePaths -RepositoryRoot $repoRoot -Commit $currentCommit -Identifier $identifier)) {
                    [void]$actualReferencePaths.Add($referencePath)
                }
            }
            Assert-Equal -Actual (@($actualReferencePaths | Sort-Object) -join "|") -Expected (@($mapping.ReferencePaths | Sort-Object) -join "|") -Message "Focused public contract '$($mapping.Path)' must enumerate every exact-head C# reference."
        }
        default {
            throw "Focused implementation '$($mapping.Path)' has unsupported kind '$($mapping.Kind)'."
        }
    }
    Assert-True -Condition (@($mapping.Tests).Count -gt 0) -Message "Focused implementation '$($mapping.Path)' must retain at least one public-boundary test."

    foreach ($testMapping in @($mapping.Tests)) {
        $mappingKey = "$($mapping.Path)|$($testMapping.Path)|$($testMapping.Class)"
        Assert-True -Condition $mappedImplementationTests.Add($mappingKey) -Message "Focused implementation test entries must be unique."
        Assert-True -Condition ($trackedPaths -ccontains $testMapping.Path) -Message "Focused implementation test '$($testMapping.Path)' must be tracked."
        $mappedTestProject = Get-QualificationTestProject -Path $testMapping.Path
        Assert-True -Condition ($null -ne $mappedTestProject -and $script:QualificationTestProjects -ccontains $mappedTestProject) -Message "Focused implementation test '$($testMapping.Path)' must belong to a canonical test project."
        $mappedTestSource = Get-Content -LiteralPath (Join-Path $repoRoot $testMapping.Path) -Raw
        $mappedClasses = @(Get-QualificationDirectXunitTestClasses -Path $testMapping.Path -Content $mappedTestSource)
        Assert-Equal -Actual ($mappedClasses -join "|") -Expected $testMapping.Class -Message "Focused implementation test '$($testMapping.Path)' must retain its exact filename-matching xUnit class."
        $externalConsumers = @(Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $currentCommit -Path $testMapping.Path -TestClass $testMapping.Class)
        Assert-Equal -Actual $externalConsumers.Count -Expected 0 -Message "Focused implementation test '$($testMapping.Path)' must not be cross-file test infrastructure."
    }
}

foreach ($consumerProject in $script:QualificationTestProjects) {
    $consumerProjectPath = Join-Path $repoRoot $consumerProject
    [xml]$consumerProjectXml = Get-Content -LiteralPath $consumerProjectPath -Raw
    $compileItems = [Collections.Generic.List[object]]::new()
    foreach ($itemGroup in @($consumerProjectXml.Project.ItemGroup)) {
        $compileProperty = $itemGroup.PSObject.Properties["Compile"]
        if ($null -ne $compileProperty) {
            foreach ($compileItem in @($compileProperty.Value)) {
                $compileItems.Add($compileItem)
            }
        }
    }
    foreach ($compileItem in $compileItems) {
        if ($null -eq $compileItem -or [string]::IsNullOrWhiteSpace($compileItem.Include)) {
            continue
        }

        $linkedFullPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $consumerProjectPath) $compileItem.Include))
        $linkedPath = [IO.Path]::GetRelativePath($repoRoot, $linkedFullPath).Replace('\', '/')
        if ($linkedPath.StartsWith("tests/Shared/", [StringComparison]::Ordinal)) {
            continue
        }

        $ownerProject = Get-QualificationTestProject -Path $linkedPath
        if ($null -eq $ownerProject -or $ownerProject -ceq $consumerProject) {
            continue
        }

        $linkedMapping = Get-QualificationLinkedTestMapping -Path $linkedPath
        Assert-True -Condition ($null -ne $linkedMapping) -Message "Cross-project linked test input '$linkedPath' must have explicit qualification ownership."
        Assert-True -Condition ($linkedMapping.TestProjects -ccontains $ownerProject) -Message "Cross-project linked test input '$linkedPath' must retain its source project '$ownerProject'."
        Assert-True -Condition ($linkedMapping.TestProjects -ccontains $consumerProject) -Message "Cross-project linked test input '$linkedPath' must select consumer '$consumerProject'."
    }
}

function Get-DirectTestProjectConsumers {
    param([Parameter(Mandatory = $true)] [string]$ReferencedProject)

    $consumers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($testProject in $script:QualificationTestProjects) {
        $testProjectPath = Join-Path $repoRoot $testProject
        [xml]$testProjectXml = Get-Content -LiteralPath $testProjectPath -Raw
        foreach ($itemGroup in @($testProjectXml.Project.ItemGroup)) {
            $projectReferenceProperty = $itemGroup.PSObject.Properties["ProjectReference"]
            if ($null -eq $projectReferenceProperty) {
                continue
            }
            foreach ($projectReference in @($projectReferenceProperty.Value)) {
                if ($null -eq $projectReference -or [string]::IsNullOrWhiteSpace($projectReference.Include)) {
                    continue
                }

                $referencedFullPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $testProjectPath) $projectReference.Include))
                $referencedPath = [IO.Path]::GetRelativePath($repoRoot, $referencedFullPath).Replace('\', '/')
                if ($referencedPath -ceq $ReferencedProject) {
                    [void]$consumers.Add($testProject)
                }
            }
        }
    }

    return [string[]]@($consumers | Sort-Object)
}

foreach ($consumerContract in @(
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Application/"; Project = "src/EmbodySense.Core.Application/EmbodySense.Core.Application.csproj"; Label = "Application" },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Clients/"; Project = "src/EmbodySense.Core.Clients/EmbodySense.Core.Clients.csproj"; Label = "Clients" },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Common/"; Project = "src/EmbodySense.Core.Common/EmbodySense.Core.Common.csproj"; Label = "Common" },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Persistence/"; Project = "src/EmbodySense.Core.Persistence/EmbodySense.Core.Persistence.csproj"; Label = "Persistence" },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Startup/"; Project = "src/EmbodySense.Core.Startup/EmbodySense.Core.Startup.csproj"; Label = "Startup" }
)) {
    $sourceMappings = @($script:QualificationSourceMappings | Where-Object { $_.Prefix -ceq $consumerContract.Prefix })
    Assert-Equal -Actual $sourceMappings.Count -Expected 1 -Message "$($consumerContract.Label) must have exactly one explicit source-ownership mapping."
    $requiredConsumers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($directConsumer in @(Get-DirectTestProjectConsumers -ReferencedProject $consumerContract.Project)) {
        [void]$requiredConsumers.Add($directConsumer)
    }
    foreach ($behavioralConsumer in @($script:QualificationBehavioralConsumerMappings | Where-Object { $_.SourceProject -ceq $consumerContract.Project })) {
        Assert-True -Condition ($script:QualificationTestProjects -ccontains $behavioralConsumer.TestProject) -Message "Behavioral consumer '$($behavioralConsumer.TestProject)' must be a canonical qualification test project."
        $evidenceFullPath = Join-Path $repoRoot $behavioralConsumer.EvidencePath
        Assert-True -Condition (Test-Path -LiteralPath $evidenceFullPath -PathType Leaf) -Message "Behavioral consumer evidence '$($behavioralConsumer.EvidencePath)' must exist."
        $evidenceContent = Get-Content -LiteralPath $evidenceFullPath -Raw
        Assert-True -Condition ($evidenceContent.IndexOf("using $($behavioralConsumer.RequiredNamespace);", [StringComparison]::Ordinal) -ge 0) -Message "Behavioral consumer evidence '$($behavioralConsumer.EvidencePath)' must retain its '$($behavioralConsumer.RequiredNamespace)' boundary."
        [void]$requiredConsumers.Add($behavioralConsumer.TestProject)
    }
    Assert-Equal -Actual (@($requiredConsumers | Sort-Object) -join "|") -Expected (@($sourceMappings[0].TestProjects | Sort-Object) -join "|") -Message "$($consumerContract.Label) qualification ownership must match every direct and checked behavioral test-project consumer."
}

$lfMarker = "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=600`n"
$crlfMarker = "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=599.999`r`n"
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $lfMarker) -Expected 1 -Message "One exact LF completion marker must be accepted."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $crlfMarker) -Expected 1 -Message "One exact Windows CRLF completion marker must be accepted."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput "VERIFY_COMPLETE schema_version=1 status=passed`r`n") -Expected 0 -Message "A partial completion marker must be rejected."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput "prefix VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=1`n") -Expected 0 -Message "A prefixed completion marker must be rejected."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput ($lfMarker + $crlfMarker)) -Expected 2 -Message "Duplicate exact completion markers must remain visible to fail-closed disposition."
$solutionMarker = "VERIFY_COMPLETE schema_version=1 component=solution status=passed elapsed_seconds=1.25`n"
$staticMarker = "VERIFY_COMPLETE schema_version=1 component=static-contracts status=passed elapsed_seconds=2`n"
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $solutionMarker -ExpectedComponent "solution") -Expected 1 -Message "The solution child must emit one identity-bearing terminal marker."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $staticMarker -ExpectedComponent "static-contracts") -Expected 1 -Message "The static child must emit one identity-bearing terminal marker."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $solutionMarker -ExpectedComponent "static-contracts") -Expected 0 -Message "A component marker for the wrong child must fail closed."

$deadlineTicks = [TimeSpan]::FromSeconds(600).Ticks
Assert-True -Condition (-not (Test-VerificationDeadlineExceeded -ElapsedTicks $deadlineTicks -DeadlineTicks $deadlineTicks)) -Message "The live watchdog decision must retain the inclusive exact 600-second boundary."
Assert-True -Condition (Test-VerificationDeadlineExceeded -ElapsedTicks ($deadlineTicks + 1) -DeadlineTicks $deadlineTicks) -Message "The live watchdog decision must reject the first timer tick over 600 seconds."

$exactDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks $deadlineTicks -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-True -Condition $exactDeadline.Succeeded -Message "Exactly 600 seconds must remain inside the inclusive deadline."
Assert-Equal -Actual $exactDeadline.Code -Expected "passed" -Message "Successful disposition code mismatch."

$overDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks ($deadlineTicks + 1) -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $overDeadline.Code -Expected "deadline-exceeded" -Message "One tick over 600 seconds must fail."

$promotionDeadlineTicks = [TimeSpan]::FromSeconds(1500).Ticks
$exactPromotionDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks $promotionDeadlineTicks -DeadlineTicks $promotionDeadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-True -Condition $exactPromotionDeadline.Succeeded -Message "Exactly 1500 seconds must remain inside the explicit promotion deadline."
$overPromotionDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks ($promotionDeadlineTicks + 1) -DeadlineTicks $promotionDeadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $overPromotionDeadline.Code -Expected "deadline-exceeded" -Message "One tick over the bounded promotion deadline must fail."

Assert-VerificationWatchdogDeadlineContract -Qualification $true -VerificationComponent "Full" -DeadlineSeconds 1680
Assert-VerificationWatchdogDeadlineContract -Qualification $true -VerificationComponent "full" -DeadlineSeconds 1680
Assert-VerificationWatchdogDeadlineContract -Qualification $false -VerificationComponent "StaticContracts" -DeadlineSeconds 600
Assert-VerificationWatchdogDeadlineContract -Qualification $false -VerificationComponent "staticcontracts" -DeadlineSeconds 600
Assert-VerificationWatchdogDeadlineContract -Qualification $false -VerificationComponent "Solution" -DeadlineSeconds 1500
Assert-VerificationWatchdogDeadlineContract -Qualification $false -VerificationComponent "sOlUtIoN" -DeadlineSeconds 1500
Assert-VerificationWatchdogDeadlineContract -Qualification $false -VerificationComponent "Full" -DeadlineSeconds 600
Assert-VerificationWatchdogDeadlineContract -Qualification $false -VerificationComponent "Full" -DeadlineSeconds 1200
foreach ($invalidDeadlineCase in @(
    [pscustomobject]@{ Qualification = $true; Component = "Full"; DeadlineSeconds = 1681; Expected = "Qualification requires the exact 1680-second watchdog deadline" }
    [pscustomobject]@{ Qualification = $false; Component = "StaticContracts"; DeadlineSeconds = 601; Expected = "Promotion component 'StaticContracts' requires the exact 600-second watchdog deadline" }
    [pscustomobject]@{ Qualification = $false; Component = "Solution"; DeadlineSeconds = 1501; Expected = "Promotion component 'Solution' requires the exact 1500-second watchdog deadline" }
    [pscustomobject]@{ Qualification = $false; Component = "Full"; DeadlineSeconds = 1201; Expected = "Full verification requires a watchdog deadline between 1 and 1200 seconds" }
    [pscustomobject]@{ Qualification = $false; Component = "Full"; DeadlineSeconds = 1500; Expected = "Full verification requires a watchdog deadline between 1 and 1200 seconds" }
)) {
    try {
        Assert-VerificationWatchdogDeadlineContract -Qualification $invalidDeadlineCase.Qualification -VerificationComponent $invalidDeadlineCase.Component -DeadlineSeconds $invalidDeadlineCase.DeadlineSeconds
        throw "Expected $($invalidDeadlineCase.Component) deadline contract failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected $invalidDeadlineCase.Expected -Message "A $($invalidDeadlineCase.Component) watchdog deadline above its mode-specific bound must fail closed."
    }
}

$childTimeout = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 1 -CompletionMarkerCount 0 -ChildTimedOut $true -CancellationRequested $false
Assert-Equal -Actual $childTimeout.Code -Expected "child-timeout" -Message "A child phase timeout must be retained as its own failure."

$cancelled = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $false -CompletionMarkerCount 0 -ChildTimedOut $false -CancellationRequested $true
Assert-Equal -Actual $cancelled.Code -Expected "cancelled" -Message "Cancellation must fail closed."

$missingMarker = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 0 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $missingMarker.Code -Expected "completion-evidence-invalid" -Message "Missing completion evidence must fail closed."

$duplicateMarker = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 2 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $duplicateMarker.Code -Expected "completion-evidence-invalid" -Message "Duplicate completion evidence must fail closed."

$partialProcess = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $false -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $partialProcess.Code -Expected "incomplete-process" -Message "A live process cannot pass from partial evidence."

$failedChild = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 17 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $failedChild.Code -Expected "child-failed" -Message "A nonzero verifier exit must fail despite a marker."

$watchdogScript = Get-Content -LiteralPath $watchdogScriptPath -Raw
$watchdogPolicyScript = Get-Content -LiteralPath $watchdogPolicyScriptPath -Raw
$qualificationPlanScript = Get-Content -LiteralPath $qualificationPlanScriptPath -Raw
$qualificationScript = Get-Content -LiteralPath $qualificationScriptPath -Raw
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$workflow = Get-Content -LiteralPath $verifyWorkflowPath -Raw
$qualificationWorkflow = (Get-Content -LiteralPath $qualificationWorkflowPath -Raw).Replace("`r`n", "`n")
$trustedLocalQualificationWorkflow = (Get-Content -LiteralPath $trustedLocalQualificationWorkflowPath -Raw).Replace("`r`n", "`n")
Assert-True -Condition ($watchdogScript.IndexOf('[int]$DeadlineSeconds = 600', [StringComparison]::Ordinal) -ge 0) -Message "The external watchdog must default to exactly 600 seconds."
Assert-True -Condition ($watchdogScript.IndexOf('[ValidateRange(1, 1680)]', [StringComparison]::Ordinal) -ge 0) -Message "No accepted watchdog override may exceed the bounded 1680-second qualification window."
Assert-True -Condition ($watchdogScript.IndexOf('Assert-VerificationWatchdogDeadlineContract -Qualification $Qualification -VerificationComponent $VerificationComponent -DeadlineSeconds $DeadlineSeconds', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must bind its exact mode-specific deadline before starting a verifier process."
Assert-True -Condition ($watchdogPolicyScript.IndexOf('$script:VerificationFullWatchdogMaximumDeadlineSeconds = 1200', [StringComparison]::Ordinal) -ge 0) -Message "Local Full verification must retain its prior 1200-second maximum while Solution owns the 1500-second budget."
Assert-True -Condition ($watchdogScript.IndexOf('[switch]$Qualification', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must expose the bounded qualification child explicitly."
Assert-True -Condition ($watchdogScript.IndexOf('[ValidateSet("Full", "Solution", "StaticContracts")]', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must expose explicit component modes for the hosted fan-out."
Assert-True -Condition ($watchdogScript.IndexOf('"StaticContracts" { "static-contracts"; break }', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must map the static component to the exact hyphenated verifier marker identity."
Assert-True -Condition ($watchdogScript.IndexOf('"qualify.ps1"', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must execute through its dedicated bounded orchestrator."
Assert-True -Condition ($watchdogScript.IndexOf('Qualification requires exact -BaseCommit and -HeadCommit values.', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must bind its exact comparison commits."
Assert-True -Condition ($qualificationScript.IndexOf('git diff --no-renames --name-only --diff-filter=ACMRDTUXB "$mergeBase..$HeadCommit"', [StringComparison]::Ordinal) -ge 0) -Message "Qualification selection must derive both sides of renames from the exact merge-base-to-head diff."
Assert-True -Condition ($qualificationScript.IndexOf('git cat-file blob $objectName', [StringComparison]::Ordinal) -ge 0) -Message "Test-only qualification must authenticate its class or helper namespace from an exact edge blob, including deleted or renamed sources."
Assert-True -Condition ($qualificationScript.IndexOf('foreach ($commit in @($HeadCommit, $mergeBase))', [StringComparison]::Ordinal) -ge 0) -Message "Focused-helper consumers must be syntax-checked on both sides of the exact edge."
Assert-True -Condition ($qualificationScript.IndexOf('Test-QualificationFocusedImplementationSource -Content $implementationContent', [StringComparison]::Ordinal) -ge 0) -Message "Focused implementation selection must authenticate the production type shape on both sides of the exact edge."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationDirectXunitTestClasses -Path $mappedTestPath -Content $mappedTestContent', [StringComparison]::Ordinal) -ge 0) -Message "Focused implementation selection must authenticate its mapped test class from the exact head."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $HeadCommit -Path $mappedTestPath', [StringComparison]::Ordinal) -ge 0) -Message "Focused implementation selection must reject mapped test classes used as cross-file infrastructure."
Assert-True -Condition ($qualificationScript.IndexOf('-TestClassesByPath $testClassesByPath -FocusedHelperRelevantPaths @($focusedHelperRelevantPaths) -FocusedImplementationFallbackPaths @($focusedImplementationFallbackPaths) -AvailableTestProjects $availableTestProjects', [StringComparison]::Ordinal) -ge 0) -Message "The exact-edge qualifier must bind class, helper-map, conservative-fallback, and surviving-project evidence into its plan."
Assert-True -Condition ($qualificationScript.IndexOf('. (Join-Path $PSScriptRoot "verification-temp.ps1")', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must reuse the canonical bounded lane-temporary-path contract."
Assert-True -Condition ($qualificationScript.IndexOf('elseif ($runningOnWindows) { [IO.Path]::GetTempPath() } else { "/tmp" }', [StringComparison]::Ordinal) -ge 0) -Message "Local Unix qualification must avoid the platform's long per-user temporary path for named-pipe fixtures."
Assert-True -Condition ($qualificationScript.IndexOf('Get-VerificationLaneFixturePath -PhysicalTempRoot $qualificationPhysicalTempRoot -RunIdentity $qualificationFixtureRunIdentity -LaneIdentity $projectName', [StringComparison]::Ordinal) -ge 0) -Message "Every selected test project must receive a short collision-resistant lane fixture root."
Assert-True -Condition ($qualificationScript.IndexOf('Join-Path $fixtureRoot $projectName', [StringComparison]::Ordinal) -lt 0) -Message "Qualification must not append long project names beneath one already-long temporary root."
Assert-True -Condition ($qualificationScript.IndexOf('Test-QualificationCommitPath -Path $drawioPath -Commit $HeadCommit', [StringComparison]::Ordinal) -ge 0) -Message "Deleted draw.io paths must be skipped from exact-head XML validation."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationBlobContent -Path $drawioPath -Commits @($HeadCommit)', [StringComparison]::Ordinal) -ge 0) -Message "Surviving draw.io XML must be read from the authenticated exact head blob."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationTestFilter -ProjectName $projectName -Namespaces @($testSelection.Namespaces) -Classes @($testSelection.Classes)', [StringComparison]::Ordinal) -ge 0) -Message "Test-only edits must execute their authenticated classes or helper namespaces rather than the entire owning assembly."
Assert-True -Condition ($qualificationScript.IndexOf('if (-not (Test-QualificationCommitPath -Path $normalizedPath -Commit $HeadCommit))', [StringComparison]::Ordinal) -ge 0) -Message "Deleted test sources must be detected against the exact head before namespace selection."
Assert-True -Condition ($qualificationScript.IndexOf('$testNamespacesByPath[$normalizedPath] = [string[]]::new(0)', [StringComparison]::Ordinal) -ge 0) -Message "A deleted test source must restore full-project selection for the surviving owner."
Assert-True -Condition ($qualificationPlanScript.IndexOf('[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content)', [StringComparison]::Ordinal) -ge 0) -Message "Changed test class and namespace ownership must come from a Roslyn C# syntax tree, not a source-text regex."
Assert-True -Condition ($qualificationPlanScript.IndexOf('TestProjects = @(', [StringComparison]::Ordinal) -ge 0) -Message "Source ownership must support explicit downstream consumer closures."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationDirectXunitTestClasses -Path $normalizedPath -Content $content', [StringComparison]::Ordinal) -ge 0) -Message "Only syntax-authenticated filename-matching xUnit classes may retain class-filtered qualification."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $HeadCommit', [StringComparison]::Ordinal) -ge 0) -Message "A direct xUnit class used by another exact-head test source must restore full-project qualification."
$qualificationContractStart = $qualificationScript.IndexOf('if ($plan.RequiresVerifierContracts)', [StringComparison]::Ordinal)
$qualificationContractEnd = $qualificationScript.IndexOf('if ($plan.RequiresWorkflowValidation)', $qualificationContractStart, [StringComparison]::Ordinal)
Assert-True -Condition ($qualificationContractStart -ge 0 -and $qualificationContractEnd -gt $qualificationContractStart) -Message "Qualification must retain one explicit verifier-contract scheduling block."
$qualificationContractBlock = $qualificationScript.Substring($qualificationContractStart, $qualificationContractEnd - $qualificationContractStart)
Assert-Equal -Actual ([regex]::Matches($qualificationContractBlock, 'Invoke-QualificationWave').Count) -Expected 0 -Message "Shared verifier contracts must enter the post-build test wave without an internal barrier."
Assert-Equal -Actual ([regex]::Matches($qualificationScript, 'Invoke-QualificationWave').Count) -Expected 5 -Message "Qualification must define one wave helper and invoke prerequisite, protected-test, shared-work, and exclusive-contract waves."
Assert-True -Condition ($qualificationScript.IndexOf('Invoke-QualificationWave', $qualificationScript.IndexOf('Add-QualificationPhase -Name "frontend"', [StringComparison]::Ordinal), [StringComparison]::Ordinal) -lt $qualificationContractStart) -Message "Build and frontend prerequisites must complete before protected tests and verifier contracts."
Assert-True -Condition ($qualificationScript.IndexOf('[ValidateRange(1, 4)]', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must retain the explicit one-through-four worker boundary."
Assert-True -Condition ($qualificationScript.IndexOf('$qualificationHardwareProcessorCount = [Environment]::ProcessorCount', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must evaluate the processor count before passing it to the supported-worker policy."
Assert-True -Condition ($qualificationScript.IndexOf('$qualificationWorkerCount = Get-QualificationWorkerCount -MaximumWorkers $MaximumWorkers -HardwareProcessorCount $qualificationHardwareProcessorCount', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must derive its worker count through the checked supported-worker policy."
Assert-True -Condition ($qualificationScript.IndexOf('-HardwareProcessorCount [Environment]::ProcessorCount', [StringComparison]::Ordinal) -lt 0) -Message "Qualification must not pass a literal processor-count expression through PowerShell parameter binding."
Assert-True -Condition ($qualificationScript.IndexOf('$qualificationResourceCapacity = Get-QualificationResourceCapacity -WorkerCount $qualificationWorkerCount', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must derive capacity through the checked worker-count policy."
Assert-True -Condition ($qualificationScript.IndexOf('-MaximumProcessHeavyWorkers ([Math]::Min(2, $qualificationWorkerCount)) -MaximumCpuBoundWorkers ([Math]::Min(1, $qualificationWorkerCount))', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must admit no third process-heavy lane while allowing checked process-light backfill."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationTestScheduleProfile -ProjectName $projectName -ResourceCapacity $qualificationResourceCapacity', [StringComparison]::Ordinal) -ge 0) -Message "Every selected test project must use its checked-in measured scheduling profile at the derived capacity."
Assert-True -Condition ($qualificationScript.IndexOf('Profile = $testScheduleProfile', [StringComparison]::Ordinal) -ge 0 -and $qualificationScript.IndexOf('-Weight $exclusiveTest.Profile.Weight -ResourceClass $exclusiveTest.Profile.ResourceClass', [StringComparison]::Ordinal) -ge 0 -and $qualificationScript.IndexOf('-Weight $sharedTest.Profile.Weight -ResourceClass $sharedTest.Profile.ResourceClass', [StringComparison]::Ordinal) -ge 0) -Message "Every selected test project must retain its measured resource posture through protected or shared scheduling."
Assert-True -Condition ($qualificationScript.IndexOf('if ($testScheduleProfile.Isolation -ceq "Exclusive")', [StringComparison]::Ordinal) -ge 0) -Message "Protected test profiles must be removed from the shared qualification wave."
$exclusiveTestCollectionInitialization = $qualificationScript.IndexOf('$exclusiveQualificationTests = [Collections.Generic.List[object]]::new()', [StringComparison]::Ordinal)
$exclusiveTestCollectionPopulation = $qualificationScript.IndexOf('$exclusiveQualificationTests.Add(', [StringComparison]::Ordinal)
$exclusiveTestLoopStart = $qualificationScript.IndexOf('foreach ($exclusiveTest in @($exclusiveQualificationTests | Sort-Object @{ Expression = { $_.Profile.ExclusiveOrder }; Ascending = $true }, @{ Expression = { $_.Profile.EstimatedDurationSeconds }; Descending = $true }))', [StringComparison]::Ordinal)
$exclusiveTestWaveInvocation = $qualificationScript.IndexOf('Invoke-QualificationWave', $exclusiveTestLoopStart, [StringComparison]::Ordinal)
$sharedTestPhaseLoopStart = $qualificationScript.IndexOf('foreach ($sharedTest in $sharedQualificationTests)', [StringComparison]::Ordinal)
$sharedTestPhaseAddition = $qualificationScript.IndexOf('Add-QualificationPhase -Name $sharedTest.Name', [StringComparison]::Ordinal)
Assert-True -Condition ($exclusiveTestCollectionInitialization -ge 0 -and $exclusiveTestCollectionPopulation -gt $exclusiveTestCollectionInitialization -and $exclusiveTestLoopStart -gt $exclusiveTestCollectionPopulation) -Message "Protected test descriptors must be constructed and populated before StrictMode enumerates them."
Assert-True -Condition ($exclusiveTestLoopStart -ge 0 -and $exclusiveTestWaveInvocation -gt $exclusiveTestLoopStart -and $sharedTestPhaseLoopStart -gt $exclusiveTestWaveInvocation -and $sharedTestPhaseAddition -gt $exclusiveTestWaveInvocation -and $sharedTestPhaseAddition -lt $qualificationContractStart) -Message "Persistence and Startup must execute in ordered protected waves before shared test or verifier work is enqueued."
Assert-True -Condition ($qualificationScript.IndexOf('EstimatedDurationSeconds 150 -Weight $qualificationProcessHeavyWeight', [StringComparison]::Ordinal) -ge 0) -Message "The build prerequisite must retain its hosted-evidence-based 150-second estimate."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationContractScheduleProfile -ScriptName $contractScript', [StringComparison]::Ordinal) -ge 0) -Message "Every verifier contract must use its checked scheduling profile."
Assert-True -Condition ($qualificationScript.IndexOf('-Weight $contractScheduleProfile.Weight -ResourceClass $contractScheduleProfile.ResourceClass', [StringComparison]::Ordinal) -ge 0) -Message "Verifier contracts must use their measured resource posture."
Assert-True -Condition ($qualificationScript.IndexOf('$contractScheduleProfile.Isolation -ceq "Exclusive"', [StringComparison]::Ordinal) -ge 0) -Message "Exclusive verifier contracts must be removed from the shared test wave."
$exclusiveContractLoopStart = $qualificationScript.IndexOf('foreach ($exclusiveContract in $exclusiveQualificationContracts)', [StringComparison]::Ordinal)
$exclusiveContractLoopEnd = $qualificationScript.IndexOf('}', $exclusiveContractLoopStart)
Assert-True -Condition ($exclusiveContractLoopStart -ge 0 -and $exclusiveContractLoopEnd -gt $exclusiveContractLoopStart) -Message "Exclusive verifier contracts must execute after shared work is aggregated."
$exclusiveContractLoop = $qualificationScript.Substring($exclusiveContractLoopStart, $exclusiveContractLoopEnd - $exclusiveContractLoopStart)
Assert-Equal -Actual ([regex]::Matches($exclusiveContractLoop, 'Invoke-QualificationWave').Count) -Expected 1 -Message "Each exclusive verifier contract must own a separate bounded wave."
Assert-True -Condition ($qualificationScript.IndexOf('@("format", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006"', [StringComparison]::Ordinal) -ge 0) -Message "Changed-file qualification must check whitespace and IDE1006 in one dotnet format workspace load."
Assert-True -Condition ($qualificationScript.IndexOf('Add-QualificationPhase -Name "format-changed"', [StringComparison]::Ordinal) -ge 0) -Message "Changed-file formatting must remain an explicit bounded phase."
Assert-True -Condition ($qualificationScript.IndexOf('Invoke-QualificationWave', $qualificationScript.IndexOf('Add-QualificationPhase -Name "git-diff-check"', [StringComparison]::Ordinal), [StringComparison]::Ordinal) -ge 0) -Message "Shared tests, workflow validation, changed-file formatting, and diff-check must complete in their bounded wave."
Assert-True -Condition ($qualificationScript.IndexOf('@("diff", "--check", "$mergeBase..$HeadCommit")', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must diff-check the exact selected range."
Assert-True -Condition ($qualificationScript.IndexOf('Add-QualificationPhase -Name "github-yaml-format"', [StringComparison]::Ordinal) -ge 0) -Message "GitHub YAML validation must remain an explicit bounded qualification phase."
Assert-True -Condition ($qualificationScript.IndexOf('@("prettier", "--check", "--end-of-line", "auto", ".github/workflows/*.{yml,yaml}", ".github/dependabot.yml")', [StringComparison]::Ordinal) -ge 0) -Message "GitHub YAML formatting must ignore checkout-only CRLF conversion while validating both workflow extensions and Dependabot configuration."
Assert-True -Condition ($watchdogScript.IndexOf('Test-VerificationDeadlineExceeded -ElapsedTicks $stopwatch.Elapsed.Ticks -DeadlineTicks $deadlineTicks', [StringComparison]::Ordinal) -ge 0) -Message "The running watchdog must use the tested inclusive deadline decision."
Assert-True -Condition ($watchdogScript.IndexOf('Stop-VerificationProcessTree $process', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must terminate the full verifier process tree."
Assert-True -Condition ($verifyScript.IndexOf('VERIFY_COMPLETE schema_version=1 status=passed', [StringComparison]::Ordinal) -ge 0) -Message "The verifier must emit an exact terminal marker only after successful completion."
Assert-True -Condition ($workflow.IndexOf('./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 1500 -VerificationComponent Solution', [StringComparison]::Ordinal) -ge 0) -Message "Solution promotion must invoke the external watchdog with its explicit twenty-five-minute certification bound."
Assert-True -Condition ($workflow.IndexOf('./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 600 -VerificationComponent StaticContracts', [StringComparison]::Ordinal) -ge 0) -Message "Static promotion must invoke the external watchdog with its bounded ten-minute certification bound."
Assert-True -Condition ($workflow.IndexOf('-SkipCoverage', [StringComparison]::Ordinal) -lt 0) -Message "Promotion verification must retain coverage collection and thresholds."
Assert-True -Condition ($workflow.IndexOf("github.event.pull_request.draft == false", [StringComparison]::Ordinal) -ge 0) -Message "Promotion verification must run only for a merge-candidate pull request or main."
Assert-True -Condition ($workflow.IndexOf('types: [opened, synchronize, reopened, ready_for_review, edited]', [StringComparison]::Ordinal) -ge 0) -Message "Every non-draft metadata edit must rerun substantive promotion verification."
Assert-True -Condition ($workflow.IndexOf('name: verify', [StringComparison]::Ordinal) -ge 0) -Message "Promotion verification must always report the exact protected context name."
Assert-Contains -Actual $qualificationWorkflow -Expected "workflow_dispatch:" -Message "Hosted qualification must require an explicit owner dispatch."
Assert-True -Condition ($qualificationWorkflow.IndexOf("pull_request:", [StringComparison]::Ordinal) -lt 0 -and $qualificationWorkflow.IndexOf("push:", [StringComparison]::Ordinal) -lt 0) -Message "Draft pushes must not spend hosted qualification minutes automatically."
Assert-Contains -Actual $qualificationWorkflow -Expected "github.actor == 'Jacob-J-Thomas'" -Message "Only the repository owner may dispatch hosted qualification."
Assert-Contains -Actual $qualificationWorkflow -Expected "github.triggering_actor == 'Jacob-J-Thomas'" -Message "Only the repository owner may rerun hosted qualification."
Assert-Contains -Actual $qualificationWorkflow -Expected "name: hosted-qualification" -Message "Manual hosted diagnostics must not publish the former automatic qualification context."
Assert-Contains -Actual $qualificationWorkflow -Expected "persist-credentials: false" -Message "Hosted exact-head checkout must not persist a GitHub credential."
Assert-Contains -Actual $qualificationWorkflow -Expected "git merge-base --is-ancestor `$env:BASE_SHA `$env:HEAD_SHA" -Message "Hosted qualification must prove the dispatched exact edge."
Assert-Contains -Actual $qualificationWorkflow -Expected '-Qualification -BaseCommit ''${{ inputs.base_sha }}'' -HeadCommit ''${{ inputs.head_sha }}'' -Configuration Release -DeadlineSeconds 1680' -Message "Hosted diagnostics must use the same bounded qualification child."
Assert-Contains -Actual $qualificationWorkflow -Expected "    timeout-minutes: 30" -Message "Hosted qualification must leave at least two minutes of setup and diagnostic-upload margin around its 1680-second child watchdog."
Assert-True -Condition ($qualificationWorkflow.IndexOf('coverage.cobertura.xml', [StringComparison]::Ordinal) -lt 0) -Message "Qualification diagnostics must not imply that coverage was collected."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "workflow_dispatch:" -Message "Trusted local qualification must require an explicit dispatch."
Assert-True -Condition ($trustedLocalQualificationWorkflow.IndexOf("pull_request:", [StringComparison]::Ordinal) -lt 0 -and $trustedLocalQualificationWorkflow.IndexOf("push:", [StringComparison]::Ordinal) -lt 0) -Message "The ephemeral local runner must never accept automatic pull-request or push work."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "github.actor == 'Jacob-J-Thomas'" -Message "Only the repository owner may dispatch the trusted local lane."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "github.triggering_actor == 'Jacob-J-Thomas'" -Message "Only the repository owner may rerun the trusted local lane."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "runs-on: [agenthome-trusted-ephemeral-macos-arm64]" -Message "The local lane must require its no-default-label ephemeral runner."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "permissions:`n  contents: read" -Message "The local lane must retain read-only repository permission."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "persist-credentials: false" -Message "The exact checkout must not persist a GitHub credential on the host."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "git merge-base --is-ancestor `$env:BASE_SHA `$env:HEAD_SHA" -Message "The local lane must prove the dispatched exact edge."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected '-Qualification -BaseCommit ''${{ inputs.base_sha }}'' -HeadCommit ''${{ inputs.head_sha }}'' -Configuration Release -DeadlineSeconds 1680' -Message "The local lane must use the same bounded qualification child."
Assert-Contains -Actual $trustedLocalQualificationWorkflow -Expected "    timeout-minutes: 30" -Message "The local lane must leave at least two minutes of setup and diagnostic-upload margin around its 1680-second child watchdog."
Assert-True -Condition ($trustedLocalQualificationWorkflow.IndexOf("verify.ps1", [StringComparison]::Ordinal) -lt 0) -Message "The local development lane must not impersonate exhaustive promotion."
Assert-True -Condition ($trustedLocalQualificationWorkflow.IndexOf("name: verify", [StringComparison]::Ordinal) -lt 0 -and $trustedLocalQualificationWorkflow.IndexOf("name: browser-e2e", [StringComparison]::Ordinal) -lt 0) -Message "The local lane must not publish protected promotion context names."
Assert-True -Condition ($workflow.IndexOf('run: ./scripts/verify.ps1 -Configuration Release', [StringComparison]::Ordinal) -lt 0) -Message "Standard CI must not bypass the external watchdog."

Write-Output "Verification watchdog contract tests passed ($assertionCount assertions)."
