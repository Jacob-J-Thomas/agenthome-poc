$script:VerificationParallelPhases = @()

function Reset-VerificationParallelPhaseState {
    $script:VerificationParallelPhases = @()
}

function Add-VerificationParallelPhase {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 86400)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$OutputPath,

        [string]$CoverageSearchRoot,

        [string]$TrxPath,

        [hashtable]$Environment,

        [ValidateRange(1, 86400)]
        [int]$EstimatedDurationSeconds = 1,

        [ValidateRange(1, 32)]
        [int]$Weight = 1,

        [ValidateSet("Ordinary", "CpuBound", "ProcessHeavy")]
        [string]$ResourceClass = "Ordinary"
    )

    if (@($script:VerificationParallelPhases | Where-Object { $_.Name -ceq $Name }).Count -gt 0) {
        throw "Parallel verification phase '$Name' is declared more than once."
    }

    $script:VerificationParallelPhases += [pscustomobject]@{
        Name = $Name
        FileName = $FileName
        Arguments = @($Arguments)
        TimeoutSeconds = $TimeoutSeconds
        WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
        OutputPath = [IO.Path]::GetFullPath($OutputPath)
        CoverageSearchRoot = if ([string]::IsNullOrWhiteSpace($CoverageSearchRoot)) { $null } else { [IO.Path]::GetFullPath($CoverageSearchRoot) }
        TrxPath = if ([string]::IsNullOrWhiteSpace($TrxPath)) { $null } else { [IO.Path]::GetFullPath($TrxPath) }
        Environment = if ($null -eq $Environment) { @{} } else { $Environment.Clone() }
        EstimatedDurationSeconds = $EstimatedDurationSeconds
        SchedulingPrioritySeconds = $EstimatedDurationSeconds
        Weight = $Weight
        EffectiveWeight = $Weight
        ResourceClass = $ResourceClass
        SchedulingDeferrals = 0
    }
}

function Get-VerificationParallelPhaseSchedulingOrder {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Phases,

        [ValidateRange(1, 32)]
        [int]$MaximumProcessHeavyWorkers,

        [ValidateRange(1, 32)]
        [int]$MaximumCpuBoundWorkers
    )

    $singletonBacklogSeconds = @{}
    foreach ($resourceClassLimit in @(
        [pscustomobject]@{ ResourceClass = "CpuBound"; MaximumWorkers = $MaximumCpuBoundWorkers }
        [pscustomobject]@{ ResourceClass = "ProcessHeavy"; MaximumWorkers = $MaximumProcessHeavyWorkers }
    )) {
        if ($resourceClassLimit.MaximumWorkers -ne 1) {
            continue
        }

        $resourceClassPhases = @($Phases | Where-Object { $_.ResourceClass -ceq $resourceClassLimit.ResourceClass })
        if ($resourceClassPhases.Count -gt 0) {
            $singletonBacklogSeconds[$resourceClassLimit.ResourceClass] = [int](($resourceClassPhases | Measure-Object -Property EstimatedDurationSeconds -Sum).Sum)
        }
    }

    foreach ($phase in $Phases) {
        if ($null -eq $phase.PSObject.Properties["SchedulingPrioritySeconds"]) {
            $phase | Add-Member -NotePropertyName SchedulingPrioritySeconds -NotePropertyValue $phase.EstimatedDurationSeconds
        }
        else {
            $phase.SchedulingPrioritySeconds = $phase.EstimatedDurationSeconds
        }
        if ($singletonBacklogSeconds.ContainsKey($phase.ResourceClass)) {
            $phase.SchedulingPrioritySeconds = [Math]::Max($phase.EstimatedDurationSeconds, [int]$singletonBacklogSeconds[$phase.ResourceClass])
        }
    }

    return @($Phases | Sort-Object -Property @{ Expression = "SchedulingPrioritySeconds"; Descending = $true }, @{ Expression = "EstimatedDurationSeconds"; Descending = $true }, @{ Expression = "Name"; Descending = $false })
}

