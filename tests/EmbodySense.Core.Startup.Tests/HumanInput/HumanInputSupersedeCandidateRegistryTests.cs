using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
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
        Assert.False(registry.TryResolve(key, current.Binding.WorkspaceId, Actor, "operation-two", registration.RequestId, registration.ExpectedLifecycleVersion, expected.RequestVersionId, expected.RequestHash, DateTimeOffset.UtcNow, out _));
        Assert.False(registry.TryResolve(key, current.Binding.WorkspaceId, Actor, registration.OperationId, registration.RequestId, registration.ExpectedLifecycleVersion + 1, expected.RequestVersionId, expected.RequestHash, DateTimeOffset.UtcNow, out _));
        Assert.False(registry.TryResolve(key, current.Binding.WorkspaceId, Actor, registration.OperationId, registration.RequestId, registration.ExpectedLifecycleVersion, expected.RequestVersionId, "different-hash", DateTimeOffset.UtcNow, out _));
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

    [Fact]
    public void Single_reroute_registration_is_rejected_in_favor_of_atomic_groups()
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var current = HumanInputRequestHash.Apply(HumanInputRequestStoreTestData.Request("request-single-reroute", "version-single-reroute", HumanInputRequestStoreTestData.Time) with
        {
            EligibleRespondents = [
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            RequestHash = string.Empty
        });
        var candidate = HumanInputRequestHash.Apply(current with { RequestVersionId = "version-single-reroute-successor", EligibleRespondents = [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")], RequestHash = string.Empty });
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-single-reroute", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, HumanInputRequestStoreTestData.CreateMutation().Operation.GrantReference!, DateTimeOffset.UtcNow.AddMinutes(5), HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestStoreTestData.HashA);

        Assert.False(registry.TryRegister(registration, out var key, out var status));
        Assert.Empty(key);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, status);
    }

    [Fact]
    public void Registry_clock_failures_are_value_free_and_unavailable()
    {
        var registry = new HumanInputSupersedeCandidateRegistry(new HumanInputThrowingTimeProvider());
        var current = HumanInputRequestStoreTestData.Request("request-clock-failure", "version-clock-failure", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("request-clock-successor", "version-clock-successor", HumanInputRequestStoreTestData.Time, current.Binding);
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-clock-failure", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, HumanInputRequestStoreTestData.CreateMutation().Operation.GrantReference!, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.False(registry.TryRegister(registration, out var key, out var status));
        Assert.Empty(key);
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, status);
    }

    [Theory]
    [InlineData("operation/invalid")]
    [InlineData("operation\\invalid")]
    [InlineData("operation with spaces")]
    public void Invalid_operation_ids_cannot_occupy_or_replay_registry_slots(string operationId)
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var current = HumanInputRequestStoreTestData.Request("request-one", "version-one", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("request-two", "version-two", HumanInputRequestStoreTestData.Time, current.Binding);
        var mutation = HumanInputRequestStoreTestData.CreateMutation();
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, operationId, current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, mutation.Operation.GrantReference!, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.False(registry.TryRegister(registration, out var key));
        Assert.Empty(key);
        Assert.False(registry.TryResolve("candidate", registration.WorkspaceId, Actor, operationId, registration.RequestId, 1, registration.ExpectedRequest.RequestVersionId, registration.ExpectedRequest.RequestHash, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void Injected_clock_controls_expiry_purge_and_exact_re_registration()
    {
        var initial = DateTimeOffset.UtcNow.AddHours(1);
        var clock = new HumanInputFixedTimeProvider(initial);
        var registry = new HumanInputSupersedeCandidateRegistry(clock);
        var current = HumanInputRequestStoreTestData.Request("request-one", "version-one", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("request-two", "version-two", HumanInputRequestStoreTestData.Time, current.Binding);
        var mutation = HumanInputRequestStoreTestData.CreateMutation();
        var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-clock", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, mutation.Operation.GrantReference!, initial.AddMinutes(5));

        Assert.True(registry.TryRegister(registration, out var first));
        clock.SetUtcNow(initial.AddMinutes(6));

        Assert.True(registry.TryRegister(registration, out var second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Reroute_group_replays_exactly_and_rejects_a_changed_preparation_intent()
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var current = HumanInputRequestHash.Apply(HumanInputRequestStoreTestData.Request("request-group", "version-group", HumanInputRequestStoreTestData.Time) with
        {
            EligibleRespondents = [
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            RequestHash = string.Empty
        });
        var firstCandidate = HumanInputRequestHash.Apply(current with { RequestVersionId = "version-route-one", EligibleRespondents = [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")], RequestHash = string.Empty });
        var secondCandidate = HumanInputRequestHash.Apply(current with { RequestVersionId = "version-route-two", EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")], RequestHash = string.Empty });
        var grant = HumanInputRequestStoreTestData.CreateMutation().Operation.GrantReference!;
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var expected = HumanInputRequestStoreTestData.Reference(current);
        var first = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, "operation-group", current.RequestId, 1, expected, firstCandidate, grant, expiry, HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestStoreTestData.HashA);
        var second = first with { CandidateRequest = secondCandidate };

        Assert.True(registry.TryRegisterGroup([first, second], out var keys, out var status));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, status);
        Assert.Equal(2, keys.Count);
        Assert.True(registry.TryRegisterGroup([first, second], out var replayKeys, out var replayStatus));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, replayStatus);
        Assert.Equal(keys, replayKeys);

        var changedIntent = first with { PreparationHash = HumanInputRequestStoreTestData.HashB };
        Assert.False(registry.TryRegisterGroup([changedIntent, second with { PreparationHash = HumanInputRequestStoreTestData.HashB }], out var conflictingKeys, out var conflictStatus));
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, conflictStatus);
        Assert.Empty(conflictingKeys);
    }

    [Fact]
    public void Reroute_group_capacity_failure_is_typed_and_publishes_no_partial_entries()
    {
        var registry = new HumanInputSupersedeCandidateRegistry();
        var grant = HumanInputRequestStoreTestData.CreateMutation().Operation.GrantReference!;
        for (var index = 0; index < 255; index++)
        {
            var current = HumanInputRequestStoreTestData.Request($"capacity-request-{index}", $"capacity-version-{index}", HumanInputRequestStoreTestData.Time);
            var candidate = HumanInputRequestStoreTestData.Request($"capacity-candidate-{index}", $"capacity-successor-{index}", HumanInputRequestStoreTestData.Time, current.Binding);
            var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, $"capacity-operation-{index}", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, grant, DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.True(registry.TryRegister(registration, out _));
        }

        var routeCurrent = HumanInputRequestHash.Apply(HumanInputRequestStoreTestData.Request("capacity-route", "capacity-route-version", HumanInputRequestStoreTestData.Time) with
        {
            EligibleRespondents = [
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            RequestHash = string.Empty
        });
        var routeCandidate = HumanInputRequestHash.Apply(routeCurrent with { RequestVersionId = "capacity-route-successor", EligibleRespondents = [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")], RequestHash = string.Empty });
        var registrationBase = new HumanInputSupersedeCandidateRegistration(routeCurrent.Binding.WorkspaceId, Actor, "capacity-route-operation", routeCurrent.RequestId, 1, HumanInputRequestStoreTestData.Reference(routeCurrent), routeCandidate, grant, DateTimeOffset.UtcNow.AddMinutes(5), HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestStoreTestData.HashA);
        var second = registrationBase with { CandidateRequest = HumanInputRequestHash.Apply(routeCurrent with { RequestVersionId = "capacity-route-successor-two", EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")], RequestHash = string.Empty }) };

        Assert.False(registry.TryRegisterGroup([registrationBase, second], out var keys, out var status));
        Assert.Equal(HumanInputSupersedePreparationStatus.LimitExceeded, status);
        Assert.Empty(keys);
        Assert.False(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Reroute, "missing", routeCurrent.Binding.WorkspaceId, Actor, "capacity-route-operation", routeCurrent.RequestId, 1, registrationBase.ExpectedRequest.RequestVersionId, registrationBase.ExpectedRequest.RequestHash, DateTimeOffset.UtcNow, out _));
    }
}
