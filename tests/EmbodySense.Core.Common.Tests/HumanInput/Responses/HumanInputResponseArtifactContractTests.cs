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
        Assert.False(HumanInputResponseValueHash.Matches(null, HumanInputResponseTestData.Hash('a')));
        Assert.False(HumanInputResponseValueHash.Matches(defaultFields, HumanInputResponseTestData.Hash('a')));
        Assert.False(HumanInputResponseValueHash.Matches(tooMany, HumanInputResponseTestData.Hash('a')));
        Assert.False(HumanInputResponseValueHash.Matches(oversized, HumanInputResponseTestData.Hash('a')));
    }

    [Fact]
    public void Value_hash_rejects_noncanonical_unicode_and_identifiers_but_accepts_replacement_character()
    {
        var malformed = new[]
        {
            new HumanInputResponseValue(HumanInputResponseKind.Text, "\uD800", null, null, null, null),
            new HumanInputResponseValue(HumanInputResponseKind.Text, "\uDC00", null, null, null, null),
            new HumanInputResponseValue(HumanInputResponseKind.Text, "e\u0301", null, null, null, null),
            new HumanInputResponseValue(HumanInputResponseKind.Choice, null, "Choice-One", null, null, null),
            new HumanInputResponseValue(
                HumanInputResponseKind.Structured,
                null,
                null,
                null,
                ImmutableArray.Create(new HumanInputStructuredFieldValue("field-one", "\uD800", null)),
                null),
            new HumanInputResponseValue(
                HumanInputResponseKind.Reference,
                null,
                null,
                null,
                null,
                new HumanInputReference(HumanInputReferenceKind.Artifact, "artifact/unsafe"))
        };

        Assert.All(malformed, value =>
        {
            Assert.Throws<ArgumentException>(() => HumanInputResponseValueHash.Compute(value));
            Assert.False(HumanInputResponseValueHash.Matches(value, HumanInputResponseTestData.Hash('a')));
        });

        var replacement = new HumanInputResponseValue(HumanInputResponseKind.Text, "value-\uFFFD", null, null, null, null);
        var hash = HumanInputResponseValueHash.Compute(replacement);
        Assert.True(HumanInputResponseValueHash.Matches(replacement, hash));
    }

    [Fact]
    public void Value_hash_enforces_every_nested_string_boundary_before_serialization()
    {
        var maximumText = new HumanInputResponseValue(
            HumanInputResponseKind.Text,
            new string('t', HumanInputLimits.MaxResponseTextCharacters),
            null,
            null,
            null,
            null);
        var maximumChoice = new HumanInputResponseValue(
            HumanInputResponseKind.Choice,
            null,
            new string('c', HumanInputLimits.MaxIdentifierCharacters),
            null,
            null,
            null);
        var maximumReference = new HumanInputResponseValue(
            HumanInputResponseKind.Reference,
            null,
            null,
            null,
            null,
            new HumanInputReference(HumanInputReferenceKind.Artifact, new string('r', HumanInputLimits.MaxReferenceCharacters)));
        var maximumStructured = new HumanInputResponseValue(
            HumanInputResponseKind.Structured,
            null,
            null,
            null,
            ImmutableArray.Create(new HumanInputStructuredFieldValue(
                new string('f', HumanInputLimits.MaxIdentifierCharacters),
                new string('s', HumanInputLimits.MaxResponseTextCharacters),
                null)),
            null);
        var oversized = new[]
        {
            maximumText with { Text = new string('t', HumanInputLimits.MaxResponseTextCharacters + 1) },
            maximumChoice with { ChoiceId = new string('c', HumanInputLimits.MaxIdentifierCharacters + 1) },
            maximumReference with { Reference = maximumReference.Reference! with { Value = new string('r', HumanInputLimits.MaxReferenceCharacters + 1) } },
            maximumStructured with
            {
                StructuredFields = ImmutableArray.Create(new HumanInputStructuredFieldValue(
                    new string('f', HumanInputLimits.MaxIdentifierCharacters + 1),
                    "value",
                    null))
            }
        };

        Assert.All(new[] { maximumText, maximumChoice, maximumReference, maximumStructured }, value => Assert.NotEmpty(HumanInputResponseValueHash.Compute(value)));
        Assert.All(oversized, value => Assert.Throws<ArgumentException>(() => HumanInputResponseValueHash.Compute(value)));
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
    public void Bounded_attempt_snapshot_deep_copies_request_invalid_structured_values_and_accepts_equivalent_arrays()
    {
        var request = HumanInputResponseTestData.Request();
        var fields = ImmutableArray.Create(
            new HumanInputStructuredFieldValue("field-two", null, "choice-one"),
            new HumanInputStructuredFieldValue("field-one", "private-value", null));
        var attempt = HumanInputResponseArtifactHash.Apply(HumanInputResponseTestData.Artifact(request) with
        {
            Value = new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, fields, null),
            ValueHash = string.Empty,
            ResponseHash = string.Empty
        });

        Assert.False(HumanInputResponseContractValidator.ValidateArtifact(request, attempt).IsValid);
        Assert.True(HumanInputResponseArtifactSnapshot.TryCaptureBoundedAttempt(attempt, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);
        Assert.NotSame(attempt, snapshot);
        Assert.NotSame(attempt.Request, snapshot!.Request);
        Assert.NotSame(attempt.Binding, snapshot.Binding);
        Assert.NotSame(attempt.Value, snapshot.Value);
        Assert.False(attempt.Value.StructuredFields!.Value.Equals(snapshot.Value.StructuredFields!.Value));
        Assert.NotSame(attempt.Value.StructuredFields.Value[0], snapshot.Value.StructuredFields.Value[0]);

        var equivalent = HumanInputResponseArtifactHash.Apply(attempt with
        {
            Value = attempt.Value with
            {
                StructuredFields = attempt.Value.StructuredFields.Value.Select(field => field with { }).ToImmutableArray()
            },
            ValueHash = string.Empty,
            ResponseHash = string.Empty
        });

        Assert.False(attempt.Value.StructuredFields.Value.Equals(equivalent.Value.StructuredFields!.Value));
        Assert.Equal(attempt.ValueHash, equivalent.ValueHash);
        Assert.Equal(attempt.ResponseHash, equivalent.ResponseHash);
        Assert.True(HumanInputResponseArtifactSnapshot.TryCaptureBoundedAttempt(equivalent, out _, out var equivalentValidation));
        Assert.True(equivalentValidation.IsValid);
    }

    [Fact]
    public void Bounded_attempt_snapshot_rejects_malformed_shape_time_privacy_and_hashes()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var variants = new HumanInputResponseArtifact?[]
        {
            null,
            HumanInputResponseArtifactHash.Apply(artifact with { SchemaVersion = 2, ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(artifact with { SubmittedAtUtc = default, ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(artifact with { SubmittedAtUtc = artifact.SubmittedAtUtc.ToOffset(TimeSpan.FromHours(1)), ResponseHash = string.Empty }),
            HumanInputResponseArtifactHash.Apply(artifact with { PrivacyClass = HumanInputPrivacyClass.Unknown, ResponseHash = string.Empty }),
            artifact with { ValueHash = HumanInputResponseTestData.Hash('d') },
            artifact with { ResponseHash = HumanInputResponseTestData.Hash('d') },
            artifact with { Value = null! }
        };

        Assert.All(variants, variant =>
        {
            Assert.False(HumanInputResponseArtifactSnapshot.TryCaptureBoundedAttempt(variant, out var snapshot, out var validation));
            Assert.Null(snapshot);
            Assert.False(validation.IsValid);
        });
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
        Assert.False(HumanInputResponseArtifactHash.Matches(null));
    }

    [Fact]
    public void Artifact_hash_rejects_malformed_nested_strings_and_fails_closed()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var variants = new[]
        {
            artifact with { Explanation = "\uD800" },
            artifact with { Explanation = "e\u0301" },
            artifact with { Explanation = new string('e', HumanInputLimits.MaxExplanationCharacters + 1) },
            artifact with { ResponseId = new string('r', HumanInputLimits.MaxIdentifierCharacters + 1) },
            artifact with { Request = artifact.Request with { RequestId = "Request-One" } },
            artifact with { Request = artifact.Request with { RequestVersionId = "\uDC00" } },
            artifact with { Request = artifact.Request with { RequestHash = "bad" } },
            artifact with { Binding = artifact.Binding with { CheckpointId = "checkpoint/unsafe" } },
            artifact with { RespondentRoleId = "role/unsafe" }
        };

        Assert.All(variants, variant =>
        {
            Assert.Throws<ArgumentException>(() => HumanInputResponseArtifactHash.Compute(variant));
            Assert.Throws<ArgumentException>(() => HumanInputResponseArtifactHash.Apply(variant));
            Assert.False(HumanInputResponseArtifactHash.Matches(variant));
        });

        var maximumExplanation = HumanInputResponseArtifactHash.Apply(artifact with
        {
            Explanation = new string('e', HumanInputLimits.MaxExplanationCharacters),
            ResponseHash = string.Empty
        });
        var replacement = HumanInputResponseArtifactHash.Apply(artifact with { Explanation = "value-\uFFFD", ResponseHash = string.Empty });
        Assert.True(HumanInputResponseArtifactHash.Matches(maximumExplanation));
        Assert.True(HumanInputResponseArtifactHash.Matches(replacement));
        Assert.False(HumanInputResponseArtifactHash.Matches(artifact with
        {
            Value = new HumanInputResponseValue(
                HumanInputResponseKind.Structured,
                null,
                null,
                null,
                default(ImmutableArray<HumanInputStructuredFieldValue>),
                null)
        }));
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
