using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityAuthorityTransactionTests
{
    [Fact]
    public async Task Null_lock_session_releases_process_gate_for_reuse()
    {
        using var workspace = new TestWorkspace();
        var provider = new DelegateCapabilityAuthorityLockSessionProvider((attempt, _) => Task.FromResult<IAsyncDisposable?>(attempt == 1 ? null : new TestCapabilityAuthorityLockSession()));
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath), lockSessionProvider: provider);

        await Assert.ThrowsAsync<IOException>(() => transaction.AcquireValidatedLeaseAsync(_ => Task.FromResult(true)));

        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Throwing_lock_session_acquisition_releases_process_gate_for_reuse()
    {
        using var workspace = new TestWorkspace();
        var provider = new DelegateCapabilityAuthorityLockSessionProvider((attempt, _) => attempt == 1 ? Task.FromException<IAsyncDisposable?>(new IOException("Injected acquisition failure.")) : Task.FromResult<IAsyncDisposable?>(new TestCapabilityAuthorityLockSession()));
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath), lockSessionProvider: provider);

        await Assert.ThrowsAsync<IOException>(() => transaction.AcquireValidatedLeaseAsync(_ => Task.FromResult(true)));

        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Cancelled_lock_session_acquisition_releases_process_gate_for_reuse()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var provider = new DelegateCapabilityAuthorityLockSessionProvider((attempt, token) =>
        {
            if (attempt != 1)
            {
                return Task.FromResult<IAsyncDisposable?>(new TestCapabilityAuthorityLockSession());
            }

            cancellation.Cancel();
            return Task.FromCanceled<IAsyncDisposable?>(token);
        });
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath), lockSessionProvider: provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.AcquireValidatedLeaseAsync(_ => Task.FromResult(true), cancellation.Token));

        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Rejected_validation_releases_process_gate_even_when_session_disposal_throws()
    {
        using var workspace = new TestWorkspace();
        var failingSession = new TestCapabilityAuthorityLockSession(throwOnDispose: true);
        var provider = new DelegateCapabilityAuthorityLockSessionProvider((attempt, _) => Task.FromResult<IAsyncDisposable?>(attempt == 1 ? failingSession : new TestCapabilityAuthorityLockSession()));
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath), lockSessionProvider: provider);

        await Assert.ThrowsAsync<IOException>(() => transaction.AcquireValidatedLeaseAsync(_ => Task.FromResult(false)));

        Assert.Equal(1, failingSession.DisposeAttempts);
        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Retained_lease_releases_process_gate_exactly_once_even_when_session_disposal_throws()
    {
        using var workspace = new TestWorkspace();
        var failingSession = new TestCapabilityAuthorityLockSession(throwOnDispose: true);
        var provider = new DelegateCapabilityAuthorityLockSessionProvider((attempt, _) => Task.FromResult<IAsyncDisposable?>(attempt == 1 ? failingSession : new TestCapabilityAuthorityLockSession()));
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath), lockSessionProvider: provider);
        var lease = await transaction.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));

        await Assert.ThrowsAsync<IOException>(() => lease!.DisposeAsync().AsTask());
        await lease!.DisposeAsync();

        Assert.Equal(1, failingSession.DisposeAttempts);
        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Concurrent_lease_disposal_cannot_release_authority_before_owned_session_disposal_completes()
    {
        using var workspace = new TestWorkspace();
        var blockingSession = new TestCapabilityAuthorityLockSession(blockOnDispose: true);
        var provider = new DelegateCapabilityAuthorityLockSessionProvider((attempt, _) => Task.FromResult<IAsyncDisposable?>(attempt == 1 ? blockingSession : new TestCapabilityAuthorityLockSession()));
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath), lockSessionProvider: provider);
        var lease = await transaction.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));
        var firstDisposal = lease!.DisposeAsync().AsTask();
        await blockingSession.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await lease.DisposeAsync();
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingOperation = transaction.ExecuteAsync(_ =>
        {
            operationEntered.TrySetResult();
            return Task.FromResult(true);
        });

        Assert.False(firstDisposal.IsCompleted);
        Assert.False(operationEntered.Task.IsCompleted);
        blockingSession.ReleaseDisposal.TrySetResult();

        await firstDisposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(await waitingOperation.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, blockingSession.DisposeAttempts);
    }

    [Fact]
    public async Task Throwing_validation_releases_process_gate_for_reuse()
    {
        using var workspace = new TestWorkspace();
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.AcquireValidatedLeaseAsync(_ => Task.FromException<bool>(new InvalidOperationException("Injected validation failure."))));

        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Cancelled_validation_releases_process_gate_for_reuse()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var transaction = new CapabilityAuthorityTransaction(new WorkspacePaths(workspace.RootPath));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.AcquireValidatedLeaseAsync(token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(token);
        }, cancellation.Token));

        Assert.True(await transaction.ExecuteAsync(_ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Same_workspace_instances_are_reentrant_and_retained_validation_serializes_other_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new CapabilityAuthorityTransaction(paths);
        var second = new CapabilityAuthorityTransaction(paths);

        var nested = await first.ExecuteAsync(_ => second.ExecuteAsync(_ => Task.FromResult("nested")));
        var rejected = await first.AcquireValidatedLeaseAsync(_ => Task.FromResult(false));
        var retained = await first.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));
        Assert.NotNull(retained);
        var probe = new ProbingCapabilityAuthorityTransaction(second);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = Task.Run(() => probe.ExecuteAsync(_ =>
        {
            entered.TrySetResult();
            return Task.FromResult(true);
        }));

        await probe.Attempted.Task;
        Assert.False(entered.Task.IsCompleted);
        await retained!.DisposeAsync();

        Assert.Equal("nested", nested);
        Assert.Null(rejected);
        Assert.True(await waiting);
        Assert.True(entered.Task.IsCompleted);
    }

    [Fact]
    public async Task Nested_validated_operation_borrows_the_active_fence_without_creating_an_escaping_lease()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var outer = new CapabilityAuthorityTransaction(paths);
        var nested = new CapabilityAuthorityTransaction(paths);
        var validatorCalls = 0;
        var operationCalls = 0;

        var accepted = await outer.ExecuteAsync(_ => nested.ExecuteWithValidatedAuthorityAsync(
            _ => Task.FromResult(Interlocked.Increment(ref validatorCalls) == 1),
            _ => Task.FromResult(Interlocked.Increment(ref operationCalls).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var rejected = await outer.ExecuteAsync(_ => nested.ExecuteWithValidatedAuthorityAsync(
            _ => Task.FromResult(false),
            _ => Task.FromResult("must-not-run")));

        Assert.Equal("1", accepted);
        Assert.Null(rejected);
        Assert.Equal(1, validatorCalls);
        Assert.Equal(1, operationCalls);
    }

    [Fact]
    public async Task Child_execution_context_cannot_reuse_an_invalidated_outer_frame()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new CapabilityAuthorityTransaction(paths);
        var second = new CapabilityAuthorityTransaction(paths);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;

        await first.ExecuteAsync(_ =>
        {
            child = Task.Run(async () =>
            {
                await releaseChild.Task;
                await second.ExecuteAsync(_ =>
                {
                    childEntered.TrySetResult();
                    return Task.FromResult(true);
                });
            });
            return Task.FromResult(true);
        });

        await using var retained = await first.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));
        releaseChild.TrySetResult();
        var early = await Task.WhenAny(childEntered.Task, Task.Delay(150));
        Assert.NotSame(childEntered.Task, early);
        await retained!.DisposeAsync();
        await child!;
        Assert.True(childEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task Catalog_and_activation_writers_cannot_commit_after_final_observation_until_the_authority_fence_releases()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new CapabilityAuthorityTransaction(paths);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var staged = CapabilityArtifactStoreTestData.Stage("authority-artifact"u8.ToArray());
        var artifacts = new CapabilityArtifactStore(paths, artifactTrust, new TestCapabilityArtifactTrustVerifier(), authorityTransaction: authority);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.StageAsync(staged)).Status);
        var catalogProbe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var artifactProbe = new ProbingCapabilityAuthorityTransaction(new CapabilityAuthorityTransaction(paths));
        var catalog = new CapabilityCatalogStore(paths, new TestCapabilityLifecycleTrustProvider(), authorityTransaction: catalogProbe);
        var activation = new CapabilityArtifactStore(paths, artifactTrust, new TestCapabilityArtifactTrustVerifier(), authorityTransaction: artifactProbe);
        var descriptor = CapabilityCatalogTestData.Descriptor();
        var retained = await authority.AcquireValidatedLeaseAsync(_ => Task.FromResult(true));

        var catalogWriter = Task.Run(() => catalog.MutateAsync(new CapabilityCatalogMutation(CapabilityCatalogMutationKind.Declare, "fenced-catalog", 0, descriptor.Id, descriptor)));
        var artifactWriter = Task.Run(() => activation.ActivateAsync(new CapabilityArtifactActivationRequest(staged.Manifest, 0, "fenced-activation")));
        await Task.WhenAll(catalogProbe.Attempted.Task, artifactProbe.Attempted.Task);

        Assert.False(catalogWriter.IsCompleted);
        Assert.False(artifactWriter.IsCompleted);
        await retained!.DisposeAsync();
        var catalogResult = await catalogWriter;
        var artifactResult = await artifactWriter;

        Assert.Equal(CapabilityCatalogMutationStatus.Applied, catalogResult.Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, artifactResult.Status);
    }
}
