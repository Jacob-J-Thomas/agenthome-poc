using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Responses;

public sealed class HumanInputResponseArtifactContractTests
{
    [Fact]
    public void Value_hash_is_canonical_across_structured_field_order_but_changes_with_data()
    {
        var first = new HumanInputResponseValue(
            HumanInputResponseKind.Structured,
            null,
            null,
            null,
            ImmutableArray.Create(
                new HumanInputStructuredFieldValue("field-two", null, "choice-one"),
                new HumanInputStructuredFieldValue("field-one", "private-text", null)),
            null);
        var reordered = first with { StructuredFields = first.StructuredFields!.Value.Reverse().ToImmutableArray() };
        var changed = first with
        {
            StructuredFields = ImmutableArray.Create(
                new HumanInputStructuredFieldValue("field-two", null, "choice-two"),
                new HumanInputStructuredFieldValue("field-one", "private-text", null))
        };

        Assert.Equal(HumanInputResponseValueHash.Compute(first), HumanInputResponseValueHash.Compute(reordered));
        Assert.NotEqual(HumanInputResponseValueHash.Compute(first), HumanInputResponseValueHash.Compute(changed));
        Assert.True(HumanInputResponseValueHash.Matches(first, HumanInputResponseValueHash.Compute(first)));
        Assert.False(HumanInputResponseValueHash.Matches(first, HumanInputResponseTestData.Hash('A')));
    }

    [Fact]
    public void Value_hash_preserves_every_typed_shape_and_null_empty_distinction()
    {
        var values = new[]
        {
            new HumanInputResponseValue(HumanInputResponseKind.Text, "text", null, null, null, null),
            new HumanInputResponseValue(HumanInputResponseKind.Choice, null, "choice-one", null, null, null),
            new HumanInputResponseValue(HumanInputResponseKind.Confirmation, null, null, true, null, null),
            new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, ImmutableArray<HumanInputStructuredFieldValue>.Empty, null),
            new HumanInputResponseValue(HumanInputResponseKind.Reference, null, null, null, null, new HumanInputReference(HumanInputReferenceKind.Artifact, "artifact-one")),
            new HumanInputResponseValue(HumanInputResponseKind.Text, null, null, null, null, null)
        };

        Assert.Equal(values.Length, values.Select(HumanInputResponseValueHash.Compute).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Value_hash_rejects_null_default_and_oversized_shapes_before_serialization()
    {
        var defaultFields = new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, default(ImmutableArray<HumanInputStructuredFieldValue>), null);
        var tooMany = new HumanInputResponseValue(
            HumanInputResponseKind.Structured,
            null,
            null,
            null,
            Enumerable.Range(0, HumanInputLimits.MaxStructuredFields + 1).Select(index => new HumanInputStructuredFieldValue($"field-{index}", "x", null)).ToImmutableArray(),
            null);
        var oversized = new HumanInputResponseValue(HumanInputResponseKind.Text, new string('x', HumanInputLimits.MaxResponseTextCharacters + 1), null, null, null, null);

        Assert.Throws<ArgumentNullException>(() => HumanInputResponseValueHash.Compute(null!));
        Assert.Throws<ArgumentException>(() => HumanInputResponseValueHash.Compute(defaultFields));
        Assert.Throws<ArgumentException>(() => HumanInputResponseValueHash.Compute(tooMany));
        Assert.Throws<ArgumentException>(() => HumanInputResponseValueHash.Compute(oversized));
    }

