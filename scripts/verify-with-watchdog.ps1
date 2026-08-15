param(
    [switch]$Qualification,

    [ValidatePattern('^$|^[0-9a-fA-F]{40}$')]
    [string]$BaseCommit = "",

    [ValidatePattern('^$|^[0-9a-fA-F]{40}$')]
    [string]$HeadCommit = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5))),

    [ValidateRange(1, 900)]
    [int]$DeadlineSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$verificationMode = if ($Qualification) { "qualification" } else { "promotion" }
$resultsRoot = Join-Path $repoRoot $(if ($Qualification) { "tests\QualificationResults" } else { "tests\VerificationResults" })
$watchdogLogPath = Join-Path $resultsRoot "watchdog.log"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-deadline.ps1")

if ($Qualification -and ([string]::IsNullOrWhiteSpace($BaseCommit) -or [string]::IsNullOrWhiteSpace($HeadCommit))) {
    throw "Qualification requires exact -BaseCommit and -HeadCommit values."
}
if (-not $Qualification -and (-not [string]::IsNullOrWhiteSpace($BaseCommit) -or -not [string]::IsNullOrWhiteSpace($HeadCommit))) {
    throw "Commit selection is valid only with -Qualification."
}

$arguments = @("-NoProfile")
if ($runningOnWindows) {
    $arguments += @("-ExecutionPolicy", "Bypass")
}

$arguments += @(
    "-File", (Join-Path $PSScriptRoot $(if ($Qualification) { "qualify.ps1" } else { "verify.ps1" })),
    "-Configuration", $Configuration
)
if ($Qualification) {
    $arguments += @("-BaseCommit", $BaseCommit, "-HeadCommit", $HeadCommit, "-MaximumWorkers", ([Math]::Min(4, $MaximumTestWorkers)).ToString([Globalization.CultureInfo]::InvariantCulture))
}
else {
    $arguments += @("-MaximumTestWorkers", $MaximumTestWorkers.ToString([Globalization.CultureInfo]::InvariantCulture))
}

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

Write-Output "VERIFY_WATCHDOG_START schema_version=1 mode=$verificationMode deadline_seconds=$DeadlineSeconds started_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"
try {
    if (-not $process.Start()) {
        throw "The verifier process API returned false."
    }

    $processStarted = $true
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $deadlineTicks = [TimeSpan]::FromSeconds($DeadlineSeconds).Ticks
    while (-not $process.HasExited) {
        if (Test-VerificationDeadlineExceeded -ElapsedTicks $stopwatch.Elapsed.Ticks -DeadlineTicks $deadlineTicks) {
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

    $completionMarkerCount = Get-VerificationCompletionMarkerCount -StandardOutput $standardOutput
    $childTimedOut = [regex]::IsMatch($combinedOutput, '(?m)^VERIFY_CHILD_TIMEOUT name=')
    $elapsedTicks = if ($deadlineExceeded) { $deadlineTicks + 1L } else { $stopwatch.Elapsed.Ticks }
    $exitCode = if ($deadlineExceeded) { $null } else { $process.ExitCode }
    $disposition = Get-VerificationDeadlineDisposition -ElapsedTicks $elapsedTicks -DeadlineTicks $deadlineTicks -ProcessExited $process.HasExited -ExitCode $exitCode -CompletionMarkerCount $completionMarkerCount -ChildTimedOut $childTimedOut -CancellationRequested $cancellationRequested
    Write-Output "VERIFY_WATCHDOG_COMPLETE schema_version=1 mode=$verificationMode status=$($disposition.Code) elapsed_seconds=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)) marker_count=$completionMarkerCount child_exit_code=$($process.ExitCode) log=$watchdogLogPath"
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
