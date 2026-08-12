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

    if ($TestProject.Name -eq "EmbodySense.Core.Application.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "loops-execution" -IncludeFullyQualifiedName @("EmbodySense.Core.Application.Tests.Loops.Execution"))
            (New-VerificationTestLane -Name "loops-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Application.Tests.Loops") -ExcludeFullyQualifiedName @("EmbodySense.Core.Application.Tests.Loops.Execution"))
            (New-VerificationTestLane -Name "capabilities-human-input" -IncludeFullyQualifiedName @("EmbodySense.Core.Application.Tests.Capabilities", "EmbodySense.Core.Application.Tests.HumanInput"))
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Core.Application.Tests.Loops", "EmbodySense.Core.Application.Tests.Capabilities", "EmbodySense.Core.Application.Tests.HumanInput"))
        )
    }

    if ($TestProject.Name -eq "EmbodySense.Core.Common.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "loops-execution" -IncludeFullyQualifiedName @("EmbodySense.Core.Common.Tests.Loops.Execution"))
            (New-VerificationTestLane -Name "loops-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Common.Tests.Loops") -ExcludeFullyQualifiedName @("EmbodySense.Core.Common.Tests.Loops.Execution"))
            (New-VerificationTestLane -Name "human-input" -IncludeFullyQualifiedName @("EmbodySense.Core.Common.Tests.HumanInput"))
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Core.Common.Tests.Loops", "EmbodySense.Core.Common.Tests.HumanInput"))
        )
    }

    if ($TestProject.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "graph-authoring" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring"))
            (New-VerificationTestLane -Name "capabilities" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Capabilities"))
            (New-VerificationTestLane -Name "contextual-roles" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ContextualRoles"))
            (New-VerificationTestLane -Name "authority-grants-process" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Authority.AuthorityGrantStoreTests"))
            (New-VerificationTestLane -Name "authority-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Authority") -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Authority.AuthorityGrantStoreTests"))
            (New-VerificationTestLane -Name "tool-results" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.ToolResults"))
            (New-VerificationTestLane -Name "audit-process" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Audit"))
            (New-VerificationTestLane -Name "credentials-external-process" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Credentials.WindowsCredentialValueProviderTests"))
            (New-VerificationTestLane -Name "credentials-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Credentials") -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Credentials.WindowsCredentialValueProviderTests"))
            (New-VerificationTestLane -Name "human-input-requests" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest"))
            (New-VerificationTestLane -Name "human-input-responses" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse"))
            (New-VerificationTestLane -Name "default-conversation-recovery" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests"))
            (New-VerificationTestLane -Name "default-conversation-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn") -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests"))
            (New-VerificationTestLane -Name "custom-control-process" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControlOperationStoreTests"))
            (New-VerificationTestLane -Name "custom-definition-control-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinition", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControl", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocation") -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControlOperationStoreTests"))
            (New-VerificationTestLane -Name "custom-run-trace" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRun", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTrace", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspace", "EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverage"))
            (New-VerificationTestLane -Name "governed-lifecycle" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.Admission", "EmbodySense.Core.Persistence.Tests.Loops.Revisions"))
            (New-VerificationTestLane -Name "triggers" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Triggers"))
            (New-VerificationTestLane -Name "effect-authority-process" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.Execution.Authority.GovernedLoopEffectAuthorityEvidenceStoreTests"))
            (New-VerificationTestLane -Name "sequential-evidence-process" -IncludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.CustomLoopSequentialEvidenceStoreTests"))
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring", "EmbodySense.Core.Persistence.Tests.Capabilities", "EmbodySense.Core.Persistence.Tests.Audit", "EmbodySense.Core.Persistence.Tests.Authority", "EmbodySense.Core.Persistence.Tests.ContextualRoles", "EmbodySense.Core.Persistence.Tests.ToolResults", "EmbodySense.Core.Persistence.Tests.Credentials", "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequest", "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponse", "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurn", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinition", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControl", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocation", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRun", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTrace", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspace", "EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverage", "EmbodySense.Core.Persistence.Tests.Loops.Admission", "EmbodySense.Core.Persistence.Tests.Loops.Revisions", "EmbodySense.Core.Persistence.Tests.Triggers", "EmbodySense.Core.Persistence.Tests.Loops.Execution.Authority.GovernedLoopEffectAuthorityEvidenceStoreTests", "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopSequentialEvidenceStoreTests"))
        )
    }

    if ($TestProject.Name -eq "EmbodySense.Core.Startup.Tests.csproj") {
        return @(
            (New-VerificationTestLane -Name "capabilities" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Capabilities"))
            (New-VerificationTestLane -Name "loop-execution-custom-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests"))
            (New-VerificationTestLane -Name "loop-execution-governed-runtime" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests"))
            (New-VerificationTestLane -Name "loop-execution-remainder" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution") -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTests", "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTests"))
            (New-VerificationTestLane -Name "loops-other" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops") -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Loops.Execution"))
            (New-VerificationTestLane -Name "runtime-triggers" -IncludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Runtime", "EmbodySense.Core.Startup.Tests.Triggers"))
            (New-VerificationTestLane -Name "remainder" -ExcludeFullyQualifiedName @("EmbodySense.Core.Startup.Tests.Capabilities", "EmbodySense.Core.Startup.Tests.Loops", "EmbodySense.Core.Startup.Tests.Runtime", "EmbodySense.Core.Startup.Tests.Triggers"))
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
