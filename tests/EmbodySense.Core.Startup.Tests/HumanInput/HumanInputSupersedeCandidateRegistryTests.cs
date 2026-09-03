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
        for (var index = 0; index < 256; index++)
        {
            var current = HumanInputRequestStoreTestData.Request($"capacity-request-{index}", $"capacity-version-{index}", HumanInputRequestStoreTestData.Time);
            var candidate = HumanInputRequestStoreTestData.Request($"capacity-candidate-{index}", $"capacity-successor-{index}", HumanInputRequestStoreTestData.Time, current.Binding);
            var registration = new HumanInputSupersedeCandidateRegistration(current.Binding.WorkspaceId, Actor, $"capacity-operation-{index}", current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, grant, DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.True(registry.TryRegister(registration, out _));
        }

        var fullCurrent = HumanInputRequestStoreTestData.Request("capacity-overflow", "capacity-overflow-version", HumanInputRequestStoreTestData.Time);
        var fullCandidate = HumanInputRequestStoreTestData.Request("capacity-overflow-candidate", "capacity-overflow-successor", HumanInputRequestStoreTestData.Time, fullCurrent.Binding);
        var fullRegistration = new HumanInputSupersedeCandidateRegistration(fullCurrent.Binding.WorkspaceId, Actor, "capacity-overflow-operation", fullCurrent.RequestId, 1, HumanInputRequestStoreTestData.Reference(fullCurrent), fullCandidate, grant, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.False(registry.TryRegister(fullRegistration, out var fullKey, out var fullStatus));
        Assert.Empty(fullKey);
        Assert.Equal(HumanInputSupersedePreparationStatus.LimitExceeded, fullStatus);

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

    [Fact]
    public void Invalid_registration_shapes_are_rejected_without_retention()
    {
        var current = HumanInputRequestStoreTestData.Request("malformed-request", "malformed-version", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("malformed-candidate", "malformed-successor", HumanInputRequestStoreTestData.Time, current.Binding);
        var valid = CreateRegistration("malformed-operation", current, candidate);
        var invalidRegistrations = new[]
        {
            valid with { WorkspaceId = string.Empty },
            valid with { Actor = string.Empty },
            valid with { OperationId = string.Empty },
            valid with { RequestId = string.Empty },
            valid with { ExpectedLifecycleVersion = 0 },
            valid with { ExpiresAtUtc = default },
            valid with { ExpectedRequest = null! },
            valid with { ExpectedRequest = valid.ExpectedRequest with { RequestId = "different-request" } },
            valid with { ExpectedRequest = valid.ExpectedRequest with { RequestHash = "invalid" } },
            valid with { GrantReference = null! },
            valid with { CandidateRequest = null! },
            valid with { CandidateRequest = valid.CandidateRequest with { Binding = null! } },
            valid with { CandidateRequest = valid.CandidateRequest with { Binding = valid.CandidateRequest.Binding! with { WorkspaceId = "different-workspace" } } },
            valid with { Kind = HumanInputRequestLifecycleOperationKind.Unknown },
            valid with { CandidateRequest = current },
            valid with { Kind = HumanInputRequestLifecycleOperationKind.Reroute, PreparationHash = HumanInputRequestStoreTestData.HashA },
            valid with { Kind = HumanInputRequestLifecycleOperationKind.Amend, PreparationHash = HumanInputRequestStoreTestData.HashA }
        };

        foreach (var invalid in invalidRegistrations)
        {
            var registry = new HumanInputSupersedeCandidateRegistry();
            Assert.False(registry.TryRegister(invalid, out var key, out var status));
            Assert.Empty(key);
            Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, status);
        }
    }

    [Fact]
    public void Amend_registration_enforces_utc_expiry_bounds_and_accepts_exact_limits()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new HumanInputFixedTimeProvider(now);
        var invalidExpiries = new[]
        {
            now.AddSeconds(30),
            now.AddMinutes(16),
            now.AddMinutes(5).ToOffset(TimeSpan.FromHours(1))
        };

        for (var index = 0; index < invalidExpiries.Length; index++)
        {
            var invalid = CreateAmendRegistration($"amend-invalid-{index}", invalidExpiries[index]);
            Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegister(invalid, out var key, out var status));
            Assert.Empty(key);
            Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, status);
        }

        var minimum = CreateAmendRegistration("amend-minimum", now.Add(HumanInputLifecycleCandidateLimits.MinCandidateLifetime));
        Assert.True(new HumanInputSupersedeCandidateRegistry(clock).TryRegister(minimum, out _, out var minimumStatus));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, minimumStatus);

        var maximum = CreateAmendRegistration("amend-maximum", now.Add(HumanInputLifecycleCandidateLimits.MaxCandidateLifetime));
        Assert.True(new HumanInputSupersedeCandidateRegistry(clock).TryRegister(maximum, out _, out var maximumStatus));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, maximumStatus);
    }

    [Fact]
    public void Group_registration_rejects_empty_malformed_and_expired_inputs()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new HumanInputFixedTimeProvider(now);
        var valid = CreateRerouteRegistration("group-invalid", now.AddMinutes(5));

        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup(null!, out var nullKeys, out var nullStatus));
        Assert.Empty(nullKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, nullStatus);

        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([], out var emptyKeys, out var emptyStatus));
        Assert.Empty(emptyKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, emptyStatus);

        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup(Enumerable.Repeat(valid, HumanInputLifecycleCandidateLimits.MaxRerouteOptions + 1).ToArray(), out var oversizedKeys, out var oversizedStatus));
        Assert.Empty(oversizedKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.LimitExceeded, oversizedStatus);

        var supersedeCurrent = HumanInputRequestStoreTestData.Request("group-supersede-request", "group-supersede-version", HumanInputRequestStoreTestData.Time);
        var supersedeCandidate = HumanInputRequestStoreTestData.Request("group-supersede-candidate", "group-supersede-successor", HumanInputRequestStoreTestData.Time, supersedeCurrent.Binding);
        var supersede = CreateRegistration("group-supersede", supersedeCurrent, supersedeCandidate);
        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([supersede], out var wrongKindKeys, out var wrongKindStatus));
        Assert.Empty(wrongKindKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, wrongKindStatus);

        var malformedPreparation = valid with { PreparationHash = null };
        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([malformedPreparation], out var malformedPreparationKeys, out var malformedPreparationStatus));
        Assert.Empty(malformedPreparationKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, malformedPreparationStatus);

        var nonUtc = valid with { ExpiresAtUtc = now.AddMinutes(5).ToOffset(TimeSpan.FromHours(1)) };
        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([nonUtc], out var nonUtcKeys, out var nonUtcStatus));
        Assert.Empty(nonUtcKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, nonUtcStatus);

        var expired = valid with { ExpiresAtUtc = now.AddSeconds(-1) };
        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([expired], out var expiredKeys, out var expiredStatus));
        Assert.Empty(expiredKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, expiredStatus);

        var tooLong = valid with { ExpiresAtUtc = now.AddMinutes(16) };
        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([tooLong], out var tooLongKeys, out var tooLongStatus));
        Assert.Empty(tooLongKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, tooLongStatus);

        var invalidReference = valid with { ExpectedRequest = valid.ExpectedRequest with { RequestHash = "invalid" } };
        Assert.False(new HumanInputSupersedeCandidateRegistry(clock).TryRegisterGroup([invalidReference], out var invalidReferenceKeys, out var invalidReferenceStatus));
        Assert.Empty(invalidReferenceKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, invalidReferenceStatus);
    }

    [Fact]
    public void Group_registration_rejects_duplicate_mismatched_and_partial_groups()
    {
        var now = DateTimeOffset.UtcNow;
        var first = CreateRerouteRegistration("group-conflicts", now.AddMinutes(5));
        var secondCandidate = HumanInputRequestHash.Apply(first.CandidateRequest with
        {
            RequestVersionId = "group-conflicts-second-version",
            EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            RequestHash = string.Empty
        });
        var second = first with { CandidateRequest = secondCandidate };

        var duplicateRegistry = new HumanInputSupersedeCandidateRegistry();
        Assert.False(duplicateRegistry.TryRegisterGroup([first, first], out var duplicateKeys, out var duplicateStatus));
        Assert.Empty(duplicateKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, duplicateStatus);

        var mismatchedRegistry = new HumanInputSupersedeCandidateRegistry();
        Assert.False(mismatchedRegistry.TryRegisterGroup([first, second with { OperationId = "group-other-operation" }], out var mismatchedKeys, out var mismatchedStatus));
        Assert.Empty(mismatchedKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, mismatchedStatus);

        var partialRegistry = new HumanInputSupersedeCandidateRegistry();
        Assert.True(partialRegistry.TryRegisterGroup([first], out var firstKeys, out var firstStatus));
        Assert.Single(firstKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, firstStatus);
        Assert.False(partialRegistry.TryRegisterGroup([first, second], out var partialKeys, out var partialStatus));
        Assert.Empty(partialKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, partialStatus);
    }

    [Fact]
    public void Single_registration_rejects_operation_and_group_conflicts()
    {
        var now = DateTimeOffset.UtcNow;
        var current = HumanInputRequestStoreTestData.Request("single-conflict-request", "single-conflict-version", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestStoreTestData.Request("single-conflict-candidate", "single-conflict-successor", HumanInputRequestStoreTestData.Time, current.Binding);
        var registry = new HumanInputSupersedeCandidateRegistry(new HumanInputFixedTimeProvider(now));
        var first = CreateRegistration("single-conflict-operation", current, candidate, now.AddMinutes(5));

        Assert.True(registry.TryRegister(first, out _));

        var changedExpiry = first with { ExpiresAtUtc = now.AddMinutes(6) };
        Assert.False(registry.TryRegister(changedExpiry, out var changedExpiryKey, out var changedExpiryStatus));
        Assert.Empty(changedExpiryKey);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, changedExpiryStatus);

        var changedCandidate = first with
        {
            CandidateRequest = HumanInputRequestStoreTestData.Request("single-conflict-other-candidate", "single-conflict-other-successor", HumanInputRequestStoreTestData.Time, current.Binding)
        };
        Assert.False(registry.TryRegister(changedCandidate, out var changedCandidateKey, out var changedCandidateStatus));
        Assert.Empty(changedCandidateKey);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, changedCandidateStatus);

        var changedPreparation = first with { PreparationHash = HumanInputRequestStoreTestData.HashA };
        Assert.False(registry.TryRegister(changedPreparation, out var changedPreparationKey, out var changedPreparationStatus));
        Assert.Empty(changedPreparationKey);
        Assert.Equal(HumanInputSupersedePreparationStatus.Conflict, changedPreparationStatus);
    }

    [Fact]
    public void Resolve_rejects_invalid_shape_missing_key_and_wrong_operation_kind()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new HumanInputFixedTimeProvider(now);
        var first = CreateRerouteRegistration("resolve-operation", now.AddMinutes(5));
        var second = first with
        {
            CandidateRequest = HumanInputRequestHash.Apply(first.CandidateRequest with
            {
                RequestVersionId = "resolve-second-version",
                EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
                RequestHash = string.Empty
            })
        };
        var registry = new HumanInputSupersedeCandidateRegistry(clock);
        Assert.True(registry.TryRegisterGroup([first, second], out var keys, out var status));
        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, status);

        var key = keys[0];
        Assert.False(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Unknown, key, first.WorkspaceId, first.Actor, first.OperationId, first.RequestId, first.ExpectedLifecycleVersion, first.ExpectedRequest.RequestVersionId, first.ExpectedRequest.RequestHash, now, out _));
        Assert.False(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Reroute, string.Empty, first.WorkspaceId, first.Actor, first.OperationId, first.RequestId, first.ExpectedLifecycleVersion, first.ExpectedRequest.RequestVersionId, first.ExpectedRequest.RequestHash, now, out _));
        Assert.False(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Reroute, "missing-key", first.WorkspaceId, first.Actor, first.OperationId, first.RequestId, first.ExpectedLifecycleVersion, first.ExpectedRequest.RequestVersionId, first.ExpectedRequest.RequestHash, now, out _));
        Assert.False(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Supersede, key, first.WorkspaceId, first.Actor, first.OperationId, first.RequestId, first.ExpectedLifecycleVersion, first.ExpectedRequest.RequestVersionId, first.ExpectedRequest.RequestHash, now, out _));
        Assert.False(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Reroute, key, first.WorkspaceId, first.Actor, first.OperationId, first.RequestId, first.ExpectedLifecycleVersion, first.ExpectedRequest.RequestVersionId, first.ExpectedRequest.RequestHash, default, out _));
        Assert.True(registry.TryResolve(HumanInputRequestLifecycleOperationKind.Reroute, key, first.WorkspaceId, first.Actor, first.OperationId, first.RequestId, first.ExpectedLifecycleVersion, first.ExpectedRequest.RequestVersionId, first.ExpectedRequest.RequestHash, now, out var resolution));
        Assert.NotNull(resolution);
    }

    [Fact]
    public void Default_and_throwing_clocks_fail_closed_for_registration_and_groups()
    {
        var registration = CreateRegistration("default-clock-operation", HumanInputRequestStoreTestData.Request("default-clock-request", "default-clock-version", HumanInputRequestStoreTestData.Time), HumanInputRequestStoreTestData.Request("default-clock-candidate", "default-clock-successor", HumanInputRequestStoreTestData.Time));
        Assert.False(new HumanInputSupersedeCandidateRegistry(new HumanInputFixedTimeProvider(default)).TryRegister(registration, out var defaultKey, out var defaultStatus));
        Assert.Empty(defaultKey);
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, defaultStatus);

        var reroute = CreateRerouteRegistration("throwing-clock-operation", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.False(new HumanInputSupersedeCandidateRegistry(new HumanInputThrowingTimeProvider()).TryRegisterGroup([reroute], out var throwingKeys, out var throwingStatus));
        Assert.Empty(throwingKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, throwingStatus);

        Assert.False(new HumanInputSupersedeCandidateRegistry(new HumanInputFixedTimeProvider(default)).TryRegisterGroup([reroute], out var defaultGroupKeys, out var defaultGroupStatus));
        Assert.Empty(defaultGroupKeys);
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, defaultGroupStatus);
    }

    private static HumanInputSupersedeCandidateRegistration CreateRegistration(
        string operationId,
        HumanInputRequest current,
        HumanInputRequest candidate,
        DateTimeOffset? expiresAtUtc = null,
        HumanInputRequestLifecycleOperationKind kind = HumanInputRequestLifecycleOperationKind.Supersede,
        string? preparationHash = null)
        => new(current.Binding.WorkspaceId, Actor, operationId, current.RequestId, 1, HumanInputRequestStoreTestData.Reference(current), candidate, HumanInputRequestStoreTestData.CreateMutation().Operation.GrantReference!, expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(5), kind, preparationHash);

    private static HumanInputSupersedeCandidateRegistration CreateAmendRegistration(string operationId, DateTimeOffset expiresAtUtc)
    {
        var current = HumanInputRequestStoreTestData.Request($"{operationId}-request", $"{operationId}-version", HumanInputRequestStoreTestData.Time);
        var candidate = HumanInputRequestHash.Apply(current with { RequestVersionId = $"{operationId}-amended", Prompt = "Amended bounded prompt.", RequestHash = string.Empty });
        return CreateRegistration(operationId, current, candidate, expiresAtUtc, HumanInputRequestLifecycleOperationKind.Amend, HumanInputRequestStoreTestData.HashA);
    }

    private static HumanInputSupersedeCandidateRegistration CreateRerouteRegistration(string operationId, DateTimeOffset expiresAtUtc)
    {
        var current = HumanInputRequestHash.Apply(HumanInputRequestStoreTestData.Request($"{operationId}-request", $"{operationId}-version", HumanInputRequestStoreTestData.Time) with
        {
            EligibleRespondents = [
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            RequestHash = string.Empty
        });
        var candidate = HumanInputRequestHash.Apply(current with
        {
            RequestVersionId = $"{operationId}-rerouted",
            EligibleRespondents = [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            RequestHash = string.Empty
        });
        return CreateRegistration(operationId, current, candidate, expiresAtUtc, HumanInputRequestLifecycleOperationKind.Reroute, HumanInputRequestStoreTestData.HashA);
    }
}
