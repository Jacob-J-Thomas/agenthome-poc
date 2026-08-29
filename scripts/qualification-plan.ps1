Set-StrictMode -Version Latest

$script:QualificationTestProjects = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
$script:QualificationE2ETestProjectName = "EmbodySense.E2ETests"
$script:QualificationInstalledBrowserTestPath = "tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs"
$script:QualificationInstalledBrowserTestClass = "EmbodySense.E2ETests.Web.BrowserFlowTests"

$script:QualificationSourceMappings = @(
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Cli.Command/"
        TestProjects = @(
            "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
        )
    },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Cli/"; TestProjects = @("tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj") },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Core.Application/"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
        )
    },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Core.Clients/"
        TestProjects = @(
            "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
        )
    },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Core.Common/"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
        )
    },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Core.Persistence/"
        TestProjects = @(
            "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
            "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Core.Startup/"
        TestProjects = @(
            "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
            "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Web/"
        TestProjects = @(
            "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
            "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
        )
    }
)

$script:QualificationBehavioralConsumerMappings = @(
    [pscustomobject]@{
        SourceProject = "src/EmbodySense.Core.Application/EmbodySense.Core.Application.csproj"
        TestProject = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"
        EvidencePath = "tests/EmbodySense.Core.Clients.Tests/Capabilities/ConfiguredCapabilityArtifactTrustVerifierTests.cs"
        RequiredNamespace = "EmbodySense.Core.Application.Capabilities.Models"
    },
    [pscustomobject]@{
        SourceProject = "src/EmbodySense.Core.Clients/EmbodySense.Core.Clients.csproj"
        TestProject = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        EvidencePath = "tests/EmbodySense.Core.Startup.Tests/Inference/LlmInferenceClientTests.cs"
        RequiredNamespace = "EmbodySense.Core.Clients.CodexAppServer"
    },
    [pscustomobject]@{
        SourceProject = "src/EmbodySense.Core.Persistence/EmbodySense.Core.Persistence.csproj"
        TestProject = "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj"
        EvidencePath = "tests/EmbodySense.Cli.Command.Tests/CliCommandTests.cs"
        RequiredNamespace = "EmbodySense.Core.Persistence.Capabilities"
    },
    [pscustomobject]@{
        SourceProject = "src/EmbodySense.Core.Persistence/EmbodySense.Core.Persistence.csproj"
        TestProject = "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj"
        EvidencePath = "tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs"
        RequiredNamespace = "EmbodySense.Core.Persistence.Memory"
    },
    [pscustomobject]@{
        SourceProject = "src/EmbodySense.Core.Persistence/EmbodySense.Core.Persistence.csproj"
        TestProject = "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
        EvidencePath = "tests/EmbodySense.Web.Tests/WebAgentRuntimeHostTests.cs"
        RequiredNamespace = "EmbodySense.Core.Persistence.Loops"
    },
    [pscustomobject]@{
        SourceProject = "src/EmbodySense.Core.Startup/EmbodySense.Core.Startup.csproj"
        TestProject = "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj"
        EvidencePath = "tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs"
        RequiredNamespace = "EmbodySense.Core.Startup.Loops.Execution.Models"
    }
)

$script:QualificationExactSourceMappings = @(
    [pscustomobject]@{
        Path = "src/EmbodySense.Core.Common/Governance/Tools/EmbodySenseDeveloperInstructions.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
        )
    }
)

$script:QualificationFocusedImplementationMappings = @(
    [pscustomobject]@{
        Kind = "PrivateMethod"
        Path = "src/EmbodySense.Core.Application/Loops/Execution/Custom/CustomLoopLifecycleService.cs"
        TypeName = "CustomLoopLifecycleService"
        MemberName = "TryCancelActiveAttemptAsync"
        ReferencePaths = @()
        Tests = @(
            [pscustomobject]@{
                Path = "tests/EmbodySense.Core.Application.Tests/Loops/Execution/Custom/CustomLoopLifecycleServiceTests.cs"
                Class = "EmbodySense.Core.Application.Tests.Loops.Execution.Custom.CustomLoopLifecycleServiceTests"
            }
        )
    },
    [pscustomobject]@{
        Kind = "PublicConstantContract"
        Path = "src/EmbodySense.Core.Common/Loops/Execution/CustomLoopAttemptCancellationContractLimits.cs"
        TypeName = "CustomLoopAttemptCancellationContractLimits"
        MemberName = "MaxRemoteRequestSeconds"
        ReferencePaths = @(
            "src/EmbodySense.Core.Application/Loops/Execution/Custom/CustomLoopLifecycleService.cs",
            "src/EmbodySense.Core.Common/Loops/Execution/CustomLoopAttemptCancellationContractLimits.cs",
            "src/EmbodySense.Core.Persistence/Loops/CustomLoopAttemptCancellationHost.cs",
            "tests/EmbodySense.Core.Application.Tests/Loops/Execution/Custom/CustomLoopLifecycleServiceTests.cs"
        )
        Tests = @(
            [pscustomobject]@{
                Path = "tests/EmbodySense.Core.Application.Tests/Loops/Execution/Custom/CustomLoopLifecycleServiceTests.cs"
                Class = "EmbodySense.Core.Application.Tests.Loops.Execution.Custom.CustomLoopLifecycleServiceTests"
            },
            [pscustomobject]@{
                Path = "tests/EmbodySense.Core.Persistence.Tests/Loops/CustomLoopWorkspaceExecutionGateTests.cs"
                Class = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspaceExecutionGateTests"
            }
        )
    },
    [pscustomobject]@{
        Kind = "InternalSealed"
        Path = "src/EmbodySense.Core.Persistence/Loops/CustomLoopAttemptCancellationHost.cs"
        TypeName = "CustomLoopAttemptCancellationHost"
        MemberName = $null
        ReferencePaths = @()
        Tests = @(
            [pscustomobject]@{
                Path = "tests/EmbodySense.Core.Persistence.Tests/Loops/CustomLoopWorkspaceExecutionGateTests.cs"
                Class = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspaceExecutionGateTests"
            }
        )
    }
)

