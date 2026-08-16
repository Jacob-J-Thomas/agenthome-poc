Set-StrictMode -Version Latest

$script:QualificationTestScheduleProfiles = @(
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Persistence.Tests"; EstimatedDurationSeconds = 220; TimeoutSeconds = 270 }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Startup.Tests"; EstimatedDurationSeconds = 180; TimeoutSeconds = 240 }
    [pscustomobject]@{ ProjectName = "EmbodySense.Web.Tests"; EstimatedDurationSeconds = 75; TimeoutSeconds = 150 }
    [pscustomobject]@{ ProjectName = "EmbodySense.IntegrationTests"; EstimatedDurationSeconds = 55; TimeoutSeconds = 120 }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Application.Tests"; EstimatedDurationSeconds = 45; TimeoutSeconds = 120 }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Clients.Tests"; EstimatedDurationSeconds = 20; TimeoutSeconds = 90 }
    [pscustomobject]@{ ProjectName = "EmbodySense.Cli.Command.Tests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60 }
    [pscustomobject]@{ ProjectName = "EmbodySense.Core.Common.Tests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60 }
    [pscustomobject]@{ ProjectName = "EmbodySense.E2ETests"; EstimatedDurationSeconds = 10; TimeoutSeconds = 60 }
)

function Get-QualificationTestScheduleProfile {
    param([Parameter(Mandatory = $true)] [string]$ProjectName)

    $profiles = @($script:QualificationTestScheduleProfiles | Where-Object { $_.ProjectName -ceq $ProjectName })
    if ($profiles.Count -ne 1) {
        throw "Qualification test project '$ProjectName' must have exactly one checked-in scheduling profile. Found $($profiles.Count)."
    }

    return $profiles[0]
}
