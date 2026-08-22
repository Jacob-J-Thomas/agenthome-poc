using EmbodySense.Core.Application.Governance.Authority.Delegation;
using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Delegation;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationConcurrencyAndFailureTests
{
    [Fact]
    public async Task CreateAsync_PropagatesCallerCancellationBeforeConclusiveResult()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreateService().CreateAsync(harness.Request, cancellation.Token));

        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_ReturnsCreatedWhenHostileTransactionCompletesBeforeCallerCancellation()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();
        using var cancellation = new CancellationTokenSource();
        harness.TransactionCallback = (operation, _) => operation(CancellationToken.None);

        var result = await service.CreateAsync(harness.Request, cancellation.Token);

        cancellation.Cancel();
        var replay = await service.CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, result.Status);
        Assert.NotNull(result.Envelope);
        Assert.Equal(AuthorityDelegationServiceStatus.Replayed, replay.Status);
        Assert.Equal(result.Envelope, replay.Envelope);
        Assert.Equal(1, harness.GrantCount);
        Assert.Equal(1, harness.OriginCount);
        Assert.Equal(1, harness.TargetCount);
        Assert.Equal(1, harness.CompletionCount);
        Assert.Equal(1, harness.TransactionCount);
    }

    [Fact]
    public async Task CreateAsync_MapsThrowingGrantPortToUnavailable()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.GrantCallback = (_, _) => throw new InvalidOperationException("secret-canary-grant");

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Equal("unavailable", result.ReasonCode);
        Assert.DoesNotContain("secret-canary", result.ReasonCode, StringComparison.Ordinal);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsNullTargetResultToAmbiguousWithoutLeakingDetails()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TargetCallback = (_, _) => Task.FromResult<AuthorityDelegationTargetResolution>(null!);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Equal("ambiguous", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsNullGrantResultToAmbiguous()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.GrantCallback = (_, _) => Task.FromResult<EmbodySense.Core.Application.Governance.Authority.Grants.Models.AuthorityGrantResolution>(null!);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsNullOriginResultToAmbiguous()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.CreateOriginCallback = (_, _) => Task.FromResult<AuthorityDelegationOriginResolution>(null!);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsThrowingOriginPortToUnavailable()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.CreateOriginCallback = (_, _) => throw new InvalidOperationException("secret-canary-origin");

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Equal("unavailable", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsThrowingTargetPortToUnavailable()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TargetCallback = (_, _) => throw new InvalidOperationException("secret-canary-target");

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsNullCompletionResultToAmbiguous()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.CompletionCallback = _ => Task.FromResult<AuthorityDelegationCompletionResolution>(null!);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_MapsThrowingTransactionToUnavailable()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TransactionCallback = (_, _) => throw new InvalidOperationException("secret-canary-transaction");

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_MapsThrowingTrustedClockToUnavailableWithoutSourceReads()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.Time.Throw = true;

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Equal(["transaction"], harness.Calls);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task RevalidateAsync_MapsThrowingCompletionPortToUnavailable()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(
            (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.CompletionCallback = _ => throw new InvalidOperationException("secret-canary-completion");

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task RevalidateAsync_PropagatesCancellationRaisedByPortBeforeResult()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(
            (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        using var cancellation = new CancellationTokenSource();
        harness.CompletionCallback = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new AuthorityDelegationCompletionResolution(AuthorityDelegationCompletionStatus.Active));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreateService().RevalidateAsync(harness.UseRequest(envelope), cancellation.Token));
    }

    [Fact]
    public async Task CreateAsync_PropagatesCallerCancellationWhenHostileTransactionDropsTheToken()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        harness.GrantCallback = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(harness.GrantResolution);
        };
        harness.TransactionCallback = (operation, _) => operation(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreateService().CreateAsync(harness.Request, cancellation.Token));

        Assert.Equal(1, harness.GrantCount);
        Assert.Equal(0, harness.OriginCount);
        Assert.Equal(0, harness.TargetCount);
        Assert.Equal(0, harness.CompletionCount);
    }

    [Fact]
    public async Task CreateAsync_PropagatesCancellationAfterTargetStartedAndRetriesAsCreated()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();
        using var cancellation = new CancellationTokenSource();
        var targetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.TargetCallback = async (_, _) =>
        {
            targetStarted.TrySetResult();
            await releaseTarget.Task;
            return harness.TargetResolution;
        };
        harness.TransactionCallback = async (operation, _) =>
        {
            try
            {
                return await operation(CancellationToken.None);
            }
            finally
            {
                transactionFinished.TrySetResult();
            }
        };

        var creation = service.CreateAsync(harness.Request, cancellation.Token);
        await targetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        releaseTarget.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        await transactionFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await service.CreateAsync(harness.Request);
        if (retry.Status == AuthorityDelegationServiceStatus.Unavailable)
        {
            retry = await service.CreateAsync(harness.Request);
        }

        Assert.Equal(AuthorityDelegationServiceStatus.Created, retry.Status);
        Assert.NotNull(retry.Envelope);
        Assert.Equal(2, harness.GrantCount);
        Assert.Equal(2, harness.OriginCount);
        Assert.Equal(2, harness.TargetCount);
        Assert.Equal(1, harness.CompletionCount);
        Assert.Equal(2, harness.TransactionCount);
    }

    [Fact]
    public async Task CreateAsync_DoesNotPublishWhenCallerCancellationWinsBeforeHostileTransactionCommit()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var service = harness.CreateService();
        using var cancellation = new CancellationTokenSource();
        var transactionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.TransactionCallback = async (operation, _) =>
        {
            transactionEntered.TrySetResult();
            try
            {
                await releaseTransaction.Task;
                return await operation(CancellationToken.None);
            }
            finally
            {
                transactionFinished.TrySetResult();
            }
        };

        var creation = service.CreateAsync(harness.Request, cancellation.Token);
        await transactionEntered.Task;
        cancellation.Cancel();
        releaseTransaction.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => creation);
        await transactionFinished.Task;

        harness.TransactionCallback = null;
        var retry = await service.CreateAsync(harness.Request);
        if (retry.Status == AuthorityDelegationServiceStatus.Unavailable)
        {
            retry = await service.CreateAsync(harness.Request);
        }

        Assert.Equal(AuthorityDelegationServiceStatus.Created, retry.Status);
        Assert.NotNull(retry.Envelope);
        Assert.Equal(1, harness.GrantCount);
        Assert.Equal(1, harness.OriginCount);
        Assert.Equal(1, harness.TargetCount);
        Assert.Equal(1, harness.CompletionCount);
        Assert.Equal(2, harness.TransactionCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsTransactionThatSkipsCallbackAndFabricatesCreatedResult()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TransactionCallback = (_, _) => Task.FromResult(new AuthorityDelegationServiceResult(
            AuthorityDelegationServiceStatus.Created,
            null,
            "created"));

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction"], harness.Calls);
    }

    [Fact]
    public async Task CreateAsync_RejectsTransactionThatReturnsDifferentResultThanCallback()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TransactionCallback = async (operation, token) =>
        {
            var authentic = await operation(token);
            return authentic with { ReasonCode = string.Concat(authentic.ReasonCode) };
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_RejectsTransactionThatInvokesCallbackTwice()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TransactionCallback = async (operation, token) =>
        {
            var first = await operation(token);
            _ = await operation(token);
            return first;
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(1, harness.GrantCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsTransactionThatReturnsNullAfterCallback()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TransactionCallback = async (operation, token) =>
        {
            _ = await operation(token);
            return null!;
        };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
    }
}