$script:QualificationTestMappings = @(
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Cli.Command.Tests/"; TestProject = "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Application.Tests/"; TestProject = "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Clients.Tests/"; TestProject = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Common.Tests/"; TestProject = "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Persistence.Tests/"; TestProject = "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Startup.Tests/"; TestProject = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.E2ETests/"; TestProject = "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.IntegrationTests/"; TestProject = "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Web.Tests/"; TestProject = "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj" }
)

$script:QualificationLinkedTestMappings = @(
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/GovernedLoopGraphTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Authority/Grants/AuthorityGrantTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Loops/Admission/GovernedLoopAdmissionTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Loops/Execution/Authority/GovernedLoopEffectAuthorityTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/TriggerDeliveryTestData.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Triggers/Schedules/ScheduleContractTestData.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Loops/Execution/Sleep/GovernedLoopSleepContractTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Sleep/GovernedLoopSleepApplicationTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Governance/Authority/Grants/AuthorityGrantApplicationTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/CommandActions/CommandActionApplicationTestData.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/GovernedModelProfileApplicationTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Execution/Effects/GovernedLoopEffectAttemptTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialApplicationTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Sleep/StubGovernedLoopAuthenticatedWakeVerificationPort.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Sleep/StubGovernedLoopSleepCurrentPosturePort.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Sleep/StubGovernedLoopSleepTimeProvider.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Loops/Sleep/StubGovernedLoopWakeContinuationPort.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    }
)
$script:QualificationFocusedHelperMappings = @(
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Capabilities/CapabilityArtifactTestData.cs"
        ConsumerNamespaces = @(
            "EmbodySense.Core.Application.Tests.Capabilities",
            "EmbodySense.Core.Application.Tests.Credentials"
        )
        ConsumerClasses = @()
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseBudget.cs"
        ConsumerNamespaces = @(
            "EmbodySense.Core.Persistence.Tests.Loops",
            "EmbodySense.Core.Persistence.Tests.Verification"
        )
        ConsumerClasses = @()
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseClassification.cs"
        ConsumerNamespaces = @(
            "EmbodySense.Core.Persistence.Tests.Loops",
            "EmbodySense.Core.Persistence.Tests.Verification",
            "EmbodySense.Core.Persistence.Tests.Verification.Models"
        )
        ConsumerClasses = @()
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.IntegrationTests/Core/Governance/Tools/ImmediateToolResultRetentionStore.cs"
        ConsumerNamespaces = @()
        ConsumerClasses = @("EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerTests")
    }
)
$script:QualificationCSharpParserInitialized = $false

function ConvertTo-QualificationPath {
    param([Parameter(Mandatory = $true)] [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.IndexOfAny([char[]]@([char]0, [char]10, [char]13)) -ge 0) {
        throw "Qualification paths must be non-empty repository-relative text."
    }

    $normalized = $Path.Replace('\', '/')
    if ($normalized.StartsWith("./", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }
    if ($normalized.Length -eq 0 -or $normalized.Split('/') -contains "..") {
        throw "Qualification path escapes the repository: '$Path'."
    }

    return $normalized
}

function Get-QualificationTestProject {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationTestMappings) {
        if ($Path.StartsWith($mapping.Prefix, [StringComparison]::Ordinal)) {
            return $mapping.TestProject
        }
    }

    return $null
}

function Get-QualificationTestProjectPrefix {
    param([Parameter(Mandatory = $true)] [string]$TestProject)

    foreach ($mapping in $script:QualificationTestMappings) {
        if ($mapping.TestProject -ceq $TestProject) {
            return $mapping.Prefix
        }
    }

    throw "Qualification has no source prefix for test project '$TestProject'."
}

function Get-QualificationLinkedTestMapping {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationLinkedTestMappings) {
        if ($Path -ceq $mapping.Path) {
            return $mapping
        }
    }

    return $null
}

function Get-QualificationFocusedHelperMapping {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationFocusedHelperMappings) {
        if ($Path -ceq $mapping.Path) {
            return $mapping
        }
    }

    return $null
}

function Get-QualificationFocusedImplementationMapping {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationFocusedImplementationMappings) {
        if ($Path -ceq $mapping.Path) {
            return $mapping
        }
    }

    return $null
}

function Get-QualificationFocusedImplementationMappingsForPath {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationFocusedImplementationMappings) {
        if ($Path -ceq $mapping.Path -or @($mapping.Tests | Where-Object { $Path -ceq $_.Path }).Count -gt 0 -or @($mapping.ReferencePaths | Where-Object { $Path -ceq $_ }).Count -gt 0) {
            Write-Output $mapping
        }
    }
}

function Test-QualificationFilterableTestSource {
    param([Parameter(Mandatory = $true)] [string]$Path)

    return $Path.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($Path) -cne "AssemblyInfo.cs" -and
        $null -eq (Get-QualificationLinkedTestMapping -Path $Path) -and
        $null -ne (Get-QualificationTestProject -Path $Path)
}

function Initialize-QualificationCSharpParser {
    if ($script:QualificationCSharpParserInitialized) {
        return
    }

    if ($null -eq ("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree" -as [type])) {
        Add-Type -AssemblyName "Microsoft.CodeAnalysis.CSharp"
    }
    if ($null -eq ("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree" -as [type])) {
        throw "Qualification could not load the PowerShell-hosted Roslyn C# parser."
    }

    $script:QualificationCSharpParserInitialized = $true
}

