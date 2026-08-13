Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$artifactScriptPath = Join-Path $repoRoot "scripts\verification-artifacts.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$laneScriptPath = Join-Path $repoRoot "scripts\verification-test-lanes.ps1"
$powerShellExecutable = (Get-Process -Id $PID).Path
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)

    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

. $phaseScriptPath
. $parallelScriptPath
. $artifactScriptPath
. $scheduleScriptPath
. $laneScriptPath

function Get-VirtualVerificationSchedule {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Profiles,

        [Parameter(Mandatory = $true)]
        [int]$MaximumWorkers,

        [Parameter(Mandatory = $true)]
        [int]$MaximumResourceCapacity,

        [Parameter(Mandatory = $true)]
        [int]$MaximumProcessHeavyWorkers,

        [Parameter(Mandatory = $true)]
        [int]$MaximumCpuBoundWorkers
    )

    $phases = @($Profiles | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            EstimatedDurationSeconds = $_.EstimatedDurationSeconds
            SchedulingPrioritySeconds = $_.EstimatedDurationSeconds
            Weight = $_.Weight
            EffectiveWeight = $_.Weight
            ResourceClass = $_.ResourceClass
            SchedulingDeferrals = 0
        }
    })
    $pending = [Collections.Generic.List[object]]::new()
    foreach ($phase in @(Get-VerificationParallelPhaseSchedulingOrder -Phases $phases -MaximumProcessHeavyWorkers $MaximumProcessHeavyWorkers -MaximumCpuBoundWorkers $MaximumCpuBoundWorkers)) {
        $pending.Add($phase)
    }

    $running = [Collections.Generic.List[object]]::new()
    $starts = [ordered]@{}
    $activeResourceCapacity = 0
    $activeResourceClassCounts = @{ Ordinary = 0; CpuBound = 0; ProcessHeavy = 0 }
    $elapsedSeconds = 0
    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        while ($pending.Count -gt 0 -and $running.Count -lt $MaximumWorkers -and $activeResourceCapacity -lt $MaximumResourceCapacity) {
            $availableResourceClassSlots = @{
                Ordinary = $MaximumWorkers
                CpuBound = $MaximumCpuBoundWorkers - $activeResourceClassCounts.CpuBound
                ProcessHeavy = $MaximumProcessHeavyWorkers - $activeResourceClassCounts.ProcessHeavy
            }
            $phase = Select-VerificationParallelPhase -Pending $pending -AvailableCapacity ($MaximumResourceCapacity - $activeResourceCapacity) -AvailableResourceClassSlots $availableResourceClassSlots
            if ($null -eq $phase) {
                break
            }

            $starts[$phase.Name] = $elapsedSeconds
            $activeResourceCapacity += $phase.EffectiveWeight
            $activeResourceClassCounts[$phase.ResourceClass]++
            $running.Add([pscustomobject]@{ Phase = $phase; CompletesAtSeconds = $elapsedSeconds + $phase.EstimatedDurationSeconds })
        }

        if ($running.Count -eq 0) {
            throw "Virtual verification scheduler made no progress."
        }

        $elapsedSeconds = [int](($running | Measure-Object -Property CompletesAtSeconds -Minimum).Minimum)
        foreach ($entry in @($running | Where-Object { $_.CompletesAtSeconds -eq $elapsedSeconds })) {
            $activeResourceCapacity -= $entry.Phase.EffectiveWeight
            $activeResourceClassCounts[$entry.Phase.ResourceClass]--
            [void]$running.Remove($entry)
        }
    }

    return [pscustomobject]@{ MakespanSeconds = $elapsedSeconds; Starts = $starts }
}

