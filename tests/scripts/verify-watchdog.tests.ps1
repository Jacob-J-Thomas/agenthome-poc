Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$deadlineScriptPath = Join-Path $repoRoot "scripts\verification-deadline.ps1"
$qualificationPlanScriptPath = Join-Path $repoRoot "scripts\qualification-plan.ps1"
$qualificationScriptPath = Join-Path $repoRoot "scripts\qualify.ps1"
$watchdogScriptPath = Join-Path $repoRoot "scripts\verify-with-watchdog.ps1"
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$verifyWorkflowPath = Join-Path $repoRoot ".github\workflows\verify.yml"
$qualificationWorkflowPath = Join-Path $repoRoot ".github\workflows\qualification.yml"
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)

    Assert-True -Condition ($Actual -ceq $Expected) -Message "$Message Expected '$Expected'. Actual '$Actual'."
}

. $deadlineScriptPath
. $qualificationPlanScriptPath

$docsPlan = Get-QualificationPlan -ChangedPaths @("README.md", "docs/VERIFICATION.md")
Assert-True -Condition (-not $docsPlan.RequiresBuild -and -not $docsPlan.RequiresFrontend -and $docsPlan.TestProjects.Count -eq 0) -Message "Documentation-only changes must not trigger unrelated compilation or tests."

$applicationTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/RunnerTests.cs"
$applicationTestNamespaces = @{ $applicationTestPath = "EmbodySense.Core.Application.Tests.Loops" }
$applicationTestClasses = @{ $applicationTestPath = "EmbodySense.Core.Application.Tests.Loops.RunnerTests" }
$applicationPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Application/Loops/Runner.cs", $applicationTestPath) -TestClassesByPath $applicationTestClasses
Assert-True -Condition ($applicationPlan.RequiresBuild -and $applicationPlan.RequiresArchitecture -and $applicationPlan.RequiresCSharpFormat) -Message "Application C# changes must compile, format, and retain architecture validation."
$expectedApplicationConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($applicationPlan.TestProjects -join "|") -Expected ($expectedApplicationConsumers -join "|") -Message "Application production changes must execute the owning suite and downstream integration boundary."
Assert-True -Condition (@($applicationPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Application production consumers must run as complete suites even when the same test class also changed."

$cliCommandPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Cli.Command/RunCommand.cs")
$expectedCliCommandConsumers = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($cliCommandPlan.TestProjects -join "|") -Expected ($expectedCliCommandConsumers -join "|") -Message "CLI Command production changes must execute the owning suite and real-process Integration behavior."
Assert-True -Condition (@($cliCommandPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "CLI Command production consumers must run as complete suites."

$clientsPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs")
$expectedClientsConsumers = @(
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($clientsPlan.TestProjects -join "|") -Expected ($expectedClientsConsumers -join "|") -Message "Clients production changes must execute the owning suite and app-server Integration behavior."
Assert-True -Condition (@($clientsPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Clients production consumers must run as complete suites."

$developerInstructionsPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Common/Governance/Tools/EmbodySenseDeveloperInstructions.cs")
$expectedDeveloperInstructionsConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($developerInstructionsPlan.TestProjects -join "|") -Expected ($expectedDeveloperInstructionsConsumers -join "|") -Message "Shared developer-instruction changes must execute every behavioral consumer suite."
Assert-True -Condition (@($developerInstructionsPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Shared developer-instruction consumers must run as complete suites."

$commonPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Common/Governance/Tools/ToolResultRetentionLimits.cs")
$expectedCommonConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($commonPlan.TestProjects -join "|") -Expected ($expectedCommonConsumers -join "|") -Message "General Common changes must execute every direct test-project consumer."
Assert-True -Condition (@($commonPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Common production consumers must run as complete suites."

$persistencePlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Persistence/Capabilities/CapabilityCatalogStore.cs")
$expectedPersistenceConsumers = @(
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
)
Assert-Equal -Actual ($persistencePlan.TestProjects -join "|") -Expected ($expectedPersistenceConsumers -join "|") -Message "Persistence production changes must execute the owning suite, Startup composition, and direct Integration behavior."
Assert-True -Condition (@($persistencePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Persistence production consumers must run as complete suites."

$startupPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Startup/Runtime/AgentRuntime.cs")
$expectedStartupConsumers = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
Assert-Equal -Actual ($startupPlan.TestProjects -join "|") -Expected ($expectedStartupConsumers -join "|") -Message "Startup production changes must execute every direct interface consumer suite."
Assert-True -Condition (@($startupPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Startup production consumers must run as complete suites."

$testOnlyPlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestClassesByPath $applicationTestClasses
Assert-Equal -Actual $testOnlyPlan.TestSelections.Count -Expected 1 -Message "A test-only edit must select exactly its owning project."
Assert-Equal -Actual @($testOnlyPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A direct test edit must not broaden to its containing namespace."
Assert-Equal -Actual @($testOnlyPlan.TestSelections[0].Classes).Count -Expected 1 -Message "A test-only edit must not expand to its entire large test assembly."
Assert-Equal -Actual $testOnlyPlan.TestSelections[0].Classes[0] -Expected "EmbodySense.Core.Application.Tests.Loops.RunnerTests" -Message "A test-only edit must retain its exact filename-matching class as the fail-closed test filter."
Assert-True -Condition (-not $testOnlyPlan.RequiresVerifierContracts) -Message "An unrelated test-only edit must not pay the verifier-contract wave."

$deletedTestSourcePlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestNamespacesByPath @{ $applicationTestPath = [string[]]::new(0) }
Assert-Equal -Actual $deletedTestSourcePlan.TestSelections.Count -Expected 1 -Message "A deleted test source must retain its surviving owning project."
Assert-Equal -Actual @($deletedTestSourcePlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "Deleting the final test in a namespace must run the remaining project unfiltered instead of scheduling an empty namespace."
Assert-Equal -Actual @($deletedTestSourcePlan.TestSelections[0].Classes).Count -Expected 0 -Message "Deleting a test source must not leave a stale class filter."

$helperConsumerPlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestClassesByPath $applicationTestClasses -FocusedHelperRelevantPaths @($applicationTestPath)
Assert-True -Condition $helperConsumerPlan.RequiresVerifierContracts -Message "A syntax-proven focused-helper consumer change must revalidate the checked helper map."

$unchangedHelperConsumerRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @($applicationTestPath) -TestClassesByPath $applicationTestClasses -FocusedHelperRelevantPaths @("tests/EmbodySense.Core.Application.Tests/Loops/UnchangedTests.cs") | Out-Null
}
catch {
    $unchangedHelperConsumerRejected = $_.Exception.Message.Contains("unchanged path", [StringComparison]::Ordinal)
}
Assert-True -Condition $unchangedHelperConsumerRejected -Message "Focused-helper relevance must be bound to the exact changed-path inventory."

$secondApplicationTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/OtherRunnerTests.cs"
$sameNamespacePlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath, $secondApplicationTestPath) -TestClassesByPath @{ $applicationTestPath = "EmbodySense.Core.Application.Tests.Loops.RunnerTests"; $secondApplicationTestPath = "EmbodySense.Core.Application.Tests.Loops.OtherRunnerTests" }
Assert-Equal -Actual @($sameNamespacePlan.TestSelections[0].Classes).Count -Expected 2 -Message "Changed direct tests in one namespace must retain both exact classes."

$helperTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/RunnerFixture.cs"
$helperTestPlan = Get-QualificationPlan -ChangedPaths @($applicationTestPath, $helperTestPath) -TestNamespacesByPath @{ $helperTestPath = "" } -TestClassesByPath $applicationTestClasses
Assert-Equal -Actual @($helperTestPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A helper edit must restore the full owning project even when a direct test in the same namespace also changed."
Assert-Equal -Actual @($helperTestPlan.TestSelections[0].Classes).Count -Expected 0 -Message "A helper edit must clear direct class filters when it restores the full owning project."

foreach ($helperModelPath in @(
    "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseBudget.cs",
    "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseClassification.cs"
)) {
    $helperModelMapping = Get-QualificationFocusedHelperMapping -Path $helperModelPath
    $helperModelPlan = Get-QualificationPlan -ChangedPaths @($helperModelPath) -TestNamespacesByPath @{ $helperModelPath = @($helperModelMapping.ConsumerNamespaces) }
    Assert-Equal -Actual @($helperModelPlan.TestSelections).Count -Expected 1 -Message "A helper model edit must retain its owning test project."
    Assert-Equal -Actual @($helperModelPlan.TestSelections[0].Namespaces).Count -Expected @($helperModelMapping.ConsumerNamespaces).Count -Message "A reviewed helper model must select every checked consumer namespace without expanding to the full project."
}

$crossNamespaceHelperPath = "tests/EmbodySense.Core.Application.Tests/Capabilities/CapabilityArtifactTestData.cs"
$crossNamespaceHelperSource = Get-Content -LiteralPath (Join-Path $repoRoot $crossNamespaceHelperPath) -Raw
Assert-True -Condition (-not (Test-QualificationContainsDirectXunitTest -Content $crossNamespaceHelperSource)) -Message "The reviewed CapabilityArtifactTestData helper must be identified from syntax as a non-test input."
$crossNamespaceHelperMapping = Get-QualificationFocusedHelperMapping -Path $crossNamespaceHelperPath
$crossNamespaceHelperPlan = Get-QualificationPlan -ChangedPaths @($crossNamespaceHelperPath) -TestNamespacesByPath @{ $crossNamespaceHelperPath = @($crossNamespaceHelperMapping.ConsumerNamespaces) }
Assert-Equal -Actual @($crossNamespaceHelperPlan.TestSelections[0].Namespaces).Count -Expected 2 -Message "A reviewed cross-namespace helper must select every checked consumer namespace."
Assert-True -Condition ($crossNamespaceHelperPlan.TestSelections[0].Namespaces -ccontains "EmbodySense.Core.Application.Tests.Credentials") -Message "The CapabilityArtifactTestData mapping must retain its Credentials consumer."

$integrationHelperPath = "tests/EmbodySense.IntegrationTests/Core/Governance/Tools/ImmediateToolResultRetentionStore.cs"
$integrationHelperMapping = Get-QualificationFocusedHelperMapping -Path $integrationHelperPath
$integrationHelperPlan = Get-QualificationPlan -ChangedPaths @($integrationHelperPath) -TestClassesByPath @{ $integrationHelperPath = @($integrationHelperMapping.ConsumerClasses) }
Assert-Equal -Actual @($integrationHelperPlan.TestSelections[0].Namespaces).Count -Expected 0 -Message "A reviewed single-class helper must not broaden to its containing namespace."
Assert-Equal -Actual @($integrationHelperPlan.TestSelections[0].Classes).Count -Expected 1 -Message "A reviewed single-class helper must remain focused."
Assert-Equal -Actual $integrationHelperPlan.TestSelections[0].Classes[0] -Expected "EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerTests" -Message "The result-retention helper must select its exact ToolBroker consumer class."
$integrationConsumerPath = "tests/EmbodySense.IntegrationTests/Core/Governance/Tools/ToolBrokerTests.cs"
$integrationHelperAndConsumerPlan = Get-QualificationPlan -ChangedPaths @($integrationHelperPath, $integrationConsumerPath) -TestClassesByPath @{ $integrationHelperPath = @($integrationHelperMapping.ConsumerClasses); $integrationConsumerPath = "EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerTests" }
Assert-Equal -Actual @($integrationHelperAndConsumerPlan.TestSelections[0].Classes).Count -Expected 1 -Message "A helper and its directly changed consumer must deduplicate to one exact class."

$missingSelectionRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @($applicationTestPath) | Out-Null
}
catch {
    $missingSelectionRejected = $_.Exception.Message.Contains("exactly one authenticated namespace or class selection", [StringComparison]::Ordinal)
}
Assert-True -Condition $missingSelectionRejected -Message "A changed test source without authenticated class or namespace ownership must fail closed."

$parsedNamespace = Get-QualificationDeclaredTestNamespace -Path $applicationTestPath -Content "namespace EmbodySense.Core.Application.Tests.Loops;`npublic sealed class RunnerTests {}"
Assert-Equal -Actual $parsedNamespace -Expected "EmbodySense.Core.Application.Tests.Loops" -Message "File-scoped test namespaces must be parsed exactly."

$syntaxAwareSource = @'
namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class SourceLayoutTests
{
    private const string Example = """
namespace Example;
""";

    // namespace Commented.Example;
}
'@
$syntaxAwareNamespace = Get-QualificationDeclaredTestNamespace -Path $applicationTestPath -Content $syntaxAwareSource
Assert-Equal -Actual $syntaxAwareNamespace -Expected "EmbodySense.Core.Application.Tests.Loops" -Message "Namespace selection must use the C# syntax tree and ignore namespace-shaped text in raw strings and comments."

$directTestSource = @'
namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class RunnerTests
{
    [Fact]
    public void Runs() {}

    private const string Example = """
[Theory]
""";
}
'@
Assert-True -Condition (Test-QualificationContainsDirectXunitTest -Content $directTestSource) -Message "A real xUnit method attribute must permit direct-test namespace selection."
Assert-True -Condition (-not (Test-QualificationContainsDirectXunitTest -Content $syntaxAwareSource)) -Message "Test-shaped text in strings or comments must not make a helper namespace filterable."
$directTestClasses = @(Get-QualificationDirectXunitTestClasses -Path $applicationTestPath -Content $directTestSource)
Assert-Equal -Actual ($directTestClasses -join "|") -Expected "EmbodySense.Core.Application.Tests.Loops.RunnerTests" -Message "A direct xUnit source must produce its exact filename-matching class filter."
Assert-True -Condition (-not (Test-QualificationContainsIdentifierReference -Content 'private const string Example = "RunnerTests";' -Identifier "RunnerTests")) -Message "Test-class consumer discovery must ignore class-shaped string content."

$sharedDirectTestPath = "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialRunMaterializerTests.cs"
$sharedDirectTestClass = "EmbodySense.Core.Application.Tests.Loops.Sequential.GovernedLoopSequentialRunMaterializerTests"
$currentCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
Assert-True -Condition ($LASTEXITCODE -eq 0 -and $currentCommit -match '^[0-9a-f]{40}$') -Message "The test-class consumer contract must bind one exact repository commit."
$sharedDirectTestConsumers = @(Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $currentCommit -Path $sharedDirectTestPath -TestClass $sharedDirectTestClass)
$expectedSharedDirectTestConsumers = @(
    "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialBindingResolverTests.cs",
    "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialFrontierMachineTests.cs",
    "tests/EmbodySense.Core.Application.Tests/Loops/Sequential/GovernedLoopSequentialInvocationCoordinatorTests.cs"
)
Assert-Equal -Actual ($sharedDirectTestConsumers -join "|") -Expected ($expectedSharedDirectTestConsumers -join "|") -Message "A direct xUnit class used as cross-file test infrastructure must expose every exact-head consumer and force full-project qualification."

$customFactSource = @'
namespace EmbodySense.E2ETests.Web;

public sealed class BrowserFlowTests
{
    [InstalledBrowserFact]
    public void Runs() {}

    private sealed class InstalledBrowserFactAttribute : FactAttribute {}
}
'@
Assert-True -Condition (Test-QualificationContainsDirectXunitTest -Content $customFactSource) -Message "A file-local FactAttribute subtype must retain its direct-test namespace selection."
$customFactClasses = @(Get-QualificationDirectXunitTestClasses -Path "tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs" -Content $customFactSource)
Assert-Equal -Actual ($customFactClasses -join "|") -Expected "EmbodySense.E2ETests.Web.BrowserFlowTests" -Message "Custom FactAttribute methods must retain the exact declaring test class."

$mismatchedClassRejected = $false
try {
    Get-QualificationDirectXunitTestClasses -Path $applicationTestPath -Content $customFactSource | Out-Null
}
catch {
    $mismatchedClassRejected = $_.Exception.Message.Contains("does not belong to owning project", [StringComparison]::Ordinal) -or $_.Exception.Message.Contains("filename-matching", [StringComparison]::Ordinal)
}
Assert-True -Condition $mismatchedClassRejected -Message "A direct test class that cannot be bound to its project path and filename must fail closed."

$crossProjectRenamePlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Core.Application/Loops/OldRunner.cs", "src/EmbodySense.Core.Common/Loops/NewRunner.cs")
Assert-Equal -Actual $crossProjectRenamePlan.TestProjects.Count -Expected 6 -Message "A cross-project rename into Common must select both owners, the former owner's downstream boundary, and every direct Common consumer."
Assert-True -Condition ($crossProjectRenamePlan.TestProjects -ccontains "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj") -Message "A cross-project rename must retain the former owner."
Assert-True -Condition ($crossProjectRenamePlan.TestProjects -ccontains "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj") -Message "A cross-project rename must select the destination owner."
Assert-True -Condition ($crossProjectRenamePlan.TestProjects -ccontains "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj") -Message "A cross-project rename must retain the Application owner's downstream integration boundary."

$webPlan = Get-QualificationPlan -ChangedPaths @("src/EmbodySense.Web/wwwroot/js/governed.js")
Assert-True -Condition ($webPlan.RequiresBuild -and $webPlan.RequiresFrontend) -Message "Web assets must retain both their owning Web build/tests and frontend checks."
$expectedWebConsumers = @(
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)
Assert-Equal -Actual ($webPlan.TestProjects -join "|") -Expected ($expectedWebConsumers -join "|") -Message "Web changes must execute the owning suite and non-browser hosted E2E behavior."
Assert-True -Condition (@($webPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Web production consumers must run as complete suites; the E2E runner separately excludes installed-browser tests."

$verifierPlan = Get-QualificationPlan -ChangedPaths @("scripts/qualify.ps1", ".github/workflows/qualification.yml")
Assert-True -Condition ($verifierPlan.RequiresVerifierContracts -and -not $verifierPlan.RequiresBuild -and $verifierPlan.TestProjects.Count -eq 0) -Message "Verifier-only changes must run verifier contracts without an unrelated solution build."
Assert-True -Condition ($verifierPlan.RequiresFrontend -and $verifierPlan.RequiresWorkflowValidation) -Message "Workflow changes must install the pinned frontend toolchain and parse every workflow through Prettier."

$sharedTestPlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Tests.Support/TestWorkspace.cs")
Assert-Equal -Actual $sharedTestPlan.TestProjects.Count -Expected 9 -Message "Shared test infrastructure must conservatively select every production test project."

$linkedSharedSourcePlan = Get-QualificationPlan -ChangedPaths @("tests/Shared/TestCapabilityAdmissionFactory.cs")
Assert-Equal -Actual $linkedSharedSourcePlan.TestProjects.Count -Expected 9 -Message "Linked shared test sources must conservatively select every production test project."

$linkedCommonFixturePlan = Get-QualificationPlan -ChangedPaths @("tests/EmbodySense.Core.Common.Tests/Authority/Grants/AuthorityGrantTestFixture.cs")
Assert-Equal -Actual $linkedCommonFixturePlan.TestProjects.Count -Expected 2 -Message "A linked Common fixture must select both the Common and Persistence consumers."
Assert-True -Condition ($linkedCommonFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj") -Message "A linked Common fixture must retain its source project."
Assert-True -Condition ($linkedCommonFixturePlan.TestProjects -ccontains "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj") -Message "A linked Common fixture must execute its Persistence consumer."
Assert-True -Condition (@($linkedCommonFixturePlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Linked test inputs must run every consuming suite without focused filtering."

$frontendConfigurationPlan = Get-QualificationPlan -ChangedPaths @("eslint.config.js", ".prettierignore")
Assert-True -Condition ($frontendConfigurationPlan.RequiresFrontend -and $frontendConfigurationPlan.TestProjects.Count -eq 0) -Message "Tracked lint and formatting configuration must run frontend verification without unrelated .NET tests."

$runSettingsPlan = Get-QualificationPlan -ChangedPaths @("tests/verification-pull-request.runsettings", "tests/verification-stress.runsettings")
Assert-True -Condition ($runSettingsPlan.RequiresBuild -and $runSettingsPlan.RequiresVerifierContracts) -Message "Changed runsettings must compile and verify their orchestration contracts."
Assert-Equal -Actual $runSettingsPlan.TestProjects.Count -Expected 9 -Message "Changed runsettings must conservatively execute every affected full test project."
Assert-True -Condition (@($runSettingsPlan.TestSelections | Where-Object { @($_.Namespaces).Count -ne 0 -or @($_.Classes).Count -ne 0 }).Count -eq 0) -Message "Runsettings changes cannot retain focused test selections."

$attributesPlan = Get-QualificationPlan -ChangedPaths @(".gitattributes")
Assert-True -Condition ($attributesPlan.RequiresBuild -and $attributesPlan.RequiresArchitecture) -Message "Repository attribute changes must retain build and architecture validation."
Assert-Equal -Actual $attributesPlan.TestProjects.Count -Expected 9 -Message "Repository attribute changes must conservatively execute every full test project."

$deletedTestProject = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"
$survivingTestProjects = @($script:QualificationTestProjects | Where-Object { $_ -cne $deletedTestProject })
$deletedProjectPlan = Get-QualificationPlan -ChangedPaths @($deletedTestProject, "EmbodySense.sln") -AvailableTestProjects $survivingTestProjects
Assert-Equal -Actual $deletedProjectPlan.TestProjects.Count -Expected 8 -Message "A project deletion must retain every surviving suite selected by the changed solution."
Assert-True -Condition ($deletedProjectPlan.TestProjects -cnotcontains $deletedTestProject) -Message "Qualification must never schedule a test project absent from the exact head."
$noTestProjectsPlan = Get-QualificationPlan -ChangedPaths @("EmbodySense.sln") -AvailableTestProjects @()
Assert-Equal -Actual $noTestProjectsPlan.TestProjects.Count -Expected 0 -Message "An explicitly empty exact-head test inventory must not fall back to deleted canonical paths."

$unknownAvailableProjectRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @("README.md") -AvailableTestProjects @("tests/Unknown.Tests/Unknown.Tests.csproj") | Out-Null
}
catch {
    $unknownAvailableProjectRejected = $_.Exception.Message.Contains("unknown available test project", [StringComparison]::Ordinal)
}
Assert-True -Condition $unknownAvailableProjectRejected -Message "Available-project evidence must remain inside the canonical test-project inventory."

$unclassifiedRejected = $false
try {
    Get-QualificationPlan -ChangedPaths @("unexpected-root/file.bin") | Out-Null
}
catch {
    $unclassifiedRejected = $_.Exception.Message.Contains("unclassified changed paths", [StringComparison]::Ordinal)
}
Assert-True -Condition $unclassifiedRejected -Message "Unknown paths must fail closed until the ownership map is updated."

$trackedPaths = @(& git -C $repoRoot ls-files)
Assert-True -Condition ($LASTEXITCODE -eq 0 -and $trackedPaths.Count -gt 0) -Message "The qualification ownership contract must enumerate the tracked repository."
$trackedTestNamespaces = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$trackedTestClasses = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($trackedPath in $trackedPaths) {
    if (Test-QualificationFilterableTestSource -Path $trackedPath) {
        $trackedSource = Get-Content -LiteralPath (Join-Path $repoRoot $trackedPath) -Raw
        $trackedNamespace = Get-QualificationDeclaredTestNamespace -Path $trackedPath -Content $trackedSource
        if (Test-QualificationContainsDirectXunitTest -Content $trackedSource) {
            $trackedTestClasses.Add($trackedPath, @(Get-QualificationDirectXunitTestClasses -Path $trackedPath -Content $trackedSource))
        }
        else {
            $focusedHelperMapping = Get-QualificationFocusedHelperMapping -Path $trackedPath
            if ($null -eq $focusedHelperMapping) {
                $trackedTestNamespaces.Add($trackedPath, [string[]]::new(0))
            }
            elseif (@($focusedHelperMapping.ConsumerClasses).Count -gt 0) {
                $trackedTestClasses.Add($trackedPath, [string[]]@($focusedHelperMapping.ConsumerClasses))
            }
            else {
                $trackedTestNamespaces.Add($trackedPath, [string[]]@($focusedHelperMapping.ConsumerNamespaces))
            }
        }
    }
}
$trackedPlan = Get-QualificationPlan -ChangedPaths $trackedPaths -TestNamespacesByPath $trackedTestNamespaces -TestClassesByPath $trackedTestClasses
Assert-Equal -Actual $trackedPlan.ChangedPaths.Count -Expected $trackedPaths.Count -Message "Every currently tracked path must have explicit qualification ownership."

$mappedHelperPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($mapping in $script:QualificationFocusedHelperMappings) {
    Assert-True -Condition $mappedHelperPaths.Add($mapping.Path) -Message "Focused helper mappings must have unique paths."
    Assert-True -Condition ($trackedPaths -ccontains $mapping.Path) -Message "Focused helper '$($mapping.Path)' must be tracked."
    $helperSource = Get-Content -LiteralPath (Join-Path $repoRoot $mapping.Path) -Raw
    Assert-True -Condition (-not (Test-QualificationContainsDirectXunitTest -Content $helperSource)) -Message "Focused helper '$($mapping.Path)' must not directly declare an xUnit test."
    $usesNamespaceMap = @($mapping.ConsumerNamespaces).Count -gt 0
    $usesClassMap = @($mapping.ConsumerClasses).Count -gt 0
    Assert-True -Condition ($usesNamespaceMap -ne $usesClassMap) -Message "Focused helper '$($mapping.Path)' must use exactly one namespace or class consumer map."
    $helperIdentifier = [IO.Path]::GetFileNameWithoutExtension($mapping.Path)
    $actualConsumerNamespaces = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $actualConsumerClasses = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($candidatePath in $trackedPaths) {
        if ($candidatePath -ceq $mapping.Path -or -not $candidatePath.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) -or $null -eq (Get-QualificationTestProject -Path $candidatePath)) {
            continue
        }

        $candidateSource = Get-Content -LiteralPath (Join-Path $repoRoot $candidatePath) -Raw
        if (-not (Test-QualificationContainsFocusedHelperReference -Content $candidateSource -HelperIdentifiers @($helperIdentifier))) {
            continue
        }

        if ($usesClassMap) {
            $candidateClasses = @(Get-QualificationDirectXunitTestClasses -Path $candidatePath -Content $candidateSource)
            Assert-True -Condition ($candidateClasses.Count -gt 0) -Message "Class-focused helper '$($mapping.Path)' has a non-test consumer '$candidatePath'."
            foreach ($candidateClass in $candidateClasses) {
                [void]$actualConsumerClasses.Add($candidateClass)
            }
        }
        else {
            [void]$actualConsumerNamespaces.Add((Get-QualificationDeclaredTestNamespace -Path $candidatePath -Content $candidateSource))
        }
    }

    Assert-Equal -Actual (@($actualConsumerNamespaces | Sort-Object) -join "|") -Expected (@($mapping.ConsumerNamespaces | Sort-Object) -join "|") -Message "Focused helper '$($mapping.Path)' must enumerate every syntax-proven consumer namespace."
    Assert-Equal -Actual (@($actualConsumerClasses | Sort-Object) -join "|") -Expected (@($mapping.ConsumerClasses | Sort-Object) -join "|") -Message "Focused helper '$($mapping.Path)' must enumerate every syntax-proven consumer class."
}

foreach ($consumerProject in $script:QualificationTestProjects) {
    $consumerProjectPath = Join-Path $repoRoot $consumerProject
    [xml]$consumerProjectXml = Get-Content -LiteralPath $consumerProjectPath -Raw
    $compileItems = [Collections.Generic.List[object]]::new()
    foreach ($itemGroup in @($consumerProjectXml.Project.ItemGroup)) {
        $compileProperty = $itemGroup.PSObject.Properties["Compile"]
        if ($null -ne $compileProperty) {
            foreach ($compileItem in @($compileProperty.Value)) {
                $compileItems.Add($compileItem)
            }
        }
    }
    foreach ($compileItem in $compileItems) {
        if ($null -eq $compileItem -or [string]::IsNullOrWhiteSpace($compileItem.Include)) {
            continue
        }

        $linkedFullPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $consumerProjectPath) $compileItem.Include))
        $linkedPath = [IO.Path]::GetRelativePath($repoRoot, $linkedFullPath).Replace('\', '/')
        if ($linkedPath.StartsWith("tests/Shared/", [StringComparison]::Ordinal)) {
            continue
        }

        $ownerProject = Get-QualificationTestProject -Path $linkedPath
        if ($null -eq $ownerProject -or $ownerProject -ceq $consumerProject) {
            continue
        }

        $linkedMapping = Get-QualificationLinkedTestMapping -Path $linkedPath
        Assert-True -Condition ($null -ne $linkedMapping) -Message "Cross-project linked test input '$linkedPath' must have explicit qualification ownership."
        Assert-True -Condition ($linkedMapping.TestProjects -ccontains $ownerProject) -Message "Cross-project linked test input '$linkedPath' must retain its source project '$ownerProject'."
        Assert-True -Condition ($linkedMapping.TestProjects -ccontains $consumerProject) -Message "Cross-project linked test input '$linkedPath' must select consumer '$consumerProject'."
    }
}

function Get-DirectTestProjectConsumers {
    param([Parameter(Mandatory = $true)] [string]$ReferencedProject)

    $consumers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($testProject in $script:QualificationTestProjects) {
        $testProjectPath = Join-Path $repoRoot $testProject
        [xml]$testProjectXml = Get-Content -LiteralPath $testProjectPath -Raw
        foreach ($itemGroup in @($testProjectXml.Project.ItemGroup)) {
            $projectReferenceProperty = $itemGroup.PSObject.Properties["ProjectReference"]
            if ($null -eq $projectReferenceProperty) {
                continue
            }
            foreach ($projectReference in @($projectReferenceProperty.Value)) {
                if ($null -eq $projectReference -or [string]::IsNullOrWhiteSpace($projectReference.Include)) {
                    continue
                }

                $referencedFullPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $testProjectPath) $projectReference.Include))
                $referencedPath = [IO.Path]::GetRelativePath($repoRoot, $referencedFullPath).Replace('\', '/')
                if ($referencedPath -ceq $ReferencedProject) {
                    [void]$consumers.Add($testProject)
                }
            }
        }
    }

    return [string[]]@($consumers | Sort-Object)
}

foreach ($consumerContract in @(
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Common/"; Project = "src/EmbodySense.Core.Common/EmbodySense.Core.Common.csproj"; Label = "Common" },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Persistence/"; Project = "src/EmbodySense.Core.Persistence/EmbodySense.Core.Persistence.csproj"; Label = "Persistence" },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Startup/"; Project = "src/EmbodySense.Core.Startup/EmbodySense.Core.Startup.csproj"; Label = "Startup" }
)) {
    $sourceMappings = @($script:QualificationSourceMappings | Where-Object { $_.Prefix -ceq $consumerContract.Prefix })
    Assert-Equal -Actual $sourceMappings.Count -Expected 1 -Message "$($consumerContract.Label) must have exactly one explicit source-ownership mapping."
    $actualConsumers = @(Get-DirectTestProjectConsumers -ReferencedProject $consumerContract.Project)
    Assert-Equal -Actual ($actualConsumers -join "|") -Expected (@($sourceMappings[0].TestProjects | Sort-Object) -join "|") -Message "$($consumerContract.Label) qualification ownership must match every direct test-project consumer."
}

$lfMarker = "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=600`n"
$crlfMarker = "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=599.999`r`n"
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $lfMarker) -Expected 1 -Message "One exact LF completion marker must be accepted."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput $crlfMarker) -Expected 1 -Message "One exact Windows CRLF completion marker must be accepted."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput "VERIFY_COMPLETE schema_version=1 status=passed`r`n") -Expected 0 -Message "A partial completion marker must be rejected."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput "prefix VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=1`n") -Expected 0 -Message "A prefixed completion marker must be rejected."
Assert-Equal -Actual (Get-VerificationCompletionMarkerCount -StandardOutput ($lfMarker + $crlfMarker)) -Expected 2 -Message "Duplicate exact completion markers must remain visible to fail-closed disposition."

$deadlineTicks = [TimeSpan]::FromSeconds(600).Ticks
Assert-True -Condition (-not (Test-VerificationDeadlineExceeded -ElapsedTicks $deadlineTicks -DeadlineTicks $deadlineTicks)) -Message "The live watchdog decision must retain the inclusive exact 600-second boundary."
Assert-True -Condition (Test-VerificationDeadlineExceeded -ElapsedTicks ($deadlineTicks + 1) -DeadlineTicks $deadlineTicks) -Message "The live watchdog decision must reject the first timer tick over 600 seconds."

$exactDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks $deadlineTicks -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-True -Condition $exactDeadline.Succeeded -Message "Exactly 600 seconds must remain inside the inclusive deadline."
Assert-Equal -Actual $exactDeadline.Code -Expected "passed" -Message "Successful disposition code mismatch."

$overDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks ($deadlineTicks + 1) -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $overDeadline.Code -Expected "deadline-exceeded" -Message "One tick over 600 seconds must fail."

$promotionDeadlineTicks = [TimeSpan]::FromSeconds(900).Ticks
$exactPromotionDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks $promotionDeadlineTicks -DeadlineTicks $promotionDeadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-True -Condition $exactPromotionDeadline.Succeeded -Message "Exactly 900 seconds must remain inside the explicit promotion deadline."
$overPromotionDeadline = Get-VerificationDeadlineDisposition -ElapsedTicks ($promotionDeadlineTicks + 1) -DeadlineTicks $promotionDeadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $overPromotionDeadline.Code -Expected "deadline-exceeded" -Message "One tick over the bounded promotion deadline must fail."

$childTimeout = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 1 -CompletionMarkerCount 0 -ChildTimedOut $true -CancellationRequested $false
Assert-Equal -Actual $childTimeout.Code -Expected "child-timeout" -Message "A child phase timeout must be retained as its own failure."

$cancelled = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $false -CompletionMarkerCount 0 -ChildTimedOut $false -CancellationRequested $true
Assert-Equal -Actual $cancelled.Code -Expected "cancelled" -Message "Cancellation must fail closed."

$missingMarker = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 0 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $missingMarker.Code -Expected "completion-evidence-invalid" -Message "Missing completion evidence must fail closed."

$duplicateMarker = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 0 -CompletionMarkerCount 2 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $duplicateMarker.Code -Expected "completion-evidence-invalid" -Message "Duplicate completion evidence must fail closed."

$partialProcess = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $false -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $partialProcess.Code -Expected "incomplete-process" -Message "A live process cannot pass from partial evidence."

$failedChild = Get-VerificationDeadlineDisposition -ElapsedTicks 1 -DeadlineTicks $deadlineTicks -ProcessExited $true -ExitCode 17 -CompletionMarkerCount 1 -ChildTimedOut $false -CancellationRequested $false
Assert-Equal -Actual $failedChild.Code -Expected "child-failed" -Message "A nonzero verifier exit must fail despite a marker."

$watchdogScript = Get-Content -LiteralPath $watchdogScriptPath -Raw
$qualificationPlanScript = Get-Content -LiteralPath $qualificationPlanScriptPath -Raw
$qualificationScript = Get-Content -LiteralPath $qualificationScriptPath -Raw
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$workflow = Get-Content -LiteralPath $verifyWorkflowPath -Raw
$qualificationWorkflow = Get-Content -LiteralPath $qualificationWorkflowPath -Raw
Assert-True -Condition ($watchdogScript.IndexOf('[int]$DeadlineSeconds = 600', [StringComparison]::Ordinal) -ge 0) -Message "The external watchdog must default to exactly 600 seconds."
Assert-True -Condition ($watchdogScript.IndexOf('[ValidateRange(1, 900)]', [StringComparison]::Ordinal) -ge 0) -Message "No accepted watchdog override may exceed the bounded 900-second promotion window."
Assert-True -Condition ($watchdogScript.IndexOf('[switch]$Qualification', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must expose the bounded qualification child explicitly."
Assert-True -Condition ($watchdogScript.IndexOf('"qualify.ps1"', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must execute through its dedicated bounded orchestrator."
Assert-True -Condition ($watchdogScript.IndexOf('Qualification requires exact -BaseCommit and -HeadCommit values.', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must bind its exact comparison commits."
Assert-True -Condition ($qualificationScript.IndexOf('git diff --no-renames --name-only --diff-filter=ACMRDTUXB "$mergeBase..$HeadCommit"', [StringComparison]::Ordinal) -ge 0) -Message "Qualification selection must derive both sides of renames from the exact merge-base-to-head diff."
Assert-True -Condition ($qualificationScript.IndexOf('git cat-file blob $objectName', [StringComparison]::Ordinal) -ge 0) -Message "Test-only qualification must authenticate its class or helper namespace from an exact edge blob, including deleted or renamed sources."
Assert-True -Condition ($qualificationScript.IndexOf('foreach ($commit in @($HeadCommit, $mergeBase))', [StringComparison]::Ordinal) -ge 0) -Message "Focused-helper consumers must be syntax-checked on both sides of the exact edge."
Assert-True -Condition ($qualificationScript.IndexOf('-TestClassesByPath $testClassesByPath -FocusedHelperRelevantPaths @($focusedHelperRelevantPaths) -AvailableTestProjects $availableTestProjects', [StringComparison]::Ordinal) -ge 0) -Message "The exact-edge qualifier must bind class, helper-map, and surviving-project evidence into its plan."
Assert-True -Condition ($qualificationScript.IndexOf('Test-QualificationCommitPath -Path $drawioPath -Commit $HeadCommit', [StringComparison]::Ordinal) -ge 0) -Message "Deleted draw.io paths must be skipped from exact-head XML validation."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationBlobContent -Path $drawioPath -Commits @($HeadCommit)', [StringComparison]::Ordinal) -ge 0) -Message "Surviving draw.io XML must be read from the authenticated exact head blob."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationTestFilter -ProjectName $projectName -Namespaces @($testSelection.Namespaces) -Classes @($testSelection.Classes)', [StringComparison]::Ordinal) -ge 0) -Message "Test-only edits must execute their authenticated classes or helper namespaces rather than the entire owning assembly."
Assert-True -Condition ($qualificationScript.IndexOf('if (-not (Test-QualificationCommitPath -Path $normalizedPath -Commit $HeadCommit))', [StringComparison]::Ordinal) -ge 0) -Message "Deleted test sources must be detected against the exact head before namespace selection."
Assert-True -Condition ($qualificationScript.IndexOf('$testNamespacesByPath[$normalizedPath] = [string[]]::new(0)', [StringComparison]::Ordinal) -ge 0) -Message "A deleted test source must restore full-project selection for the surviving owner."
Assert-True -Condition ($qualificationPlanScript.IndexOf('[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content)', [StringComparison]::Ordinal) -ge 0) -Message "Changed test class and namespace ownership must come from a Roslyn C# syntax tree, not a source-text regex."
Assert-True -Condition ($qualificationPlanScript.IndexOf('TestProjects = @(', [StringComparison]::Ordinal) -ge 0) -Message "Source ownership must support explicit downstream consumer closures."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationDirectXunitTestClasses -Path $normalizedPath -Content $content', [StringComparison]::Ordinal) -ge 0) -Message "Only syntax-authenticated filename-matching xUnit classes may retain class-filtered qualification."
Assert-True -Condition ($qualificationScript.IndexOf('Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $HeadCommit', [StringComparison]::Ordinal) -ge 0) -Message "A direct xUnit class used by another exact-head test source must restore full-project qualification."
$qualificationContractStart = $qualificationScript.IndexOf('if ($plan.RequiresVerifierContracts)', [StringComparison]::Ordinal)
$qualificationContractEnd = $qualificationScript.IndexOf('if ($plan.RequiresDrawioValidation)', $qualificationContractStart, [StringComparison]::Ordinal)
Assert-True -Condition ($qualificationContractStart -ge 0 -and $qualificationContractEnd -gt $qualificationContractStart) -Message "Qualification must retain one explicit verifier-contract scheduling block."
$qualificationContractBlock = $qualificationScript.Substring($qualificationContractStart, $qualificationContractEnd - $qualificationContractStart)
Assert-Equal -Actual ([regex]::Matches($qualificationContractBlock, 'Invoke-QualificationWave').Count) -Expected 1 -Message "Build, frontend, and every verifier contract must share one bounded dependency-safe scheduler wave."
Assert-True -Condition ($qualificationContractBlock.IndexOf('Add-QualificationPhase', [StringComparison]::Ordinal) -lt $qualificationContractBlock.IndexOf('Invoke-QualificationWave', [StringComparison]::Ordinal)) -Message "Qualification must enqueue every verifier contract before completing its first bounded wave."
Assert-Equal -Actual ([regex]::Matches($qualificationScript, 'Invoke-QualificationWave').Count) -Expected 3 -Message "Qualification must define one wave helper and invoke exactly two bounded work waves."
Assert-True -Condition ($qualificationScript.IndexOf('@("format", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006"', [StringComparison]::Ordinal) -ge 0) -Message "Changed-file qualification must check whitespace and IDE1006 in one dotnet format workspace load."
Assert-True -Condition ($qualificationScript.IndexOf('Add-QualificationPhase -Name "format-changed"', [StringComparison]::Ordinal) -ge 0) -Message "Changed-file formatting must remain an explicit bounded phase."
Assert-True -Condition ($qualificationScript.IndexOf('Invoke-QualificationWave', $qualificationScript.IndexOf('Add-QualificationPhase -Name "git-diff-check"', [StringComparison]::Ordinal), [StringComparison]::Ordinal) -ge 0) -Message "Tests, workflow validation, changed-file formatting, and diff-check must complete in the second bounded wave."
Assert-True -Condition ($qualificationScript.IndexOf('@("diff", "--check", "$mergeBase..$HeadCommit")', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must diff-check the exact selected range."
Assert-True -Condition ($qualificationScript.IndexOf('@("prettier", "--check", "--end-of-line", "auto", ".github/workflows/*.yml")', [StringComparison]::Ordinal) -ge 0) -Message "Workflow formatting must ignore checkout-only CRLF conversion while validating every workflow."
Assert-True -Condition ($watchdogScript.IndexOf('Test-VerificationDeadlineExceeded -ElapsedTicks $stopwatch.Elapsed.Ticks -DeadlineTicks $deadlineTicks', [StringComparison]::Ordinal) -ge 0) -Message "The running watchdog must use the tested inclusive deadline decision."
Assert-True -Condition ($watchdogScript.IndexOf('Stop-VerificationProcessTree $process', [StringComparison]::Ordinal) -ge 0) -Message "The watchdog must terminate the full verifier process tree."
Assert-True -Condition ($verifyScript.IndexOf('VERIFY_COMPLETE schema_version=1 status=passed', [StringComparison]::Ordinal) -ge 0) -Message "The verifier must emit an exact terminal marker only after successful completion."
Assert-True -Condition ($workflow.IndexOf('./scripts/verify-with-watchdog.ps1 -Configuration Release -DeadlineSeconds 900', [StringComparison]::Ordinal) -ge 0) -Message "Standard promotion must invoke the external watchdog with its explicit fifteen-minute certification bound."
Assert-True -Condition ($workflow.IndexOf('-SkipCoverage', [StringComparison]::Ordinal) -lt 0) -Message "Promotion verification must retain coverage collection and thresholds."
Assert-True -Condition ($workflow.IndexOf("github.event.pull_request.draft == false", [StringComparison]::Ordinal) -ge 0) -Message "Promotion verification must run only for a merge-candidate pull request or main."
Assert-True -Condition ($workflow.IndexOf("github.event.action != 'edited' || github.event.changes.base != null", [StringComparison]::Ordinal) -ge 0) -Message "Promotion verification must invalidate an edited edge only when its base changes."
Assert-True -Condition ($qualificationWorkflow.IndexOf('./scripts/verify-with-watchdog.ps1 -Qualification -BaseCommit "${{ github.event.pull_request.base.sha }}" -HeadCommit "${{ github.event.pull_request.head.sha }}" -Configuration Release -DeadlineSeconds 300', [StringComparison]::Ordinal) -ge 0) -Message "Draft qualification must bind the exact PR edge under a five-minute watchdog."
Assert-True -Condition ($qualificationWorkflow.IndexOf('ref: ${{ github.event.pull_request.head.sha }}', [StringComparison]::Ordinal) -ge 0) -Message "Qualification must execute the exact pull-request head rather than a synthetic merge checkout."
Assert-True -Condition ($qualificationWorkflow.IndexOf('edited,', [StringComparison]::Ordinal) -ge 0) -Message "Retargeting a pull request must create a new exact-edge qualification event."
Assert-True -Condition ($qualificationWorkflow.IndexOf("if: github.event.action != 'edited' || github.event.changes.base != null", [StringComparison]::Ordinal) -ge 0) -Message "Edited events must qualify only a changed pull-request base."
Assert-True -Condition ($qualificationWorkflow.IndexOf('cancel-in-progress: true', [StringComparison]::Ordinal) -ge 0) -Message "A superseded draft qualification must release its runner promptly."
Assert-True -Condition ($qualificationWorkflow.IndexOf('coverage.cobertura.xml', [StringComparison]::Ordinal) -lt 0) -Message "Qualification diagnostics must not imply that coverage was collected."
Assert-True -Condition ($workflow.IndexOf('run: ./scripts/verify.ps1 -Configuration Release', [StringComparison]::Ordinal) -lt 0) -Message "Standard CI must not bypass the external watchdog."

Write-Output "Verification watchdog contract tests passed ($assertionCount assertions)."
