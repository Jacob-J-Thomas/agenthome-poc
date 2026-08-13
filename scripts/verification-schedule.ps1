Set-StrictMode -Version Latest

$script:VerificationRequiredGateResourceCapacity = 8
$script:VerificationRequiredGateMaximumProcessHeavyWorkers = 2
$script:VerificationRequiredGateMaximumCpuBoundWorkers = 1
$script:VerificationRequiredGateScheduleProfiles = @(
    # The prior Startup remainder profiles totaled 312 seconds; two xUnit workers imply
    # a 156-second floor, with 24 seconds retained for collection imbalance and setup.
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Startup.Tests-remainder"; EstimatedDurationSeconds = 180; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Startup.Tests-loop-execution-custom-runtime"; EstimatedDurationSeconds = 140; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-custom-run-trace"; EstimatedDurationSeconds = 140; Weight = 3; ResourceClass = "ProcessHeavy" }
    # Hosted Loop API plus Web remainder observations totaled about 220 seconds;
    # 130 seconds allows for their serialized API collection under two xUnit workers.
    [pscustomobject]@{ Name = "tests-EmbodySense.Web.Tests-remainder"; EstimatedDurationSeconds = 130; Weight = 3; ResourceClass = "ProcessHeavy" }
    # The prior non-Codex Integration profiles totaled 190 seconds; two xUnit workers
    # imply a 95-second floor, with 25 seconds retained for imbalance and setup.
    [pscustomobject]@{ Name = "tests-EmbodySense.IntegrationTests-remainder"; EstimatedDurationSeconds = 120; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Web.Tests-runtime-host"; EstimatedDurationSeconds = 120; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Startup.Tests-loop-execution-governed-runtime"; EstimatedDurationSeconds = 115; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.IntegrationTests-codex-app-server"; EstimatedDurationSeconds = 92; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-remainder-triggers"; EstimatedDurationSeconds = 78; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-contextual-roles"; EstimatedDurationSeconds = 75; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-capabilities"; EstimatedDurationSeconds = 68; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-custom-definition-control"; EstimatedDurationSeconds = 65; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-default-conversation"; EstimatedDurationSeconds = 65; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-credentials"; EstimatedDurationSeconds = 62; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-authority"; EstimatedDurationSeconds = 60; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-human-input"; EstimatedDurationSeconds = 60; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-graph-lifecycle"; EstimatedDurationSeconds = 55; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-tool-results-audit"; EstimatedDurationSeconds = 50; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Application.Tests-all"; EstimatedDurationSeconds = 45; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Common.Tests-all"; EstimatedDurationSeconds = 20; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Clients.Tests-all"; EstimatedDurationSeconds = 15; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.E2ETests-all"; EstimatedDurationSeconds = 15; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Cli.Command.Tests-all"; EstimatedDurationSeconds = 10; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "git-diff-check"; EstimatedDurationSeconds = 5; Weight = 1; ResourceClass = "Ordinary" }
)

function Get-VerificationRequiredGateResourceCapacity {
    return $script:VerificationRequiredGateResourceCapacity
}

function Get-VerificationRequiredGateMaximumProcessHeavyWorkers {
    return $script:VerificationRequiredGateMaximumProcessHeavyWorkers
}

function Get-VerificationRequiredGateMaximumCpuBoundWorkers {
    return $script:VerificationRequiredGateMaximumCpuBoundWorkers
}

function Get-VerificationPreflightCoverageContractWeight {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 8)]
        [int]$ResourceCapacity
    )

    return [Math]::Min(3, $ResourceCapacity)
}

function Get-VerificationPreflightFrontendWeight {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 8)]
        [int]$ResourceCapacity
    )

    return [Math]::Min(2, $ResourceCapacity)
}

function Get-VerificationPreflightNestedProcessContractWeight {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 8)]
        [int]$ResourceCapacity
    )

    return $ResourceCapacity
}

function Assert-VerificationPreflightContractClassification {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ContractScripts,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$CoverageContractScript,

        [Parameter(Mandatory = $true)]
        [string[]]$NestedProcessContractScripts,

        [Parameter(Mandatory = $true)]
        [string[]]$OrdinaryContractScripts
    )

    $classifiedScripts = @($CoverageContractScript) + @($NestedProcessContractScripts) + @($OrdinaryContractScripts)
    $duplicateContracts = @($ContractScripts | Group-Object -CaseSensitive | Where-Object Count -gt 1 | ForEach-Object Name | Sort-Object)
    $duplicateClassifications = @($classifiedScripts | Group-Object -CaseSensitive | Where-Object Count -gt 1 | ForEach-Object Name | Sort-Object)
    $missingClassifications = @($ContractScripts | Where-Object { $candidate = $_; @($classifiedScripts | Where-Object { $_ -ceq $candidate }).Count -eq 0 } | Sort-Object)
    $unexpectedClassifications = @($classifiedScripts | Where-Object { $candidate = $_; @($ContractScripts | Where-Object { $_ -ceq $candidate }).Count -eq 0 } | Sort-Object)
    if ($duplicateContracts.Count -gt 0 -or $duplicateClassifications.Count -gt 0 -or $missingClassifications.Count -gt 0 -or $unexpectedClassifications.Count -gt 0) {
        throw "Preflight script contracts must have exactly one resource classification. duplicate_contracts=[$($duplicateContracts -join ',')] duplicate_classifications=[$($duplicateClassifications -join ',')] missing_classifications=[$($missingClassifications -join ',')] unexpected_classifications=[$($unexpectedClassifications -join ',')]"
    }
}