function Get-QualificationDeclaredTestNamespace {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Content
    )

    Initialize-QualificationCSharpParser
    $syntaxTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content)
    $namespaceDeclarations = @($syntaxTree.GetCompilationUnitRoot().Members | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax] })
    if ($namespaceDeclarations.Count -ne 1) {
        throw "Qualification requires exactly one top-level declared namespace in changed test source '$Path'. Found $($namespaceDeclarations.Count)."
    }

    $declaredNamespace = $namespaceDeclarations[0].Name.ToString()

    $testProject = Get-QualificationTestProject -Path $Path
    $projectNamespace = [IO.Path]::GetFileNameWithoutExtension($testProject)
    if ($declaredNamespace -cne $projectNamespace -and -not $declaredNamespace.StartsWith("$projectNamespace.", [StringComparison]::Ordinal)) {
        throw "Qualification test namespace '$declaredNamespace' does not belong to owning project '$projectNamespace' for '$Path'."
    }

    return $declaredNamespace
}

function Get-QualificationDirectXunitTestTypeNames {
    param([Parameter(Mandatory = $true)] [string]$Content)

    Initialize-QualificationCSharpParser
    $root = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content).GetCompilationUnitRoot()
    $testAttributeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($attributeName in @("Fact", "FactAttribute", "Theory", "TheoryAttribute")) {
        [void]$testAttributeNames.Add($attributeName)
    }

    foreach ($typeDeclaration in @($root.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] })) {
        if ($null -eq $typeDeclaration.BaseList) {
            continue
        }

        foreach ($baseType in @($typeDeclaration.BaseList.Types)) {
            $baseName = @($baseType.Type.ToString() -split '::|\.')[-1]
            if ($baseName -cne "FactAttribute" -and $baseName -cne "TheoryAttribute") {
                continue
            }

            $declaredName = $typeDeclaration.Identifier.ValueText
            [void]$testAttributeNames.Add($declaredName)
            if ($declaredName.EndsWith("Attribute", [StringComparison]::Ordinal)) {
                [void]$testAttributeNames.Add($declaredName.Substring(0, $declaredName.Length - "Attribute".Length))
            }
        }
    }

    $directTestTypeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($methodDeclaration in @($root.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] })) {
        foreach ($attributeList in @($methodDeclaration.AttributeLists)) {
            foreach ($attribute in @($attributeList.Attributes)) {
                $simpleName = @($attribute.Name.ToString() -split '::|\.')[-1]
                if ($testAttributeNames.Contains($simpleName)) {
                    $containingType = $methodDeclaration.Parent
                    while ($null -ne $containingType -and $containingType -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax]) {
                        $containingType = $containingType.Parent
                    }
                    if ($containingType -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -or $containingType.Parent -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax]) {
                        throw "Qualification requires direct xUnit methods to belong to one top-level class."
                    }
                    if ($null -ne $containingType.TypeParameterList -and $containingType.TypeParameterList.Parameters.Count -gt 0) {
                        throw "Qualification does not class-filter a generic xUnit test type."
                    }

                    [void]$directTestTypeNames.Add($containingType.Identifier.ValueText)
                }
            }
        }
    }

    return [string[]]@($directTestTypeNames | Sort-Object)
}

function Get-QualificationDirectXunitTestClasses {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Content
    )

    $declaredNamespace = Get-QualificationDeclaredTestNamespace -Path $Path -Content $Content
    $typeNames = @(Get-QualificationDirectXunitTestTypeNames -Content $Content)
    if ($typeNames.Count -eq 0) {
        return [string[]]::new(0)
    }

    $expectedTypeName = [IO.Path]::GetFileNameWithoutExtension($Path)
    $fragmentSeparatorIndex = $expectedTypeName.IndexOf('.')
    if ($fragmentSeparatorIndex -gt 0) {
        $expectedTypeName = $expectedTypeName.Substring(0, $fragmentSeparatorIndex)
        Initialize-QualificationCSharpParser
        $root = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content).GetCompilationUnitRoot()
        $matchingPartialTypes = @(
            $root.DescendantNodes() |
                Where-Object {
                    $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and
                    $_.Parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax] -and
                    $_.Identifier.ValueText -ceq $expectedTypeName -and
                    @($_.Modifiers | ForEach-Object { $_.ValueText }) -ccontains "partial"
                }
        )
        if ($matchingPartialTypes.Count -ne 1) {
            throw "Qualification requires dotted xUnit test fragments to contain exactly one filename-prefix-matching partial class in '$Path'."
        }
    }
    if ($typeNames.Count -ne 1 -or $typeNames[0] -cne $expectedTypeName) {
        throw "Qualification requires exactly one filename-matching top-level direct xUnit test class in '$Path'. Found: $($typeNames -join ', ')."
    }

    return [string[]]@("$declaredNamespace.$expectedTypeName")
}

function Test-QualificationContainsDirectXunitTest {
    param([Parameter(Mandatory = $true)] [string]$Content)

    return @(Get-QualificationDirectXunitTestTypeNames -Content $Content).Count -gt 0
}

