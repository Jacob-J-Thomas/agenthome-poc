Set-StrictMode -Version Latest

$script:QualificationContractScheduleProfiles = @(
    [pscustomobject]@{ ScriptName = "verify-bounded-phases.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-coverage.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-parallel.tests.ps1"; EstimatedDurationSeconds = 40; TimeoutSeconds = 90; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Exclusive" }
    [pscustomobject]@{ ScriptName = "verify-preflight-overlap.tests.ps1"; EstimatedDurationSeconds = 60; TimeoutSeconds = 90; Weight = 3; ResourceClass = "ProcessHeavy"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-sdk-diagnostics.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-test-inventory.tests.ps1"; EstimatedDurationSeconds = 30; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
    [pscustomobject]@{ ScriptName = "verify-watchdog.tests.ps1"; EstimatedDurationSeconds = 40; TimeoutSeconds = 120; Weight = 1; ResourceClass = "ProcessLight"; Isolation = "Shared" }
)
$script:QualificationTestScheduleProfiles = @(
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Persistence.Tests"; EstimatedDurationSeconds = 220; TimeoutSeconds = 270; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Startup.Tests"; EstimatedDurationSeconds = 180; TimeoutSeconds = 240; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Web.Tests"; EstimatedDurationSeconds = 75; TimeoutSeconds = 150; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.IntegrationTests"; EstimatedDurationSeconds = 55; TimeoutSeconds = 120; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Application.Tests"; EstimatedDurationSeconds = 45; TimeoutSeconds = 120; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Clients.Tests"; EstimatedDurationSeconds = 20; TimeoutSeconds = 90; Weight = 1; ResourceClass = "ProcessLight" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Cli.Command.Tests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Common.Tests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight" }
    [pscustomobject]@{ ProjectName = "EmbodySense.E2ETests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60; Weight = 1; ResourceClass = "ProcessLight" }
)

function Get-QualificationTestScheduleProfile {
    param([Parameter(Mandatory = $true)] [string]$ProjectName)

    $profiles = @($script:QualificationTestScheduleProfiles | Where-Object { $_.ProjectName -ceq $ProjectName })
    if ($profiles.Count -ne 1) {
        throw "Qualification test project '$ProjectName' must have exactly one checked-in scheduling profile. Found $($profiles.Count)."
    }

    return $profiles[0]
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