    [Fact]
    public void Canonical_artifact_validates_references_snapshots_and_inclusive_endpoint()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request, submittedAtUtc: request.Timing.ExpiresAtUtc, explanation: "private explanation");

        var validation = HumanInputResponseContractValidator.ValidateArtifact(request, artifact);
        Assert.True(validation.IsValid);
        Assert.True(HumanInputResponseArtifactHash.Matches(artifact));
        Assert.True(HumanInputResponseReference.TryCreate(request, artifact, out var reference, out var referenceValidation));
        Assert.True(referenceValidation.IsValid);
        Assert.True(reference!.Matches(request, artifact));
        Assert.True(HumanInputResponseContractValidator.ValidateReference(reference).IsValid);
        Assert.Contains(artifact.ResponseId, reference.ToString(), StringComparison.Ordinal);
        Assert.True(HumanInputResponseArtifactSnapshot.TryCapture(request, artifact, out var snapshot, out var snapshotValidation));
        Assert.True(snapshotValidation.IsValid);
        Assert.NotSame(artifact, snapshot);
        Assert.NotSame(artifact.Request, snapshot!.Request);
        Assert.NotSame(artifact.Binding, snapshot.Binding);
        Assert.NotSame(artifact.Value, snapshot.Value);
    }

    [Fact]
    public void Artifact_validation_rejects_forged_scope_role_time_privacy_value_and_hash()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var variants = new[]
        {
            artifact with { SchemaVersion = 2 },
            artifact with { ResponseId = "Invalid" },
            artifact with { Request = artifact.Request with { RequestHash = HumanInputResponseTestData.Hash('d') } },
            artifact with { Binding = artifact.Binding with { WorkspaceId = "workspace-other" } },
            artifact with { ActorId = null! },
            artifact with { RespondentRoleId = "role-other" },
            artifact with { SubmittedAtUtc = request.Timing.ExpiresAtUtc.AddTicks(1) },
            artifact with { SubmittedAtUtc = artifact.SubmittedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            artifact with { PrivacyClass = HumanInputPrivacyClass.Sensitive },
            artifact with { Value = artifact.Value with { Text = new string('x', 129) } },
            artifact with { Explanation = new string('x', HumanInputLimits.MaxExplanationCharacters + 1) },
            artifact with { ValueHash = HumanInputResponseTestData.Hash('d') },
            artifact with { ResponseHash = HumanInputResponseTestData.Hash('d') }
        };

        Assert.All(variants, variant => Assert.False(HumanInputResponseContractValidator.ValidateArtifact(request, variant).IsValid));
        Assert.False(HumanInputResponseContractValidator.ValidateArtifact(null, artifact).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateArtifact(request, null).IsValid);
        Assert.False(HumanInputResponseReference.TryCreate(request, artifact with { ResponseHash = "bad" }, out var invalidReference, out var invalidReferenceValidation));
        Assert.Null(invalidReference);
        Assert.False(invalidReferenceValidation.IsValid);
    }

    [Fact]
    public void Artifact_hash_covers_every_behavior_field_and_apply_repairs_both_hashes()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var variants = new[]
        {
            artifact with { SchemaVersion = 2 },
            artifact with { ResponseId = "response-two" },
            artifact with { Request = artifact.Request with { RequestVersionId = "request-version-two" } },
            artifact with { Binding = artifact.Binding with { RunId = "run-two" } },
            artifact with { RespondentRoleId = "role-two" },
            artifact with { SubmittedAtUtc = artifact.SubmittedAtUtc.AddTicks(1) },
            artifact with { PrivacyClass = HumanInputPrivacyClass.Sensitive },
            artifact with { Explanation = "different explanation" },
            artifact with { ValueHash = HumanInputResponseTestData.Hash('d') }
        };

        Assert.All(variants, variant => Assert.NotEqual(artifact.ResponseHash, HumanInputResponseArtifactHash.Compute(variant)));
        var changedValue = artifact with { Value = artifact.Value with { Text = "changed" } };
        var repaired = HumanInputResponseArtifactHash.Apply(changedValue);
        Assert.True(HumanInputResponseArtifactHash.Matches(repaired));
        Assert.NotEqual(artifact.ValueHash, repaired.ValueHash);
        Assert.Throws<ArgumentNullException>(() => HumanInputResponseArtifactHash.Compute(null!));
        Assert.Throws<ArgumentNullException>(() => HumanInputResponseArtifactHash.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => HumanInputResponseArtifactHash.Matches(null!));
    }

    [Fact]
    public void Default_formatting_and_validation_errors_never_expose_response_or_attribution_data()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request, text: "value-canary", explanation: "explanation-canary");
        var raw = new HumanInputResponse(request.RequestId, request.RequestVersionId, request.Binding, "actor-canary", "role-canary", HumanInputResponseTestData.Now, artifact.Value, "explanation-canary");
        var invalid = artifact with { ResponseHash = HumanInputResponseTestData.Hash('d') };
        var text = string.Join('\n', new[]
        {
            artifact.ToString(),
            artifact.Value.ToString(),
            raw.ToString(),
            string.Join('\n', HumanInputResponseContractValidator.ValidateArtifact(request, invalid).Errors.Select(error => $"{error.Path}:{error.Message}"))
        });

        Assert.DoesNotContain("value-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("explanation-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("actor-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role-canary", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_reference_and_invalid_snapshot_fail_closed()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var malformed = new HumanInputResponseReference(2, "Invalid", null!, "bad", "bad");

        Assert.False(HumanInputResponseContractValidator.ValidateReference(malformed).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateReference(null).IsValid);
        Assert.False(malformed.Matches(request, artifact));
        Assert.False(HumanInputResponseArtifactSnapshot.TryCapture(request, artifact with { Value = null! }, out _, out var validation));
        Assert.False(validation.IsValid);
        var boundedButInvalid = HumanInputResponseArtifactHash.Apply(artifact with { SchemaVersion = 2, ResponseHash = string.Empty });
        Assert.False(HumanInputResponseArtifactSnapshot.TryCapture(request, boundedButInvalid, out var invalidSnapshot, out var boundedValidation));
        Assert.Null(invalidSnapshot);
        Assert.False(boundedValidation.IsValid);
    }
}
