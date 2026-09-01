using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

public sealed class HumanInputSupersedeCandidateRegistryTests
{
    private const string Actor = "embodysense.web";

    [Fact]
    public void Register_and_resolve_rebinds_every_exact_lookup_term()
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var current = HumanInputRequestStoreTestData.Request("request-one", "version-one", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("request-two", "version-two", HumanInputRequestStoreTestData.Time, current.Binding, HumanInputPrivacyClass.Sensitive);
        var mutation = HumanInputRequestStoreTestData.CreateMutation();
        var expected = HumanInputRequestStoreTestData.Reference(current);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-one", current.RequestId, 1, expected, candidate, mutation.Operation.GrantReference!, expires);

        Assert.True(registry.TryRegister(registration, out var key));
        Assert.True(registry.TryResolve(key, current.Binding.WorkspaceId, Actor, registration.OperationId, registration.RequestId, registration.ExpectedLifecycleVersion, expected.RequestVersionId, expected.RequestHash, DateTimeOffset.UtcNow, out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal(candidate.RequestHash, resolution!.CandidateRequest.RequestHash);
        Assert.Equal(candidate.Binding, resolution.CandidateRequest.Binding);
        Assert.Equal(candidate.RequestId, resolution.CandidateRequest.RequestId);
        Assert.Equal(registration.GrantReference, resolution.GrantReference);
        Assert.False(registry.TryResolve(key, current.Binding.WorkspaceId, "embodysense.cli", registration.OperationId, registration.RequestId, registration.ExpectedLifecycleVersion, expected.RequestVersionId, expected.RequestHash, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void Exact_registration_replays_the_same_key_and_expiry_removes_it()
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var current = HumanInputRequestStoreTestData.Request("request-one", "version-one", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("request-two", "version-two", HumanInputRequestStoreTestData.Time, current.Binding);
        var mutation = HumanInputRequestStoreTestData.CreateMutation();
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-one", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, mutation.Operation.GrantReference!, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(registry.TryRegister(registration, out var first));
        Assert.True(registry.TryRegister(registration, out var replay));
        Assert.Equal(first, replay);
        Assert.False(registry.TryResolve(first, registration.WorkspaceId, Actor, registration.OperationId, registration.RequestId, registration.ExpectedLifecycleVersion, registration.ExpectedRequest.RequestVersionId, registration.ExpectedRequest.RequestHash, registration.ExpiresAtUtc.AddTicks(1), out _));
    }

    [Fact]
    public void Invalid_candidate_integrity_is_rejected_before_retention()
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var current = HumanInputRequestStoreTestData.Request("request-one", "version-one", HumanInputRequestStoreTestData.Time);
        var mutation = HumanInputRequestStoreTestData.CreateMutation();
        var candidate = HumanInputRequestStoreTestData.Request("request-two", "version-two", HumanInputRequestStoreTestData.Time, current.Binding) with { RequestHash = "invalid" };
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-one", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, mutation.Operation.GrantReference!, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.False(registry.TryRegister(registration, out var key));
        Assert.Empty(key);
    }
}
