param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = 8,

    [ValidateRange(1, 600)]
    [int]$DeadlineSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $repoRoot "tests\VerificationResults"
$watchdogLogPath = Join-Path $resultsRoot "watchdog.log"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$completionMarkerPattern = '(?m)^VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=[0-9]+(?:\.[0-9]+)?$'

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-deadline.ps1")

$arguments = @("-NoProfile")
if ($runningOnWindows) {
    $arguments += @("-ExecutionPolicy", "Bypass")
}

$arguments += @(
    "-File", (Join-Path $PSScriptRoot "verify.ps1"),
    "-Configuration", $Configuration,
    "-MaximumTestWorkers", $MaximumTestWorkers.ToString([Globalization.CultureInfo]::InvariantCulture)
)

$startInfo = New-VerificationProcessStartInfo -FileName $powerShellExecutable -Arguments $arguments -WorkingDirectory $repoRoot
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$processStarted = $false
$deadlineExceeded = $false
$cancellationRequested = $false
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

Write-Output "VERIFY_WATCHDOG_START schema_version=1 deadline_seconds=$DeadlineSeconds started_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"
try {
    if (-not $process.Start()) {
        throw "The verifier process API returned false."
    }

    $processStarted = $true
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $deadline = [TimeSpan]::FromSeconds($DeadlineSeconds)
    while (-not $process.HasExited) {
        if ($stopwatch.Elapsed -gt $deadline) {
            $deadlineExceeded = $true
            Stop-VerificationProcessTree $process
            break
        }

        Start-Sleep -Milliseconds 50
    }

    $process.WaitForExit()
    $stopwatch.Stop()
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    $combinedOutput = $standardOutput + $standardError

    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
    [IO.File]::WriteAllText($watchdogLogPath, $combinedOutput, [Text.UTF8Encoding]::new($false))
    if (-not [string]::IsNullOrEmpty($standardOutput)) {
        Write-Output $standardOutput.TrimEnd()
    }
    if (-not [string]::IsNullOrEmpty($standardError)) {
        [Console]::Error.WriteLine($standardError.TrimEnd())
    }

    $completionMarkerCount = [regex]::Matches($standardOutput, $completionMarkerPattern).Count
    $childTimedOut = [regex]::IsMatch($combinedOutput, '(?m)^VERIFY_CHILD_TIMEOUT name=')
    $elapsedTicks = if ($deadlineExceeded) { $deadline.Ticks + 1L } else { $stopwatch.Elapsed.Ticks }
    $exitCode = if ($deadlineExceeded) { $null } else { $process.ExitCode }
    $disposition = Get-VerificationDeadlineDisposition -ElapsedTicks $elapsedTicks -DeadlineTicks $deadline.Ticks -ProcessExited $process.HasExited -ExitCode $exitCode -CompletionMarkerCount $completionMarkerCount -ChildTimedOut $childTimedOut -CancellationRequested $cancellationRequested
    Write-Output "VERIFY_WATCHDOG_COMPLETE schema_version=1 status=$($disposition.Code) elapsed_seconds=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)) marker_count=$completionMarkerCount child_exit_code=$($process.ExitCode) log=$watchdogLogPath"
    if (-not $disposition.Succeeded) {
        throw "Verification watchdog failed closed: $($disposition.Code). $($disposition.Message) Log: $watchdogLogPath"
    }
}
catch [Management.Automation.PipelineStoppedException] {
    $cancellationRequested = $true
    throw
}
finally {
    if ($processStarted -and -not $process.HasExited) {
        Stop-VerificationProcessTree $process
    }

    $process.Dispose()
}
