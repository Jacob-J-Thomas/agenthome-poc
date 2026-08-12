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

        [int]$Priority = 0
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
    }
}

function Invoke-VerificationParallelPhases {
    param(
        [ValidateRange(1, 32)]
        [int]$MaximumWorkers = 2
    )

    $pending = [Collections.Generic.Queue[object]]::new()
    foreach ($phase in @($script:VerificationParallelPhases | Sort-Object -Property @{ Expression = "Priority"; Descending = $true }, @{ Expression = "Name"; Descending = $false })) {
        $pending.Enqueue($phase)
    }

    $running = [Collections.Generic.List[object]]::new()
    $results = [Collections.Generic.List[object]]::new()
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            while ($pending.Count -gt 0 -and $running.Count -lt $MaximumWorkers) {
                $phase = $pending.Dequeue()
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
                Write-Host "VERIFY_PARALLEL_PHASE_START name=$($phase.Name) priority=$($phase.Priority) started_at_utc=$($startedAtUtc.ToString("O")) timeout_seconds=$($phase.TimeoutSeconds) active_workers=$($running.Count + 1) maximum_workers=$MaximumWorkers"
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
                }
                catch {
                    $process.Dispose()
                    throw "Parallel verification phase '$($phase.Name)' could not start '$($phase.FileName)'. $($_.Exception.Message)"
                }
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
                }
                $results.Add($result)
                $status = if ($timedOut) { "timeout" } elseif ($result.ExitCode -eq 0) { "passed" } else { "failed" }
                if ($timedOut) {
                    Write-Host "VERIFY_CHILD_TIMEOUT name=$($result.Name) timeout_seconds=$($entry.Phase.TimeoutSeconds) elapsed_seconds=$($result.ElapsedSeconds)"
                }
                Write-Host "VERIFY_PARALLEL_PHASE_COMPLETE name=$($result.Name) status=$status exit_code=$($result.ExitCode) elapsed_seconds=$($result.ElapsedSeconds) output_path=$($result.OutputPath) completed_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"
                [void]$running.Remove($entry)
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