function Test-QualificationFocusedImplementationSource {
    param([Parameter(Mandatory = $true)] [string]$Content)

    Initialize-QualificationCSharpParser
    $syntaxTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content)
    if (@($syntaxTree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error }).Count -gt 0) {
        return $false
    }

    $root = $syntaxTree.GetCompilationUnitRoot()
    $topLevelTypes = @(
        $root.DescendantNodes() |
            Where-Object {
                $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] -and
                $null -eq ($_.Ancestors() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] } | Select-Object -First 1)
            }
    )
    if ($topLevelTypes.Count -ne 1) {
        return $false
    }

    $modifiers = @($topLevelTypes[0].Modifiers | ForEach-Object { $_.ValueText })
    return $modifiers -ccontains "internal" -and
        $modifiers -ccontains "sealed" -and
        $modifiers -cnotcontains "partial" -and
        $modifiers -cnotcontains "public" -and
        $modifiers -cnotcontains "protected"
}

function Test-QualificationFocusedPrivateMethodEdge {
    param(
        [Parameter(Mandatory = $true)] [string]$BaseContent,
        [Parameter(Mandatory = $true)] [string]$HeadContent,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$TypeName,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$MemberName
    )

    Initialize-QualificationCSharpParser
    $contents = @($BaseContent, $HeadContent)
    $methods = [Collections.Generic.List[object]]::new()
    foreach ($content in $contents) {
        $syntaxTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($content)
        if (@($syntaxTree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error }).Count -gt 0) {
            return $false
        }

        $root = $syntaxTree.GetCompilationUnitRoot()
        $topLevelTypes = @(
            $root.DescendantNodes() |
                Where-Object {
                    $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] -and
                    $null -eq ($_.Ancestors() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] } | Select-Object -First 1)
                }
        )
        if ($topLevelTypes.Count -ne 1 -or $topLevelTypes[0] -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -or $topLevelTypes[0].Identifier.ValueText -cne $TypeName) {
            return $false
        }

        $typeModifiers = @($topLevelTypes[0].Modifiers | ForEach-Object { $_.ValueText })
        if ($typeModifiers -cnotcontains "public" -or $typeModifiers -cnotcontains "sealed" -or $typeModifiers -ccontains "partial") {
            return $false
        }

        $matchingMethods = @(
            $topLevelTypes[0].Members |
                Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq $MemberName }
        )
        if ($matchingMethods.Count -ne 1 -or $null -eq $matchingMethods[0].Body -or $null -ne $matchingMethods[0].ExpressionBody) {
            return $false
        }

        $methodModifiers = @($matchingMethods[0].Modifiers | ForEach-Object { $_.ValueText })
        if ($methodModifiers -cnotcontains "private" -or $methodModifiers -ccontains "public" -or $methodModifiers -ccontains "protected" -or $methodModifiers -ccontains "internal") {
            return $false
        }

        $methods.Add($matchingMethods[0])
    }

    $baseMethod = $methods[0]
    $headMethod = $methods[1]
    $basePrefix = $BaseContent.Substring(0, $baseMethod.Body.Span.Start)
    $headPrefix = $HeadContent.Substring(0, $headMethod.Body.Span.Start)
    $baseSuffix = $BaseContent.Substring($baseMethod.Body.Span.End)
    $headSuffix = $HeadContent.Substring($headMethod.Body.Span.End)
    return $basePrefix -ceq $headPrefix -and $baseSuffix -ceq $headSuffix
}

function Test-QualificationPublicConstantContractSource {
    param(
        [Parameter(Mandatory = $true)] [string]$Content,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$TypeName,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$MemberName
    )

    Initialize-QualificationCSharpParser
    $syntaxTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content)
    if (@($syntaxTree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error }).Count -gt 0) {
        return $false
    }

    $root = $syntaxTree.GetCompilationUnitRoot()
    $topLevelTypes = @(
        $root.DescendantNodes() |
            Where-Object {
                $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] -and
                $null -eq ($_.Ancestors() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] } | Select-Object -First 1)
            }
    )
    if ($topLevelTypes.Count -ne 1 -or $topLevelTypes[0] -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -or $topLevelTypes[0].Identifier.ValueText -cne $TypeName) {
        return $false
    }

    $typeModifiers = @($topLevelTypes[0].Modifiers | ForEach-Object { $_.ValueText })
    if ($typeModifiers.Count -ne 2 -or $typeModifiers -cnotcontains "public" -or $typeModifiers -cnotcontains "static") {
        return $false
    }
    if ($topLevelTypes[0].Members.Count -ne 1 -or $topLevelTypes[0].Members[0] -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax]) {
        return $false
    }

    $field = $topLevelTypes[0].Members[0]
    $fieldModifiers = @($field.Modifiers | ForEach-Object { $_.ValueText })
    if ($fieldModifiers.Count -ne 2 -or $fieldModifiers -cnotcontains "public" -or $fieldModifiers -cnotcontains "const" -or $field.Declaration.Type.ToString() -cne "int") {
        return $false
    }
    if ($field.Declaration.Variables.Count -ne 1 -or $field.Declaration.Variables[0].Identifier.ValueText -cne $MemberName) {
        return $false
    }

    $initializer = $field.Declaration.Variables[0].Initializer
    if ($null -eq $initializer -or $initializer.Value -isnot [Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax] -or $initializer.Value.RawKind -ne [int][Microsoft.CodeAnalysis.CSharp.SyntaxKind]::NumericLiteralExpression) {
        return $false
    }

    $value = $initializer.Value.Token.Value
    return $value -is [int] -and $value -ge 1 -and $value -le 60
}

function Test-QualificationContainsIdentifierReference {
    param(
        [Parameter(Mandatory = $true)] [string]$Content,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Identifier
    )

    Initialize-QualificationCSharpParser
    $root = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content).GetCompilationUnitRoot()
    foreach ($token in $root.DescendantTokens()) {
        if ($token.RawKind -eq [int][Microsoft.CodeAnalysis.CSharp.SyntaxKind]::IdentifierToken -and $token.ValueText -ceq $Identifier) {
            return $true
        }
    }

    return $false
}

