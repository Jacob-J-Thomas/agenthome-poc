using System.Diagnostics;
using System.Security.Cryptography;
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
    public async Task Secure_fake_is_deterministic_fail_closed_and_preserves_prior_value_after_hostile_replace_failure()
    {
        using var provider = new SecureFakeCredentialValueProvider();
        var requests = Requests("workspace-fake", "credential-fake");
        var original = Encoding.UTF8.GetBytes("original-fake-canary");
        var replacement = Encoding.UTF8.GetBytes("replacement-fake-canary");
        Assert.True((await provider.CreateAsync(requests.Mutation with { ValueByteLength = original.Length }, destination => Copy(original, destination), CancellationToken.None)).Succeeded);
        var target = CredentialProviderTarget.Derive(requests.Mutation.WorkspaceId, requests.Mutation.ReferenceId);
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
        provider.Store.EnqueueWrite((_, _) => WindowsCredentialStoreStatus.Unavailable);

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
        provider.Store.EnqueueRead(WindowsCredentialStoreStatus.Corrupt);
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
        preserved.Store.EnqueueDelete(_ => WindowsCredentialStoreStatus.Unavailable);
        var preservedResult = await preserved.DeleteAsync(requests.Delete, CancellationToken.None);
        Assert.Equal(CredentialFailureCode.Unavailable, preservedResult.Failure?.Code);

        using var uncertain = new SecureFakeCredentialValueProvider();
        Assert.True((await uncertain.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination => Copy(value, destination), CancellationToken.None)).Succeeded);
        uncertain.Store.EnqueueRead(WindowsCredentialStoreStatus.Unavailable);
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
    public async Task Public_results_diagnostics_process_state_and_serialization_do_not_expose_values_or_private_targets()
    {
        using var provider = new SecureFakeCredentialValueProvider();
        using var workspace = new TestWorkspace();
        var requests = Requests("workspace-canary-" + Guid.NewGuid().ToString("N"), "credential-canary-" + Guid.NewGuid().ToString("N"));
        var canaryText = "secret-value-" + Guid.NewGuid().ToString("N");
        var canary = Encoding.UTF8.GetBytes(canaryText);
        var target = CredentialProviderTarget.Derive(requests.Mutation.WorkspaceId, requests.Mutation.ReferenceId);
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
        if (!OperatingSystem.IsWindows() || string.Equals(Environment.GetEnvironmentVariable(ChildModeVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-external-process-v1", "credential-external-process-v1");
        await provider.DeleteAsync(requests.Delete, CancellationToken.None);
        using var process = StartCredentialChild();
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
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable(ChildModeVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var provider = new WindowsCredentialValueProvider();
        var requests = Requests("workspace-external-process-v1", "credential-external-process-v1");
        var value = CrossProcessValue();
        var result = await provider.CreateAsync(requests.Mutation with { ValueByteLength = value.Length }, destination => Copy(value, destination), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    private static Process StartCredentialChild()
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
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(WindowsCredentialValueProviderTests).Assembly.Location);
        startInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=EmbodySense.Core.Persistence.Tests.Credentials.WindowsCredentialValueProviderTests.External_process_fixture_creates_credential_without_receiving_value_or_locator");
        startInfo.Environment[ChildModeVariable] = "1";
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The external credential-provider fixture did not start.");
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
