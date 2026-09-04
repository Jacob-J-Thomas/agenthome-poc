Set-StrictMode -Version Latest

$script:VerificationQualificationWatchdogDeadlineSeconds = 1680
$script:VerificationStaticContractsWatchdogDeadlineSeconds = 600
$script:VerificationNestedProcessWatchdogDeadlineSeconds = 600
$script:VerificationFullWatchdogMaximumDeadlineSeconds = 1200
$script:VerificationSolutionWatchdogDeadlineSeconds = 1500

function Assert-VerificationWatchdogDeadlineContract {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Qualification,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Full", "Solution", "StaticContracts", "NestedProcess")]
        [string]$VerificationComponent,

        [Parameter(Mandatory = $true)]
        [int]$DeadlineSeconds
    )

    if ($Qualification) {
        if ($VerificationComponent -ine "Full") {
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
        "NestedProcess" { $script:VerificationNestedProcessWatchdogDeadlineSeconds; break }
        "Full" { $null; break }
    }
    if ($null -eq $expectedDeadlineSeconds) {
        if ($DeadlineSeconds -lt 1 -or $DeadlineSeconds -gt $script:VerificationFullWatchdogMaximumDeadlineSeconds) {
            throw "Full verification requires a watchdog deadline between 1 and $script:VerificationFullWatchdogMaximumDeadlineSeconds seconds."
        }
        return
    }
    if ($DeadlineSeconds -ne $expectedDeadlineSeconds) {
        throw "Promotion component '$VerificationComponent' requires the exact $expectedDeadlineSeconds-second watchdog deadline."
    }
}
