using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleCommandTests
{
    [Fact]
    public void Canonical_hash_is_deterministic_and_binds_every_nested_candidate_value()
    {
        var grant = HumanInputRequestLifecycleTestData.Grant();
        var candidate = HumanInputRequestLifecycleTestData.Request();
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-request-one",
            candidate.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(grant),
            candidate);

        Assert.Equal(command.RequestHash, HumanInputRequestLifecycleCommandHash.Compute(command));
        Assert.True(HumanInputRequestLifecycleCommandHash.Matches(command));
        Assert.NotEqual(
            command.RequestHash,
            HumanInputRequestLifecycleCommandHash.Compute(command with { OperationId = "create-request-two" }));

        candidate.EligibleRespondents[0] = candidate.EligibleRespondents[0] with { RoutingReference = "changed-private-route" };

        Assert.False(HumanInputRequestLifecycleCommandHash.Matches(command));
    }

    [Fact]
    public void Canonical_hash_covers_structured_choice_and_reference_schema_values()
    {
        var grant = HumanInputRequestLifecycleTestData.Grant();
        var structured = HumanInputRequestHash.Apply(HumanInputRequestLifecycleTestData.Request() with
        {
            ResponseSchema = new HumanInputResponseSchema(
                HumanInputResponseKind.Structured,
                null,
                null,
                [
                    new HumanInputStructuredFieldSchema(
                        "note",
                        HumanInputStructuredFieldKind.Text,
                        true,
                        64,
                        null),
                    new HumanInputStructuredFieldSchema(
                        "decision",
                        HumanInputStructuredFieldKind.Choice,
                        false,
                        null,
                        [new HumanInputChoice("approve", "Approve"), new HumanInputChoice("decline", "Decline")]),
                ],
                null),
            RequestHash = string.Empty,
        });
        var reference = HumanInputRequestHash.Apply(HumanInputRequestLifecycleTestData.Request(
            requestId: "reference-request",
            requestVersionId: "reference-version") with
        {
            ResponseSchema = new HumanInputResponseSchema(
                HumanInputResponseKind.Reference,
                null,
                null,
                null,
                new HumanInputReferencePolicy(HumanInputReferenceKind.Reference, 128)),
            RequestHash = string.Empty,
        });
        var structuredCommand = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "hash-structured-schema",
            structured.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(grant),
            structured);
        var referenceCommand = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "hash-reference-schema",
            reference.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(grant),
            reference);

        Assert.True(HumanInputRequestLifecycleCommandHash.Matches(structuredCommand));
        Assert.True(HumanInputRequestLifecycleCommandHash.Matches(referenceCommand));
        Assert.NotEqual(structuredCommand.RequestHash, referenceCommand.RequestHash);
    }

    [Fact]
    public void Canonical_hash_rejects_null_unbounded_and_malformed_utf16_commands()
    {
        var grant = HumanInputRequestLifecycleTestData.Grant();
        var candidate = HumanInputRequestLifecycleTestData.Request();
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-request-one",
            candidate.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(grant),
            candidate);

        Assert.Throws<ArgumentNullException>(() => HumanInputRequestLifecycleCommandHash.Compute(null!));
        Assert.Throws<ArgumentException>(() => HumanInputRequestLifecycleCommandHash.Compute(
            command with { OperationId = new string('a', 121) }));
        var malformed = command with { OperationId = "malformed-\ud800" };
        Assert.Throws<ArgumentException>(() => HumanInputRequestLifecycleCommandHash.Compute(malformed));
        Assert.False(HumanInputRequestLifecycleCommandHash.Matches(malformed));
    }

    [Fact]
    public void Validator_accepts_exact_create_and_reports_each_command_shape_family()
    {
        var grant = HumanInputRequestLifecycleTestData.Grant();
        var candidate = HumanInputRequestLifecycleTestData.Request();
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-request-one",
            candidate.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(grant),
            candidate);

        Assert.Empty(HumanInputRequestLifecycleCommandValidator.Validate(command));

        var invalid = new[]
        {
            command with { SchemaVersion = 2, OperationId = string.Empty, RequestId = string.Empty },
            command with { Kind = (HumanInputRequestLifecycleOperationKind)999 },
            command with
            {
                ExpectedLifecycleVersion = 1,
                ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Pending,
                ExpectedRequest = HumanInputRequestLifecycleTestData.Reference(candidate),
            },
            command with { CandidateRequest = null },
            command with { GrantReference = null },
            command with { Reason = null! },
            command with { RequestHash = HumanInputRequestLifecycleTestData.Hash('f') },
        };
        var codes = invalid
            .SelectMany(HumanInputRequestLifecycleCommandValidator.Validate)
            .Select(error => error.Code)
            .ToHashSet();

        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.UnsupportedSchemaVersion, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidIdentifier, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidOperationKind, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidExpectedState, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidCandidateRequest, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidGrantReference, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidReason, codes);
        Assert.Contains(HumanInputRequestLifecycleMutationValidationErrorCode.InvalidRequestHash, codes);
    }

    [Fact]
    public void Validation_is_bounded_and_does_not_echo_private_values()
    {
        var command = new HumanInputRequestLifecycleCommand(
            999,
            "PrivateOperationValue",
            (HumanInputRequestLifecycleOperationKind)999,
            "PrivateRequestValue",
            -1,
            (HumanInputRequestLifecycleStatus)999,
            null,
            null,
            null,
            null,
            null!,
            "PrivateHashValue");

        var errors = HumanInputRequestLifecycleCommandValidator.Validate(command);
        var rendered = string.Join('|', errors.Select(error => $"{error.Path}:{error.Message}"));

        Assert.InRange(errors.Count, 1, 64);
        Assert.DoesNotContain("PrivateOperationValue", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateRequestValue", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateHashValue", rendered, StringComparison.Ordinal);
    }
}
