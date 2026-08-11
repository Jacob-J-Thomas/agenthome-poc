using System.Diagnostics;
using System.Text;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Tests.Loops.Execution.Authority;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopEffectAuthorityEvidenceStoreTests
{
    private const string CrossProcessMode = "EMBODYSENSE_EFFECT_AUTHORITY_STORE_MODE";
    private const string CrossProcessWorkspace = "EMBODYSENSE_EFFECT_AUTHORITY_STORE_WORKSPACE";
    private const string CrossProcessTrustRoot = "EMBODYSENSE_EFFECT_AUTHORITY_STORE_TRUST_ROOT";
    private const string CrossProcessGate = "EMBODYSENSE_EFFECT_AUTHORITY_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_EFFECT_AUTHORITY_STORE_READY";
    private const string CrossProcessOperation = "EMBODYSENSE_EFFECT_AUTHORITY_STORE_OPERATION";

    [Fact]
    public async Task Append_restart_and_exact_replay_preserve_one_authenticated_immutable_decision()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var decision = Decision("effect-operation-one");
        var firstTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);

        var appended = await Store(paths, firstTrust).AppendAsync(decision);
        var replayed = await Store(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath)).AppendAsync(decision);
        var primary = PrimaryPath(paths);
        var content = await File.ReadAllTextAsync(primary);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, appended.Status);
        Assert.Equal(decision.ContentHash, appended.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
        Assert.Contains("\"effectOperationId\": \"effect-operation-one\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", content, StringComparison.OrdinalIgnoreCase);
        Assert.False(content.StartsWith('\ufeff'));
        Assert.True(File.Exists(ProofPath(paths)));
    }

    [Fact]
    public async Task Same_effect_identity_with_any_different_coordinates_conflicts_without_replacement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var original = Decision("effect-operation-one");
        var changed = Rehash(original with
        {
            RunId = "run-two",
            CorrelationId = "provider-request-two"
        });
        var store = Store(paths, trust);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await store.AppendAsync(original)).Status);
        var conflict = await store.AppendAsync(changed);
        var replay = await store.AppendAsync(original);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Conflict, conflict.Status);
        Assert.Equal(original.ContentHash, conflict.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replay.Status);
        Assert.Equal(original.ContentHash, replay.ContentHash);
        Assert.DoesNotContain("run-two", await File.ReadAllTextAsync(PrimaryPath(paths)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_exact_writers_append_once_and_replay_once()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var decision = Decision("effect-operation-one");

        var results = await Task.WhenAll(Store(paths, trust).AppendAsync(decision), Store(paths, trust).AppendAsync(decision));

        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended);
        Assert.Single(results, result => result.Status == GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent);
        Assert.All(results, result => Assert.Equal(decision.ContentHash, result.ContentHash));
    }

    [Fact]
    public async Task Distinct_effects_append_without_rewriting_prior_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var first = Decision("effect-operation-one");
        var second = Decision("effect-operation-two");
        var store = Store(paths, trust);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await store.AppendAsync(first)).Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await store.AppendAsync(second)).Status);
        var firstReplay = await Store(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath))
            .AppendAsync(first);
        var content = await File.ReadAllTextAsync(PrimaryPath(paths));

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, firstReplay.Status);
        Assert.Equal(first.ContentHash, firstReplay.ContentHash);
        Assert.Contains("effect-operation-one", content, StringComparison.Ordinal);
        Assert.Contains("effect-operation-two", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_decision_and_exhausted_quotas_fail_before_new_evidence()
    {
        using var invalidWorkspace = new TestWorkspace();
        var invalidPaths = new WorkspacePaths(invalidWorkspace.RootPath);
        var invalid = Decision("effect-operation-one") with { ContentHash = new string('f', 64) };
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await Store(invalidPaths, new TestCapabilityLifecycleTrustProvider()).AppendAsync(invalid)).Status);
        Assert.False(File.Exists(PrimaryPath(invalidPaths)));

        using var quotaWorkspace = new TestWorkspace();
        var quotaPaths = new WorkspacePaths(quotaWorkspace.RootPath);
        var quotaStore = Store(
            quotaPaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxDecisions = 1 });
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            (await quotaStore.AppendAsync(Decision("effect-operation-one"))).Status);
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await quotaStore.AppendAsync(Decision("effect-operation-two"))).Status);
        Assert.DoesNotContain("effect-operation-two", await File.ReadAllTextAsync(PrimaryPath(quotaPaths)), StringComparison.Ordinal);

        using var byteWorkspace = new TestWorkspace();
        var bytePaths = new WorkspacePaths(byteWorkspace.RootPath);
        var byteStore = Store(
            bytePaths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxArtifactUtf8Bytes = 1 });
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await byteStore.AppendAsync(Decision("effect-operation-one"))).Status);
        Assert.False(File.Exists(PrimaryPath(bytePaths)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("enum-case")]
    [InlineData("authentication")]
    [InlineData("schema")]
    [InlineData("bom")]
    [InlineData("invalid-utf8")]
    public async Task Corrupt_or_noncanonical_authenticated_ledger_is_quarantined_as_ambiguous(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var original = Decision("effect-operation-one");
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, (await Store(paths, trust).AppendAsync(original)).Status);
        var primary = PrimaryPath(paths);
        var text = await File.ReadAllTextAsync(primary);
        switch (corruption)
        {
            case "unknown":
                await File.WriteAllTextAsync(primary, text.Replace("\"workspaceIdentity\":", "\"unknown\": true,\n  \"workspaceIdentity\":", StringComparison.Ordinal));
                break;
            case "duplicate":
                await File.WriteAllTextAsync(primary, text.Replace("\"generation\": 1", "\"generation\": 1,\n  \"generation\": 1", StringComparison.Ordinal));
                break;
            case "enum-case":
                await File.WriteAllTextAsync(primary, text.Replace("\"provider-transport\"", "\"ProviderTransport\"", StringComparison.Ordinal));
                break;
            case "authentication":
                var tagIndex = text.IndexOf("\"authenticationTag\": \"test:", StringComparison.Ordinal);
                Assert.True(tagIndex >= 0);
                var tagCharacter = tagIndex + "\"authenticationTag\": \"test:".Length;
                var replacement = text[tagCharacter] == 'a' ? 'b' : 'a';
                await File.WriteAllTextAsync(primary, text[..tagCharacter] + replacement + text[(tagCharacter + 1)..]);
                break;
            case "schema":
                await File.WriteAllTextAsync(primary, text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
                break;
            case "bom":
                await File.WriteAllBytesAsync(primary, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)]);
                break;
            case "invalid-utf8":
                await File.WriteAllBytesAsync(primary, [0xff, 0xfe, 0xfd]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var exact = await Store(paths, trust).AppendAsync(original);
        var unrelated = await Store(paths, trust).AppendAsync(Decision("effect-operation-two"));

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, exact.Status);
        Assert.Null(exact.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, unrelated.Status);
        Assert.Null(unrelated.ContentHash);
        Assert.DoesNotContain("effect-operation-two", await File.ReadAllTextAsync(primary), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_pending_successor_is_finalized_before_an_unrelated_effect_appends()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var first = Decision("effect-operation-one");
        var second = Decision("effect-operation-two");
        var interrupted = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) => boundary == GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished
                ? ValueTask.FromException(new IOException("Injected process loss after primary publication."))
                : ValueTask.CompletedTask
        };

        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous,
            (await Store(paths, trust, interrupted).AppendAsync(first)).Status);
        var unrelated = await Store(paths, trust).AppendAsync(second);
        var firstReplay = await Store(paths, trust).AppendAsync(first);
        var secondReplay = await Store(paths, trust).AppendAsync(second);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, unrelated.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, firstReplay.Status);
        Assert.Equal(first.ContentHash, firstReplay.ContentHash);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, secondReplay.Status);
        Assert.Equal(second.ContentHash, secondReplay.ContentHash);
    }

    [Fact]
    public async Task Copied_evidence_and_symlinked_paths_never_gain_authority()
    {
        using var source = new TestWorkspace();
        using var destination = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            (await Store(sourcePaths, trust).AppendAsync(Decision("effect-operation-one"))).Status);
        CopyDirectory(sourcePaths.AgentPath, destinationPaths.AgentPath);

        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await Store(destinationPaths, trust).AppendAsync(Decision("effect-operation-one"))).Status);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var linked = new TestWorkspace();
        using var outside = new TestWorkspace();
        var linkedPaths = new WorkspacePaths(linked.RootPath);
        Directory.CreateDirectory(linkedPaths.AgentPath);
        Directory.CreateSymbolicLink(Path.Combine(linkedPaths.AgentPath, "loops"), outside.RootPath);
        Assert.Equal(
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable,
            (await Store(linkedPaths, new TestCapabilityLifecycleTrustProvider()).AppendAsync(Decision("effect-operation-one"))).Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Shared_authority_transaction_is_reentrant_and_release_failure_preserves_completed_result()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var transaction = new CapabilityAuthorityTransaction(paths);
        var decision = Decision("effect-operation-one");
        var nested = new GovernedLoopEffectAuthorityEvidenceStore(paths, trust, authorityTransaction: transaction);

        var appended = await transaction.ExecuteAsync(token => nested.AppendAsync(decision, token));
        var releasing = new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            trust,
            authorityTransaction: new ThrowAfterEffectAuthorityCallbackTransaction());
        var replayed = await releasing.AppendAsync(decision);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, appended.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable, GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)]
    [InlineData(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)]
    public async Task Durable_boundary_failure_returns_conservative_posture_and_exact_retry_recovers(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        GovernedLoopEffectAuthorityEvidenceStoreStatus interruptedStatus,
        GovernedLoopEffectAuthorityEvidenceStoreStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var decision = Decision("effect-operation-one");
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

        var interrupted = await Store(paths, trust, options).AppendAsync(decision);
        var retried = await Store(paths, trust).AppendAsync(decision);
        var replayed = await Store(paths, trust).AppendAsync(decision);

        Assert.Equal(interruptedStatus, interrupted.Status);
        Assert.Equal(retryStatus, retried.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
    }

    [Fact]
    public async Task Cancellation_propagates_before_durable_intent_and_becomes_ambiguous_after_proof()
    {
        using var beforeWorkspace = new TestWorkspace();
        var beforePaths = new WorkspacePaths(beforeWorkspace.RootPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(
                beforePaths,
                new TestCapabilityLifecycleTrustProvider())
            .AppendAsync(Decision("effect-operation-one"), new CancellationToken(canceled: true)));
        Assert.False(File.Exists(PrimaryPath(beforePaths)));

        using var afterWorkspace = new TestWorkspace();
        var afterPaths = new WorkspacePaths(afterWorkspace.RootPath);
        var cancellation = new CancellationTokenSource();
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                if (boundary == GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished)
                {
                    cancellation.Cancel();
                }

                return ValueTask.CompletedTask;
            }
        };
        var result = await Store(afterPaths, new TestCapabilityLifecycleTrustProvider(), options)
            .AppendAsync(Decision("effect-operation-one"), cancellation.Token);

        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, result.Status);
        Assert.Null(result.ContentHash);
    }

    [Theory]
    [InlineData("crash-proof")]
    [InlineData("crash-primary")]
    [InlineData("crash-trust")]
    public async Task Abrupt_process_loss_at_each_durable_boundary_recovers_exactly_once(string mode)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var gate = workspace.File("gate");
        var ready = workspace.File("ready");
        using var process = StartCrossProcessHost(
            mode,
            workspace.RootPath,
            trustRoot.RootPath,
            gate,
            ready,
            "effect-operation-one");
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);

        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var decision = Decision("effect-operation-one");
        var recovered = await Store(paths, trust).AppendAsync(decision);
        var replayed = await Store(paths, new FileCapabilityCatalogTrustProvider(trustRoot.RootPath)).AppendAsync(decision);

        Assert.Equal(
            mode == "crash-proof"
                ? GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                : GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
            recovered.Status);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, replayed.Status);
        Assert.Equal(decision.ContentHash, replayed.ContentHash);
    }

    [Fact]
    public async Task Cross_process_effect_authority_store_host()
    {
        var mode = Environment.GetEnvironmentVariable(CrossProcessMode);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        var workspace = Environment.GetEnvironmentVariable(CrossProcessWorkspace)!;
        var trustRoot = Environment.GetEnvironmentVariable(CrossProcessTrustRoot)!;
        var gate = Environment.GetEnvironmentVariable(CrossProcessGate)!;
        var ready = Environment.GetEnvironmentVariable(CrossProcessReady)!;
        var operation = Environment.GetEnvironmentVariable(CrossProcessOperation)!;
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForPathAsync(gate);
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                var target = mode switch
                {
                    "crash-proof" => GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished,
                    "crash-primary" => GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished,
                    "crash-trust" => GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
                if (boundary == target)
                {
                    Process.GetCurrentProcess().Kill();
                    Thread.Sleep(Timeout.Infinite);
                }

                return ValueTask.CompletedTask;
            }
        };
        var paths = new WorkspacePaths(workspace);
        var store = new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new FileCapabilityCatalogTrustProvider(trustRoot),
            options);
        _ = await store.AppendAsync(Decision(operation));
    }

    [Fact]
    public void Constructor_rejects_invalid_bounds_and_overlapping_trust()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectAuthorityEvidenceStore(null!));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopEffectAuthorityEvidenceStore(paths, (ICapabilityCatalogTrustProvider)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions { MaxDecisions = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(),
            new GovernedLoopEffectAuthorityEvidenceStoreOptions
            {
                MaxArtifactUtf8Bytes = GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumArtifactUtf8Bytes + 1
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new TestCapabilityLifecycleTrustProvider(0)));
        Assert.Throws<InvalidOperationException>(() => new GovernedLoopEffectAuthorityEvidenceStore(
            paths,
            new FileCapabilityCatalogTrustProvider(Path.Combine(paths.AgentPath, "server-trust"))));
    }

    private static GovernedLoopEffectAuthorityEvidenceStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        GovernedLoopEffectAuthorityEvidenceStoreOptions? options = null)
        => new(paths, trust, options);

    private static GovernedLoopEffectAuthorityDecision Decision(string effectOperationId)
    {
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision();
        return Rehash(decision with { EffectOperationId = effectOperationId });
    }

    private static GovernedLoopEffectAuthorityDecision Rehash(GovernedLoopEffectAuthorityDecision decision)
        => GovernedLoopEffectAuthorityContractHash.Apply(decision with { ContentHash = string.Empty });

    private static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "effect-authority", "decisions.json");

    private static string ProofPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "effect-authority", "decisions.proved.json");

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static Process StartCrossProcessHost(
        string mode,
        string workspace,
        string trustRoot,
        string gate,
        string ready,
        string operation)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(GovernedLoopEffectAuthorityEvidenceStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Loops.Execution.Authority.GovernedLoopEffectAuthorityEvidenceStoreTests.Cross_process_effect_authority_store_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessMode] = mode;
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessTrustRoot] = trustRoot;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOperation] = operation;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process effect-authority evidence-store host did not start.");
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(15), $"Cross-process effect-authority store host did not publish `{path}`.");
            await Task.Delay(10);
        }
    }
}
