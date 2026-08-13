Set-StrictMode -Version Latest

function Test-VerificationDeadlineExceeded {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, [long]::MaxValue)]
        [long]$ElapsedTicks,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, [long]::MaxValue)]
        [long]$DeadlineTicks
    )

    return $ElapsedTicks -gt $DeadlineTicks
}

function Get-VerificationCompletionMarkerCount {
    param(
        [AllowEmptyString()]
        [string]$StandardOutput
    )

    if ([string]::IsNullOrEmpty($StandardOutput)) {
        return 0
    }

    $pattern = '(?m)^VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=[0-9]+(?:\.[0-9]+)?\r?$'
    return [regex]::Matches($StandardOutput, $pattern).Count
}

function Get-VerificationDeadlineDisposition {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, [long]::MaxValue)]
        [long]$ElapsedTicks,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, [long]::MaxValue)]
        [long]$DeadlineTicks,

        [Parameter(Mandatory = $true)]
        [bool]$ProcessExited,

        [AllowNull()]
        [object]$ExitCode,

        [Parameter(Mandatory = $true)]
        [ValidateRange(0, [int]::MaxValue)]
        [int]$CompletionMarkerCount,

        [Parameter(Mandatory = $true)]
        [bool]$ChildTimedOut,

        [Parameter(Mandatory = $true)]
        [bool]$CancellationRequested
    )

    if ($CancellationRequested) {
        return [pscustomobject]@{ Succeeded = $false; Code = "cancelled"; Message = "Verification was cancelled before a complete result was accepted." }
    }

    if ($ChildTimedOut) {
        return [pscustomobject]@{ Succeeded = $false; Code = "child-timeout"; Message = "A verifier child phase reported a timeout." }
    }

    if (Test-VerificationDeadlineExceeded -ElapsedTicks $ElapsedTicks -DeadlineTicks $DeadlineTicks) {
        return [pscustomobject]@{ Succeeded = $false; Code = "deadline-exceeded"; Message = "Verification exceeded its inclusive deadline." }
    }

    if (-not $ProcessExited) {
        return [pscustomobject]@{ Succeeded = $false; Code = "incomplete-process"; Message = "The verifier process did not reach a terminal state." }
    }

    if ($null -eq $ExitCode -or [int]$ExitCode -ne 0) {
        $actualExitCode = if ($null -eq $ExitCode) { "missing" } else { [int]$ExitCode }
        return [pscustomobject]@{ Succeeded = $false; Code = "child-failed"; Message = "The verifier exited unsuccessfully (exit code: $actualExitCode)." }
    }

    if ($CompletionMarkerCount -ne 1) {
        return [pscustomobject]@{ Succeeded = $false; Code = "completion-evidence-invalid"; Message = "The verifier emitted $CompletionMarkerCount exact terminal completion markers; exactly one is required." }
    }

    return [pscustomobject]@{ Succeeded = $true; Code = "passed"; Message = "Verification completed within its inclusive deadline with exact terminal evidence." }
}
