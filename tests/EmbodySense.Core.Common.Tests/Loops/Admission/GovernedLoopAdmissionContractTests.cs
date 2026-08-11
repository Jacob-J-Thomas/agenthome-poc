using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionContractTests
{
    [Fact]
    public void Valid_schema_one_contracts_are_accepted_through_the_public_boundary()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent, evidence);
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
        var admitted = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        var rejected = GovernedLoopAdmissionTestFixture.RejectedOutcome(intent, rejection);

        Assert.True(GovernedLoopAdmissionValidator.Validate(intent).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(evidence, intent).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(rejection).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(admitted).IsValid);
        Assert.True(GovernedLoopAdmissionValidator.Validate(rejected).IsValid);
    }

    [Fact]
    public void Null_top_level_contracts_return_structured_required_errors()
    {
        var intent = GovernedLoopAdmissionValidator.Validate((GovernedLoopAdmissionIntent?)null);
        var evidence = GovernedLoopAdmissionValidator.Validate(
            (GovernedLoopAdmissionEvidence?)null,
            GovernedLoopAdmissionTestFixture.Intent());
        var receipt = GovernedLoopAdmissionValidator.Validate((GovernedLoopAdmissionReceipt?)null);
        var rejection = GovernedLoopAdmissionValidator.Validate((GovernedLoopAdmissionRejection?)null);
        var outcome = GovernedLoopAdmissionValidator.Validate((GovernedLoopAdmissionTerminalOutcome?)null);

        Assert.All(
            new[] { intent, evidence, receipt, rejection, outcome },
            result => Assert.Contains(result.Errors, error => error.Code == GovernedLoopAdmissionValidationErrorCode.Required));
    }

    [Fact]
    public void Every_persisted_contract_rejects_unsupported_schema_versions()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent, evidence);
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
        var outcome = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        var schemaTwoEvidence = new GovernedLoopAdmissionEvidence(
            2,
            evidence.IntentHash,
            evidence.Binding,
            evidence.EffectiveAuthority,
            evidence.CapabilityAdmission,
            evidence.References,
            evidence.EvaluatedAtUtc,
            evidence.ContentHash);

        AssertUnsupportedSchema(GovernedLoopAdmissionValidator.Validate(intent with { SchemaVersion = 2 }));
        AssertUnsupportedSchema(GovernedLoopAdmissionValidator.Validate(schemaTwoEvidence, intent));
        AssertUnsupportedSchema(GovernedLoopAdmissionValidator.Validate(receipt with { SchemaVersion = 2 }));
        AssertUnsupportedSchema(GovernedLoopAdmissionValidator.Validate(rejection with { SchemaVersion = 2 }));
        AssertUnsupportedSchema(GovernedLoopAdmissionValidator.Validate(outcome with { SchemaVersion = 2 }));
    }

    [Fact]
    public void Intent_rejects_every_missing_or_malformed_exact_binding()
    {
        var valid = GovernedLoopAdmissionTestFixture.Intent();
        GovernedLoopAdmissionIntent[] invalid =
        [
            valid with { WorkspaceId = null! },
            valid with { OperationId = null! },
            valid with { RequestHash = null! },
            valid with { Publication = null! },
            valid with { AuthorityGrant = null! },
            valid with { Role = null! },
            valid with { ActorId = null! },
            valid with { Surface = null! },
            valid with { GraphArtifactHash = null! },
            valid with { GraphLayoutHash = null! },
            valid with { Role = new ContextualRoleRevisionPin(null!, GovernedLoopAdmissionTestFixture.Hash('a')) },
            valid with { Role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("bounded-helper", 0), GovernedLoopAdmissionTestFixture.Hash('a')) },
            valid with { RequestHash = GovernedLoopAdmissionTestFixture.Hash('A') },
            valid with { GraphArtifactHash = "sha256:" + GovernedLoopAdmissionTestFixture.Hash('a') }
        ];

        Assert.All(invalid, candidate => Assert.False(GovernedLoopAdmissionValidator.Validate(candidate).IsValid));
    }

    [Fact]
    public void Successful_evidence_rejects_missing_malformed_or_unsupported_nested_shapes()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var valid = GovernedLoopAdmissionTestFixture.Evidence(intent);
        GovernedLoopAdmissionEvidence[] invalid =
        [
            CopyEvidence(valid, omitBinding: true),
            CopyEvidence(valid, omitEffectiveAuthority: true),
            CopyEvidence(valid, omitCapabilityAdmission: true),
            CopyEvidence(valid, omitReferences: true),
            CopyEvidence(valid, references: [null!]),
            CopyEvidence(valid, evaluatedAtUtc: (DateTimeOffset)default),
            CopyEvidence(valid, omitIntentHash: true),
            CopyEvidence(valid, omitContentHash: true)
        ];

        Assert.All(invalid, candidate => Assert.False(GovernedLoopAdmissionValidator.Validate(candidate, intent).IsValid));
    }

    [Fact]
    public void Recomputed_evidence_hash_cannot_hide_revision_workspace_time_or_intent_drift()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var otherRevision = GovernedLoopRevisionReference.Create(
            1,
            intent.Publication.Revision.GraphId,
            "revision-other",
            GovernedLoopAdmissionTestFixture.Hash('e'));
        var mismatchedBinding = GovernedLoopExecutionBinding.Create(1, "run-1", otherRevision, 1);
        var mismatchedWorkspace = GovernedLoopAdmissionTestFixture.CapabilityAdmission() with
        {
            WorkspaceScopeId = "workspace-sha256:" + GovernedLoopAdmissionTestFixture.Hash('9')
        };
        var futureCapability = GovernedLoopAdmissionTestFixture.CapabilityAdmission(
            GovernedLoopAdmissionTestFixture.EvaluatedAtUtc.AddSeconds(1));

        var revisionDrift = GovernedLoopAdmissionTestFixture.Evidence(intent, binding: mismatchedBinding);
        var workspaceDrift = GovernedLoopAdmissionTestFixture.Evidence(intent, capabilityAdmission: mismatchedWorkspace);
        var timeDrift = GovernedLoopAdmissionTestFixture.Evidence(intent, capabilityAdmission: futureCapability);
        var intentDrift = GovernedLoopAdmissionTestFixture.Evidence(
            intent,
            intentHash: GovernedLoopAdmissionTestFixture.Hash('f'));

        Assert.All(
            new[] { revisionDrift, workspaceDrift, timeDrift, intentDrift },
            candidate =>
            {
                Assert.True(GovernedLoopAdmissionContractHash.Matches(candidate));
                Assert.Contains(
                    GovernedLoopAdmissionValidator.Validate(candidate, intent).Errors,
                    error => error.Code == GovernedLoopAdmissionValidationErrorCode.BindingMismatch);
            });
    }

    [Fact]
    public void Successful_evidence_requires_the_exact_canonical_reference_set()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var valid = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var omitted = valid.References.Skip(1).ToArray();
        var substituted = valid.References.ToArray();
        substituted[0] = substituted[0] with { EvidenceHash = GovernedLoopAdmissionTestFixture.Hash('f') };
        var reordered = valid.References.Reverse().ToArray();
        var duplicate = valid.References.ToArray();
        duplicate[1] = duplicate[0];
        var unknown = valid.References.ToArray();
        unknown[0] = unknown[0] with { Kind = GovernedLoopAdmissionEvidenceKind.Unknown };
        var undefined = valid.References.ToArray();
        undefined[0] = undefined[0] with { Kind = (GovernedLoopAdmissionEvidenceKind)int.MaxValue };

        Assert.All(
            new[] { omitted, substituted, reordered, duplicate, unknown, undefined },
            references =>
            {
                var candidate = CopyEvidence(valid, references: references);
                Assert.False(GovernedLoopAdmissionValidator.Validate(candidate, intent).IsValid);
            });
    }

    [Fact]
    public void Receipt_rejects_non_utc_or_pre_evaluation_recording_time()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var beforeEvidence = GovernedLoopAdmissionTestFixture.Receipt(
            intent,
            evidence,
            evidence.EvaluatedAtUtc.AddTicks(-1),
            applyHash: false);
        var nonUtc = GovernedLoopAdmissionTestFixture.Receipt(
            intent,
            evidence,
            new DateTimeOffset(2026, 8, 10, 13, 0, 0, TimeSpan.FromHours(1)),
            applyHash: false);

        Assert.Contains(
            GovernedLoopAdmissionValidator.Validate(beforeEvidence).Errors,
            error => error.Code == GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp);
        Assert.Contains(
            GovernedLoopAdmissionValidator.Validate(nonUtc).Errors,
            error => error.Code == GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp);
    }

    [Fact]
    public void Every_definitive_failure_code_except_none_is_accepted_and_unsupported_values_fail_closed()
    {
        var supported = Enum.GetValues<GovernedLoopAdmissionFailureCode>()
            .Where(value => value != GovernedLoopAdmissionFailureCode.None)
            .ToArray();

        Assert.All(
            supported,
            failureCode => Assert.True(
                GovernedLoopAdmissionValidator.Validate(GovernedLoopAdmissionTestFixture.Rejection(failureCode: failureCode)).IsValid,
                failureCode.ToString()));

        foreach (var failureCode in new[]
                 {
                     GovernedLoopAdmissionFailureCode.None,
                     (GovernedLoopAdmissionFailureCode)(-1),
                     (GovernedLoopAdmissionFailureCode)int.MaxValue
                 })
        {
            var invalid = GovernedLoopAdmissionTestFixture.Rejection(failureCode: failureCode, applyHash: false);
            Assert.Contains(
                GovernedLoopAdmissionValidator.Validate(invalid).Errors,
                error => error.Code == GovernedLoopAdmissionValidationErrorCode.InvalidEnumeration);
        }
    }

    [Fact]
    public void Durable_rejection_taxonomy_excludes_transient_or_ambiguous_operation_failures()
    {
        var names = Enum.GetNames<GovernedLoopAdmissionFailureCode>();

        Assert.Contains(nameof(GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied), names);
        Assert.DoesNotContain(names, name => name.Contains("Unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Ambiguous", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Conflict", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Store", StringComparison.Ordinal));
        Assert.DoesNotContain("PublicationMismatch", names);
        Assert.DoesNotContain("GraphArtifactMismatch", names);
        Assert.Equal(
            [
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
                GovernedLoopAdmissionEvidenceKind.EffectiveAuthority,
                GovernedLoopAdmissionEvidenceKind.CapabilityAdmission
            ],
            GovernedLoopAdmissionValidator.RequiredRejectionEvidenceKinds(
                GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied));
    }

    [Fact]
    public void Every_failure_code_requires_its_exact_canonical_evidence_kind_set()
    {
        var allKinds = Enum.GetValues<GovernedLoopAdmissionEvidenceKind>()
            .Where(kind => kind != GovernedLoopAdmissionEvidenceKind.Unknown)
            .ToArray();
        foreach (var failureCode in Enum.GetValues<GovernedLoopAdmissionFailureCode>().Where(value => value != GovernedLoopAdmissionFailureCode.None))
        {
            var expectedKinds = GovernedLoopAdmissionValidator.RequiredRejectionEvidenceKinds(failureCode);
            var valid = GovernedLoopAdmissionTestFixture.Rejection(failureCode: failureCode);
            var omitted = valid.References.Skip(1).ToArray();
            var extraKind = allKinds.First(kind => !expectedKinds.Contains(kind));
            var extra = valid.References
                .Append(GovernedLoopAdmissionTestFixture.Reference(extraKind, 'f'))
                .OrderBy(reference => reference.Kind)
                .ToArray();
            var omittedCandidate = NewRejection(valid, omitted);
            var extraCandidate = NewRejection(valid, extra);

            Assert.Equal(expectedKinds, valid.References.Select(reference => reference.Kind));
            Assert.False(GovernedLoopAdmissionValidator.Validate(omittedCandidate).IsValid);
            Assert.False(GovernedLoopAdmissionValidator.Validate(extraCandidate).IsValid);
            Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopAdmissionEvidenceKind>)expectedKinds).Clear());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopAdmissionValidator.RequiredRejectionEvidenceKinds(GovernedLoopAdmissionFailureCode.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopAdmissionValidator.RequiredRejectionEvidenceKinds((GovernedLoopAdmissionFailureCode)int.MaxValue));
    }

    [Fact]
    public void Terminal_outcomes_accept_only_exact_admitted_or_rejected_compositions()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent);
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
        GovernedLoopAdmissionTerminalOutcome[] invalid =
        [
            NewOutcome(intent, GovernedLoopAdmissionDisposition.Unknown, null, null),
            NewOutcome(intent, (GovernedLoopAdmissionDisposition)(-1), null, null),
            NewOutcome(intent, (GovernedLoopAdmissionDisposition)int.MaxValue, null, null),
            NewOutcome(intent, GovernedLoopAdmissionDisposition.Admitted, null, null),
            NewOutcome(intent, GovernedLoopAdmissionDisposition.Admitted, receipt, rejection),
            NewOutcome(intent, GovernedLoopAdmissionDisposition.Rejected, null, null),
            NewOutcome(intent, GovernedLoopAdmissionDisposition.Rejected, receipt, rejection)
        ];

        Assert.All(invalid, candidate => Assert.False(GovernedLoopAdmissionValidator.Validate(candidate).IsValid));
    }

    [Fact]
    public void Terminal_outcome_requires_exact_nested_intent_and_terminal_time_even_after_hash_recalculation()
    {
        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var otherIntent = intent with { OperationId = "admit-operation-2" };
        var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent);
        var mismatchedIntent = NewOutcome(
            otherIntent,
            GovernedLoopAdmissionDisposition.Admitted,
            receipt,
            null,
            receipt.RecordedAtUtc);
        var mismatchedTime = NewOutcome(
            intent,
            GovernedLoopAdmissionDisposition.Admitted,
            receipt,
            null,
            receipt.RecordedAtUtc.AddTicks(1));

        Assert.False(GovernedLoopAdmissionValidator.Validate(mismatchedIntent).IsValid);
        Assert.False(GovernedLoopAdmissionValidator.Validate(mismatchedTime).IsValid);
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeTerminalOutcomeHash(mismatchedIntent));
        Assert.Throws<ArgumentException>(() => GovernedLoopAdmissionContractHash.ComputeTerminalOutcomeHash(mismatchedTime));
    }

    private static GovernedLoopAdmissionEvidence CopyEvidence(
        GovernedLoopAdmissionEvidence value,
        GovernedLoopExecutionBinding? binding = default,
        EmbodySense.Core.Common.Authority.Models.AuthorityCeiling? effectiveAuthority = default,
        CapabilityAdmissionSnapshot? capabilityAdmission = default,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = default,
        DateTimeOffset? evaluatedAtUtc = null,
        string? intentHash = default,
        string? contentHash = default,
        bool omitBinding = false,
        bool omitEffectiveAuthority = false,
        bool omitCapabilityAdmission = false,
        bool omitReferences = false,
        bool omitIntentHash = false,
        bool omitContentHash = false)
        => new(
            value.SchemaVersion,
            omitIntentHash ? null! : intentHash ?? value.IntentHash,
            omitBinding ? null! : binding ?? value.Binding,
            omitEffectiveAuthority ? null! : effectiveAuthority ?? value.EffectiveAuthority,
            omitCapabilityAdmission ? null! : capabilityAdmission ?? value.CapabilityAdmission,
            omitReferences ? null! : references ?? value.References,
            evaluatedAtUtc ?? value.EvaluatedAtUtc,
            omitContentHash ? null! : contentHash ?? value.ContentHash);

    private static GovernedLoopAdmissionTerminalOutcome NewOutcome(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionDisposition disposition,
        GovernedLoopAdmissionReceipt? receipt,
        GovernedLoopAdmissionRejection? rejection,
        DateTimeOffset? recordedAtUtc = null)
        => new(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            intent,
            disposition,
            receipt,
            rejection,
            recordedAtUtc ?? GovernedLoopAdmissionTestFixture.RecordedAtUtc,
            string.Empty);

    private static GovernedLoopAdmissionRejection NewRejection(
        GovernedLoopAdmissionRejection value,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference> references)
        => new(
            value.SchemaVersion,
            value.Intent,
            value.FailureCode,
            value.AuthorityDenial,
            value.CapabilityDenial,
            references,
            value.RejectedAtUtc,
            string.Empty);

    private static void AssertUnsupportedSchema(GovernedLoopAdmissionValidationResult result)
        => Assert.Contains(
            result.Errors,
            error => error.Code == GovernedLoopAdmissionValidationErrorCode.UnsupportedSchemaVersion);
}
