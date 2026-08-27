Set-StrictMode -Version Latest

$script:QualificationContractScheduleProfiles = @(
    [pscustomobject]@{ ScriptName = "verify-bounded-phases.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-coverage.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    # https://github.com/Jacob-J-Thomas/agenthome-poc/issues/610: recent Windows exclusive samples for parallel were 17.052, 16.769, and 14.791 seconds; retain a 20-second estimate (2.948 seconds above the maximum) while preserving the 90-second child bound.
    [pscustomobject]@{ ScriptName = "verify-parallel.tests.ps1"; EstimatedDurationSeconds = 20; TimeoutSeconds = 90; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Exclusive" }
    # https://github.com/Jacob-J-Thomas/agenthome-poc/issues/610: recent Windows exclusive samples for preflight were 14.225, 13.938, and 12.975 seconds; retain a 20-second estimate (5.775 seconds above the maximum) while preserving the 90-second child bound.
    [pscustomobject]@{ ScriptName = "verify-preflight-overlap.tests.ps1"; EstimatedDurationSeconds = 20; TimeoutSeconds = 90; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Exclusive" }
    [pscustomobject]@{ ScriptName = "verify-sdk-diagnostics.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-test-inventory.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-watchdog.tests.ps1"; EstimatedDurationSeconds = 40; TimeoutSeconds = 120; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-promotion-fan-in.tests.ps1"; EstimatedDurationSeconds = 20; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
)
$script:QualificationTestScheduleProfiles = @(
    # https://github.com/Jacob-J-Thomas/agenthome-poc/issues/610: reserve six of eight units on the four-worker qualification posture for full Persistence. Run the two Windows-dominant full suites in separate bounded waves because hosted run 33033802308 proved their concurrent pair unsafe; smaller supported postures reserve all available units for Persistence.
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Persistence.Tests"; EstimatedDurationSeconds = 220; TimeoutSeconds = 270; Weight = 6; ResourceClass = "ProcessHeavy"; Isolation = "Exclusive" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Startup.Tests"; EstimatedDurationSeconds = 180; TimeoutSeconds = 240; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Exclusive" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Web.Tests"; EstimatedDurationSeconds = 75; TimeoutSeconds = 150; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Shared" }
    [pscustomobject]@{ ProjectName = "EmbodySense.IntegrationTests"; EstimatedDurationSeconds = 55; TimeoutSeconds = 120; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Shared" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Application.Tests"; EstimatedDurationSeconds = 45; TimeoutSeconds = 120; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Shared" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Clients.Tests"; EstimatedDurationSeconds = 20; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Cli.Command.Tests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Common.Tests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ProjectName = "EmbodySense.E2ETests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
)

function Get-QualificationWorkerCount {
    param(
        [Parameter(Mandatory = $true)] [ValidateRange(1, 4)] [int]$MaximumWorkers,
        [Parameter(Mandatory = $true)] [ValidateRange(1, [int]::MaxValue)] [int]$HardwareProcessorCount
    )

    return [Math]::Min($MaximumWorkers, [Math]::Min(4, $HardwareProcessorCount))
}

function Get-QualificationResourceCapacity {
    param([Parameter(Mandatory = $true)] [ValidateRange(1, 4)] [int]$WorkerCount)

    return [Math]::Max(3, 2 * $WorkerCount)
}

function Get-QualificationTestScheduleProfile {
    param(
        [Parameter(Mandatory = $true)] [string]$ProjectName,
        [ValidateRange(3, 8)] [int]$ResourceCapacity = 8
    )

    $profiles = @($script:QualificationTestScheduleProfiles | Where-Object { $_.ProjectName -ceq $ProjectName })
    if ($profiles.Count -ne 1) {
        throw "Qualification test project '$ProjectName' must have exactly one checked-in scheduling profile. Found $($profiles.Count)."
    }

    $profile = $profiles[0]
    if ($profile.ProjectName -cne "EmbodySense.Core.Persistence.Tests") {
        return $profile
    }

    return [pscustomobject]@{
        ProjectName = $profile.ProjectName
        EstimatedDurationSeconds = $profile.EstimatedDurationSeconds
        TimeoutSeconds = $profile.TimeoutSeconds
        Weight = [Math]::Min($profile.Weight, $ResourceCapacity)
        ResourceClass = $profile.ResourceClass
        Isolation = $profile.Isolation
    }
}

function Get-QualificationContractScheduleProfile {
    param([Parameter(Mandatory = $true)] [string]$ScriptName)

    $profiles = @($script:QualificationContractScheduleProfiles | Where-Object { $_.ScriptName -ceq $ScriptName })
    if ($profiles.Count -ne 1) {
        throw "Qualification contract '$ScriptName' must have exactly one checked-in scheduling profile. Found $($profiles.Count)."
    }
    if ($profiles[0].Isolation -cnotin @("Shared", "Exclusive")) {
        throw "Qualification contract '$ScriptName' has unsupported isolation '$($profiles[0].Isolation)'."
    }

    return $profiles[0]
}
