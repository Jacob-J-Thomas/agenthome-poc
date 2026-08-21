using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

public sealed class AuthorityDelegationSubsetEvaluatorTests
{
    [Fact]
    public void Evaluate_AcceptsEqualAuthorityBecauseEnvelopeAddsExactRestrictions()
    {
        var ceiling = AuthorityDelegationTestFixture.Ceiling();
        var pins = new[] { AuthorityDelegationTestFixture.Pin() };
        var ids = Ids(ceiling.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(ceiling, pins, ids, ids, ids, ceiling, pins, AuthorityDelegationTestFixture.Hash('1'), AuthorityDelegationTestFixture.Hash('2'));

        Assert.NotNull(proof);
        Assert.Empty(proof.NarrowingDimensions);
        Assert.True(AuthorityDelegationContractValidator.Validate(proof).IsValid);
    }

    [Theory]
    [InlineData(AuthorityDelegationNarrowingDimension.CapabilityIdentitySet)]
    [InlineData(AuthorityDelegationNarrowingDimension.DataClassSet)]
    [InlineData(AuthorityDelegationNarrowingDimension.TargetCount)]
    [InlineData(AuthorityDelegationNarrowingDimension.SideEffectClass)]
    [InlineData(AuthorityDelegationNarrowingDimension.Recurrence)]
    [InlineData(AuthorityDelegationNarrowingDimension.ExternalPublication)]
    [InlineData(AuthorityDelegationNarrowingDimension.IrreversibleAction)]
    public void Evaluate_RecordsEveryActuallyNarrowedDimension(AuthorityDelegationNarrowingDimension dimension)
    {
        var parentPin = AuthorityDelegationTestFixture.Pin();
        var parent = AuthorityDelegationTestFixture.Ceiling();
        var delegated = dimension switch
        {
            AuthorityDelegationNarrowingDimension.CapabilityIdentitySet => AuthorityDelegationTestFixture.Ceiling(capabilities: [], dataClasses: parent.DataClasses),
            AuthorityDelegationNarrowingDimension.DataClassSet => AuthorityDelegationTestFixture.Ceiling(dataClasses: []),
            AuthorityDelegationNarrowingDimension.TargetCount => AuthorityDelegationTestFixture.Ceiling(maxTargetCount: parent.MaxTargetCount - 1),
            AuthorityDelegationNarrowingDimension.SideEffectClass => AuthorityDelegationTestFixture.Ceiling(sideEffectClass: CapabilitySideEffectClass.None),
            AuthorityDelegationNarrowingDimension.Recurrence => AuthorityDelegationTestFixture.Ceiling(recurrence: false),
            AuthorityDelegationNarrowingDimension.ExternalPublication => AuthorityDelegationTestFixture.Ceiling(externalPublication: false),
            AuthorityDelegationNarrowingDimension.IrreversibleAction => AuthorityDelegationTestFixture.Ceiling(irreversible: false),
            _ => throw new InvalidOperationException(),
        };
        var parentPins = new[] { parentPin };
        var delegatedPins = delegated.Capabilities.Count == 0 ? [] : parentPins;
        var ids = Ids(parent.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(parent, parentPins, ids, ids, ids, delegated, delegatedPins, AuthorityDelegationTestFixture.Hash('1'), AuthorityDelegationTestFixture.Hash('2'));

        Assert.NotNull(proof);
        Assert.Contains(dimension, proof.NarrowingDimensions);
    }

    [Fact]
    public void Evaluate_RejectsEveryAuthorityWidening()
    {
        var pin = AuthorityDelegationTestFixture.Pin();
        var parent = AuthorityDelegationTestFixture.Ceiling(maxTargetCount: 2, recurrence: false, externalPublication: false, irreversible: false);
        var ids = Ids(parent.Capabilities);
        var widened = new[]
        {
            AuthorityDelegationTestFixture.Ceiling(maxTargetCount: 3, recurrence: false, externalPublication: false, irreversible: false),
            AuthorityDelegationTestFixture.Ceiling(maxTargetCount: 2, recurrence: true, externalPublication: false, irreversible: false),
            AuthorityDelegationTestFixture.Ceiling(maxTargetCount: 2, recurrence: false, externalPublication: true, irreversible: false),
            AuthorityDelegationTestFixture.Ceiling(maxTargetCount: 2, recurrence: false, externalPublication: false, irreversible: true),
        };

        Assert.All(widened, candidate => Assert.Null(AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [pin],
            ids,
            ids,
            ids,
            candidate,
            [pin],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'))));
    }

    [Fact]
    public void Evaluate_RejectsCapabilityUnionWidening()
    {
        var parentPin = AuthorityDelegationTestFixture.Pin();
        var foreignPin = AuthorityDelegationTestFixture.ForeignPin();
        var parent = AuthorityDelegationTestFixture.Ceiling();
        var widenedPins = new[] { parentPin, foreignPin }
            .OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var widened = AuthorityDelegationTestFixture.Ceiling(
            capabilities: widenedPins.Select(pin => pin.DescriptorIdentity).ToArray());
        var targetIds = Ids(widened.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [parentPin],
            targetIds,
            targetIds,
            targetIds,
            widened,
            widenedPins,
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Fact]
    public void Evaluate_RejectsDataClassUnionWidening()
    {
        var pin = AuthorityDelegationTestFixture.Pin();
        var parent = AuthorityDelegationTestFixture.Ceiling();
        var widened = AuthorityDelegationTestFixture.Ceiling(
            dataClasses: parent.DataClasses.Concat([AuthorityDelegationTestFixture.DataClass("zz-content")]).ToArray());
        var ids = Ids(parent.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [pin],
            ids,
            ids,
            ids,
            widened,
            [pin],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Fact]
    public void Evaluate_RejectsSideEffectClassWidening()
    {
        var pin = AuthorityDelegationTestFixture.Pin();
        var parent = AuthorityDelegationTestFixture.Ceiling(sideEffectClass: CapabilitySideEffectClass.ReadOnly);
        var widened = AuthorityDelegationTestFixture.Ceiling(sideEffectClass: CapabilitySideEffectClass.LocalReversible);
        var ids = Ids(parent.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [pin],
            ids,
            ids,
            ids,
            widened,
            [pin],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Fact]
    public void Evaluate_RejectsCapabilityOutsideExactNodeMaximum()
    {
        var parent = AuthorityDelegationTestFixture.Ceiling();
        var ids = Ids(parent.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [AuthorityDelegationTestFixture.Pin()],
            ids,
            ids,
            [],
            parent,
            [AuthorityDelegationTestFixture.Pin()],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("loop")]
    [InlineData("node")]
    public void Evaluate_RejectsCapabilityOutsideEveryExactTargetMaximum(string maximum)
    {
        var parent = AuthorityDelegationTestFixture.Ceiling();
        var ids = Ids(parent.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [AuthorityDelegationTestFixture.Pin()],
            maximum == "role" ? [] : ids,
            maximum == "loop" ? [] : ids,
            maximum == "node" ? [] : ids,
            parent,
            [AuthorityDelegationTestFixture.Pin()],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Theory]
    [InlineData("loop-outside-role")]
    [InlineData("node-outside-loop")]
    public void Evaluate_RejectsIncomparableTargetMaximumsEvenWhenDelegatedCapabilitiesAreInTheirIntersection(string posture)
    {
        var parent = AuthorityDelegationTestFixture.Ceiling();
        var delegatedIds = Ids(parent.Capabilities);
        var roleOnly = "org.embodysense/role-only";
        var loopOnly = "org.embodysense/loop-only";
        var roleIds = delegatedIds.Concat(posture == "loop-outside-role" ? [roleOnly] : [roleOnly, loopOnly]).Order(StringComparer.Ordinal).ToArray();
        var loopIds = delegatedIds.Concat(posture == "loop-outside-role" ? [loopOnly] : []).Order(StringComparer.Ordinal).ToArray();
        var nodeIds = delegatedIds.Concat(posture == "node-outside-loop" ? [loopOnly] : []).Order(StringComparer.Ordinal).ToArray();

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            parent,
            [AuthorityDelegationTestFixture.Pin()],
            roleIds,
            loopIds,
            nodeIds,
            parent,
            [AuthorityDelegationTestFixture.Pin()],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Fact]
    public void Evaluate_RejectsAbsentOrMalformedTargetMaximumEvidenceHash()
    {
        var ceiling = AuthorityDelegationTestFixture.Ceiling();
        var pins = new[] { AuthorityDelegationTestFixture.Pin() };
        var ids = Ids(ceiling.Capabilities);

        Assert.Null(AuthorityDelegationSubsetEvaluator.Evaluate(ceiling, pins, ids, ids, ids, ceiling, pins, AuthorityDelegationTestFixture.Hash('1'), null));
        Assert.Null(AuthorityDelegationSubsetEvaluator.Evaluate(ceiling, pins, ids, ids, ids, ceiling, pins, AuthorityDelegationTestFixture.Hash('1'), "not-a-hash"));
    }

    [Fact]
    public void Evaluate_RejectsRepinnedCapabilityEvenWhenDescriptorIdentityMatches()
    {
        var parentPin = AuthorityDelegationTestFixture.Pin();
        var changedPin = parentPin with { SafeDescription = parentPin.SafeDescription + " changed" };
        var ceiling = AuthorityDelegationTestFixture.Ceiling();
        var ids = Ids(ceiling.Capabilities);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            ceiling,
            [parentPin],
            ids,
            ids,
            ids,
            ceiling,
            [changedPin],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.Null(proof);
    }

    [Fact]
    public void Evaluate_RejectsEveryExactPinFieldSubstitution()
    {
        var parentPin = AuthorityDelegationTestFixture.Pin();
        Assert.True(CapabilityVersion.TryParse("2.0.0", out var version, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('f', 64), out var hash, out _));
        var substitutions = new[]
        {
            parentPin with
            {
                DescriptorIdentity = parentPin.DescriptorIdentity with { Version = version! },
            },
            parentPin with
            {
                DescriptorIdentity = parentPin.DescriptorIdentity with { Hash = hash! },
            },
            parentPin with
            {
                Implementation = parentPin.Implementation with
                {
                    ImplementationId = parentPin.Implementation.ImplementationId + "-substituted",
                },
            },
            parentPin with
            {
                Provenance = parentPin.Provenance with { SourceRevision = "substituted-revision" },
            },
        };
        var parent = AuthorityDelegationTestFixture.Ceiling();

        Assert.All(substitutions, substituted =>
        {
            var delegated = AuthorityDelegationTestFixture.Ceiling(capabilities: [substituted.DescriptorIdentity]);
            var ids = Ids(delegated.Capabilities);
            Assert.Null(AuthorityDelegationSubsetEvaluator.Evaluate(
                parent,
                [parentPin],
                ids,
                ids,
                ids,
                delegated,
                [substituted],
                AuthorityDelegationTestFixture.Hash('1'),
                AuthorityDelegationTestFixture.Hash('2')));
        });
    }

    [Fact]
    public void Evaluate_AcceptsCanonicalEmptyAuthority()
    {
        var empty = AuthorityDelegationTestFixture.Ceiling(
            capabilities: [],
            dataClasses: [],
            maxTargetCount: 0,
            sideEffectClass: CapabilitySideEffectClass.None,
            recurrence: false,
            externalPublication: false,
            irreversible: false);

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            empty,
            [],
            [],
            [],
            [],
            empty,
            [],
            AuthorityDelegationTestFixture.Hash('1'),
            AuthorityDelegationTestFixture.Hash('2'));

        Assert.NotNull(proof);
        Assert.Empty(proof.NarrowingDimensions);
    }

    private static IReadOnlyList<string> Ids(IReadOnlyList<EmbodySense.Core.Common.Capabilities.CapabilityDescriptorIdentity> capabilities)
        => capabilities.Select(value => value.Id.Value).Order(StringComparer.Ordinal).ToArray();
}
