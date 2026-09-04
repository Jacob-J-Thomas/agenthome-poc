using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeReconciliationFactoryTests
{
    [Fact]
    public async Task Effect_reconciliation_factory_rejects_a_durable_candidate_when_its_current_effect_is_missing()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, persistCase: false, retainRunBinding: true);
        DeleteEffectAttemptArtifacts(workspace.RootPath, seeded.Attempt.Payload.OperationId, seeded.Attempt.Payload.EffectGeneration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateEffectReconciliationRuntimeAsync(workspace, commandActionRuntimeProvider: seeded.RuntimeProvider));

        Assert.Contains("effect_reconciliation_recovery_failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RepairRequired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Effect_reconciliation_factory_keeps_the_read_surface_available_when_a_candidate_effect_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, persistCase: false, retainRunBinding: true);
        await File.WriteAllTextAsync(CurrentEffectAttemptPath(workspace.RootPath, seeded.Attempt.Payload.OperationId, seeded.Attempt.Payload.EffectGeneration, seeded.Attempt.ContentHash), "{}");

        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, commandActionRuntimeProvider: seeded.RuntimeProvider);

        var page = await runtime.EffectReconciliation.ListAsync();
        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Ready, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Effect_reconciliation_factory_keeps_the_read_surface_available_when_a_candidate_run_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, persistCase: false, retainRunBinding: true);
        var runPath = Assert.Single(Directory.EnumerateFiles(new WorkspacePaths(workspace.RootPath).CustomLoopRunsPath, "run-reconciliation-command.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(runPath, "{}");

        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, commandActionRuntimeProvider: seeded.RuntimeProvider);

        var page = await runtime.EffectReconciliation.ListAsync();
        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Ready, page.Status);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_maps_a_missing_current_effect_to_a_closed_not_found_assessment()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, retainRunBinding: true);
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider
        {
            OnCall = call =>
            {
                if (call == 1)
                {
                    DeleteEffectAttemptArtifacts(workspace.RootPath, seeded.Attempt.Payload.OperationId, seeded.Attempt.Payload.EffectGeneration);
                }
            },
        };
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.AssessAsync("assess-missing-current-effect", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_maps_a_corrupt_current_effect_to_a_closed_corrupt_assessment()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, retainRunBinding: true);
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider
        {
            OnCall = call =>
            {
                if (call == 1)
                {
                    File.WriteAllText(CurrentEffectAttemptPath(workspace.RootPath, seeded.Attempt.Payload.OperationId, seeded.Attempt.Payload.EffectGeneration, seeded.Attempt.ContentHash), "{}");
                }
            },
        };
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.AssessAsync("assess-corrupt-current-effect", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Corrupt, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_maps_current_effect_store_contention_to_unavailable()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        FileStream? mutationLease = null;
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider
        {
            OnCall = call =>
            {
                if (call == 1)
                {
                    mutationLease = new FileStream(
                        Path.Combine(new WorkspacePaths(workspace.RootPath).GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
            },
        };
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);

        try
        {
            var result = await runtime.EffectReconciliation.AssessAsync("assess-contended-current-effect", Reference(seeded.Case));

            Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, result.Status);
            Assert.Null(result.Detail);
        }
        finally
        {
            mutationLease?.Dispose();
        }
    }

    [Fact]
    public async Task Effect_reconciliation_facade_maps_an_absent_pinned_graph_store_to_unavailable()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider
        {
            OnCall = call =>
            {
                if (call == 1)
                {
                    Directory.Delete(new WorkspacePaths(workspace.RootPath).GovernedLoopRevisionsPath, recursive: true);
                }
            },
        };
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.AssessAsync("assess-missing-pinned-graph-store", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, result.Status);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Effect_reconciliation_assessment_rejects_runtime_input_that_no_longer_matches_the_exact_case(
        bool mismatchRunNodeBinding,
        bool mismatchCommandParameters)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(
            workspace,
            mismatchRunNodeBinding: mismatchRunNodeBinding,
            mismatchCommandParameters: mismatchCommandParameters);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(
            workspace,
            new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(),
            seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.AssessAsync(
            "assess-mismatched-runtime-input-" + mismatchRunNodeBinding.ToString().ToLowerInvariant() + mismatchCommandParameters.ToString().ToLowerInvariant(),
            Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_authority_survives_human_input_and_legacy_approval_factory_clones()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(workspace.RootPath, "factory-clones");
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider();
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithGovernedLoopEffectReconciliationAuthorizationProvider(provider)
            .WithHumanInputSupersedeCandidateRegistry(new HumanInputSupersedeCandidateRegistry())
            .WithoutLegacyCustomLoopToolApprovals();
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var result = await runtime.EffectReconciliation.AssessAsync("assess-after-factory-clones", Reference(seeded.Current));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, result.Status);
        Assert.Null(result.Detail);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void Effect_reconciliation_surface_pages_reject_a_null_detached_collection()
    {
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectReconciliationPage(GovernedLoopEffectReconciliationPageStatus.Ready, null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectReconciliationProbeCatalogPage(GovernedLoopEffectReconciliationProbeCatalogStatus.Ready, null!));
    }

    private static void DeleteEffectAttemptArtifacts(string workspaceRoot, string operationId, long effectGeneration)
    {
        var root = new WorkspacePaths(workspaceRoot).GovernedLoopEffectAttemptsPath;
        var key = EffectStorageKey(operationId, effectGeneration);
        foreach (var path in Directory.EnumerateFiles(root, key + ".*.json", SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }

        var head = Path.Combine(root, key + ".head");
        if (File.Exists(head))
        {
            File.Delete(head);
        }
    }

    private static string CurrentEffectAttemptPath(string workspaceRoot, string operationId, long effectGeneration, string contentHash)
    {
        var root = new WorkspacePaths(workspaceRoot).GovernedLoopEffectAttemptsPath;
        var key = EffectStorageKey(operationId, effectGeneration);
        var path = Path.Combine(root, key + "." + contentHash + ".json");
        Assert.True(File.Exists(path), "The seeded current effect-attempt artifact was not found.");
        return path;
    }

    private static string EffectStorageKey(string operationId, long effectGeneration)
    {
        var material = Encoding.UTF8.GetBytes($"embodysense.governed-loop-effect-attempt-storage.v1\n{operationId}\n{effectGeneration}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }
}
