param(
    [switch]$Qualification,

    [ValidatePattern('^$|^[0-9a-fA-F]{40}$')]
    [string]$BaseCommit = "",

    [ValidatePattern('^$|^[0-9a-fA-F]{40}$')]
    [string]$HeadCommit = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("Full", "Solution", "StaticContracts", "NestedProcess")]
    [string]$VerificationComponent = "Full",

    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5))),

    [ValidateRange(1, 1680)]
    [int]$DeadlineSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$verificationMode = if ($Qualification) { "qualification" } else { "promotion" }
$verificationComponentName = switch ($VerificationComponent) {
    "Full" { "full"; break }
    "Solution" { "solution"; break }
    "StaticContracts" { "static-contracts"; break }
    "NestedProcess" { "nested-process"; break }
}
$resultsRoot = Join-Path $repoRoot $(if ($Qualification) { "tests\QualificationResults" } else { "tests\VerificationResults" })
$watchdogLogPath = Join-Path $resultsRoot "watchdog.log"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-deadline.ps1")
. (Join-Path $PSScriptRoot "verification-watchdog-policy.ps1")

function Assert-VerificationWatchdogReceiptInput {
    param([string]$Path, [string]$Description)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Verification watchdog cannot authenticate its successful receipt because the $Description is missing: $Path"
    }
}

if ($Qualification -and ([string]::IsNullOrWhiteSpace($BaseCommit) -or [string]::IsNullOrWhiteSpace($HeadCommit))) {
    throw "Qualification requires exact -BaseCommit and -HeadCommit values."
}
if (-not $Qualification -and (-not [string]::IsNullOrWhiteSpace($BaseCommit) -or -not [string]::IsNullOrWhiteSpace($HeadCommit))) {
    throw "Commit selection is valid only with -Qualification."
}
if ($Qualification -and $VerificationComponent -ne "Full") {
    throw "A non-Full verification component is valid only for promotion verification."
}
Assert-VerificationWatchdogDeadlineContract -Qualification $Qualification -VerificationComponent $VerificationComponent -DeadlineSeconds $DeadlineSeconds

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
    $arguments += @("-MaximumTestWorkers", $MaximumTestWorkers.ToString([Globalization.CultureInfo]::InvariantCulture), "-VerificationComponent", $VerificationComponent)
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

    $expectedComponent = if ($Qualification -or $VerificationComponent -eq "Full") { "" } else { $verificationComponentName }
    $completionMarkerCount = Get-VerificationCompletionMarkerCount -StandardOutput $standardOutput -ExpectedComponent $expectedComponent
    $childTimedOut = [regex]::IsMatch($combinedOutput, '(?m)^VERIFY_CHILD_TIMEOUT name=')
    $elapsedTicks = if ($deadlineExceeded) { $deadlineTicks + 1L } else { $stopwatch.Elapsed.Ticks }
    $exitCode = if ($deadlineExceeded) { $null } else { $process.ExitCode }
    $disposition = Get-VerificationDeadlineDisposition -ElapsedTicks $elapsedTicks -DeadlineTicks $deadlineTicks -ProcessExited $process.HasExited -ExitCode $exitCode -CompletionMarkerCount $completionMarkerCount -ChildTimedOut $childTimedOut -CancellationRequested $cancellationRequested
    if ($disposition.Succeeded -and -not $Qualification -and $VerificationComponent -ne "Full") {
        Assert-VerificationWatchdogReceiptInput -Path $watchdogLogPath -Description "watchdog log"
        $componentEvidencePath = Join-Path $resultsRoot "verification-component-evidence.json"
        $componentManifestPath = Join-Path $resultsRoot "verification-component-manifest.json"
        Assert-VerificationWatchdogReceiptInput -Path $componentEvidencePath -Description "component evidence"
        Assert-VerificationWatchdogReceiptInput -Path $componentManifestPath -Description "component manifest"
        $repositoryHead = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
        if ([string]::IsNullOrWhiteSpace($repositoryHead)) {
            throw "Verification watchdog could not resolve the repository HEAD for its authenticated receipt."
        }
        $watchdogEvidencePath = Join-Path $resultsRoot "verification-watchdog-evidence.json"
        $watchdogEvidence = [ordered]@{
            schemaVersion = 1
            component = $verificationComponentName
            mode = $verificationMode
            repositoryHead = $repositoryHead
            githubRunId = $env:GITHUB_RUN_ID
            githubRunAttempt = $env:GITHUB_RUN_ATTEMPT
            deadlineSeconds = $DeadlineSeconds
            elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            exitCode = [int]$exitCode
            completionMarkerCount = $completionMarkerCount
            status = "passed"
            watchdogLogSha256 = (Get-FileHash -LiteralPath $watchdogLogPath -Algorithm SHA256).Hash.ToLowerInvariant()
            componentEvidenceSha256 = (Get-FileHash -LiteralPath $componentEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
            componentManifestSha256 = (Get-FileHash -LiteralPath $componentManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        [IO.File]::WriteAllText($watchdogEvidencePath, ($watchdogEvidence | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    }
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
