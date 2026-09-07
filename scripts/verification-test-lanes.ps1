Set-StrictMode -Version Latest

function New-VerificationTestLane {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [string[]]$IncludeFullyQualifiedName = @(),

        [string[]]$ExcludeFullyQualifiedName = @()
    )

    foreach ($value in @($IncludeFullyQualifiedName) + @($ExcludeFullyQualifiedName)) {
        if ([string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny(@('(', ')', '&', '|', '~', '=', '!')) -ge 0) {
            throw "Verification lane '$Name' contains an unsafe fully-qualified-name predicate."
        }
    }

    return [pscustomobject]@{
        Name = $Name
        IncludeFullyQualifiedName = @($IncludeFullyQualifiedName)
        ExcludeFullyQualifiedName = @($ExcludeFullyQualifiedName)
    }
}

function Get-VerificationTestProjectLanes {
    param(
        [System.IO.FileInfo]$TestProject,
        [switch]$NestedProcessOnly,
        [switch]$SolutionCoreOnly
    )

    if ($TestProject.Name -eq "EmbodySense.Core.Startup.Tests.csproj") {
        $nestedProcessFullyQualifiedNames = @(
            "EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryNestedProcessTests.CreateAsync_exposes_authoring_that_observes_the_runtime_materialized_nonterminal_run_until_runtime_disposal",
            # This held-provider public runtime scenario requires the same full-capacity isolation as the other restart and held-attempt fixtures.
            "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsSchedules.Durable_schedule_overlap_retry_runs_through_canonical_local_background_runtime",
            "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsModels.Model_attempt_crash_windows_are_durable_and_never_redispatch_across_external_restart",
            "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsWait.Production_runtime_parks_and_wakes_a_canonical_wait_after_restart",
            "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsWait.Explicit_background_request_activates_once_after_late_workspace_host_reacquisition"
        )

        if ($NestedProcessOnly) {
            return @((New-VerificationTestLane -Name "nested-process" -IncludeFullyQualifiedName $nestedProcessFullyQualifiedNames))
        }

        if ($SolutionCoreOnly) {
            return @((New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName $nestedProcessFullyQualifiedNames))
        }

        return @(
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName $nestedProcessFullyQualifiedNames)
            (New-VerificationTestLane -Name "nested-process" -IncludeFullyQualifiedName $nestedProcessFullyQualifiedNames)
        )
    }

    # One process per assembly avoids repeated VSTest startup, deployment, coverage instrumentation,
    # and Cobertura serialization. Assembly-level xUnit bounds and explicit collections provide the
    # safe inner parallelism; the stable-ID partition contract still proves every case exactly once.
    return @((New-VerificationTestLane -Name "all"))
}

function Get-VerificationTestLaneFilter {
    param(
        [object]$Lane,
        [string[]]$AdditionalExclusions = @()
    )

    $parts = [Collections.Generic.List[string]]::new()
    if (@($Lane.IncludeFullyQualifiedName).Count -gt 0) {
        $include = @($Lane.IncludeFullyQualifiedName | ForEach-Object { "(FullyQualifiedName=$_)" }) -join '|'
        $parts.Add("($include)")
    }

    $exclusions = [Collections.Generic.List[string]]::new()
    foreach ($exclusion in @($Lane.ExcludeFullyQualifiedName)) {
        $parts.Add("(FullyQualifiedName!=$exclusion)")
    }
    foreach ($exclusion in @($AdditionalExclusions)) {
        $parts.Add("(FullyQualifiedName!~$exclusion)")
    }

    $parts.Add("(VerificationTier!=Stress)")
    return $parts -join '&'
}