$requiredGateProfiles = @(Get-VerificationRequiredGateScheduleProfiles)
Assert-True -Condition ((Get-VerificationRequiredGateResourceCapacity) -eq 12) -Message "Required gates must retain the explicit twelve-unit logical resource capacity."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumProcessHeavyWorkers) -eq 4) -Message "Required gates must admit at most four assembly-wide helper-process-heavy phases."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumCpuBoundWorkers) -eq 2) -Message "Required gates must admit at most two CPU-bound format gates."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 8 -HardwareProcessorCount 10) -eq 4) -Message "A larger host must retain the checked-in four-process required-gate ceiling."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 6 -HardwareProcessorCount 4) -eq 4) -Message "A hosted four-core runner must admit exactly four physical required-gate workers."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 4 -HardwareProcessorCount 10) -eq 4) -Message "A lower explicit worker request must remain authoritative below the required-gate ceiling."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 4 -HardwareProcessorCount 4) -eq 4) -Message "A hosted four-core request must not be expanded beyond its explicit four-worker bound."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 6 -HardwareProcessorCount 2) -eq 2) -Message "A smaller host must reduce required-gate workers to its physical processor count."
Assert-True -Condition ($requiredGateProfiles.Count -eq 12) -Message "The exact nine-assembly test plan, two format gates, and git-diff gate must have checked-in duration/resource profiles."
Assert-True -Condition (@($requiredGateProfiles | Group-Object Name -CaseSensitive | Where-Object Count -ne 1).Count -eq 0) -Message "Required gate scheduling profiles must have exact unique names."
Assert-VerificationRequiredGateSchedule -Phases $requiredGateProfiles
$expectedRequiredGateNames = @(
    "format-naming-style"
    "format-whitespace"
    "git-diff-check"
    "tests-EmbodySense.Cli.Command.Tests-all"
    "tests-EmbodySense.Core.Application.Tests-all"
    "tests-EmbodySense.Core.Clients.Tests-all"
    "tests-EmbodySense.Core.Common.Tests-all"
    "tests-EmbodySense.Core.Persistence.Tests-all"
    "tests-EmbodySense.Core.Startup.Tests-all"
    "tests-EmbodySense.E2ETests-all"
    "tests-EmbodySense.IntegrationTests-all"
    "tests-EmbodySense.Web.Tests-all"
)
Assert-True -Condition ((@($requiredGateProfiles.Name | Sort-Object) -join "`n") -ceq (@($expectedRequiredGateNames | Sort-Object) -join "`n")) -Message "Required-gate profiles must equal the canonical nine-assembly catalog plus both formats and git-diff exactly."
$declaredRequiredGateNames = [Collections.Generic.List[string]]::new()
$declaredRequiredGateNames.Add("format-naming-style")
$declaredRequiredGateNames.Add("format-whitespace")
$declaredRequiredGateNames.Add("git-diff-check")
$testProjects = @(Get-ChildItem -Path (Join-Path $repoRoot "tests") -Recurse -Filter "*.csproj" | Where-Object { $_.Name -ne "EmbodySense.CancellationHost.csproj" -and $_.Name -ne "EmbodySense.Tests.Support.csproj" } | Sort-Object FullName)
foreach ($testProject in $testProjects) {
    foreach ($lane in @(Get-VerificationTestProjectLanes -TestProject $testProject)) {
        $declaredRequiredGateNames.Add("tests-$($testProject.BaseName)-$($lane.Name)")
    }
}
$declaredRequiredGateProfiles = @($declaredRequiredGateNames | ForEach-Object { Get-VerificationRequiredGateScheduleProfile -Name $_ })
Assert-VerificationRequiredGateSchedule -Phases $declaredRequiredGateProfiles
Assert-True -Condition ($declaredRequiredGateProfiles.Count -eq $declaredRequiredGateNames.Count) -Message "Every dynamically declared required gate must resolve to one checked-in profile."
Assert-True -Condition ($declaredRequiredGateProfiles.Count -eq $requiredGateProfiles.Count) -Message "The checked-in scheduling catalog cannot retain stale profiles for gates outside the current plan."
foreach ($processHeavyGateName in @("tests-EmbodySense.Core.Persistence.Tests-all", "tests-EmbodySense.Core.Startup.Tests-all", "tests-EmbodySense.IntegrationTests-all", "tests-EmbodySense.Web.Tests-all")) {
    $processHeavyProfile = Get-VerificationRequiredGateScheduleProfile -Name $processHeavyGateName
    Assert-True -Condition ($processHeavyProfile.Weight -eq 3 -and $processHeavyProfile.ResourceClass -ceq "ProcessHeavy") -Message "An internally parallel assembly gate '$processHeavyGateName' must retain its bounded logical weight."
}
foreach ($formatGateName in @("format-naming-style", "format-whitespace")) {
    $formatProfile = Get-VerificationRequiredGateScheduleProfile -Name $formatGateName
    Assert-True -Condition ($formatProfile.Weight -eq 2 -and $formatProfile.ResourceClass -ceq "CpuBound") -Message "Format gate '$formatGateName' must remain bounded and overlap only immutable test-output execution."
}

