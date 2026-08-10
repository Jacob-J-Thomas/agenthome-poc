using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Tests.Authority.Grants;

public sealed class AuthorityCeilingSubsetTests
{
    [Fact]
    public void Exact_requested_capability_identity_must_exist_in_profile_and_its_id_in_role_and_loop_maxima()
    {
        var requestedIdentity = AuthorityGrantTestFixture.Capability();
        var requested = AuthorityGrantTestFixture.Ceiling([requestedIdentity]);
        var profile = AuthorityGrantTestFixture.Ceiling([requestedIdentity], maxTargetCount: 10, maxSideEffectClass: CapabilitySideEffectClass.Irreversible, allowsRecurrence: true, allowsExternalPublication: true, allowsIrreversibleAction: true);
        var id = requestedIdentity.Id.Value;

        Assert.True(AuthorityCeilingSubset.Validate(requested, profile, [id], [id]).IsSubset);

        var changedVersion = AuthorityGrantTestFixture.Capability(version: "1.2.4");
        AssertViolation(AuthorityCeilingSubset.Validate(AuthorityGrantTestFixture.Ceiling([changedVersion]), profile, [id], [id]), AuthorityCeilingSubsetViolationCode.CapabilityIdentityOutsideProfile);

        var changedHash = AuthorityGrantTestFixture.Capability(hash: 'f');
        AssertViolation(AuthorityCeilingSubset.Validate(AuthorityGrantTestFixture.Ceiling([changedHash]), profile, [id], [id]), AuthorityCeilingSubsetViolationCode.CapabilityIdentityOutsideProfile);
        AssertViolation(AuthorityCeilingSubset.Validate(requested, profile, [], [id]), AuthorityCeilingSubsetViolationCode.CapabilityIdOutsideRole);
        AssertViolation(AuthorityCeilingSubset.Validate(requested, profile, [id], []), AuthorityCeilingSubsetViolationCode.CapabilityIdOutsideLoop);
    }

    [Fact]
    public void Every_non_capability_ceiling_dimension_is_monotonically_bounded_by_profile()
    {
        var profile = AuthorityGrantTestFixture.Ceiling(
            dataClasses: [AuthorityGrantTestFixture.DataClass("workspace-content")],
            maxTargetCount: 5,
            maxSideEffectClass: CapabilitySideEffectClass.ReadOnly,
            allowsRecurrence: false,
            allowsExternalPublication: false,
            allowsIrreversibleAction: false);

        var candidates = new (EmbodySense.Core.Common.Authority.Models.AuthorityCeiling Ceiling, AuthorityCeilingSubsetViolationCode Code)[]
        {
            (AuthorityGrantTestFixture.Ceiling(dataClasses: [AuthorityGrantTestFixture.DataClass("user-content")]), AuthorityCeilingSubsetViolationCode.DataClassOutsideProfile),
            (AuthorityGrantTestFixture.Ceiling(maxTargetCount: 6), AuthorityCeilingSubsetViolationCode.TargetCountExceedsProfile),
            (AuthorityGrantTestFixture.Ceiling(maxSideEffectClass: CapabilitySideEffectClass.LocalReversible), AuthorityCeilingSubsetViolationCode.SideEffectClassExceedsProfile),
            (AuthorityGrantTestFixture.Ceiling(allowsRecurrence: true), AuthorityCeilingSubsetViolationCode.RecurrenceExceedsProfile),
            (AuthorityGrantTestFixture.Ceiling(allowsExternalPublication: true), AuthorityCeilingSubsetViolationCode.ExternalPublicationExceedsProfile),
            (AuthorityGrantTestFixture.Ceiling(allowsIrreversibleAction: true), AuthorityCeilingSubsetViolationCode.IrreversibleActionExceedsProfile),
        };

        foreach (var (candidate, code) in candidates)
        {
            AssertViolation(AuthorityCeilingSubset.Validate(candidate, profile, [], []), code);
        }
    }