function Select-VerificationParallelPhase {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.List[object]]$Pending,

        [ValidateRange(0, 32)]
        [int]$AvailableCapacity,

        [hashtable]$AvailableResourceClassSlots = @{
            Ordinary = [int]::MaxValue
            CpuBound = [int]::MaxValue
            ProcessHeavy = [int]::MaxValue
        }
    )

    if ($Pending.Count -eq 0 -or $AvailableCapacity -eq 0) {
        return $null
    }

    $fitIndex = -1
    for ($index = 0; $index -lt $Pending.Count; $index++) {
        $resourceClass = if ($null -eq $Pending[$index].PSObject.Properties["ResourceClass"]) { "Ordinary" } else { [string]$Pending[$index].ResourceClass }
        if (-not $AvailableResourceClassSlots.ContainsKey($resourceClass)) {
            throw "Parallel verification phase '$($Pending[$index].Name)' has no admitted concurrency limit for resource class '$resourceClass'."
        }

        if ($Pending[$index].EffectiveWeight -le $AvailableCapacity -and [int]$AvailableResourceClassSlots[$resourceClass] -gt 0) {
            $fitIndex = $index
            break
        }
    }

    if ($fitIndex -lt 0) {
        return $null
    }

    if ($fitIndex -gt 0) {
        for ($index = 0; $index -lt $fitIndex; $index++) {
            $resourceClass = if ($null -eq $Pending[$index].PSObject.Properties["ResourceClass"]) { "Ordinary" } else { [string]$Pending[$index].ResourceClass }
            if ([int]$AvailableResourceClassSlots[$resourceClass] -gt 0 -and $Pending[$index].SchedulingDeferrals -ge 1) {
                return $null
            }
        }

        for ($index = 0; $index -lt $fitIndex; $index++) {
            $resourceClass = if ($null -eq $Pending[$index].PSObject.Properties["ResourceClass"]) { "Ordinary" } else { [string]$Pending[$index].ResourceClass }
            if ([int]$AvailableResourceClassSlots[$resourceClass] -gt 0) {
                $Pending[$index].SchedulingDeferrals++
            }
        }
    }

    $selected = $Pending[$fitIndex]
    $Pending.RemoveAt($fitIndex)
    return $selected
}