$requiredGateVirtualSchedule = Get-VirtualVerificationSchedule -Profiles $requiredGateProfiles -MaximumWorkers 4 -MaximumResourceCapacity 12 -MaximumProcessHeavyWorkers 4 -MaximumCpuBoundWorkers 2
Assert-True -Condition ($requiredGateVirtualSchedule.MakespanSeconds -le 360) -Message "The assembly-wide four-process schedule must retain a deterministic estimate below six minutes. Actual: $($requiredGateVirtualSchedule.MakespanSeconds)."
foreach ($assemblyGateName in @("tests-EmbodySense.Core.Persistence.Tests-all", "tests-EmbodySense.Core.Startup.Tests-all", "tests-EmbodySense.IntegrationTests-all", "tests-EmbodySense.Web.Tests-all")) {
    Assert-True -Condition ($requiredGateVirtualSchedule.Starts[$assemblyGateName] -eq 0) -Message "The four longest assembly gates must start at virtual second zero."
}
$initialResourceCapacity = ($requiredGateProfiles | Where-Object { $requiredGateVirtualSchedule.Starts[$_.Name] -eq 0 } | Measure-Object -Property Weight -Sum).Sum
Assert-True -Condition ($initialResourceCapacity -eq 12) -Message "Four assembly-wide phases must pack the initial twelve logical units without exceeding four actual workers."
foreach ($formatGateName in @("format-naming-style", "format-whitespace")) {
    Assert-True -Condition ($requiredGateVirtualSchedule.Starts[$formatGateName] -gt 0 -and $requiredGateVirtualSchedule.Starts[$formatGateName] -lt 300) -Message "Format gate '$formatGateName' must backfill a released worker while the longest immutable assembly gate is still running."
}

