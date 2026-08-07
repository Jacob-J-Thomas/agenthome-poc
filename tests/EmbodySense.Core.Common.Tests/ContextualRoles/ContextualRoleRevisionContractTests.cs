using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using System.Collections.Immutable;
using System.Text;

namespace EmbodySense.Core.Common.Tests.ContextualRoles;

public sealed class ContextualRoleRevisionContractTests
{
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_immutable_revision_has_exact_attribution_and_non_granting_maxima()
    {
        var revision = ValidRevision();

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.True(result.IsValid);
        Assert.Equal("reviewer", revision.Identity.RoleId);
        Assert.Equal(1, revision.Identity.Revision);
        Assert.Equal("user-jake", revision.Provenance.AuthorId);
        Assert.Equal(_createdAtUtc, revision.Provenance.CreatedAtUtc);
        Assert.True(revision.PolicyMaxima.IsNonGranting);
        Assert.Contains("workspace-read", revision.PolicyMaxima.CapabilityIds);
        Assert.True(revision.WorkspaceApplicability.AppliesTo("agenthome"));
        Assert.False(revision.WorkspaceApplicability.AppliesTo("other-workspace"));
    }

    [Fact]
    public void Copying_display_or_provenance_metadata_does_not_reinterpret_or_change_semantic_hash()
    {
        var revision = ValidRevision();
        var changedMetadata = revision with
        {
            DisplayName = "A clear review role",
            Provenance = revision.Provenance with { RecordedAtUtc = _createdAtUtc.AddHours(2) }
        };

        Assert.Equal(revision.ContentHash, ContextualRoleRevisionContentHash.Compute(changedMetadata));
        Assert.Equal(1, changedMetadata.Identity.Revision);
        Assert.Equal("reviewer", changedMetadata.Identity.RoleId);
        Assert.True(ContextualRoleRevisionValidator.Validate(changedMetadata).IsValid);
    }

    [Fact]
    public void Canonical_hash_is_deterministic_and_independent_of_declared_set_order()
    {
        var first = ValidRevision();
        var reordered = first with
        {
            WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(["workspace-b", "agenthome"]),
            PolicyMaxima = new ContextualRolePolicyMaxima(["workspace-read", "file-read"])
        };
        var reversed = reordered with
        {
            WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(["agenthome", "workspace-b"]),
            PolicyMaxima = new ContextualRolePolicyMaxima(["file-read", "workspace-read"])
        };

        Assert.Equal(ContextualRoleRevisionContentHash.Compute(reordered), ContextualRoleRevisionContentHash.Compute(reversed));
        Assert.NotEqual(first.ContentHash, ContextualRoleRevisionContentHash.Compute(reordered));
    }

    [Theory]
    [InlineData(ContextualRoleStatus.Unknown)]
    [InlineData((ContextualRoleStatus)99)]
    public void Undefined_lifecycle_statuses_fail_closed(ContextualRoleStatus status)
    {
        var revision = ContextualRoleRevisionContentHash.Apply(ValidRevision() with { Status = status });

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Contains(result.Errors, error => error.Code == "invalid_status");
    }

    [Fact]
    public void Bounded_fields_accept_limits_and_reject_boundary_plus_one()
    {
        var maximum = ValidRevision() with
        {
            DisplayName = new string('d', ContextualRoleLimits.MaxDisplayNameCharacters),
            Purpose = new string('p', ContextualRoleLimits.MaxPurposeCharacters),
            WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(Enumerable.Range(1, ContextualRoleLimits.MaxWorkspaceScopes).Select(index => $"workspace-{index}").ToImmutableArray()),
            PolicyMaxima = new ContextualRolePolicyMaxima(Enumerable.Range(1, ContextualRoleLimits.MaxCapabilityMaximums).Select(index => $"capability-{index}").ToImmutableArray())
        };
        maximum = ContextualRoleRevisionContentHash.Apply(maximum);
        var tooLarge = maximum with
        {
            DisplayName = new string('d', ContextualRoleLimits.MaxDisplayNameCharacters + 1),
            Purpose = new string('p', ContextualRoleLimits.MaxPurposeCharacters + 1),
            WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(Enumerable.Range(1, ContextualRoleLimits.MaxWorkspaceScopes + 1).Select(index => $"workspace-{index}").ToImmutableArray()),
            PolicyMaxima = new ContextualRolePolicyMaxima(Enumerable.Range(1, ContextualRoleLimits.MaxCapabilityMaximums + 1).Select(index => $"capability-{index}").ToImmutableArray())
        };
        tooLarge = ContextualRoleRevisionContentHash.Apply(tooLarge);

        Assert.True(ContextualRoleRevisionValidator.Validate(maximum).IsValid);
        var result = ContextualRoleRevisionValidator.Validate(tooLarge);
        Assert.Contains(result.Errors, error => error.Code == "displayName_too_long");
        Assert.Contains(result.Errors, error => error.Code == "purpose_too_long");
        Assert.Contains(result.Errors, error => error.Code == "workspace_scope_count_out_of_range");
        Assert.Contains(result.Errors, error => error.Code == "capability_maximum_count_out_of_range");
    }

