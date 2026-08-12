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
    param([System.IO.FileInfo]$TestProject)

    if ($TestProject.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "capabilities" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Capabilities"))
            (New-VerificationTestLane -Name "contextual-roles" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ContextualRoles"))
            (New-VerificationTestLane -Name "authority" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Authority"))
            (New-VerificationTestLane -Name "tool-results-audit" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ToolResults", "EmbodySense.Core.Persistence.Tests.Audit"))
            (New-VerificationTestLane -Name "credentials" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Credentials"))
            (New-VerificationTestLane -Name "human-input" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest", "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse"))
            (New-VerificationTestLane -Name "default-conversation" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn"))
            (New-VerificationTestLane -Name "custom-definition-control" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinition", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControl", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocation"))
            (New-VerificationTestLane -Name "custom-run-trace" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRun", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTrace", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspace", "EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverage"))
            (New-VerificationTestLane -Name "graph-lifecycle" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring", "EmbodySense.Core.Persistence.Tests.Loops.Admission", "EmbodySense.Core.Persistence.Tests.Loops.Revisions"))
            (New-VerificationTestLane -Name "remainder-triggers" -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring", "EmbodySense.Core.Persistence.Tests.Capabilities", "EmbodySense.Core.Persistence.Tests.Audit", "EmbodySense.Core.Persistence.Tests.Authority", "EmbodySense.Core.Persistence.Tests.ContextualRoles", "EmbodySense.Core.Persistence.Tests.ToolResults", "EmbodySense.Core.Persistence.Tests.Credentials", "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest", "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse", "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinition", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControl", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocation", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRun", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTrace", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspace", "EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverage", "EmbodySense.Core.Persistence.Tests.Loops.Admission", "EmbodySense.Core.Persistence.Tests.Loops.Revisions"))
        )
    }

    if ($TestProject.Name -eq "EmbodySense.Core.Startup.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "loop-execution-custom-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests"))
            (New-VerificationTestLane -Name "loop-execution-governed-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests"))
            (New-VerificationTestLane -Name "loop-execution-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution") -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests", "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests"))
            (New-VerificationTestLane -Name "loops-other" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops") -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution"))
            (New-VerificationTestLane -Name "runtime-triggers" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Runtime", "EmbodySense.Core.Startup.Tests.Triggers"))
            (New-VerificationTestLane -Name "remainder-capabilities" -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops", "EmbodySense.Core.Startup.Tests.Runtime", "EmbodySense.Core.Startup.Tests.Triggers"))
        )
    }

    if ($TestProject.Name -eq "EmbodySense.IntegrationTests.csproj") {
        return @(
            (New-VerificationTestLane -Name "governance" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.Core.Governance"))
            (New-VerificationTestLane -Name "cli" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.Cli"))
            (New-VerificationTestLane -Name "codex-app-server" -IncludeFullyQualifiedName @("EmbodySense.IntegrationTests.CodexAppServer"))
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.IntegrationTests.Core.Governance", "EmbodySense.IntegrationTests.Cli", "EmbodySense.IntegrationTests.CodexAppServer"))
        )
    }

    if ($TestProject.Name -eq "EmbodySense.Web.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "runtime-host" -IncludeFullyQualifiedName @("EmbodySense.Web.Tests.WebAgentRuntimeHostTests"))
            (New-VerificationTestLane -Name "loop-api-run" -IncludeFullyQualifiedName @("EmbodySense.Web.Tests.LoopApiControllerTests", "EmbodySense.Web.Tests.LoopRunApiControllerTests"))
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Web.Tests.WebAgentRuntimeHostTests", "EmbodySense.Web.Tests.LoopApiControllerTests", "EmbodySense.Web.Tests.LoopRunApiControllerTests"))
        )
    }

    return @((New-VerificationTestLane -Name "all"))
}

function Get-VerificationTestLaneFilter {
    param(
        [object]$Lane,
        [string[]]$AdditionalExclusions = @()
    )

    $parts = [Collections.Generic.List[string]]::new()
    if (@($Lane.IncludeFullyQualifiedName).Count -gt 0) {
        $include = @($Lane.IncludeFullyQualifiedName | ForEach-Object { "(FullyQualifiedName~$_)" }) -join '|'
        $parts.Add("($include)")
    }

    $exclusions = [Collections.Generic.List[string]]::new()
    foreach ($exclusion in @($Lane.ExcludeFullyQualifiedName)) {
        $exclusions.Add([string]$exclusion)
    }
    foreach ($exclusion in @($AdditionalExclusions)) {
        $exclusions.Add([string]$exclusion)
    }
    foreach ($exclusion in $exclusions) {
        $parts.Add("(FullyQualifiedName!~$exclusion)")
    }

    $parts.Add("(VerificationTier!=Stress)")
    return $parts -join '&'
}