$counterexamplePhases = @(
    [pscustomobject]@{ Name = "long-ordinary"; EstimatedDurationSeconds = 100; SchedulingPrioritySeconds = 100; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "long-heavy"; EstimatedDurationSeconds = 90; SchedulingPrioritySeconds = 90; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "short-cpu"; EstimatedDurationSeconds = 5; SchedulingPrioritySeconds = 5; ResourceClass = "CpuBound" }
)
$counterexampleOrder = @(Get-VerificationParallelPhaseSchedulingOrder -Phases $counterexamplePhases -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
Assert-True -Condition ($counterexampleOrder[0].Name -ceq "long-ordinary" -and $counterexampleOrder[1].Name -ceq "long-heavy" -and $counterexampleOrder[2].Name -ceq "short-cpu") -Message "A singleton CPU class with no backlog cannot jump ahead of longer ordinary or process-heavy work unconditionally."
Assert-True -Condition ($counterexampleOrder[2].SchedulingPrioritySeconds -eq 5) -Message "A singleton class's static priority must equal its initial backlog rather than an unconditional class boost."

$priorityTiePhases = @(
    [pscustomobject]@{ Name = "ordinary-long"; EstimatedDurationSeconds = 100; SchedulingPrioritySeconds = 100; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "cpu-zulu"; EstimatedDurationSeconds = 50; SchedulingPrioritySeconds = 50; ResourceClass = "CpuBound" }
    [pscustomobject]@{ Name = "cpu-alpha"; EstimatedDurationSeconds = 50; SchedulingPrioritySeconds = 50; ResourceClass = "CpuBound" }
)
$priorityTieOrder = @(Get-VerificationParallelPhaseSchedulingOrder -Phases $priorityTiePhases -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
Assert-True -Condition ($priorityTieOrder[0].Name -ceq "ordinary-long" -and $priorityTieOrder[1].Name -ceq "cpu-alpha" -and $priorityTieOrder[2].Name -ceq "cpu-zulu") -Message "Scheduling priority ties must fall back to duration and then exact name deterministically."
Assert-True -Condition ($priorityTieOrder[1].SchedulingPrioritySeconds -eq 100 -and $priorityTieOrder[2].SchedulingPrioritySeconds -eq 100) -Message "Every phase in a singleton-limited class must receive the same initial static backlog priority."
try {
    Get-VerificationRequiredGateScheduleProfile -Name "tests-unprofiled-gate" | Out-Null
    throw "Expected missing scheduling profile failure."
}
catch {
    Assert-Contains -Actual $_.Exception.Message -Expected "must have exactly one checked-in scheduling profile" -Message "A new gate without a checked-in duration/resource profile must fail closed."
}

try {
    Assert-VerificationRequiredGateSchedule -Phases @($declaredRequiredGateProfiles | Select-Object -Skip 1)
    throw "Expected stale scheduling profile failure."
}
catch {
    Assert-Contains -Actual $_.Exception.Message -Expected "unexpected_profiles=[" -Message "A profile without a current declared gate must fail closed."
}

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-parallel-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $probePath = Join-Path $scenarioRoot "probe.ps1"
    @'
param([string]$Name, [int]$DelayMilliseconds, [int]$ExitCode, [string]$OrderPath, [string]$SynchronizationRoot, [int]$ExpectedConcurrent)
if (-not [string]::IsNullOrWhiteSpace($OrderPath) -and $OrderPath -cne "-") { Add-Content -LiteralPath $OrderPath -Value $Name }
if (-not [string]::IsNullOrWhiteSpace($SynchronizationRoot) -and $SynchronizationRoot -cne "-" -and $ExpectedConcurrent -gt 0) {
    New-Item -ItemType Directory -Path $SynchronizationRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $SynchronizationRoot "$Name.ready") -Value "ready" -Encoding UTF8
    $synchronizationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (@(Get-ChildItem -LiteralPath $SynchronizationRoot -Filter "*.ready" -File).Count -lt $ExpectedConcurrent) {
        if ([DateTimeOffset]::UtcNow -ge $synchronizationDeadline) { exit 41 }
        Start-Sleep -Milliseconds 10
    }
}
Start-Sleep -Milliseconds $DelayMilliseconds
Write-Output "probe=$Name"
Write-Output "environment=$env:VERIFY_PARALLEL_PROBE"
Write-Output "physical_temp=$([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))"
exit $ExitCode
'@ | Set-Content -LiteralPath $probePath -Encoding UTF8

    $baseArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $baseArguments += @("-ExecutionPolicy", "Bypass")
    }
    $baseArguments += @("-File", $probePath)
    $sixUnitCapacity = 6
    $synchronizationRoot = Join-Path $scenarioRoot "overlap"
    Reset-VerificationParallelPhaseState
    foreach ($name in @("first", "second", "third", "fourth", "fifth", "sixth")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($baseArguments + @($name, "50", "0", "-", $synchronizationRoot, $sixUnitCapacity.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log")
    }
    $results = @(Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity)

    Assert-True -Condition ($results.Count -eq 6) -Message "Every successful parallel phase must be aggregated."
    Assert-True -Condition (@(Get-ChildItem -LiteralPath $synchronizationRoot -Filter "*.ready" -File).Count -eq 6) -Message "Six ordinary probes must be able to pack the explicit six-unit logical capacity."
    Assert-Contains -Actual (Get-Content -Raw (Join-Path $scenarioRoot "first.log")) -Expected "probe=first" -Message "Each phase must retain isolated output."

    $weightedProbePath = Join-Path $scenarioRoot "weighted-probe.ps1"
    @'
param([string]$Name, [string]$ActiveRoot, [int]$ExpectedConcurrent, [int]$MaximumExpectedConcurrent)
$activePath = Join-Path $ActiveRoot "$Name.active"
try {
    Set-Content -LiteralPath $activePath -Value "active" -Encoding UTF8
    $synchronizationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (@(Get-ChildItem -LiteralPath $ActiveRoot -Filter "*.active" -File).Count -lt $ExpectedConcurrent) {
        if ([DateTimeOffset]::UtcNow -ge $synchronizationDeadline) { exit 42 }
        Start-Sleep -Milliseconds 10
    }
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds(250)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $activeCount = @(Get-ChildItem -LiteralPath $ActiveRoot -Filter "*.active" -File).Count
        if ($activeCount -gt $MaximumExpectedConcurrent) {
            Write-Output "overcommitted=$activeCount"
            exit 43
        }
        Start-Sleep -Milliseconds 10
    }
    Write-Output "weighted_probe=$Name"
}
finally {
    Remove-Item -LiteralPath $activePath -Force -ErrorAction SilentlyContinue
}
'@ | Set-Content -LiteralPath $weightedProbePath -Encoding UTF8
    $weightedArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $weightedArguments += @("-ExecutionPolicy", "Bypass")
    }
    $weightedArguments += @("-File", $weightedProbePath)
    $activeRoot = Join-Path $scenarioRoot "weighted-active"
    New-Item -ItemType Directory -Path $activeRoot | Out-Null
    $heavyWeight = 3
    $expectedHeavyConcurrency = [Math]::Floor($sixUnitCapacity / $heavyWeight)
    Reset-VerificationParallelPhaseState
    foreach ($name in @("heavy-first", "heavy-second", "heavy-third", "heavy-fourth")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $activeRoot, $expectedHeavyConcurrency.ToString([Globalization.CultureInfo]::InvariantCulture), $expectedHeavyConcurrency.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight $heavyWeight -ResourceClass ProcessHeavy
    }
    $weightedResults = @(Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity)
    Assert-True -Condition ($weightedResults.Count -eq 4) -Message "Every weighted phase must be scheduled and aggregated."
    Assert-True -Condition (@($weightedResults | Where-Object { $_.Weight -eq $heavyWeight -and $_.EffectiveWeight -eq $heavyWeight -and $_.ResourceClass -ceq "ProcessHeavy" }).Count -eq 4) -Message "Weighted result evidence must preserve the declared process-heavy posture without adapting its weight downward."

    $physicalHeavyRoot = Join-Path $scenarioRoot "physical-heavy-active"
    New-Item -ItemType Directory -Path $physicalHeavyRoot | Out-Null
    Reset-VerificationParallelPhaseState
    foreach ($name in @("physical-heavy-first", "physical-heavy-second")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $physicalHeavyRoot, "2", "2")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight 3 -ResourceClass ProcessHeavy
    }
    $physicalHeavyResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($physicalHeavyResults.Count -eq 2) -Message "Two evidence-weighted process-heavy phases must overlap within eight logical units and the four-process ceiling."

    $exclusiveRoot = Join-Path $scenarioRoot "full-capacity-active"
    New-Item -ItemType Directory -Path $exclusiveRoot | Out-Null
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "full-capacity-format" -FileName $powerShellExecutable -Arguments ($weightedArguments + @("full-capacity-format", $exclusiveRoot, "1", "1")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "full-capacity-format.log") -EstimatedDurationSeconds 2 -Weight 4 -ResourceClass CpuBound
    Add-VerificationParallelPhase -Name "excluded-ordinary" -FileName $powerShellExecutable -Arguments ($weightedArguments + @("excluded-ordinary", $exclusiveRoot, "1", "1")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "excluded-ordinary.log")
    $exclusiveResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4 -MaximumResourceCapacity 4 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($exclusiveResults.Count -eq 2) -Message "A full-capacity CPU phase must exclude ordinary work and then allow the plan to drain."

    foreach ($resourceClassScenario in @(
        [pscustomobject]@{ Name = "process-heavy"; ResourceClass = "ProcessHeavy"; Weight = 3; Maximum = 2; Count = 4 },
        [pscustomobject]@{ Name = "cpu-bound"; ResourceClass = "CpuBound"; Weight = 2; Maximum = 1; Count = 3 }
    )) {
        $resourceClassRoot = Join-Path $scenarioRoot "$($resourceClassScenario.Name)-active"
        New-Item -ItemType Directory -Path $resourceClassRoot | Out-Null
        Reset-VerificationParallelPhaseState
        foreach ($index in 1..$resourceClassScenario.Count) {
            $name = "$($resourceClassScenario.Name)-$index"
            Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $resourceClassRoot, $resourceClassScenario.Maximum.ToString([Globalization.CultureInfo]::InvariantCulture), $resourceClassScenario.Maximum.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight $resourceClassScenario.Weight -ResourceClass $resourceClassScenario.ResourceClass
        }

        $resourceClassResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 6 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
        Assert-True -Condition ($resourceClassResults.Count -eq $resourceClassScenario.Count) -Message "The $($resourceClassScenario.Name) concurrency-limit proof must drain every phase."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "one-worker-heavy" -FileName $powerShellExecutable -Arguments ($baseArguments + @("one-worker-heavy", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "one-worker-heavy.log") -Weight 3 -ResourceClass ProcessHeavy
    $oneWorkerResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 1 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 1 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($oneWorkerResults.Count -eq 1 -and $oneWorkerResults[0].ResourceClass -ceq "ProcessHeavy") -Message "One-worker hosts must preserve process-heavy execution after effective class-limit capping."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "invalid-resource-limit" -FileName $powerShellExecutable -Arguments ($baseArguments + @("invalid-resource-limit", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "invalid-resource-limit.log")
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers 2 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 3 | Out-Null
        throw "Expected invalid resource-class concurrency limit failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "resource-class limits cannot exceed the maximum worker count" -Message "Resource-class concurrency limits must fail closed when they exceed the worker ceiling."
    }

    $workerBoundRoot = Join-Path $scenarioRoot "worker-bound-active"
    New-Item -ItemType Directory -Path $workerBoundRoot | Out-Null
    $workerBound = 2
    Reset-VerificationParallelPhaseState
    foreach ($name in @("worker-first", "worker-second", "worker-third", "worker-fourth")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $workerBoundRoot, $workerBound.ToString([Globalization.CultureInfo]::InvariantCulture), $workerBound.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log")
    }
    $workerBoundResults = @(Invoke-VerificationParallelPhases -MaximumWorkers $workerBound -MaximumResourceCapacity $sixUnitCapacity)
    Assert-True -Condition ($workerBoundResults.Count -eq 4) -Message "A worker ceiling below logical capacity must still drain every admitted phase."
    Assert-True -Condition (@($workerBoundResults | Where-Object { $_.Weight -eq 1 }).Count -eq 4) -Message "The worker-ceiling proof must use ordinary phases that logical capacity alone would admit together."

    $fairPending = [Collections.Generic.List[object]]::new()
    $fairPending.Add([pscustomobject]@{ Name = "heavy"; EffectiveWeight = 3; SchedulingDeferrals = 0 })
    $fairPending.Add([pscustomobject]@{ Name = "ordinary-one"; EffectiveWeight = 1; SchedulingDeferrals = 0 })
    $fairPending.Add([pscustomobject]@{ Name = "ordinary-two"; EffectiveWeight = 1; SchedulingDeferrals = 0 })
    $backfill = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($backfill.Name -ceq "ordinary-one") -Message "A fitting ordinary phase must backfill capacity behind a temporarily blocked heavy phase."
    $reserved = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($null -eq $reserved) -Message "A bypassed heavy phase must reserve the next fitting opportunity instead of starving behind ordinary work."
    $heavy = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 3
    Assert-True -Condition ($heavy.Name -ceq "heavy") -Message "A previously bypassed heavy phase must run as soon as its required capacity is available."
    $lastOrdinary = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($lastOrdinary.Name -ceq "ordinary-two" -and $fairPending.Count -eq 0) -Message "Fair reservation cannot lose or strand the remaining ordinary phase."

    $classLimitedPending = [Collections.Generic.List[object]]::new()
    $classLimitedPending.Add([pscustomobject]@{ Name = "saturated-heavy"; EffectiveWeight = 3; ResourceClass = "ProcessHeavy"; SchedulingDeferrals = 0 })
    $classLimitedPending.Add([pscustomobject]@{ Name = "ordinary-backfill"; EffectiveWeight = 1; ResourceClass = "Ordinary"; SchedulingDeferrals = 0 })
    $classBackfill = Select-VerificationParallelPhase -Pending $classLimitedPending -AvailableCapacity 5 -AvailableResourceClassSlots @{ Ordinary = 6; CpuBound = 1; ProcessHeavy = 0 }
    Assert-True -Condition ($classBackfill.Name -ceq "ordinary-backfill") -Message "A saturated resource class cannot reserve capacity needed by an admissible ordinary phase."
    Assert-True -Condition ($classLimitedPending[0].SchedulingDeferrals -eq 0) -Message "Class saturation must not count as capacity backfill against fairness accounting."

    $oversizedOutput = Join-Path $scenarioRoot "oversized.log"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "oversized" -FileName $powerShellExecutable -Arguments ($baseArguments + @("oversized", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $oversizedOutput -Weight 7 -ResourceClass ProcessHeavy
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity | Out-Null
        throw "Expected oversized resource declaration failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "beyond logical resource capacity 6" -Message "A phase cannot weaken its declared weight by adapting it down to available capacity."
    }
    Assert-True -Condition (-not (Test-Path -LiteralPath $oversizedOutput)) -Message "An oversized phase must fail before execution."

    foreach ($underweightedClass in @("CpuBound", "ProcessHeavy")) {
        $underweightedOutput = Join-Path $scenarioRoot "underweighted-$underweightedClass.log"
        Reset-VerificationParallelPhaseState
        Add-VerificationParallelPhase -Name "underweighted-$underweightedClass" -FileName $powerShellExecutable -Arguments ($baseArguments + @("underweighted-$underweightedClass", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $underweightedOutput -Weight 1 -ResourceClass $underweightedClass
        try {
            Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity | Out-Null
            throw "Expected underweighted resource-class failure."
        }
        catch {
            Assert-Contains -Actual $_.Exception.Message -Expected "resource classes are underweighted" -Message "$underweightedClass phases must retain minimum logical-capacity protection."
        }
        Assert-True -Condition (-not (Test-Path -LiteralPath $underweightedOutput)) -Message "An underweighted $underweightedClass phase must fail before execution."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "failure" -FileName $powerShellExecutable -Arguments ($baseArguments + @("failure", "50", "17")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "failure.log") -Weight 3 -ResourceClass ProcessHeavy
    Add-VerificationParallelPhase -Name "success" -FileName $powerShellExecutable -Arguments ($baseArguments + @("success", "300", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "success.log") -Weight 3 -ResourceClass ProcessHeavy
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity | Out-Null
        throw "Expected aggregate failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "'failure' exited with code 17" -Message "Nonzero child exits must fail the aggregate with the exact phase."
    }

    Assert-Contains -Actual (Get-Content -Raw (Join-Path $scenarioRoot "success.log")) -Expected "probe=success" -Message "The harness must drain already-running peers before reporting failure."

    $laneTempRoot = Join-Path $scenarioRoot "lane-temp"
    New-Item -ItemType Directory -Path $laneTempRoot | Out-Null
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "environment" -FileName $powerShellExecutable -Arguments ($baseArguments + @("environment", "10", "0", "-")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "environment.log") -Environment @{ VERIFY_PARALLEL_PROBE = "scoped-child"; TEMP = $laneTempRoot; TMP = $laneTempRoot; TMPDIR = $laneTempRoot }
    Invoke-VerificationParallelPhases -MaximumResourceCapacity 1 | Out-Null
    $environmentLog = Get-Content -Raw (Join-Path $scenarioRoot "environment.log")
    Assert-Contains -Actual $environmentLog -Expected "environment=scoped-child" -Message "Per-phase environment overrides must reach only the child ProcessStartInfo."
    Assert-Contains -Actual $environmentLog -Expected "physical_temp=$([IO.Path]::GetFullPath($laneTempRoot))" -Message "A lane's .NET process and descendants must resolve the isolated fixture root as physical temporary storage."
    Assert-True -Condition ([string]::IsNullOrEmpty($env:VERIFY_PARALLEL_PROBE)) -Message "Per-phase environment overrides cannot mutate the verifier process environment."

    $orderPath = Join-Path $scenarioRoot "priority-order.txt"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "low" -FileName $powerShellExecutable -Arguments ($baseArguments + @("low", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "low.log") -EstimatedDurationSeconds 1
    Add-VerificationParallelPhase -Name "tie-zulu" -FileName $powerShellExecutable -Arguments ($baseArguments + @("tie-zulu", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "tie-zulu.log") -EstimatedDurationSeconds 50
    Add-VerificationParallelPhase -Name "tie-alpha" -FileName $powerShellExecutable -Arguments ($baseArguments + @("tie-alpha", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "tie-alpha.log") -EstimatedDurationSeconds 50
    Add-VerificationParallelPhase -Name "high" -FileName $powerShellExecutable -Arguments ($baseArguments + @("high", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "high.log") -EstimatedDurationSeconds 100
    Invoke-VerificationParallelPhases -MaximumResourceCapacity 1 | Out-Null
    $order = @(Get-Content -LiteralPath $orderPath)
    Assert-True -Condition ($order.Count -eq 4 -and $order[0] -ceq "high" -and $order[1] -ceq "tie-alpha" -and $order[2] -ceq "tie-zulu" -and $order[3] -ceq "low") -Message "Ordinary phases must retain longest-estimate ordering, with exact-name ordering for deterministic ties."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "timeout" -FileName $powerShellExecutable -Arguments ($baseArguments + @("timeout", "5000", "0")) -TimeoutSeconds 1 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "timeout.log")
    try {
        Invoke-VerificationParallelPhases -MaximumResourceCapacity 1 | Out-Null
        throw "Expected aggregate timeout."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "'timeout' timed out" -Message "Timeouts must kill the child tree and fail the aggregate."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "duplicate" -FileName $powerShellExecutable -Arguments ($baseArguments + @("one", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "one.log")
    try {
        Add-VerificationParallelPhase -Name "duplicate" -FileName $powerShellExecutable -Arguments ($baseArguments + @("two", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "two.log")
        throw "Expected duplicate declaration failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "declared more than once" -Message "Duplicate phase identities must fail before execution."
    }

    $artifactSource = Join-Path $scenarioRoot "artifact-source"
    $artifactCopy = Join-Path $scenarioRoot "artifact-copy"
    New-Item -ItemType Directory -Path (Join-Path $artifactSource "nested") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactSource "nested\assembly.dll") -Value "immutable" -Encoding UTF8
    $artifactManifest = @(Copy-VerifiedDirectory -SourceDirectory $artifactSource -DestinationDirectory $artifactCopy -Description "contract artifact")
    Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $artifactCopy -Description "unchanged contract artifact"
    Set-Content -LiteralPath (Join-Path $artifactCopy "nested\assembly.dll") -Value "substituted" -Encoding UTF8
    try {
        Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $artifactCopy -Description "mutated contract artifact"
        throw "Expected immutable artifact verification to fail."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "failed immutable artifact verification" -Message "Artifact mutation or substitution must fail closed."
    }

    try {
        Copy-VerifiedDirectory -SourceDirectory $artifactSource -DestinationDirectory $artifactCopy -Description "stale destination" | Out-Null
        throw "Expected stale artifact destination rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "could substitute stale artifacts" -Message "Pre-existing lane output cannot be reused as a stale substitute."
    }

    $manifestCopy = Join-Path $scenarioRoot "manifest-copy"
    $manifestCopyResult = @(Copy-VerifiedDirectoryFromManifest -SourceDirectory $artifactSource -SourceManifest $artifactManifest -DestinationDirectory $manifestCopy -Description "authenticated manifest copy")
    Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $manifestCopy -Description "authenticated manifest copy"
    Assert-True -Condition ($manifestCopyResult.Count -eq $artifactManifest.Count) -Message "A manifest-backed copy must retain every authenticated source entry exactly once."

    $staleManifestDestination = Join-Path $scenarioRoot "stale-manifest-destination"
    New-Item -ItemType Directory -Path $staleManifestDestination | Out-Null
    try {
        Copy-VerifiedDirectoryFromManifest -SourceDirectory $artifactSource -SourceManifest $artifactManifest -DestinationDirectory $staleManifestDestination -Description "stale manifest destination" | Out-Null
        throw "Expected stale manifest-backed destination rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "could substitute stale artifacts" -Message "A manifest-backed copy cannot reuse a pre-existing destination."
    }

    Set-Content -LiteralPath (Join-Path $artifactSource "nested\assembly.dll") -Value "source-mutated-after-manifest" -Encoding UTF8
    try {
        Copy-VerifiedDirectoryFromManifest -SourceDirectory $artifactSource -SourceManifest $artifactManifest -DestinationDirectory (Join-Path $scenarioRoot "mutated-source-copy") -Description "mutated manifest source" | Out-Null
        throw "Expected stale source manifest rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "failed immutable artifact verification" -Message "A source mutation after manifest capture must fail the manifest-backed copy closed."
    }

    $isolatedOutput = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $scenarioRoot "project\lane") -Configuration "Release" -TargetFramework "net10.0"
    $isolatedSegments = $isolatedOutput.Replace('\', '/').Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    $expectedSuffix = @("project", "lane", "bin", "Release", "net10.0")
    Assert-True -Condition (@(Compare-Object -ReferenceObject $expectedSuffix -DifferenceObject $isolatedSegments[($isolatedSegments.Count - $expectedSuffix.Count)..($isolatedSegments.Count - 1)] -CaseSensitive).Count -eq 0) -Message "Isolated lanes must preserve the bin/<Configuration>/<TargetFramework> AppContext suffix used by helper-host tests."

    try {
        Get-VerificationIsolatedOutputPath -IsolationRoot $scenarioRoot -Configuration "Release/escape" -TargetFramework "net10.0" | Out-Null
        throw "Expected unsafe topology segment rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "not a single safe path segment" -Message "Isolated topology segments must fail closed instead of escaping their lane."
    }
}
finally {
    if (Test-Path -LiteralPath $scenarioRoot) {
        Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
    }
}

Write-Output "Parallel verifier contract tests passed ($assertionCount assertions)."
