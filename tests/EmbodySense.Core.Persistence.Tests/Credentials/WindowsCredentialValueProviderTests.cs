using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

public sealed class WindowsCredentialValueProviderTests
{
    private const string ChildModeVariable = "EMBODYSENSE_CREDENTIAL_PROVIDER_CHILD";
    private const string ExternalProcessMode = "external-value";
    private const string MutexContentionIdVariable = "EMBODYSENSE_CREDENTIAL_MUTEX_CONTENTION_ID";
    private const string MutexContentionMode = "mutex-contention";

    [Fact]
    public async Task Public_provider_round_trips_replaces_checks_health_and_deletes_without_workspace_artifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var provider = new WindowsCredentialValueProvider();
        var identity = "workspace-provider-" + Guid.NewGuid().ToString("N");
        var requests = Requests(identity, "credential-" + Guid.NewGuid().ToString("N"));
        var original = Encoding.UTF8.GetBytes("credential-canary-" + Guid.NewGuid().ToString("N"));
        var replacement = Encoding.UTF8.GetBytes("replacement-canary-" + Guid.NewGuid().ToString("N"));
        await provider.DeleteAsync(requests.Delete, CancellationToken.None);

        try
        {
            var create = await provider.CreateAsync(requests.Mutation with { ValueByteLength = original.Length }, destination => Copy(original, destination), CancellationToken.None);
            var health = await provider.GetHealthAsync(requests.Use, CancellationToken.None);
            var firstConsumer = new RecordingCredentialConsumer();
            var use = await provider.UseAsync(requests.Use, firstConsumer, CancellationToken.None);
            var replace = await provider.ReplaceAsync(requests.Mutation with { ValueByteLength = replacement.Length }, destination => Copy(replacement, destination), CancellationToken.None);
            var secondConsumer = new RecordingCredentialConsumer();
            await provider.UseAsync(requests.Use, secondConsumer, CancellationToken.None);
            var delete = await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            var missing = await provider.GetHealthAsync(requests.Use, CancellationToken.None);

            Assert.True(create.Succeeded);
            Assert.Equal(CredentialProviderHealthStatus.Available, health.Status);
            Assert.True(use.Succeeded);
            Assert.Equal(original, firstConsumer.Observed);
            Assert.True(replace.Succeeded);
            Assert.Equal(replacement, secondConsumer.Observed);
            Assert.True(delete.Succeeded);
            Assert.Equal(CredentialProviderHealthStatus.Missing, missing.Status);
            Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.RootPath));
        }
        finally
        {
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(replacement);
        }
    }

    [Fact]
    public async Task Public_provider_fails_closed_for_real_missing_conflicting_callback_limit_and_cancelled_operations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-fail-closed-" + Guid.NewGuid().ToString("N"), "credential-fail-closed-" + Guid.NewGuid().ToString("N"));
        var value = new byte[16];
        RandomNumberGenerator.Fill(value);
        Assert.True(CredentialProviderId.TryParse("org.embodysense.other", out var otherProvider, out _));

        try
        {
            var invalid = await provider.CreateAsync(requests.Mutation with { ProviderId = otherProvider! }, _ => throw new InvalidOperationException("must-not-run"), CancellationToken.None);
            var missingReplace = await provider.ReplaceAsync(requests.Mutation, destination => Copy(value, destination), CancellationToken.None);
            var created = await provider.CreateAsync(requests.Mutation, destination => Copy(value, destination), CancellationToken.None);
            var conflict = await provider.CreateAsync(requests.Mutation, destination => Copy(value, destination), CancellationToken.None);
            var callbackFailure = await provider.ReplaceAsync(requests.Mutation, _ => throw new InvalidOperationException("hostile callback detail"), CancellationToken.None);
            var wrongLength = await provider.ReplaceAsync(requests.Mutation, _ => 0, CancellationToken.None);
            var limited = await provider.ReplaceAsync(requests.Mutation with { ValueByteLength = 2_561 }, _ => throw new InvalidOperationException("must-not-run"), CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledUse = await provider.UseAsync(requests.Use, new RecordingCredentialConsumer(), cancelled.Token);
            var cancelledDelete = await provider.DeleteAsync(requests.Delete, cancelled.Token);
            var cancelledHealth = await provider.GetHealthAsync(requests.Use, cancelled.Token);

            Assert.Equal(CredentialFailureCode.InvalidRequest, invalid.Failure?.Code);
            Assert.Equal(CredentialFailureCode.NotFound, missingReplace.Failure?.Code);
            Assert.True(created.Succeeded);
            Assert.Equal(CredentialFailureCode.Conflict, conflict.Failure?.Code);
            Assert.Equal(CredentialFailureCode.CallbackFailed, callbackFailure.Failure?.Code);
            Assert.Equal(CredentialFailureCode.CallbackFailed, wrongLength.Failure?.Code);
            Assert.Equal(CredentialFailureCode.LimitExceeded, limited.Failure?.Code);
            Assert.Equal(CredentialFailureCode.Unavailable, cancelledUse.Failure?.Code);
            Assert.Equal(CredentialFailureCode.Unavailable, cancelledDelete.Failure?.Code);
            Assert.Equal(CredentialProviderHealthStatus.Unavailable, cancelledHealth.Status);
        }
        finally
        {
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(value);
        }
    }

    [Fact]
    public async Task Public_provider_is_unavailable_without_the_windows_credential_manager()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-not-supported", "credential-not-supported");
        var callbackInvoked = false;
        var create = await provider.CreateAsync(requests.Mutation, _ =>
        {
            callbackInvoked = true;
            return requests.Mutation.ValueByteLength;
        }, CancellationToken.None);
        var replace = await provider.ReplaceAsync(requests.Mutation, _ => throw new InvalidOperationException("must-not-run"), CancellationToken.None);
        var use = await provider.UseAsync(requests.Use, new RecordingCredentialConsumer(), CancellationToken.None);
        var delete = await provider.DeleteAsync(requests.Delete, CancellationToken.None);
        var health = await provider.GetHealthAsync(requests.Use, CancellationToken.None);

        Assert.False(callbackInvoked);
        Assert.Equal(CredentialFailureCode.Unavailable, create.Failure?.Code);
        Assert.Equal(CredentialFailureCode.Unavailable, replace.Failure?.Code);
        Assert.Equal(CredentialFailureCode.Unavailable, use.Failure?.Code);
        Assert.Equal(CredentialFailureCode.Unavailable, delete.Failure?.Code);
        Assert.Equal(CredentialProviderHealthStatus.Unavailable, health.Status);
    }

    [Fact]
    public async Task Secure_fake_is_deterministic_fail_closed_and_preserves_prior_value_after_hostile_replace_failure()
    {
        using var provider = new SecureFakeCredentialValueProvider();
        var requests = Requests("workspace-fake", "credential-fake");
        var original = Encoding.UTF8.GetBytes("original-fake-canary");
        var replacement = Encoding.UTF8.GetBytes("replacement-fake-canary");
        Assert.True((await provider.CreateAsync(requests.Mutation with { ValueByteLength = original.Length }, destination => Copy(original, destination), CancellationToken.None)).Succeeded);
        var target = SecureFakeCredentialValueProvider.DeriveTarget(requests.Mutation.WorkspaceId, requests.Mutation.ReferenceId);
        provider.Store.EnqueueWrite(provider.Store.MutateThenFail);

        var failed = await provider.ReplaceAsync(requests.Mutation with { ValueByteLength = replacement.Length }, destination => Copy(replacement, destination), CancellationToken.None);
        var consumer = new RecordingCredentialConsumer();
        var use = await provider.UseAsync(requests.Use, consumer, CancellationToken.None);

        Assert.Equal(CredentialFailureCode.Unavailable, failed.Failure?.Code);
        Assert.True(use.Succeeded);
        Assert.Equal(original, consumer.Observed);
        Assert.Equal(original, provider.Store.Snapshot(target));
    }

    [Fact]
    public async Task Secure_fake_reports_uncertain_when_hostile_replace_and_rollback_cannot_be_proved()
    {
        using var provider = new SecureFakeCredentialValueProvider();
        var requests = Requests("workspace-uncertain", "credential-uncertain");
        var original = Encoding.UTF8.GetBytes("original-uncertain");
        var replacement = Encoding.UTF8.GetBytes("replacement-uncertain");
        Assert.True((await provider.CreateAsync(requests.Mutation with { ValueByteLength = original.Length }, destination => Copy(original, destination), CancellationToken.None)).Succeeded);
        provider.Store.EnqueueWrite(provider.Store.MutateThenFail);
        provider.Store.EnqueueWrite((_, _) => ScriptedCredentialStoreStatus.Unavailable);

        var result = await provider.ReplaceAsync(requests.Mutation with { ValueByteLength = replacement.Length }, destination => Copy(replacement, destination), CancellationToken.None);

        Assert.Equal(CredentialFailureCode.OutcomeUncertain, result.Failure?.Code);
    }

    [Fact]
    public async Task Secure_fake_structures_outage_corruption_limits_callback_failure_and_cancellation_without_values()
    {
        var requests = Requests("workspace-failures", "credential-failures");
        var canary = Encoding.UTF8.GetBytes("failure-canary-" + Guid.NewGuid().ToString("N"));
        using var unavailable = new SecureFakeCredentialValueProvider(isSupported: false);
        var unavailableCreate = await unavailable.CreateAsync(requests.Mutation with { ValueByteLength = canary.Length }, _ => throw new InvalidOperationException("must-not-run"), CancellationToken.None);
        var unavailableUse = await unavailable.UseAsync(requests.Use, new ThrowingCredentialConsumer(), CancellationToken.None);
        var unavailableHealth = await unavailable.GetHealthAsync(requests.Use, CancellationToken.None);

        using var provider = new SecureFakeCredentialValueProvider(maxValueByteLength: 8);
        var callbackInvoked = false;
        var limited = await provider.CreateAsync(requests.Mutation with { ValueByteLength = 9 }, _ =>
        {
            callbackInvoked = true;
            return 9;
        }, CancellationToken.None);
        provider.Store.EnqueueRead(ScriptedCredentialStoreStatus.Corrupt);
        var corruptHealth = await provider.GetHealthAsync(requests.Use, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledDelete = await provider.DeleteAsync(requests.Delete, cancelled.Token);

        Assert.Equal(CredentialFailureCode.Unavailable, unavailableCreate.Failure?.Code);
        Assert.Equal(CredentialFailureCode.Unavailable, unavailableUse.Failure?.Code);
        Assert.Equal(CredentialProviderHealthStatus.Unavailable, unavailableHealth.Status);
        Assert.Equal(CredentialFailureCode.LimitExceeded, limited.Failure?.Code);
        Assert.False(callbackInvoked);
        Assert.Equal(CredentialProviderHealthStatus.Corrupt, corruptHealth.Status);
        Assert.Equal(CredentialFailureCode.Unavailable, cancelledDelete.Failure?.Code);
        Assert.DoesNotContain(Encoding.UTF8.GetString(canary), JsonSerializer.Serialize(new[] { unavailableCreate, unavailableUse, limited, cancelledDelete }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secure_fake_delete_distinguishes_proved_absence_preserved_value_and_uncertainty()
    {
        var requests = Requests("workspace-delete", "credential-delete");
        var value = Encoding.UTF8.GetBytes("delete-canary");

        using var proved = new SecureFakeCredentialValueProvider();
        Assert.True((await proved.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination => Copy(value, destination), CancellationToken.None)).Succeeded);
        proved.Store.EnqueueDelete(proved.Store.RemoveThenFail);
        Assert.True((await proved.DeleteAsync(requests.Delete, CancellationToken.None)).Succeeded);

        using var preserved = new SecureFakeCredentialValueProvider();
        Assert.True((await preserved.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination => Copy(value, destination), CancellationToken.None)).Succeeded);
        preserved.Store.EnqueueDelete(_ => ScriptedCredentialStoreStatus.Unavailable);
        var preservedResult = await preserved.DeleteAsync(requests.Delete, CancellationToken.None);
        Assert.Equal(CredentialFailureCode.Unavailable, preservedResult.Failure?.Code);

        using var uncertain = new SecureFakeCredentialValueProvider();
        Assert.True((await uncertain.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination => Copy(value, destination), CancellationToken.None)).Succeeded);
        uncertain.Store.EnqueueRead(ScriptedCredentialStoreStatus.Unavailable);
        var uncertainResult = await uncertain.DeleteAsync(requests.Delete, CancellationToken.None);
        Assert.Equal(CredentialFailureCode.OutcomeUncertain, uncertainResult.Failure?.Code);
    }

    [Fact]
    public async Task Secure_fake_serializes_racing_creates_and_does_not_overwrite_the_winner()
    {
        using var provider = new SecureFakeCredentialValueProvider();
        var requests = Requests("workspace-race", "credential-race");
        var first = Encoding.UTF8.GetBytes("race-winner");
        var second = Encoding.UTF8.GetBytes("race-loser");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var firstTask = Task.Run(async () => await provider.CreateAsync(requests.Mutation with { ValueByteLength = first.Length }, destination =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Copy(first, destination);
        }, CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var secondTask = Task.Run(async () => await provider.CreateAsync(requests.Mutation with { ValueByteLength = second.Length }, destination => Copy(second, destination), CancellationToken.None));
        release.Set();

        var results = await Task.WhenAll(firstTask, secondTask);
        var consumer = new RecordingCredentialConsumer();
        await provider.UseAsync(requests.Use, consumer, CancellationToken.None);

        Assert.True(results[0].Succeeded);
        Assert.Equal(CredentialFailureCode.Conflict, results[1].Failure?.Code);
        Assert.Equal(first, consumer.Observed);
    }

    [Fact]
    public async Task Windows_provider_uses_a_current_user_secured_global_mutex_for_a_shared_credential_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workspaceId = "workspace-global-mutex-" + Guid.NewGuid().ToString("N");
        var referenceId = "credential-global-mutex-" + Guid.NewGuid().ToString("N");
        var requests = Requests(workspaceId, referenceId);
        var provider = new WindowsCredentialValueProvider();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var value = Encoding.UTF8.GetBytes("global-mutex-canary-" + Guid.NewGuid().ToString("N"));
        var createTask = Task.Run(async () => await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Copy(value, destination);
        }, CancellationToken.None));

        CredentialProviderResult? create = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var target = DeriveTargetForTest(workspaceId, referenceId);
            var name = "Global\\EmbodySense.Credentials.v1." + target["EmbodySense:v1:".Length..];
            Assert.StartsWith("Global\\", name, StringComparison.Ordinal);
            AssertCurrentUserMutexSecurity(name);
        }
        finally
        {
            release.Set();
            try
            {
                create = await createTask;
            }
            finally
            {
                await provider.DeleteAsync(requests.Delete, CancellationToken.None);
                CryptographicOperations.ZeroMemory(value);
            }
        }

        Assert.NotNull(create);
        Assert.True(create.Succeeded);
    }

    [Fact]
    public async Task Windows_provider_rejects_same_target_mutation_reentrancy_from_source_callbacks_without_blocking_other_targets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var primary = Requests("workspace-reentrant-" + Guid.NewGuid().ToString("N"), "credential-reentrant-" + Guid.NewGuid().ToString("N"));
        var secondary = Requests("workspace-reentrant-" + Guid.NewGuid().ToString("N"), "credential-reentrant-" + Guid.NewGuid().ToString("N"));
        var primaryValue = Encoding.UTF8.GetBytes("primary-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        var secondaryValue = Encoding.UTF8.GetBytes("secondary-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        CredentialProviderResult? nestedSameTarget = null;
        CredentialProviderResult? nestedDelete = null;
        CredentialProviderResult? nestedOtherTarget = null;

        try
        {
            var outer = await provider.CreateAsync(primary.Mutation with { ValueByteLength = primaryValue.Length }, destination =>
            {
                nestedSameTarget = provider.CreateAsync(primary.Mutation with { ValueByteLength = primaryValue.Length }, nestedDestination => Copy(primaryValue, nestedDestination), CancellationToken.None).GetAwaiter().GetResult();
                nestedDelete = provider.DeleteAsync(primary.Delete, CancellationToken.None).GetAwaiter().GetResult();
                nestedOtherTarget = provider.CreateAsync(secondary.Mutation with { ValueByteLength = secondaryValue.Length }, nestedDestination => Copy(secondaryValue, nestedDestination), CancellationToken.None).GetAwaiter().GetResult();
                return Copy(primaryValue, destination);
            }, CancellationToken.None);
            var primaryConsumer = new RecordingCredentialConsumer();
            var secondaryConsumer = new RecordingCredentialConsumer();
            var primaryUse = await provider.UseAsync(primary.Use, primaryConsumer, CancellationToken.None);
            var secondaryUse = await provider.UseAsync(secondary.Use, secondaryConsumer, CancellationToken.None);

            Assert.True(outer.Succeeded);
            Assert.Equal(CredentialFailureCode.Conflict, nestedSameTarget?.Failure?.Code);
            Assert.Equal(CredentialFailureCode.Conflict, nestedDelete?.Failure?.Code);
            Assert.True(nestedOtherTarget?.Succeeded);
            Assert.True(primaryUse.Succeeded);
            Assert.True(secondaryUse.Succeeded);
            Assert.Equal(primaryValue, primaryConsumer.Observed);
            Assert.Equal(secondaryValue, secondaryConsumer.Observed);
        }
        finally
        {
            await provider.DeleteAsync(primary.Delete, CancellationToken.None);
            await provider.DeleteAsync(secondary.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(primaryValue);
            CryptographicOperations.ZeroMemory(secondaryValue);
        }
    }

    [Fact]
    public async Task Windows_provider_rejects_same_target_replace_reentrancy_from_a_source_callback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-reentrant-replace-" + Guid.NewGuid().ToString("N"), "credential-reentrant-replace-" + Guid.NewGuid().ToString("N"));
        var original = Encoding.UTF8.GetBytes("original-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        var replacement = Encoding.UTF8.GetBytes("replacement-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        CredentialProviderResult? nested = null;

        try
        {
            Assert.True((await provider.CreateAsync(requests.Mutation with { ValueByteLength = original.Length }, destination => Copy(original, destination), CancellationToken.None)).Succeeded);
            var outer = await provider.ReplaceAsync(requests.Mutation with { ValueByteLength = replacement.Length }, destination =>
            {
                nested = provider.ReplaceAsync(requests.Mutation with { ValueByteLength = replacement.Length }, nestedDestination => Copy(replacement, nestedDestination), CancellationToken.None).GetAwaiter().GetResult();
                return Copy(replacement, destination);
            }, CancellationToken.None);
            var consumer = new RecordingCredentialConsumer();
            var use = await provider.UseAsync(requests.Use, consumer, CancellationToken.None);

            Assert.True(outer.Succeeded);
            Assert.Equal(CredentialFailureCode.Conflict, nested?.Failure?.Code);
            Assert.True(use.Succeeded);
            Assert.Equal(replacement, consumer.Observed);
        }
        finally
        {
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(replacement);
        }
    }

    [Fact]
    public async Task Windows_provider_propagates_same_target_callback_scope_into_a_synchronously_waited_worker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-reentrant-worker-" + Guid.NewGuid().ToString("N"), "credential-reentrant-worker-" + Guid.NewGuid().ToString("N"));
        var value = Encoding.UTF8.GetBytes("worker-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        CredentialProviderResult? nested = null;

        try
        {
            var outer = await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination =>
            {
                nested = Task.Run(async () => await provider.DeleteAsync(requests.Delete, CancellationToken.None)).GetAwaiter().GetResult();
                return Copy(value, destination);
            }, CancellationToken.None);

            Assert.True(outer.Succeeded);
            Assert.Equal(CredentialFailureCode.Conflict, nested?.Failure?.Code);
        }
        finally
        {
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(value);
        }
    }

    [Fact]
    public async Task Windows_provider_rejects_a_synchronously_waited_worker_when_execution_context_flow_is_suppressed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-reentrant-suppressed-worker-" + Guid.NewGuid().ToString("N"), "credential-reentrant-suppressed-worker-" + Guid.NewGuid().ToString("N"));
        var value = Encoding.UTF8.GetBytes("suppressed-worker-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        CredentialProviderResult? nested = null;

        try
        {
            var outer = await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination =>
            {
                using (ExecutionContext.SuppressFlow())
                {
                    nested = Task.Run(async () => await provider.DeleteAsync(requests.Delete, CancellationToken.None)).GetAwaiter().GetResult();
                }

                return Copy(value, destination);
            }, CancellationToken.None);

            Assert.True(outer.Succeeded);
            Assert.Equal(CredentialFailureCode.Conflict, nested?.Failure?.Code);
        }
        finally
        {
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(value);
        }
    }

    [Fact]
    public async Task Windows_provider_retains_captured_same_target_scope_after_the_source_returns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-reentrant-deferred-" + Guid.NewGuid().ToString("N"), "credential-reentrant-deferred-" + Guid.NewGuid().ToString("N"));
        var value = Encoding.UTF8.GetBytes("deferred-reentrant-canary-" + Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CredentialProviderResult>? nested = null;

        try
        {
            var outer = await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination =>
            {
                nested = Task.Run(async () =>
                {
                    await release.Task;
                    return await provider.DeleteAsync(requests.Delete, CancellationToken.None);
                });
                return Copy(value, destination);
            }, CancellationToken.None);
            release.SetResult();
            var nestedResult = await nested!;
            var consumer = new RecordingCredentialConsumer();
            var use = await provider.UseAsync(requests.Use, consumer, CancellationToken.None);

            Assert.True(outer.Succeeded);
            Assert.Equal(CredentialFailureCode.Conflict, nestedResult.Failure?.Code);
            Assert.True(use.Succeeded);
            Assert.Equal(value, consumer.Observed);
        }
        finally
        {
            release.TrySetResult();
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
            CryptographicOperations.ZeroMemory(value);
        }
    }

    [Fact]
    public async Task Public_results_diagnostics_process_state_and_serialization_do_not_expose_values_or_private_targets()
    {
        using var provider = new SecureFakeCredentialValueProvider();
        using var workspace = new TestWorkspace();
        var requests = Requests("workspace-canary-" + Guid.NewGuid().ToString("N"), "credential-canary-" + Guid.NewGuid().ToString("N"));
        var canaryText = "secret-value-" + Guid.NewGuid().ToString("N");
        var canary = Encoding.UTF8.GetBytes(canaryText);
        var target = SecureFakeCredentialValueProvider.DeriveTarget(requests.Mutation.WorkspaceId, requests.Mutation.ReferenceId);
        var create = await provider.CreateAsync(requests.Mutation with { ValueByteLength = canary.Length }, destination => Copy(canary, destination), CancellationToken.None);
        var callbackFailure = await provider.UseAsync(requests.Use, new ThrowingCredentialConsumer(), CancellationToken.None);
        var projections = string.Join('\n', create, callbackFailure, JsonSerializer.Serialize(create), JsonSerializer.Serialize(callbackFailure), Environment.CommandLine, string.Join('\n', Environment.GetEnvironmentVariables().Values.Cast<object>()));

        Assert.DoesNotContain(canaryText, projections, StringComparison.Ordinal);
        Assert.DoesNotContain(target, projections, StringComparison.Ordinal);
        Assert.Equal(CredentialFailureCode.CallbackFailed, callbackFailure.Failure?.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.RootPath));
        Assert.DoesNotContain(typeof(WindowsCredentialValueProvider).GetMethods().Where(method => method.DeclaringType == typeof(WindowsCredentialValueProvider)), method => method.ReturnType == typeof(string) || method.ReturnType == typeof(byte[]));
    }

    [Fact]
    public async Task Windows_provider_value_survives_an_external_process_without_value_in_arguments_environment_or_output()
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildModeVariable)))
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-external-process-v1", "credential-external-process-v1");
        await provider.DeleteAsync(requests.Delete, CancellationToken.None);
        using var process = StartCredentialChild(nameof(External_process_fixture_creates_credential_without_receiving_value_or_locator), ExternalProcessMode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = await process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            var consumer = new RecordingCredentialConsumer();
            var use = await provider.UseAsync(requests.Use, consumer, CancellationToken.None);
            Assert.Equal(0, process.ExitCode);
            Assert.True(use.Succeeded, error);
            Assert.Equal(CrossProcessValue(), consumer.Observed);
            Assert.DoesNotContain(Encoding.UTF8.GetString(CrossProcessValue()), process.StartInfo.Arguments + output + error, StringComparison.Ordinal);
            Assert.DoesNotContain(Encoding.UTF8.GetString(CrossProcessValue()), string.Join('\n', process.StartInfo.Environment.Values), StringComparison.Ordinal);
        }
        finally
        {
            await provider.DeleteAsync(requests.Delete, CancellationToken.None);
        }
    }

    [Fact]
    public async Task External_process_fixture_creates_credential_without_receiving_value_or_locator()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable(ChildModeVariable), ExternalProcessMode, StringComparison.Ordinal))
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-external-process-v1", "credential-external-process-v1");
        var value = CrossProcessValue();
        var result = await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination => Copy(value, destination), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Windows_global_mutex_serializes_a_second_process_for_the_same_shared_target()
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildModeVariable)))
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var contentionId = Guid.NewGuid().ToString("N");
        var requests = ContentionRequests(contentionId);
        var value = Encoding.UTF8.GetBytes("global-contention-canary");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await provider.DeleteAsync(requests.Delete, CancellationToken.None);
        var createTask = Task.Run(async () => await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
            return Copy(value, destination);
        }, CancellationToken.None));

        CredentialProviderResult? create = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Process? process = null;
            try
            {
                process = StartCredentialChild(nameof(External_process_fixture_cannot_enter_shared_target_while_parent_holds_global_mutex), MutexContentionMode, contentionId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.DoesNotContain(Encoding.UTF8.GetString(value), process.StartInfo.Arguments + output + error, StringComparison.Ordinal);
            }
            finally
            {
                if (process is not null)
                {
                    try
                    {
                        await TerminateProcessTreeIfRunningAsync(process);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }
        finally
        {
            release.Set();
            try
            {
                create = await createTask;
            }
            finally
            {
                await provider.DeleteAsync(requests.Delete, CancellationToken.None);
                CryptographicOperations.ZeroMemory(value);
            }
        }

        Assert.True(create.Succeeded);
    }

    [Fact]
    public async Task External_process_fixture_cannot_enter_shared_target_while_parent_holds_global_mutex()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable(ChildModeVariable), MutexContentionMode, StringComparison.Ordinal))
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var contentionId = Environment.GetEnvironmentVariable(MutexContentionIdVariable);
        Assert.True(Guid.TryParseExact(contentionId, "N", out _));
        var requests = ContentionRequests(contentionId!);
        var callbackInvoked = false;
        var result = await provider.CreateAsync(requests.Mutation, _ =>
        {
            callbackInvoked = true;
            return requests.Mutation.ValueByteLength;
        }, CancellationToken.None);

        Assert.Equal(CredentialFailureCode.Unavailable, result.Failure?.Code);
        Assert.False(callbackInvoked);
    }

    private static Process StartCredentialChild(string fixtureName, string childMode, string? contentionId = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        EmbodySense.Core.Persistence.Tests.Verification.CoverageChildProcessAssembly.AddVstestArguments(
            startInfo,
            typeof(WindowsCredentialValueProviderTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Credentials.WindowsCredentialValueProviderTests." + fixtureName);
        startInfo.Environment[ChildModeVariable] = childMode;
        if (contentionId is not null)
        {
            startInfo.Environment[MutexContentionIdVariable] = contentionId;
        }

        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The external credential-provider fixture did not start.");
    }

    private static async Task TerminateProcessTreeIfRunningAsync(Process process)
    {
        try
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and termination request.
            }
        }
        finally
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static byte[] CrossProcessValue() => [99, 114, 111, 115, 115, 45, 112, 114, 111, 99, 101, 115, 115, 45, 115, 101, 99, 114, 101, 116];

    private static ProviderRequests Requests(string workspaceId, string referenceId)
    {
        Assert.True(CredentialReferenceId.TryParse(referenceId, out var reference, out _));
        Assert.True(CredentialProviderId.TryParse("org.embodysense.windows", out var provider, out _));
        Assert.True(CredentialContractId.TryParse("operation-1", out var operation, out _));
        return new ProviderRequests(
            new CredentialProviderMutationRequest(workspaceId, reference!, provider!, operation!, 16),
            new CredentialProviderUseRequest(workspaceId, reference!, provider!, operation!),
            new CredentialProviderDeleteRequest(workspaceId, reference!, provider!, operation!));
    }

    private static ProviderRequests ContentionRequests(string contentionId)
    {
        return Requests("workspace-global-contention-" + contentionId, "credential-global-contention-" + contentionId);
    }

    private static string DeriveTargetForTest(string workspaceId, string referenceId)
    {
        var workspaceBytes = Encoding.UTF8.GetBytes(workspaceId);
        var referenceBytes = Encoding.UTF8.GetBytes(referenceId);
        var input = new byte[sizeof(int) + workspaceBytes.Length + referenceBytes.Length];
        BitConverter.GetBytes(workspaceBytes.Length).CopyTo(input, 0);
        workspaceBytes.CopyTo(input, sizeof(int));
        referenceBytes.CopyTo(input, sizeof(int) + workspaceBytes.Length);
        var digest = SHA256.HashData(input);
        var target = "EmbodySense:v1:" + Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(workspaceBytes);
        CryptographicOperations.ZeroMemory(referenceBytes);
        CryptographicOperations.ZeroMemory(input);
        CryptographicOperations.ZeroMemory(digest);
        return target;
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserMutexSecurity(string name)
    {
        using var opened = MutexAcl.OpenExisting(name, MutexRights.ReadPermissions | MutexRights.Synchronize);
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        var security = opened.GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).OfType<MutexAccessRule>().ToArray();

        Assert.NotNull(currentUser);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(currentUser, security.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.True(rule.MutexRights.HasFlag(MutexRights.FullControl));
    }

    private static int Copy(byte[] source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    private sealed class ThrowingCredentialConsumer : ICredentialTrustedUseConsumer
    {
        public void Use(ReadOnlySpan<byte> credential) => throw new InvalidOperationException("hostile callback detail");
    }

    private sealed record ProviderRequests(CredentialProviderMutationRequest Mutation, CredentialProviderUseRequest Use, CredentialProviderDeleteRequest Delete);
}