    [Fact]
    public void Malformed_or_untrusted_instruction_sources_fail_closed_without_loading_text()
    {
        var malformed = ValidRevision() with
        {
            InstructionSource = new ContextualRoleInstructionSourceReference((ContextualRoleInstructionSourceKind)77, "../../ROLE.md", ContextualRoleInstructionClassification.UntrustedContext)
        };
        malformed = ContextualRoleRevisionContentHash.Apply(malformed);

        var result = ContextualRoleRevisionValidator.Validate(malformed);

        Assert.Contains(result.Errors, error => error.Code == "invalid_instruction_source_kind");
        Assert.Contains(result.Errors, error => error.Code == "invalid_instruction_source_reference");
        Assert.Contains(result.Errors, error => error.Code == "untrusted_instruction_source");
    }

    [Fact]
    public void Unsafe_unicode_and_noncanonical_text_fail_closed()
    {
        var revision = ValidRevision() with { Purpose = "unsafe\0 text", DisplayName = "e\u0301" };
        revision = ContextualRoleRevisionContentHash.Apply(revision);

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Equal(2, result.Errors.Count(error => error.Code == "unsafe_text_characters"));
    }

    [Theory]
    [InlineData("displayName", true)]
    [InlineData("displayName", false)]
    [InlineData("purpose", true)]
    [InlineData("purpose", false)]
    public void Unpaired_surrogates_in_user_visible_text_return_structured_errors_without_throwing(string field, bool highSurrogate)
    {
        var malformedText = new string(highSurrogate ? '\ud800' : '\udc00', 1);
        var revision = field == "displayName"
            ? ValidRevision() with { DisplayName = malformedText }
            : ValidRevision() with { Purpose = malformedText };

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Contains(result.Errors, error => error.Code == "unsafe_text_characters" && error.Field == field);
    }

