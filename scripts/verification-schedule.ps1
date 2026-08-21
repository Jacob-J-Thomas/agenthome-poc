Set-StrictMode -Version Latest

$script:VerificationRequiredGateResourceCapacity = 12
$script:VerificationRequiredGateMaximumProcessHeavyWorkers = 2
$script:VerificationRequiredGateMaximumCpuBoundWorkers = 1
$script:VerificationRequiredGateScheduleProfiles = @(
    # One VSTest process per assembly lets the test runner schedule isolated classes itself and
    # removes repeated deployment, discovery, instrumentation, and report-write overhead.
    # https://github.com/Jacob-J-Thomas/agenthome-poc/issues/422: reserve the complete four-core runner for the two dominant assemblies so a third process cannot starve both coverage lanes.
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Persistence.Tests-all"; EstimatedDurationSeconds = 300; Weight = 6; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Startup.Tests-all"; EstimatedDurationSeconds = 240; Weight = 6; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Web.Tests-all"; EstimatedDurationSeconds = 210; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "tests-EmbodySense.IntegrationTests-all"; EstimatedDurationSeconds = 180; Weight = 3; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "format-naming-style"; EstimatedDurationSeconds = 65; Weight = 2; ResourceClass = "CpuBound" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Application.Tests-all"; EstimatedDurationSeconds = 50; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "format-whitespace"; EstimatedDurationSeconds = 35; Weight = 2; ResourceClass = "CpuBound" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Common.Tests-all"; EstimatedDurationSeconds = 25; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Core.Clients.Tests-all"; EstimatedDurationSeconds = 20; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.E2ETests-all"; EstimatedDurationSeconds = 20; Weight = 1; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "tests-EmbodySense.Cli.Command.Tests-all"; EstimatedDurationSeconds = 15; Weight = 1; ResourceClass = "Ordinary" }
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

function Get-VerificationPreflightNestedProcessContractScripts {
    param(
        [switch]$RunningOnWindows
    )

    $scripts = @(
        "verify-preflight-overlap.tests.ps1",
        "verify-parallel.tests.ps1"
    )
    if ($RunningOnWindows) {
        return @("verify-sdk-diagnostics.tests.ps1") + $scripts
    }

    return $scripts
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

    # https://github.com/Jacob-J-Thomas/agenthome-poc/issues/422: keep the outer process load below the four-core hosted runner while retaining twelve logical units.
    # Exact run 32451304219 proved that three internally parallel assemblies still starve Persistence; reserve the third process for CPU-bound or ordinary backfill.
    $actualProcessCeiling = [Math]::Min(3, [Math]::Min($script:VerificationRequiredGateResourceCapacity, $HardwareProcessorCount))
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
        [object[]]$Phases,

        [string[]]$ExcludedNames = @()
    )

    $declaredNames = @($Phases | ForEach-Object { [string]$_.Name })
    $knownProfileNames = @($script:VerificationRequiredGateScheduleProfiles | ForEach-Object { [string]$_.Name })
    $duplicateExclusions = @($ExcludedNames | Group-Object -CaseSensitive | Where-Object Count -gt 1 | ForEach-Object Name | Sort-Object)
    $unknownExclusions = @($ExcludedNames | Where-Object { $candidate = $_; $knownProfileNames -notcontains $candidate } | Sort-Object -Unique)
    if ($duplicateExclusions.Count -gt 0 -or $unknownExclusions.Count -gt 0) {
        throw "Required verification gate exclusions must be unique and match checked-in profiles exactly. duplicate_exclusions=[$($duplicateExclusions -join ',')] unknown_exclusions=[$($unknownExclusions -join ',')]"
    }

    $profileNames = @($script:VerificationRequiredGateScheduleProfiles | Where-Object { $ExcludedNames -notcontains $_.Name } | ForEach-Object { [string]$_.Name })
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
