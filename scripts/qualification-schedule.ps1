Set-StrictMode -Version Latest

$script:QualificationContractWeight = 1
$script:QualificationContractResourceClass = "ProcessLight"
$script:QualificationTestScheduleProfiles = @(
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Persistence.Tests"; EstimatedDurationSeconds = 220; TimeoutSeconds = 270; Weight = 2; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Startup.Tests"; EstimatedDurationSeconds = 180; TimeoutSeconds = 240; Weight = 2; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Web.Tests"; EstimatedDurationSeconds = 75; TimeoutSeconds = 150; Weight = 2; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.IntegrationTests"; EstimatedDurationSeconds = 55; TimeoutSeconds = 120; Weight = 2; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Application.Tests"; EstimatedDurationSeconds = 45; TimeoutSeconds = 120; Weight = 2; ResourceClass = "ProcessHeavy" }
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
