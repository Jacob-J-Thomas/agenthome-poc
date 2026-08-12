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

        [int]$Priority = 0,

        [ValidateRange(1, 32)]
        [int]$Weight = 1,

        [ValidateSet("Ordinary", "ProcessHeavy")]
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
        Priority = $Priority
        Weight = $Weight
        ResourceClass = $ResourceClass
        SchedulingDeferrals = 0
    }
}

function Select-VerificationParallelPhase {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.List[object]]$Pending,

        [ValidateRange(0, 32)]
        [int]$AvailableCapacity
    )

    if ($Pending.Count -eq 0 -or $AvailableCapacity -eq 0) {
        return $null
    }

    $fitIndex = -1
    for ($index = 0; $index -lt $Pending.Count; $index++) {
        if ($Pending[$index].Weight -le $AvailableCapacity) {
            $fitIndex = $index
            break
        }
    }

    if ($fitIndex -lt 0) {
        return $null
    }

    if ($fitIndex -gt 0) {
        for ($index = 0; $index -lt $fitIndex; $index++) {
            if ($Pending[$index].SchedulingDeferrals -ge 1) {
                return $null
            }
        }

        for ($index = 0; $index -lt $fitIndex; $index++) {
            $Pending[$index].SchedulingDeferrals++
        }
    }

    $selected = $Pending[$fitIndex]
    $Pending.RemoveAt($fitIndex)
    return $selected
}

function Invoke-VerificationParallelPhases {
    param(
        [ValidateRange(1, 32)]
        [int]$MaximumWorkers = 2
    )

    $maximumResourceCapacity = [Math]::Min($MaximumWorkers, [Math]::Max(1, [Environment]::ProcessorCount))
    $oversizedPhases = @($script:VerificationParallelPhases | Where-Object { $_.Weight -gt $maximumResourceCapacity } | Sort-Object Name)
    if ($oversizedPhases.Count -gt 0) {
        $details = $oversizedPhases | ForEach-Object { "'$($_.Name)' requires weight $($_.Weight)" }
        throw "Parallel verification cannot schedule phases beyond the hardware-bounded resource capacity $maximumResourceCapacity. $($details -join '; ')"
    }

    $pending = [Collections.Generic.List[object]]::new()
    foreach ($phase in @($script:VerificationParallelPhases | Sort-Object -Property @{ Expression = "Priority"; Descending = $true }, @{ Expression = "Name"; Descending = $false })) {
        $phase.SchedulingDeferrals = 0
        $pending.Add($phase)
    }

    $running = [Collections.Generic.List[object]]::new()
    $results = [Collections.Generic.List[object]]::new()
    $activeResourceCapacity = 0
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            while ($pending.Count -gt 0 -and $activeResourceCapacity -lt $maximumResourceCapacity) {
                $availableCapacity = $maximumResourceCapacity - $activeResourceCapacity
                $phase = Select-VerificationParallelPhase -Pending $pending -AvailableCapacity $availableCapacity
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
                Write-Host "VERIFY_PARALLEL_PHASE_START name=$($phase.Name) priority=$($phase.Priority) resource_class=$($phase.ResourceClass) weight=$($phase.Weight) started_at_utc=$($startedAtUtc.ToString("O")) timeout_seconds=$($phase.TimeoutSeconds) active_workers=$($running.Count + 1) active_capacity=$($activeResourceCapacity + $phase.Weight) maximum_capacity=$maximumResourceCapacity requested_capacity=$MaximumWorkers"
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
                    $activeResourceCapacity += $phase.Weight
                }
                catch {
                    $process.Dispose()
                    throw "Parallel verification phase '$($phase.Name)' could not start '$($phase.FileName)'. $($_.Exception.Message)"
                }
            }

            if ($running.Count -eq 0) {
                throw "Parallel verification scheduler made no progress within resource capacity $maximumResourceCapacity."
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
                    ResourceClass = $entry.Phase.ResourceClass
                }
                $results.Add($result)
                $status = if ($timedOut) { "timeout" } elseif ($result.ExitCode -eq 0) { "passed" } else { "failed" }
                if ($timedOut) {
                    Write-Host "VERIFY_CHILD_TIMEOUT name=$($result.Name) timeout_seconds=$($entry.Phase.TimeoutSeconds) elapsed_seconds=$($result.ElapsedSeconds)"
                }
                Write-Host "VERIFY_PARALLEL_PHASE_COMPLETE name=$($result.Name) status=$status exit_code=$($result.ExitCode) resource_class=$($result.ResourceClass) weight=$($result.Weight) elapsed_seconds=$($result.ElapsedSeconds) output_path=$($result.OutputPath) completed_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"
                [void]$running.Remove($entry)
                $activeResourceCapacity -= $entry.Phase.Weight
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