function Get-QualificationExactIdentifierReferencePaths {
    param(
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string]$Commit,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Identifier
    )

    $grepLines = @(& git -C $RepositoryRoot grep -l -F $Identifier $Commit -- src tests 2>$null)
    $grepExitCode = $LASTEXITCODE
    if ($grepExitCode -eq 1) {
        return [string[]]::new(0)
    }
    if ($grepExitCode -ne 0) {
        throw "Qualification could not enumerate exact-head references to '$Identifier'."
    }

    $commitPrefix = "$Commit`:"
    $referencePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($grepLine in $grepLines) {
        if (-not $grepLine.StartsWith($commitPrefix, [StringComparison]::Ordinal)) {
            throw "Qualification received malformed exact-head grep evidence '$grepLine'."
        }

        $candidatePath = ConvertTo-QualificationPath -Path $grepLine.Substring($commitPrefix.Length)
        if (-not $candidatePath.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $objectName = "$Commit`:$candidatePath"
        $contentLines = @(& git -C $RepositoryRoot cat-file blob $objectName 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Qualification could not read exact-head reference '$candidatePath'."
        }
        if (Test-QualificationContainsIdentifierReference -Content ($contentLines -join "`n") -Identifier $Identifier) {
            [void]$referencePaths.Add($candidatePath)
        }
    }

    return [string[]]@($referencePaths | Sort-Object)
}

function Get-QualificationExternalTestClassConsumerPaths {
    param(
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string]$Commit,
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$TestClass
    )

    $normalizedPath = ConvertTo-QualificationPath -Path $Path
    $testProject = Get-QualificationTestProject -Path $normalizedPath
    if ($null -eq $testProject) {
        throw "Qualification cannot inspect test-class consumers outside a known test project: '$normalizedPath'."
    }

    $projectPrefix = Get-QualificationTestProjectPrefix -TestProject $testProject
    $separatorIndex = $TestClass.LastIndexOf('.')
    $identifier = if ($separatorIndex -lt 0) { $TestClass } else { $TestClass.Substring($separatorIndex + 1) }
    if ([string]::IsNullOrWhiteSpace($identifier)) {
        throw "Qualification test class must end in a non-empty identifier: '$TestClass'."
    }

    $grepLines = @(& git -C $RepositoryRoot grep -l -F $identifier $Commit -- $projectPrefix 2>$null)
    $grepExitCode = $LASTEXITCODE
    if ($grepExitCode -eq 1) {
        return [string[]]::new(0)
    }
    if ($grepExitCode -ne 0) {
        throw "Qualification could not enumerate exact-head consumers of '$TestClass'."
    }

    $commitPrefix = "$Commit`:"
    $consumerPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($grepLine in $grepLines) {
        if (-not $grepLine.StartsWith($commitPrefix, [StringComparison]::Ordinal)) {
            throw "Qualification received malformed exact-head grep evidence '$grepLine'."
        }

        $candidatePath = ConvertTo-QualificationPath -Path $grepLine.Substring($commitPrefix.Length)
        if ($candidatePath -ceq $normalizedPath) {
            continue
        }
        if (-not $candidatePath.StartsWith($projectPrefix, [StringComparison]::Ordinal) -or -not $candidatePath.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Qualification found an out-of-project test-class consumer '$candidatePath'."
        }

        $objectName = "$Commit`:$candidatePath"
        $contentLines = @(& git -C $RepositoryRoot cat-file blob $objectName 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Qualification could not read exact-head consumer '$candidatePath'."
        }
        if (Test-QualificationContainsIdentifierReference -Content ($contentLines -join "`n") -Identifier $identifier) {
            [void]$consumerPaths.Add($candidatePath)
        }
    }

    return [string[]]@($consumerPaths | Sort-Object)
}

function Test-QualificationContainsFocusedHelperReference {
    param(
        [Parameter(Mandatory = $true)] [string]$Content,
        [AllowEmptyCollection()] [string[]]$HelperIdentifiers = @()
    )

    if ($HelperIdentifiers.Count -eq 0) {
        $HelperIdentifiers = @($script:QualificationFocusedHelperMappings | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Path) })
    }

    $identifiers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($helperIdentifier in $HelperIdentifiers) {
        if ([string]::IsNullOrWhiteSpace($helperIdentifier)) {
            throw "Qualification focused helper identifiers must be non-empty."
        }
        [void]$identifiers.Add($helperIdentifier)
    }

    Initialize-QualificationCSharpParser
    $root = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content).GetCompilationUnitRoot()
    foreach ($token in $root.DescendantTokens()) {
        if ($token.RawKind -eq [int][Microsoft.CodeAnalysis.CSharp.SyntaxKind]::IdentifierToken -and $identifiers.Contains($token.ValueText)) {
            return $true
        }
    }

    return $false
}

function Get-QualificationTestFilter {
    param(
        [Parameter(Mandatory = $true)] [string]$ProjectName,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Namespaces,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Classes
    )

    $isE2EProject = $ProjectName -ceq $script:QualificationE2ETestProjectName
    $clauses = [Collections.Generic.List[string]]::new()
    $focusedClauses = [Collections.Generic.List[string]]::new()
    foreach ($namespace in $Namespaces) {
        $focusedClauses.Add("FullyQualifiedName~$namespace")
    }
    foreach ($class in $Classes) {
        if ($isE2EProject -and $class -ceq $script:QualificationInstalledBrowserTestClass) {
            continue
        }
        $focusedClauses.Add("FullyQualifiedName~$class.")
    }
    if ($focusedClauses.Count -gt 0) {
        $clauses.Add("($($focusedClauses -join '|'))")
    }
    if ($isE2EProject) {
        $clauses.Add("(FullyQualifiedName!~BrowserFlowTests)")
    }
    $clauses.Add("(VerificationTier!=Stress)")

    return $clauses -join "&"
}

