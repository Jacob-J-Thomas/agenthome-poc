Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$deadlineScriptPath = Join-Path $repoRoot "scripts\verification-deadline.ps1"
$watchdogScriptPath = Join-Path $repoRoot "scripts\verify-with-watchdog.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$verifyWorkflowPath = Join-Path $repoRoot ".github\workflows\verify.yml"
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

. $deadlineScriptPath

$lfMarker = "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=600`n"
$crlfMarker = "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=599.999`r`n"
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $lfMarker) -Expected 1 -Message "One exact LF completion marker must be accepted."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $crlfMarker) -Expected 1 -Message "One exact Windows CRLF completion marker must be accepted."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput "VERIFY_COMPLETE schema_version=1 status=passed`r`n") -Expected 0 -Message "A partial completion marker must be rejected."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput "prefix VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=1`n") -Expected 0 -Message "A prefixed completion marker must be rejected."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput ($lfMarker + $crlfMarker)) -Expected 2 -Message "Duplicate exact completion markers must remain visible to fail-closed disposition."

$deadlineTicks = [TimeSpan]::FromSeconds(600).Ticks
$exactDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks $deadlineTicks -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-True -Condition $exactDeadline.Succeeded -Message "Exactly 600 seconds must remain inside the inclusive deadline."
Assert-Equal -Actual $exactDeadline.Code -Expected "passed" -Message "Successful disposition code mismatch."

$overDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks ($deadlineTicks + 1) -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $overDeadline.Code -Expected "deadline-exceeded" -Message "One tick over 600 seconds must fail."

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
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$workflow = Get-Content -LiteralPath $verifyWorkflowPath -Raw
Assert-True -Condition ($watchdogScript.IndexOf('[int]$DeadlineSeconds = 600', [StringComparison]::Ordinal) -ge 0) -Message "The external watchdog must default to exactly 600 seconds."
Assert-True -Condition ($watchdogScript.IndexOf('[ValidateRange(1, 600)]', [StringComparison]::Ordinal) -ge 0) -Message "No accepted watchdog override may exceed 600 seconds."
Assert-True -Condition ($watchdogScript.IndexOf('Stop-VerificationProcessTree $process', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must terminate the full verifier process tree."
Assert-True -Condition ($verifyScript.IndexOf('VERIFY_COMPLETE schema_version=1 status=passed', [StringComparison]::Ordinal) -ge 0) -Message "The verifier must emit an exact terminal marker only after successful completion."
Assert-True -Condition ($workflow.IndexOf('./scripts/verify-with-watchdog.ps1 -Configuration Release', [StringComparison]::Ordinal) -ge 0) -Message "Standard CI must invoke the external watchdog."
Assert-True -Condition ($workflow.IndexOf('run: ./scripts/verify.ps1 -Configuration Release', [StringComparison]::Ordinal) -lt 0) -Message "Standard CI must not bypass the external watchdog."

Write-Output "Verification watchdog contract tests passed ($assertionCount assertions)."