    [Fact]
    public void Equal_and_strict_subset_comparisons_are_order_independent_and_cover_all_dimensions()
    {
        var capabilityA = AuthorityGrantTestFixture.Capability();
        var capabilityB = AuthorityGrantTestFixture.Capability("org.embodysense/workspace/write-file", "2.0.0", 'f');
        var first = AuthorityGrantTestFixture.Ceiling([capabilityA, capabilityB], [AuthorityGrantTestFixture.DataClass("user-content"), AuthorityGrantTestFixture.DataClass("workspace-content")], 5, CapabilitySideEffectClass.LocalReversible, true, false, false);
        var reordered = AuthorityGrantTestFixture.Ceiling([capabilityB, capabilityA], [AuthorityGrantTestFixture.DataClass("workspace-content"), AuthorityGrantTestFixture.DataClass("user-content")], 5, CapabilitySideEffectClass.LocalReversible, true, false, false);
        var strict = AuthorityGrantTestFixture.Ceiling([capabilityA], [AuthorityGrantTestFixture.DataClass("workspace-content")], 4, CapabilitySideEffectClass.ReadOnly, false, false, false);

        Assert.True(AuthorityCeilingSubset.IsEqual(first, reordered));
        Assert.False(AuthorityCeilingSubset.IsStrictSubset(reordered, first));
        Assert.True(AuthorityCeilingSubset.IsStrictSubset(strict, first));
        Assert.False(AuthorityCeilingSubset.IsEqual(null, first));
        Assert.False(AuthorityCeilingSubset.IsStrictSubset(null, first));
    }

    [Fact]
    public void Capability_maximum_inputs_use_their_own_finite_role_and_loop_bounds_and_reject_duplicates()
    {
        var requested = AuthorityGrantTestFixture.Ceiling();
        var roleIds = Enumerable.Range(0, ContextualRoleLimits.MaxCapabilityMaximums).Select(index => $"org.example/capability-{index}").ToArray();
        var loopIds = Enumerable.Range(0, CustomLoopLimits.MaxGraphAuthorityCapabilities).Select(index => $"org.example/capability-{index}").ToArray();
        Assert.True(AuthorityCeilingSubset.Validate(requested, requested, roleIds, loopIds).IsSubset);

        AssertViolation(AuthorityCeilingSubset.Validate(requested, requested, roleIds.Append(roleIds[0]).ToArray(), loopIds), AuthorityCeilingSubsetViolationCode.InvalidContract);
        AssertViolation(AuthorityCeilingSubset.Validate(requested, requested, roleIds, loopIds.Append("org.example/overflow").ToArray()), AuthorityCeilingSubsetViolationCode.InvalidContract);
        AssertViolation(AuthorityCeilingSubset.Validate(requested, requested, ["not-a-capability-id"], []), AuthorityCeilingSubsetViolationCode.InvalidContract);
    }

    [Fact]
    public void Capability_maximum_inputs_fail_closed_for_lying_counts_and_throwing_enumerators()
    {
        var requested = AuthorityGrantTestFixture.Ceiling();
        var canonicalId = AuthorityGrantTestFixture.Capability().Id.Value;
        var oversized = Enumerable.Range(0, ContextualRoleLimits.MaxCapabilityMaximums + 1)
            .Select(index => $"org.example/capability-{index}")
            .ToArray();

        AssertViolation(
            AuthorityCeilingSubset.Validate(requested, requested, new LyingCountReadOnlyList<string>(0, [canonicalId]), []),
            AuthorityCeilingSubsetViolationCode.InvalidContract);
        AssertViolation(
            AuthorityCeilingSubset.Validate(requested, requested, new LyingCountReadOnlyList<string>(1, oversized), []),
            AuthorityCeilingSubsetViolationCode.InvalidContract);
        AssertViolation(
            AuthorityCeilingSubset.Validate(requested, requested, new ThrowingEnumerationReadOnlyList<string>(1, new InvalidOperationException("Hostile enumeration.")), []),
            AuthorityCeilingSubsetViolationCode.InvalidContract);
        AssertViolation(
            AuthorityCeilingSubset.Validate(requested, requested, new ThrowingEnumerationReadOnlyList<string>(1, new OperationCanceledException("Untrusted enumeration cancellation.")), []),
            AuthorityCeilingSubsetViolationCode.InvalidContract);
        AssertViolation(
            AuthorityCeilingSubset.Validate(requested, requested, new ThrowingCountReadOnlyList<string>(), []),
            AuthorityCeilingSubsetViolationCode.InvalidContract);
    }

    private static void AssertViolation(AuthorityCeilingSubsetResult result, AuthorityCeilingSubsetViolationCode expected)
    {
        Assert.False(result.IsSubset);
        Assert.Contains(result.Violations, violation => violation.Code == expected);
    }

    private sealed class LyingCountReadOnlyList<T>(int count, IEnumerable<T> values) : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _values = values.ToArray();

        public int Count { get; } = count;

        public T this[int index] => _values[index];

        public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingEnumerationReadOnlyList<T>(int count, Exception failure) : IReadOnlyList<T>
    {
        public int Count { get; } = count;

        public T this[int index] => throw new InvalidOperationException($"Index {index} must not be read.");

        public IEnumerator<T> GetEnumerator() => throw failure;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingCountReadOnlyList<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Hostile capability-maxima count must fail closed.");

        public T this[int index] => throw new InvalidOperationException($"Index {index} must not be read.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Hostile capability maxima must fail closed.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
