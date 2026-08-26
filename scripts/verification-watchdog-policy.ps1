Set-StrictMode -Version Latest

$script:VerificationQualificationWatchdogDeadlineSeconds = 480
$script:VerificationStaticContractsWatchdogDeadlineSeconds = 600
$script:VerificationSolutionWatchdogDeadlineSeconds = 1500

function Assert-VerificationWatchdogDeadlineContract {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Qualification,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Full", "Solution", "StaticContracts")]
        [string]$VerificationComponent,

        [Parameter(Mandatory = $true)]
        [int]$DeadlineSeconds
    )

    if ($Qualification) {
        if ($VerificationComponent -cne "Full") {
            throw "Qualification requires the Full verification component."
        }
        if ($DeadlineSeconds -ne $script:VerificationQualificationWatchdogDeadlineSeconds) {
            throw "Qualification requires the exact $script:VerificationQualificationWatchdogDeadlineSeconds-second watchdog deadline."
        }
        return
    }

    $expectedDeadlineSeconds = switch ($VerificationComponent) {
        "Solution" { $script:VerificationSolutionWatchdogDeadlineSeconds; break }
        "StaticContracts" { $script:VerificationStaticContractsWatchdogDeadlineSeconds; break }
        "Full" { $null; break }
    }
    if ($null -eq $expectedDeadlineSeconds) {
        if ($DeadlineSeconds -lt 1 -or $DeadlineSeconds -gt $script:VerificationSolutionWatchdogDeadlineSeconds) {
            throw "Full verification requires a watchdog deadline between 1 and $script:VerificationSolutionWatchdogDeadlineSeconds seconds."
        }
        return
    }
    if ($DeadlineSeconds -ne $expectedDeadlineSeconds) {
        throw "Promotion component '$VerificationComponent' requires the exact $expectedDeadlineSeconds-second watchdog deadline."
    }
}