function Get-VerificationRequiredGateMaximumWorkers {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 8)]
        [int]$MaximumTestWorkers,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$HardwareProcessorCount
    )

    $actualProcessCeiling = [Math]::Min(4, [Math]::Min($script:VerificationRequiredGateResourceCapacity, $HardwareProcessorCount))
    return [Math]::Min($MaximumTestWorkers, $actualProcessCeiling)
}

function Get-VerificationRequiredGateScheduleProfiles {
    return @($script:VerificationRequiredGateScheduleProfiles | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            EstimatedDurationSeconds = $_.EstimatedDurationSeconds
            Weight = $_.Weight
            ResourceClass = $_.ResourceClass
        }
    })
}

function Get-VerificationRequiredGateScheduleProfile {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name
    )

    $matches = @($script:VerificationRequiredGateScheduleProfiles | Where-Object { $_.Name -ceq $Name })
    if ($matches.Count -ne 1) {
        throw "Required verification gate '$Name' must have exactly one checked-in scheduling profile; found $($matches.Count)."
    }

    $profile = $matches[0]
    if ($profile.EstimatedDurationSeconds -lt 1) {
        throw "Required verification gate '$Name' has an invalid duration estimate."
    }
    if ($profile.Weight -lt 1 -or $profile.Weight -gt $script:VerificationRequiredGateResourceCapacity) {
        throw "Required verification gate '$Name' has weight $($profile.Weight), outside logical capacity $script:VerificationRequiredGateResourceCapacity."
    }

    $minimumWeight = switch ($profile.ResourceClass) {
        "Ordinary" { 1; break }
        "CpuBound" { 2; break }
        "ProcessHeavy" { 3; break }
        default { throw "Required verification gate '$Name' has unknown resource class '$($profile.ResourceClass)'." }
    }
    if ($profile.Weight -lt $minimumWeight) {
        throw "Required verification gate '$Name' under-declares $($profile.ResourceClass) weight $($profile.Weight); minimum is $minimumWeight."
    }

    return [pscustomobject]@{
        Name = $profile.Name
        EstimatedDurationSeconds = $profile.EstimatedDurationSeconds
        Weight = $profile.Weight
        ResourceClass = $profile.ResourceClass
    }
}

function Assert-VerificationRequiredGateSchedule {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Phases
    )

    $declaredNames = @($Phases | ForEach-Object { [string]$_.Name })
    $profileNames = @($script:VerificationRequiredGateScheduleProfiles | ForEach-Object { [string]$_.Name })
    $duplicateProfiles = @($profileNames | Group-Object -CaseSensitive | Where-Object Count -gt 1 | ForEach-Object Name | Sort-Object)
    $duplicatePhases = @($declaredNames | Group-Object -CaseSensitive | Where-Object Count -gt 1 | ForEach-Object Name | Sort-Object)
    $missingProfiles = @($declaredNames | Where-Object { $candidate = $_; @($profileNames | Where-Object { $_ -ceq $candidate }).Count -eq 0 } | Sort-Object)
    $unexpectedProfiles = @($profileNames | Where-Object { $candidate = $_; @($declaredNames | Where-Object { $_ -ceq $candidate }).Count -eq 0 } | Sort-Object)
    if ($duplicateProfiles.Count -gt 0 -or $duplicatePhases.Count -gt 0 -or $missingProfiles.Count -gt 0 -or $unexpectedProfiles.Count -gt 0) {
        throw "Required verification scheduling profiles must equal the declared gate set exactly once. duplicate_profiles=[$($duplicateProfiles -join ',')] duplicate_gates=[$($duplicatePhases -join ',')] missing_profiles=[$($missingProfiles -join ',')] unexpected_profiles=[$($unexpectedProfiles -join ',')]"
    }

    foreach ($phase in $Phases) {
        $profile = Get-VerificationRequiredGateScheduleProfile -Name $phase.Name
        if ($phase.EstimatedDurationSeconds -ne $profile.EstimatedDurationSeconds -or $phase.Weight -ne $profile.Weight -or $phase.ResourceClass -cne $profile.ResourceClass) {
            throw "Required verification gate '$($phase.Name)' does not match its checked-in duration and resource profile."
        }
    }
}