function Get-QualificationPlan {
    param(
        [Parameter(Mandatory = $true)] [string[]]$ChangedPaths,
        [Collections.IDictionary]$TestNamespacesByPath = @{},
        [Collections.IDictionary]$TestClassesByPath = @{},
        [AllowEmptyCollection()] [string[]]$FocusedHelperRelevantPaths = @(),
        [AllowEmptyCollection()] [string[]]$FocusedImplementationFallbackPaths = @(),
        [AllowNull()] [AllowEmptyCollection()] [string[]]$AvailableTestProjects = $null
    )

    if ($ChangedPaths.Count -eq 0) {
        throw "Qualification requires at least one changed path."
    }

    $normalizedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $canonicalTestProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($testProject in $script:QualificationTestProjects) {
        [void]$canonicalTestProjects.Add($testProject)
    }
    $availableTestProjectSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (-not $PSBoundParameters.ContainsKey("AvailableTestProjects")) {
        foreach ($testProject in $script:QualificationTestProjects) {
            [void]$availableTestProjectSet.Add($testProject)
        }
    }
    else {
        foreach ($availableTestProject in $AvailableTestProjects) {
            $normalizedTestProject = ConvertTo-QualificationPath -Path $availableTestProject
            if (-not $canonicalTestProjects.Contains($normalizedTestProject)) {
                throw "Qualification received an unknown available test project '$normalizedTestProject'."
            }
            [void]$availableTestProjectSet.Add($normalizedTestProject)
        }
    }
    $focusedHelperRelevantPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($focusedHelperRelevantPath in $FocusedHelperRelevantPaths) {
        [void]$focusedHelperRelevantPathSet.Add((ConvertTo-QualificationPath -Path $focusedHelperRelevantPath))
    }
    $focusedImplementationFallbackPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($focusedImplementationFallbackPath in $FocusedImplementationFallbackPaths) {
        [void]$focusedImplementationFallbackPathSet.Add((ConvertTo-QualificationPath -Path $focusedImplementationFallbackPath))
    }
    $testProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unfilteredTestProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $filteredTestNamespaces = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $filteredTestClasses = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $unclassifiedPaths = [Collections.Generic.List[string]]::new()
    $requiresBuild = $false
    $requiresFrontend = $false
    $requiresWorkflowValidation = $false
    $requiresVerifierContracts = $false
    $requiresCSharpFormat = $false
    $requiresArchitecture = $false
    $requiresDrawioValidation = $false
    $changedCSharpFiles = [Collections.Generic.List[string]]::new()

    foreach ($changedPath in $ChangedPaths) {
        $path = ConvertTo-QualificationPath -Path $changedPath
        if (-not $normalizedPaths.Add($path)) {
            continue
        }

        $classified = $false
        if ($focusedHelperRelevantPathSet.Contains($path)) {
            $requiresVerifierContracts = $true
        }
        if (@(Get-QualificationFocusedImplementationMappingsForPath -Path $path).Count -gt 0) {
            $requiresVerifierContracts = $true
        }
        if ($path.StartsWith("scripts/", [StringComparison]::Ordinal) -or $path.StartsWith("tests/scripts/", [StringComparison]::Ordinal) -or $path.StartsWith(".github/", [StringComparison]::Ordinal)) {
            $requiresVerifierContracts = $true
            $classified = $true
        }

        if ($path.StartsWith(".github/workflows/", [StringComparison]::Ordinal) -or $path -ceq ".github/dependabot.yml") {
            $requiresFrontend = $true
            $requiresWorkflowValidation = $true
        }

        if ($path -ceq "package.json" -or $path -ceq "package-lock.json" -or $path -ceq "eslint.config.js" -or $path -ceq ".prettierignore" -or $path.StartsWith("tests/frontend/", [StringComparison]::Ordinal) -or $path.StartsWith("src/EmbodySense.Web/wwwroot/", [StringComparison]::Ordinal)) {
            $requiresFrontend = $true
            $classified = $true
        }

        $focusedImplementationMapping = Get-QualificationFocusedImplementationMapping -Path $path
        if ($null -ne $focusedImplementationMapping -and -not $focusedImplementationFallbackPathSet.Contains($path)) {
            $requiresBuild = $true
            $requiresArchitecture = $true
            $requiresVerifierContracts = $true
            foreach ($testMapping in @($focusedImplementationMapping.Tests)) {
                $testProject = Get-QualificationTestProject -Path $testMapping.Path
                if ($null -eq $testProject) {
                    throw "Focused implementation mapping '$path' names an unknown test source '$($testMapping.Path)'."
                }

                $projectNamespace = [IO.Path]::GetFileNameWithoutExtension($testProject)
                if ([string]::IsNullOrWhiteSpace($testMapping.Class) -or -not $testMapping.Class.StartsWith("$projectNamespace.", [StringComparison]::Ordinal)) {
                    throw "Focused implementation mapping '$path' names an invalid test class '$($testMapping.Class)'."
                }

                [void]$testProjects.Add($testProject)
                if (-not $unfilteredTestProjects.Contains($testProject)) {
                    if (-not $filteredTestClasses.ContainsKey($testProject)) {
                        $filteredTestClasses.Add($testProject, [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal))
                    }
                    [void]$filteredTestClasses[$testProject].Add($testMapping.Class)
                }
            }
            $classified = $true
        }
        else {
            foreach ($mapping in $script:QualificationExactSourceMappings) {
                if ($path -ceq $mapping.Path) {
                    $requiresBuild = $true
                    $requiresArchitecture = $true
                    foreach ($testProject in $mapping.TestProjects) {
                        [void]$testProjects.Add($testProject)
                        [void]$unfilteredTestProjects.Add($testProject)
                        [void]$filteredTestNamespaces.Remove($testProject)
                        [void]$filteredTestClasses.Remove($testProject)
                    }
                    $classified = $true
                    break
                }
            }

            foreach ($mapping in $script:QualificationSourceMappings) {
                if ($path.StartsWith($mapping.Prefix, [StringComparison]::Ordinal)) {
                    $requiresBuild = $true
                    $requiresArchitecture = $true
                    foreach ($testProject in $mapping.TestProjects) {
                        [void]$testProjects.Add($testProject)
                        [void]$unfilteredTestProjects.Add($testProject)
                        [void]$filteredTestNamespaces.Remove($testProject)
                        [void]$filteredTestClasses.Remove($testProject)
                    }
                    $classified = $true
                    break
                }
            }
        }

        foreach ($mapping in $script:QualificationTestMappings) {
            if ($path.StartsWith($mapping.Prefix, [StringComparison]::Ordinal)) {
                $requiresBuild = $true
                if ($path -ceq $mapping.TestProject) {
                    $requiresArchitecture = $true
                }
                [void]$testProjects.Add($mapping.TestProject)
                if ($path -ceq $script:QualificationInstalledBrowserTestPath) {
                    [void]$unfilteredTestProjects.Add($mapping.TestProject)
                    [void]$filteredTestNamespaces.Remove($mapping.TestProject)
                    [void]$filteredTestClasses.Remove($mapping.TestProject)
                    $classified = $true
                    break
                }
                if (Test-QualificationFilterableTestSource -Path $path) {
                    $hasNamespaces = $TestNamespacesByPath.ContainsKey($path)
                    $hasClasses = $TestClassesByPath.ContainsKey($path)
                    if ($hasNamespaces -eq $hasClasses) {
                        throw "Qualification requires exactly one authenticated namespace or class selection for changed test source '$path'."
                    }

                    if ($hasNamespaces) {
                        $testNamespaces = @($TestNamespacesByPath[$path])
                    }
                    else {
                        $testClasses = @($TestClassesByPath[$path])
                    }
                    if (($hasNamespaces -and ($testNamespaces.Count -eq 0 -or @($testNamespaces | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0)) -or ($hasClasses -and ($testClasses.Count -eq 0 -or @($testClasses | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0))) {
                        [void]$unfilteredTestProjects.Add($mapping.TestProject)
                        [void]$filteredTestNamespaces.Remove($mapping.TestProject)
                        [void]$filteredTestClasses.Remove($mapping.TestProject)
                        $classified = $true
                        break
                    }

                    $projectNamespace = [IO.Path]::GetFileNameWithoutExtension($mapping.TestProject)
                    if ($hasNamespaces) {
                        foreach ($testNamespaceValue in $testNamespaces) {
                            $testNamespace = [string]$testNamespaceValue
                            if ($testNamespace -cne $projectNamespace -and -not $testNamespace.StartsWith("$projectNamespace.", [StringComparison]::Ordinal)) {
                                throw "Qualification test namespace '$testNamespace' does not belong to owning project '$projectNamespace' for '$path'."
                            }

                            if (-not $unfilteredTestProjects.Contains($mapping.TestProject)) {
                                if (-not $filteredTestNamespaces.ContainsKey($mapping.TestProject)) {
                                    $filteredTestNamespaces.Add($mapping.TestProject, [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal))
                                }
                                [void]$filteredTestNamespaces[$mapping.TestProject].Add($testNamespace)
                            }
                        }
                    }
                    else {
                        foreach ($testClassValue in $testClasses) {
                            $testClass = [string]$testClassValue
                            if (-not $testClass.StartsWith("$projectNamespace.", [StringComparison]::Ordinal)) {
                                throw "Qualification test class '$testClass' does not belong to owning project '$projectNamespace' for '$path'."
                            }

                            if (-not $unfilteredTestProjects.Contains($mapping.TestProject)) {
                                if (-not $filteredTestClasses.ContainsKey($mapping.TestProject)) {
                                    $filteredTestClasses.Add($mapping.TestProject, [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal))
                                }
                                [void]$filteredTestClasses[$mapping.TestProject].Add($testClass)
                            }
                        }
                    }
                }
                else {
                    [void]$unfilteredTestProjects.Add($mapping.TestProject)
                    [void]$filteredTestNamespaces.Remove($mapping.TestProject)
                    [void]$filteredTestClasses.Remove($mapping.TestProject)
                }
                $classified = $true
                break
            }
        }

        $linkedTestMapping = Get-QualificationLinkedTestMapping -Path $path
        if ($null -ne $linkedTestMapping) {
            $requiresBuild = $true
            foreach ($testProject in $linkedTestMapping.TestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
                [void]$filteredTestClasses.Remove($testProject)
            }
            $classified = $true
        }

        if ($path.StartsWith("tests/EmbodySense.E2EBrowserHost/", [StringComparison]::Ordinal)) {
            $requiresBuild = $true
            $e2eTestProject = "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj"
            [void]$testProjects.Add($e2eTestProject)
            [void]$unfilteredTestProjects.Add($e2eTestProject)
            [void]$filteredTestNamespaces.Remove($e2eTestProject)
            [void]$filteredTestClasses.Remove($e2eTestProject)
            $classified = $true
        }

        if ($path.StartsWith("tests/EmbodySense.HumanInputContinuationHost/", [StringComparison]::Ordinal)) {
            $requiresBuild = $true
            $persistenceTestProject = "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
            [void]$testProjects.Add($persistenceTestProject)
            [void]$unfilteredTestProjects.Add($persistenceTestProject)
            [void]$filteredTestNamespaces.Remove($persistenceTestProject)
            [void]$filteredTestClasses.Remove($persistenceTestProject)
            $classified = $true
        }

        if ($path.StartsWith("tests/EmbodySense.CancellationHost/", [StringComparison]::Ordinal) -or $path.StartsWith("tests/EmbodySense.Tests.Support/", [StringComparison]::Ordinal) -or $path.StartsWith("tests/Shared/", [StringComparison]::Ordinal)) {
            $requiresBuild = $true
            foreach ($testProject in $script:QualificationTestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
                [void]$filteredTestClasses.Remove($testProject)
            }
            $classified = $true
        }

        if ($path -ceq "tests/verification-pull-request.runsettings" -or $path -ceq "tests/verification-stress.runsettings") {
            $requiresBuild = $true
            $requiresVerifierContracts = $true
            foreach ($testProject in $script:QualificationTestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
                [void]$filteredTestClasses.Remove($testProject)
            }
            $classified = $true
        }

        if ($path.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase)) {
            $requiresCSharpFormat = $true
            $changedCSharpFiles.Add($path)
        }

        if ($path -ceq "EmbodySense.sln" -or $path -ceq "Directory.Build.props" -or $path -ceq "global.json" -or $path -ceq "NuGet.config" -or $path -ceq ".editorconfig" -or $path -ceq ".gitattributes" -or $path.EndsWith(".props", [StringComparison]::OrdinalIgnoreCase) -or $path.EndsWith(".targets", [StringComparison]::OrdinalIgnoreCase)) {
            $requiresBuild = $true
            $requiresArchitecture = $true
            foreach ($testProject in $script:QualificationTestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
                [void]$filteredTestClasses.Remove($testProject)
            }
            $classified = $true
        }

        if ($path.EndsWith(".drawio", [StringComparison]::OrdinalIgnoreCase)) {
            $requiresDrawioValidation = $true
        }

        if ($path -ceq "README.md" -or $path -ceq "AGENTS.md" -or $path -ceq ".gitignore" -or $path.StartsWith("docs/", [StringComparison]::Ordinal)) {
            $classified = $true
        }

        if (-not $classified) {
            $unclassifiedPaths.Add($path)
        }
    }

    if ($unclassifiedPaths.Count -gt 0) {
        throw "Qualification has unclassified changed paths: $(@($unclassifiedPaths | Sort-Object) -join ', '). Update the explicit qualification ownership map instead of silently skipping them."
    }
    foreach ($focusedHelperRelevantPath in $focusedHelperRelevantPathSet) {
        if (-not $normalizedPaths.Contains($focusedHelperRelevantPath)) {
            throw "Qualification focused helper relevance named an unchanged path '$focusedHelperRelevantPath'."
        }
    }
    foreach ($focusedImplementationFallbackPath in $focusedImplementationFallbackPathSet) {
        if (-not $normalizedPaths.Contains($focusedImplementationFallbackPath)) {
            throw "Qualification focused implementation fallback named an unchanged path '$focusedImplementationFallbackPath'."
        }
        $fallbackMapping = Get-QualificationFocusedImplementationMapping -Path $focusedImplementationFallbackPath
        if ($null -eq $fallbackMapping) {
            throw "Qualification focused implementation fallback named an unmapped path '$focusedImplementationFallbackPath'."
        }
        if ($fallbackMapping.Kind -cne "PrivateMethod") {
            throw "Qualification focused implementation fallback named non-private-method mapping '$focusedImplementationFallbackPath'."
        }
    }

    $scheduledTestProjects = @($testProjects | Where-Object { $availableTestProjectSet.Contains($_) } | Sort-Object)
    $testSelections = @(
        foreach ($testProject in $scheduledTestProjects) {
            if ($unfilteredTestProjects.Contains($testProject)) {
                $namespaces = [string[]]::new(0)
                $classes = [string[]]::new(0)
            }
            else {
                $namespaces = [string[]]::new(0)
                $classes = [string[]]::new(0)
                if ($filteredTestNamespaces.ContainsKey($testProject)) {
                    $namespaces = [string[]]@($filteredTestNamespaces[$testProject] | Sort-Object)
                }
                if ($filteredTestClasses.ContainsKey($testProject)) {
                    $classes = [string[]]@($filteredTestClasses[$testProject] | Sort-Object)
                }
                if ($namespaces.Count -eq 0 -and $classes.Count -eq 0) {
                    throw "Qualification test project '$testProject' has neither full-project, namespace, nor class ownership."
                }
            }

            [pscustomobject]@{
                Project = $testProject
                Namespaces = $namespaces
                Classes = $classes
            }
        }
    )

    return [pscustomobject]@{
        SchemaVersion = 1
        ChangedPaths = @($normalizedPaths | Sort-Object)
        FocusedImplementationFallbackPaths = @($focusedImplementationFallbackPathSet | Sort-Object)
        RequiresBuild = $requiresBuild
        RequiresFrontend = $requiresFrontend
        RequiresWorkflowValidation = $requiresWorkflowValidation
        RequiresVerifierContracts = $requiresVerifierContracts
        RequiresCSharpFormat = $requiresCSharpFormat
        RequiresArchitecture = $requiresArchitecture
        RequiresDrawioValidation = $requiresDrawioValidation
        ChangedCSharpFiles = @($changedCSharpFiles | Sort-Object -Unique)
        TestProjects = $scheduledTestProjects
        TestSelections = $testSelections
    }
}