function Invoke-VerificationParallelPhases {
    param(
        [ValidateRange(1, 32)]
        [int]$MaximumWorkers = 2,

        [ValidateRange(1, 32)]
        [int]$MaximumResourceCapacity = $MaximumWorkers,

        [ValidateRange(1, 32)]
        [int]$MaximumProcessHeavyWorkers = $MaximumWorkers,

        [ValidateRange(1, 32)]
        [int]$MaximumCpuBoundWorkers = $MaximumWorkers
    )

    if ($MaximumProcessHeavyWorkers -gt $MaximumWorkers -or $MaximumCpuBoundWorkers -gt $MaximumWorkers) {
        throw "Parallel verification resource-class limits cannot exceed the maximum worker count $MaximumWorkers. process_heavy=$MaximumProcessHeavyWorkers cpu_bound=$MaximumCpuBoundWorkers"
    }

    $oversizedPhases = @($script:VerificationParallelPhases | Where-Object { $_.Weight -gt $MaximumResourceCapacity } | Sort-Object Name)
    if ($oversizedPhases.Count -gt 0) {
        $details = $oversizedPhases | ForEach-Object { "'$($_.Name)' requires weight $($_.Weight)" }
        throw "Parallel verification cannot schedule phases beyond logical resource capacity $MaximumResourceCapacity. $($details -join '; ')"
    }

    $underweightedPhases = @($script:VerificationParallelPhases | Where-Object {
        $minimumWeight = switch ($_.ResourceClass) {
            "Ordinary" { 1; break }
            "CpuBound" { [Math]::Min(2, [Math]::Max(1, [int][Math]::Ceiling($MaximumResourceCapacity / 3.0))); break }
            "ProcessHeavy" { [Math]::Min(3, [Math]::Max(1, [int][Math]::Ceiling($MaximumResourceCapacity / 2.0))); break }
        }
        $_.Weight -lt $minimumWeight
    } | Sort-Object Name)
    if ($underweightedPhases.Count -gt 0) {
        $details = $underweightedPhases | ForEach-Object { "'$($_.Name)' declares $($_.ResourceClass) weight $($_.Weight)" }
        throw "Parallel verification resource classes are underweighted for logical resource capacity $MaximumResourceCapacity. $($details -join '; ')"
    }

    $pending = [Collections.Generic.List[object]]::new()
    $schedulingOrder = @(Get-VerificationParallelPhaseSchedulingOrder -Phases @($script:VerificationParallelPhases) -MaximumProcessHeavyWorkers $MaximumProcessHeavyWorkers -MaximumCpuBoundWorkers $MaximumCpuBoundWorkers)
    foreach ($phase in $schedulingOrder) {
        $phase.EffectiveWeight = $phase.Weight
        $phase.SchedulingDeferrals = 0
        $pending.Add($phase)
    }

    $running = [Collections.Generic.List[object]]::new()
    $results = [Collections.Generic.List[object]]::new()
    $activeResourceCapacity = 0
    $activeResourceClassCounts = @{
        Ordinary = 0
        CpuBound = 0
        ProcessHeavy = 0
    }
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            while ($pending.Count -gt 0 -and $running.Count -lt $MaximumWorkers -and $activeResourceCapacity -lt $MaximumResourceCapacity) {
                $availableCapacity = $MaximumResourceCapacity - $activeResourceCapacity
                $availableResourceClassSlots = @{
                    Ordinary = $MaximumWorkers
                    CpuBound = $MaximumCpuBoundWorkers - $activeResourceClassCounts.CpuBound
                    ProcessHeavy = $MaximumProcessHeavyWorkers - $activeResourceClassCounts.ProcessHeavy
                }
                $phase = Select-VerificationParallelPhase -Pending $pending -AvailableCapacity $availableCapacity -AvailableResourceClassSlots $availableResourceClassSlots
                if ($null -eq $phase) {
                    break
                }

                $outputDirectory = Split-Path -Parent $phase.OutputPath
                New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
                if (Test-Path -LiteralPath $phase.OutputPath) {
                    Remove-Item -LiteralPath $phase.OutputPath -Force
                }

                $startInfo = New-VerificationProcessStartInfo -FileName $phase.FileName -Arguments $phase.Arguments -WorkingDirectory $phase.WorkingDirectory -Environment $phase.Environment
                $startInfo.RedirectStandardOutput = $true
                $startInfo.RedirectStandardError = $true
                $startInfo.CreateNoWindow = $true
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $startInfo
                $startedAtUtc = [DateTimeOffset]::UtcNow
                $stopwatch = [Diagnostics.Stopwatch]::StartNew()
                Write-Host "VERIFY_PARALLEL_PHASE_START name=$($phase.Name) duration_estimate_seconds=$($phase.EstimatedDurationSeconds) scheduling_priority_seconds=$($phase.SchedulingPrioritySeconds) resource_class=$($phase.ResourceClass) declared_weight=$($phase.Weight) effective_weight=$($phase.EffectiveWeight) started_at_utc=$($startedAtUtc.ToString("O")) timeout_seconds=$($phase.TimeoutSeconds) active_workers=$($running.Count + 1) maximum_workers=$MaximumWorkers active_capacity=$($activeResourceCapacity + $phase.EffectiveWeight) maximum_capacity=$MaximumResourceCapacity active_process_heavy=$($activeResourceClassCounts.ProcessHeavy + [int]($phase.ResourceClass -ceq "ProcessHeavy")) maximum_process_heavy=$MaximumProcessHeavyWorkers active_cpu_bound=$($activeResourceClassCounts.CpuBound + [int]($phase.ResourceClass -ceq "CpuBound")) maximum_cpu_bound=$MaximumCpuBoundWorkers"
                try {
                    if (-not $process.Start()) {
                        throw "The process API returned false."
                    }

                    $outputTask = $process.StandardOutput.ReadToEndAsync()
                    $errorTask = $process.StandardError.ReadToEndAsync()
                    $running.Add([pscustomobject]@{
                        Phase = $phase
                        Process = $process
                        Stopwatch = $stopwatch
                        StandardOutput = $outputTask
                        StandardError = $errorTask
                    })
                    $activeResourceCapacity += $phase.EffectiveWeight
                    $activeResourceClassCounts[$phase.ResourceClass]++
                }
                catch {
                    $process.Dispose()
                    throw "Parallel verification phase '$($phase.Name)' could not start '$($phase.FileName)'. $($_.Exception.Message)"
                }
            }

            if ($running.Count -eq 0) {
                throw "Parallel verification scheduler made no progress within resource capacity $MaximumResourceCapacity."
            }

            Start-Sleep -Milliseconds 50
            foreach ($entry in @($running)) {
                $timedOut = $entry.Stopwatch.Elapsed.TotalSeconds -ge $entry.Phase.TimeoutSeconds
                if (-not $entry.Process.HasExited -and -not $timedOut) {
                    continue
                }

                if ($timedOut -and -not $entry.Process.HasExited) {
                    Stop-VerificationProcessTree $entry.Process
                }

                $entry.Process.WaitForExit()
                $entry.Stopwatch.Stop()
                $standardOutput = $entry.StandardOutput.GetAwaiter().GetResult()
                $standardError = $entry.StandardError.GetAwaiter().GetResult()
                [IO.File]::WriteAllText($entry.Phase.OutputPath, $standardOutput + $standardError, [Text.UTF8Encoding]::new($false))
                $result = [pscustomobject]@{
                    Name = $entry.Phase.Name
                    ExitCode = if ($timedOut) { $null } else { $entry.Process.ExitCode }
                    TimedOut = $timedOut
                    ElapsedSeconds = [Math]::Round($entry.Stopwatch.Elapsed.TotalSeconds, 3)
                    OutputPath = $entry.Phase.OutputPath
                    CoverageSearchRoot = $entry.Phase.CoverageSearchRoot
                    TrxPath = $entry.Phase.TrxPath
                    Weight = $entry.Phase.Weight
                    EffectiveWeight = $entry.Phase.EffectiveWeight
                    ResourceClass = $entry.Phase.ResourceClass
                    EstimatedDurationSeconds = $entry.Phase.EstimatedDurationSeconds
                    SchedulingPrioritySeconds = $entry.Phase.SchedulingPrioritySeconds
                }
                $results.Add($result)
                $status = if ($timedOut) { "timeout" } elseif ($result.ExitCode -eq 0) { "passed" } else { "failed" }
                if ($timedOut) {
                    Write-Host "VERIFY_CHILD_TIMEOUT name=$($result.Name) timeout_seconds=$($entry.Phase.TimeoutSeconds) elapsed_seconds=$($result.ElapsedSeconds)"
                }
                Write-Host "VERIFY_PARALLEL_PHASE_COMPLETE name=$($result.Name) status=$status exit_code=$($result.ExitCode) duration_estimate_seconds=$($result.EstimatedDurationSeconds) scheduling_priority_seconds=$($result.SchedulingPrioritySeconds) resource_class=$($result.ResourceClass) declared_weight=$($result.Weight) effective_weight=$($result.EffectiveWeight) elapsed_seconds=$($result.ElapsedSeconds) output_path=$($result.OutputPath) completed_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"
                [void]$running.Remove($entry)
                $activeResourceCapacity -= $entry.Phase.EffectiveWeight
                $activeResourceClassCounts[$entry.Phase.ResourceClass]--
                $entry.Process.Dispose()
            }
        }
    }
    finally {
        foreach ($entry in @($running)) {
            if (-not $entry.Process.HasExited) {
                Stop-VerificationProcessTree $entry.Process
            }

            $entry.Process.Dispose()
        }
    }

    $failures = @($results | Where-Object { $_.TimedOut -or $_.ExitCode -ne 0 })
    if ($failures.Count -gt 0) {
        $details = $failures | ForEach-Object {
            $status = if ($_.TimedOut) { "timed out" } else { "exited with code $($_.ExitCode)" }
            "'$($_.Name)' $status after $($_.ElapsedSeconds) seconds; output: $($_.OutputPath)"
        }
        throw "Parallel verification failed closed after all running phases were aggregated. $($details -join '; ')"
    }

    return @($results | Sort-Object Name)
}