    [Theory]
    [InlineData("roleId", "invalid_role_id")]
    [InlineData("purpose", "unsafe_text_characters")]
    [InlineData("workspace", "invalid_workspace_id")]
    [InlineData("instructionSource", "invalid_instruction_source_reference")]
    [InlineData("policyMaximum", "invalid_capability_maximum")]
    public void Unpaired_surrogates_in_semantic_hash_inputs_return_structured_errors_without_hashing(string input, string errorCode)
    {
        var malformedText = new string('\ud800', 1);
        var revision = input switch
        {
            "roleId" => ValidRevision() with { Identity = new ContextualRoleRevisionIdentity(malformedText, 1) },
            "purpose" => ValidRevision() with { Purpose = malformedText },
            "workspace" => ValidRevision() with { WorkspaceApplicability = new ContextualRoleWorkspaceApplicability([malformedText]) },
            "instructionSource" => ValidRevision() with { InstructionSource = new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, malformedText, ContextualRoleInstructionClassification.RoleInstruction) },
            _ => ValidRevision() with { PolicyMaxima = new ContextualRolePolicyMaxima([malformedText]) }
        };

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Contains(result.Errors, error => error.Code == errorCode);
        Assert.DoesNotContain(result.Errors, error => error.Code == "content_hash_mismatch");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Public_hash_operations_reject_malformed_utf16_with_the_documented_contract(bool highSurrogate)
    {
        var malformedText = new string(highSurrogate ? '\ud800' : '\udc00', 1);
        var revision = ValidRevision() with { Purpose = malformedText };

        var computeFailure = Assert.Throws<ArgumentException>(() => ContextualRoleRevisionContentHash.Compute(revision));
        var applyFailure = Assert.Throws<ArgumentException>(() => ContextualRoleRevisionContentHash.Apply(revision));
        var matchesFailure = Assert.Throws<ArgumentException>(() => ContextualRoleRevisionContentHash.Matches(revision));

        Assert.Contains("well-formed UTF-16", computeFailure.Message, StringComparison.Ordinal);
        Assert.Contains("well-formed UTF-16", applyFailure.Message, StringComparison.Ordinal);
        Assert.Contains("well-formed UTF-16", matchesFailure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Default_semantic_identifier_arrays_return_structured_validation_errors_and_documented_hash_failures(bool workspaceIds)
    {
        var revision = workspaceIds
            ? ValidRevision() with { WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(default) }
            : ValidRevision() with { PolicyMaxima = new ContextualRolePolicyMaxima(default) };

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Contains(result.Errors, error => error.Code == (workspaceIds ? "workspace_applicability_required" : "policy_maxima_required"));
        Assert.DoesNotContain(result.Errors, error => error.Code == "content_hash_mismatch");
        var computeFailure = Assert.Throws<ArgumentException>(() => ContextualRoleRevisionContentHash.Compute(revision));
        var applyFailure = Assert.Throws<ArgumentException>(() => ContextualRoleRevisionContentHash.Apply(revision));
        var matchesFailure = Assert.Throws<ArgumentException>(() => ContextualRoleRevisionContentHash.Matches(revision));
        Assert.Contains("must be initialized", computeFailure.Message, StringComparison.Ordinal);
        Assert.Contains("must be initialized", applyFailure.Message, StringComparison.Ordinal);
        Assert.Contains("must be initialized", matchesFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_semantic_text_remains_safe_for_hash_validation()
    {
        var revision = ValidRevision() with { Purpose = null! };

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Contains(result.Errors, error => error.Code == "purpose_required");
        Assert.Contains(result.Errors, error => error.Code == "content_hash_mismatch");
    }

    [Theory]
    [InlineData("\u202E")]
    [InlineData("\u200B")]
    [InlineData("\U000E0001")]
    public void Unicode_format_characters_fail_closed_in_user_visible_canonical_text(string formatCharacter)
    {
        var revision = ValidRevision() with { DisplayName = $"Review{formatCharacter}er", Purpose = $"Review{formatCharacter} changes within the declared workspace." };
        revision = ContextualRoleRevisionContentHash.Apply(revision);

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Equal(2, result.Errors.Count(error => error.Code == "unsafe_text_characters"));
    }

    [Fact]
    public void Visible_normalized_unicode_including_astral_characters_remains_valid()
    {
        var revision = ValidRevision() with { DisplayName = "Réviseur 😀", Purpose = "Réviser les modifications dans l’espace de travail déclaré." };
        revision = ContextualRoleRevisionContentHash.Apply(revision);

        Assert.True(ContextualRoleRevisionValidator.Validate(revision).IsValid);
    }

    [Fact]
    public void Malformed_workspace_and_provenance_are_rejected()
    {
        var revision = ValidRevision() with
        {
            WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(["agenthome", "agenthome", "unsafe space"]),
            Provenance = new ContextualRoleProvenance("Invalid Author", _createdAtUtc, _createdAtUtc.AddMinutes(-1))
        };
        revision = ContextualRoleRevisionContentHash.Apply(revision);

        var result = ContextualRoleRevisionValidator.Validate(revision);

        Assert.Contains(result.Errors, error => error.Code == "duplicate_workspace_id");
        Assert.Contains(result.Errors, error => error.Code == "invalid_workspace_id");
        Assert.Contains(result.Errors, error => error.Code == "invalid_author_id");
        Assert.Contains(result.Errors, error => error.Code == "invalid_provenance_timestamp_order");
    }

    private static ContextualRoleRevision ValidRevision()
    {
        var revision = new ContextualRoleRevision(
            ContextualRoleLimits.SchemaVersion,
            new ContextualRoleRevisionIdentity("reviewer", 1),
            string.Empty,
            "Reviewer",
            "Review changes within the declared workspace.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("user-jake", _createdAtUtc, _createdAtUtc),
            new ContextualRoleWorkspaceApplicability(["agenthome"]),
            new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, "reviewer-instructions", ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(["file-read", "workspace-read"]));
        return ContextualRoleRevisionContentHash.Apply(revision);
    }
}
