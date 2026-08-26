using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Validates immutable schema-1 Human Input timeout and failure policy artifacts without resolving, persisting, or authorizing them.</summary>
public static class HumanInputPolicyArtifactValidator
{
    private static readonly string[] _forbiddenTerms = ["secret", "password", "token", "credential", "api-key", "apikey", "approve", "approval", "grant", "authorize", "review"];

    /// <summary>Validates one complete immutable policy artifact and its canonical content hash.</summary>
    /// <param name="artifact">The untrusted artifact candidate.</param>
    /// <returns>Every deterministic artifact validation failure.</returns>
    public static HumanInputPolicyArtifactValidationResult Validate(HumanInputPolicyArtifact? artifact)
    {
        var errors = new List<HumanInputPolicyArtifactValidationError>();
        if (artifact is null)
        {
            Add(errors, "artifact_required", "$", "A Human Input policy artifact is required.");
            return Result(errors);
        }

        if (artifact.SchemaVersion != HumanInputPolicyArtifact.CurrentSchemaVersion) Add(errors, "unsupported_schema_version", "$.schemaVersion", "Human Input policy artifacts must use schema version 1.");
        Identifier(artifact.PolicyId, "$.policyId", errors);
        Identifier(artifact.RevisionId, "$.revisionId", errors);
        if (!HumanInputPolicyReference.TryParse($"{artifact.PolicyId}{HumanInputPolicyReference.Separator}{artifact.RevisionId}", out _)) Add(errors, "non_exact_policy_reference", "$.policyId", "Policy and revision identities must not select a default, current, or latest policy.");
        Identifier(artifact.WorkspaceId, "$.workspaceId", errors);
        Identifier(artifact.GraphId, "$.graphId", errors);
        Identifier(artifact.AuthorityActorId, "$.authorityActorId", errors);
        if (!Enum.IsDefined(artifact.Kind) || artifact.Kind == HumanInputPolicyKind.Unknown) Add(errors, "unsupported_kind", "$.kind", "One supported Human Input policy kind is required.");
        if (!Enum.IsDefined(artifact.TerminalDisposition)) Add(errors, "unsupported_terminal_disposition", "$.terminalDisposition", "The terminal disposition must be a defined closed value.");
        if (artifact.Kind == HumanInputPolicyKind.ResponseWindow)
        {
            if (artifact.ResponseWindowMilliseconds is not { } window || window < (long)HumanInputLimits.MinResponseWindow.TotalMilliseconds || window > (long)HumanInputLimits.MaxResponseWindow.TotalMilliseconds)
            {
                Add(errors, "unbounded_response_window", "$.responseWindowMilliseconds", "A bounded finite response window within the schema-1 limits is required.");
            }
            if (artifact.TerminalDisposition != HumanInputTerminalDisposition.Unknown) Add(errors, "wrong_kind_shape", "$.terminalDisposition", "A response-window policy cannot select a terminal disposition.");
        }
        else if (artifact.Kind == HumanInputPolicyKind.DeadlineDisposition)
        {
            if (artifact.ResponseWindowMilliseconds is not null) Add(errors, "wrong_kind_shape", "$.responseWindowMilliseconds", "A deadline-disposition policy cannot define a response window.");
            if (artifact.TerminalDisposition != HumanInputTerminalDisposition.Expired) Add(errors, "unsupported_terminal_disposition", "$.terminalDisposition", "Only the non-authorizing Expired terminal disposition is supported.");
        }

        if (!HumanInputPolicyArtifactHash.IsSha256(artifact.ContentHash)) Add(errors, "invalid_content_hash", "$.contentHash", "The policy content hash must be a lowercase SHA-256 digest.");
        else if (errors.Count == 0 && !HumanInputPolicyArtifactHash.Matches(artifact)) Add(errors, "content_hash_mismatch", "$.contentHash", "The policy content hash does not match canonical policy content.");
        return Result(errors);
    }

    private static void Identifier(string? value, string path, List<HumanInputPolicyArtifactValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(value) || _forbiddenTerms.Any(term => value!.Contains(term, StringComparison.OrdinalIgnoreCase))) Add(errors, "unsafe_identifier", path, "Policy identities and scopes must be canonical non-secret data-only identifiers.");
    }

    private static void Add(List<HumanInputPolicyArtifactValidationError> errors, string code, string path, string message) => errors.Add(new HumanInputPolicyArtifactValidationError(code, path, message));

    private static HumanInputPolicyArtifactValidationResult Result(List<HumanInputPolicyArtifactValidationError> errors)
        => new(Array.AsReadOnly(errors.ToArray()));
}
