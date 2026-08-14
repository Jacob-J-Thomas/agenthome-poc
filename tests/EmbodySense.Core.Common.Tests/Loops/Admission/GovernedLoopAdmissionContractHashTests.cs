using System.Globalization;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Tests.Authority.Grants;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionContractHashTests
{
    [Fact]
    public void Canonical_hashes_are_deterministic_lowercase_and_domain_separated()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent, evidence);
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
        var outcome = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        var hashes = new[]
        {
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            GovernedLoopAdmissionContractHash.ComputeEvidenceHash(evidence),
            GovernedLoopAdmissionContractHash.ComputeReceiptHash(receipt),
            GovernedLoopAdmissionContractHash.ComputeRejectionHash(rejection),
            GovernedLoopAdmissionContractHash.ComputeTerminalOutcomeHash(outcome)
        };

        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(hashes, AssertCanonicalHash);
        Assert.Equal(hashes[0], GovernedLoopAdmissionContractHash.ComputeIntentHash(intent with { }));
        Assert.True(GovernedLoopAdmissionContractHash.Matches(evidence));
        Assert.True(GovernedLoopAdmissionContractHash.Matches(receipt));
        Assert.True(GovernedLoopAdmissionContractHash.Matches(rejection));
        Assert.True(GovernedLoopAdmissionContractHash.Matches(outcome));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("operation")]
    [InlineData("request")]
    [InlineData("publication")]
    [InlineData("grant")]
    [InlineData("role")]
    [InlineData("actor")]
    [InlineData("surface")]
    [InlineData("artifact")]
    [InlineData("layout")]
    public void Intent_hash_binds_every_stable_field(string field)
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var changed = field switch
        {
            "workspace" => intent with { WorkspaceId = "workspace-sha256:" + GovernedLoopAdmissionTestFixture.Hash('9') },
            "operation" => intent with { OperationId = "admit-operation-2" },
            "request" => intent with { RequestHash = GovernedLoopAdmissionTestFixture.Hash('4') },
            "publication" => intent with { Publication = intent.Publication with { PublicationOperationId = "publish-8" } },
            "grant" => intent with
            {
                AuthorityGrant = new AuthorityGrantReference(
                    intent.AuthorityGrant.GrantId,
                    intent.AuthorityGrant.Revision,
                    "sha256:" + GovernedLoopAdmissionTestFixture.Hash('5'))
            },
            "role" => intent with
            {
                Role = new ContextualRoleRevisionPin(
                    new ContextualRoleRevisionIdentity(intent.Role.Identity.RoleId, intent.Role.Identity.Revision + 1),
                    intent.Role.ContentHash)
            },
            "actor" => intent with { ActorId = AuthorityGrantTestFixture.Actor("user-reviewer") },
            "surface" => intent with { Surface = "web" },
            "artifact" => intent with { GraphArtifactHash = GovernedLoopAdmissionTestFixture.Hash('6') },
            "layout" => intent with { GraphLayoutHash = GovernedLoopAdmissionTestFixture.Hash('7') },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.True(GovernedLoopAdmissionValidator.Validate(changed).IsValid);
        Assert.NotEqual(
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            GovernedLoopAdmissionContractHash.ComputeIntentHash(changed));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("binding")]
    [InlineData("grant-profile")]
    [InlineData("grant-boundary")]
    [InlineData("grant-dependencies")]
    [InlineData("authority")]
    [InlineData("capabilities")]
    [InlineData("references")]
    [InlineData("time")]
    public void Evidence_hash_binds_every_success_evidence_dimension(string field)
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var changedReferences = evidence.References.ToArray();
        changedReferences[0] = changedReferences[0] with { EvidenceHash = GovernedLoopAdmissionTestFixture.Hash('f') };
        var changed = field switch
        {
            "intent" => NewEvidence(evidence, intentHash: GovernedLoopAdmissionTestFixture.Hash('f')),
            "binding" => NewEvidence(
                evidence,
                binding: GovernedLoopExecutionBinding.Create(1, "run-2", evidence.Binding.Revision, 1)),
            "grant-profile" => NewEvidence(
                evidence,
                grantProfile: AuthorityGrantTestFixture.Binding(profileHash: 'f').Profile),
            "grant-boundary" => NewEvidence(
                evidence,
                grantBoundary: new AuthorityGrantBoundary(
                    evidence.GrantBoundary.EffectiveAtUtc.AddSeconds(-1),
                    evidence.GrantBoundary.ExpiresAtUtc,
                    evidence.GrantBoundary.CompletionConstraint)),
            "grant-dependencies" => NewEvidence(
                evidence,
                grantDependencyEvidenceHash: GovernedLoopAdmissionTestFixture.Hash('8')),
            "authority" => NewEvidence(
                evidence,
                effectiveAuthority: GovernedLoopAdmissionTestFixture.EffectiveAuthority(maxTargetCount: 3)),
            "capabilities" => NewEvidence(
                evidence,
                capabilityAdmission: GovernedLoopAdmissionTestFixture.CapabilityAdmission(
                    GovernedLoopAdmissionTestFixture.CapabilityAdmittedAtUtc.AddSeconds(1))),
            "references" => NewEvidence(evidence, references: changedReferences),
            "time" => NewEvidence(evidence, evaluatedAtUtc: evidence.EvaluatedAtUtc.AddSeconds(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.NotEqual(
            GovernedLoopAdmissionContractHash.ComputeEvidenceHash(evidence),
            GovernedLoopAdmissionContractHash.ComputeEvidenceHash(changed));
    }

    [Fact]
    public void Receipt_rejection_and_terminal_hashes_bind_nested_content_and_times()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent);
        var laterReceipt = GovernedLoopAdmissionTestFixture.Receipt(
            intent,
            recordedAtUtc: receipt.RecordedAtUtc.AddSeconds(1));
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
        var otherRejection = GovernedLoopAdmissionTestFixture.Rejection(
            intent,
            GovernedLoopAdmissionFailureCode.RoleReplaced,
            rejectedAtUtc: rejection.RejectedAtUtc.AddSeconds(1));
        var admitted = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        var rejected = GovernedLoopAdmissionTestFixture.RejectedOutcome(intent, rejection);

        Assert.NotEqual(
            GovernedLoopAdmissionContractHash.ComputeReceiptHash(receipt),
            GovernedLoopAdmissionContractHash.ComputeReceiptHash(laterReceipt));
        Assert.NotEqual(
            GovernedLoopAdmissionContractHash.ComputeRejectionHash(rejection),
            GovernedLoopAdmissionContractHash.ComputeRejectionHash(otherRejection));
        Assert.NotEqual(
            GovernedLoopAdmissionContractHash.ComputeTerminalOutcomeHash(admitted),
            GovernedLoopAdmissionContractHash.ComputeTerminalOutcomeHash(rejected));
    }

    [Fact]
    public void Stored_hashes_are_recomputed_and_malformed_nested_contracts_cannot_be_recertified()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent, evidence);
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
        var outcome = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        var malformedCapabilities = evidence.CapabilityAdmission with
        {
            RequirementsHash = "sha256:" + GovernedLoopAdmissionTestFixture.Hash('f')
        };
        var malformedEvidence = NewEvidence(evidence, capabilityAdmission: malformedCapabilities);

        Assert.False(GovernedLoopAdmissionContractHash.Matches(evidence with { ContentHash = GovernedLoopAdmissionTestFixture.Hash('f') }));
        Assert.False(GovernedLoopAdmissionContractHash.Matches(receipt with { ContentHash = GovernedLoopAdmissionTestFixture.Hash('f') }));
        Assert.False(GovernedLoopAdmissionContractHash.Matches(rejection with { ContentHash = GovernedLoopAdmissionTestFixture.Hash('f') }));
        Assert.False(GovernedLoopAdmissionContractHash.Matches(outcome with { ContentHash = GovernedLoopAdmissionTestFixture.Hash('f') }));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeEvidenceHash(malformedEvidence));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeIntentHash(intent with { Role = null! }));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeReceiptHash(receipt with
        {
            Evidence = evidence with { ContentHash = GovernedLoopAdmissionTestFixture.Hash('e') }
        }));
    }

    [Fact]
    public void Hashing_null_or_structurally_invalid_contracts_fails_closed()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();

        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeIntentHash(null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeEvidenceHash(null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeReceiptHash(null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeRejectionHash(null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeTerminalOutcomeHash(null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopAdmissionContractHash.Apply((GovernedLoopAdmissionEvidence)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopAdmissionContractHash.Apply((GovernedLoopAdmissionReceipt)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopAdmissionContractHash.Apply((GovernedLoopAdmissionRejection)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopAdmissionContractHash.Apply((GovernedLoopAdmissionTerminalOutcome)null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.CreateEvidenceReferences(
            intent with { Publication = null! },
            GovernedLoopAdmissionTestFixture.EffectiveAuthority(),
            GovernedLoopAdmissionTestFixture.CapabilityAdmission()));
    }

    [Fact]
    public void Canonical_authority_hash_is_order_independent_and_collection_boundaries_are_explicit()
    {
        var authority = AuthorityGrantTestFixture.Ceiling(
            capabilities:
            [
                AuthorityGrantTestFixture.Capability("org.embodysense/workspace/read-file", "1.0.0", 'a'),
                AuthorityGrantTestFixture.Capability("org.embodysense/workspace/write-file", "1.0.0", 'b')
            ],
            dataClasses:
            [
                AuthorityGrantTestFixture.DataClass("private-content"),
                AuthorityGrantTestFixture.DataClass("workspace-content")
            ]);
        var reordered = new AuthorityCeiling(
            authority.Capabilities.Reverse().ToArray(),
            authority.DataClasses.Reverse().ToArray(),
            authority.MaxTargetCount,
            authority.MaxSideEffectClass,
            authority.AllowsRecurrence,
            authority.AllowsExternalPublication,
            authority.AllowsIrreversibleAction);
        var fewerDataClasses = new AuthorityCeiling(
            authority.Capabilities,
            authority.DataClasses.Skip(1).ToArray(),
            authority.MaxTargetCount,
            authority.MaxSideEffectClass,
            authority.AllowsRecurrence,
            authority.AllowsExternalPublication,
            authority.AllowsIrreversibleAction);

        Assert.Equal(
            GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(authority),
            GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(reordered));
        Assert.NotEqual(
            GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(authority),
            GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(fewerDataClasses));
    }

    [Fact]
    public void Canonical_hashes_are_culture_independent()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var expected = GovernedLoopAdmissionContractHash.ComputeIntentHash(intent);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, GovernedLoopAdmissionContractHash.ComputeIntentHash(intent));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static GovernedLoopAdmissionEvidence NewEvidence(
        GovernedLoopAdmissionEvidence value,
        string? intentHash = null,
        GovernedLoopExecutionBinding? binding = null,
        AuthorityGrantProfilePin? grantProfile = null,
        AuthorityGrantBoundary? grantBoundary = null,
        string? grantDependencyEvidenceHash = null,
        AuthorityCeiling? effectiveAuthority = null,
        CapabilityAdmissionSnapshot? capabilityAdmission = null,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        DateTimeOffset? evaluatedAtUtc = null)
        => new(
            value.SchemaVersion,
            intentHash ?? value.IntentHash,
            binding ?? value.Binding,
            grantProfile ?? value.GrantProfile,
            grantBoundary ?? value.GrantBoundary,
            grantDependencyEvidenceHash ?? value.GrantDependencyEvidenceHash,
            effectiveAuthority ?? value.EffectiveAuthority,
            capabilityAdmission ?? value.CapabilityAdmission,
            references ?? value.References,
            evaluatedAtUtc ?? value.EvaluatedAtUtc,
            string.Empty);

    private static void AssertCanonicalHash(string hash)
    {
        Assert.Equal(GovernedLoopAdmissionLimits.Sha256HexCharacters, hash.Length);
        Assert.All(hash, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
