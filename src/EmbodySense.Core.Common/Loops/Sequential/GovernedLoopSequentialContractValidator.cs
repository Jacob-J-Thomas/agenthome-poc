using System.Text;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Common.Loops.Sequential;

/// <summary>Validates bounded schema-1 sequential invocation and adapter-binding contracts.</summary>
public static class GovernedLoopSequentialContractValidator
{
    /// <summary>Validates one exact immutable sequential invocation snapshot.</summary>
    public static GovernedLoopSequentialValidationResult Validate(GovernedLoopSequentialInvocationSnapshot? snapshot)
    {
        var errors = ValidateSnapshotStructure(snapshot);
        if (errors.Count == 0 && !GovernedLoopSequentialContractHash.Matches(snapshot))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.HashMismatch, "$.contentHash");
        }

        return Result(errors);
    }

    /// <summary>Validates one exact sequential adapter binding.</summary>
    public static GovernedLoopSequentialValidationResult Validate(GovernedLoopSequentialAdapterBinding? binding)
    {
        var errors = ValidateBindingStructure(binding);
        if (errors.Count == 0 && !GovernedLoopSequentialContractHash.Matches(binding))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.HashMismatch, "$.contentHash");
        }

        return Result(errors);
    }

    internal static GovernedLoopSequentialValidationResult ValidateForHash(GovernedLoopSequentialInvocationSnapshot? snapshot)
        => Result(ValidateSnapshotStructure(snapshot, validateContentHash: false));

    internal static GovernedLoopSequentialValidationResult ValidateForHash(GovernedLoopSequentialAdapterBinding? binding)
        => Result(ValidateBindingStructure(binding, validateContentHash: false));

    private static List<GovernedLoopSequentialValidationError> ValidateSnapshotStructure(
        GovernedLoopSequentialInvocationSnapshot? snapshot,
        bool validateContentHash = true)
    {
        var errors = new List<GovernedLoopSequentialValidationError>();
        if (snapshot is null)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(snapshot.SchemaVersion, "$.schemaVersion", errors);
        ValidateText(snapshot.TriggerPrompt, "$.triggerPrompt", GovernedLoopSequentialContractLimits.MaxTriggerPromptCharacters, required: true, errors);
        ValidateModel(snapshot.ModelSnapshot, errors);
        ValidateConversation(snapshot.InvokingConversation, snapshot.ContextCapturedAtUtc, errors);
        ValidateUtc(snapshot.ContextCapturedAtUtc, "$.contextCapturedAtUtc", errors);
        ValidateContext(snapshot.ContextManifest, snapshot.InvokingConversation, snapshot.ContextCapturedAtUtc, errors);
        if (validateContentHash)
        {
            ValidateHash(snapshot.ContentHash, "$.contentHash", errors);
        }

        return errors;
    }

    private static List<GovernedLoopSequentialValidationError> ValidateBindingStructure(
        GovernedLoopSequentialAdapterBinding? binding,
        bool validateContentHash = true)
    {
        var errors = new List<GovernedLoopSequentialValidationError>();
        if (binding is null)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(binding.SchemaVersion, "$.schemaVersion", errors);
        if (!ContextualRoleWorkspaceId.IsValid(binding.WorkspaceId))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidIdentity, "$.workspaceId");
        }

        if (!EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionValidator.Validate(binding.ExecutionBinding).IsValid)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.executionBinding");
        }

        ValidateToken(binding.AdmissionOperationId, "$.admissionOperationId", errors);
        ValidateHash(binding.AdmissionReceiptHash, "$.admissionReceiptHash", errors);
        ValidateHash(binding.AdmissionRequestHash, "$.admissionRequestHash", errors);
        ValidateHash(binding.InvocationPayloadHash, "$.invocationPayloadHash", errors);
        ValidateHash(binding.GraphArtifactHash, "$.graphArtifactHash", errors);
        ValidateHash(binding.GraphLayoutHash, "$.graphLayoutHash", errors);
        if (validateContentHash)
        {
            ValidateHash(binding.ContentHash, "$.contentHash", errors);
        }

        return errors;
    }

    private static void ValidateModel(CustomLoopModelSnapshot? model, List<GovernedLoopSequentialValidationError> errors)
    {
        if (model is null)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.Required, "$.modelSnapshot");
            return;
        }

        ValidateText(model.Provider, "$.modelSnapshot.provider", GovernedLoopSequentialContractLimits.MaxReferenceCharacters, required: true, errors);
        if (model.Model is not null)
        {
            ValidateText(model.Model, "$.modelSnapshot.model", GovernedLoopSequentialContractLimits.MaxReferenceCharacters, required: false, errors);
        }
    }

    private static void ValidateConversation(
        CustomLoopConversationReference? conversation,
        DateTimeOffset contextCapturedAtUtc,
        List<GovernedLoopSequentialValidationError> errors)
    {
        if (conversation is null)
        {
            return;
        }

        if (!CustomLoopArtifactIdentifier.IsValid(conversation.ConversationId))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidIdentity, "$.invokingConversation.conversationId");
        }

        ValidateText(conversation.CapturedVersion, "$.invokingConversation.capturedVersion", GovernedLoopSequentialContractLimits.MaxReferenceCharacters, required: true, errors);
        ValidateUtc(conversation.CapturedAtUtc, "$.invokingConversation.capturedAtUtc", errors);
        if (IsUtc(conversation.CapturedAtUtc) && IsUtc(contextCapturedAtUtc) && conversation.CapturedAtUtc > contextCapturedAtUtc)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidTimestamp, "$.invokingConversation.capturedAtUtc");
        }
    }

    private static void ValidateContext(
        IReadOnlyList<CustomLoopContextManifestSource>? manifest,
        CustomLoopConversationReference? invokingConversation,
        DateTimeOffset capturedAtUtc,
        List<GovernedLoopSequentialValidationError> errors)
    {
        if (manifest is null)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.Required, "$.contextManifest");
            return;
        }

        if (manifest.Count > GovernedLoopSequentialContractLimits.MaxContextSources)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.CollectionTooLarge, "$.contextManifest");
            return;
        }

        if (manifest.Count < 7)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.contextManifest");
        }

        if (manifest.Count > 7 && invokingConversation is null)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, "$.invokingConversation");
        }

        var workspaceSources = new[]
        {
            (Id: "nearest-agents", PathSuffix: "AGENTS.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "role", PathSuffix: ".agent/ROLE.md", Source: CustomLoopContextSource.RoleInstruction, Provenance: CustomLoopContextProvenance.WorkspaceRoleFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "soul", PathSuffix: ".agent/SOUL.md", Source: CustomLoopContextSource.AgentIdentity, Provenance: CustomLoopContextProvenance.WorkspaceAgentIdentityFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "personality", PathSuffix: ".agent/PERSONALITY.md", Source: CustomLoopContextSource.AgentIdentity, Provenance: CustomLoopContextProvenance.WorkspaceAgentIdentityFile, Trust: CustomLoopContextTrustClass.TrustedInstruction, Role: LlmMessageRole.System),
            (Id: "context", PathSuffix: ".agent/CONTEXT.md", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
            (Id: "memory", PathSuffix: ".agent/MEMORY.md", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
            (Id: "models", PathSuffix: ".agent/models.json", Source: CustomLoopContextSource.ContextualState, Provenance: CustomLoopContextProvenance.WorkspaceContextFile, Trust: CustomLoopContextTrustClass.UntrustedData, Role: LlmMessageRole.User),
        };

        long totalCharacters = 0;
        long conversationCharacters = 0;
        var conversationSources = 0;
        var omittedConversationSources = 0;
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Count; index++)
        {
            var source = manifest[index];
            var path = $"$.contextManifest[{index}]";
            if (source is null)
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.Required, path);
                continue;
            }

            if (source.Order != index + 1)
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, $"{path}.order");
            }

            ValidateEnumeration(source.SourceType, $"{path}.sourceType", errors);
            ValidateEnumeration(source.Provenance, $"{path}.provenance", errors);
            ValidateEnumeration(source.TrustClass, $"{path}.trustClass", errors);
            ValidateEnumeration(source.Role, $"{path}.role", errors);
            ValidateText(source.SourceId, $"{path}.sourceId", GovernedLoopSequentialContractLimits.MaxReferenceCharacters, required: true, errors);
            ValidateText(source.SourcePath, $"{path}.sourcePath", GovernedLoopSequentialContractLimits.MaxReferenceCharacters, required: true, errors);
            ValidateText(source.Content, $"{path}.content", GovernedLoopSequentialContractLimits.MaxContextSourceCharacters, required: source.OmissionReason is null, errors);
            ValidateOptionalText(source.TruncationReason, $"{path}.truncationReason", GovernedLoopSequentialContractLimits.MaxReasonCharacters, errors);
            ValidateOptionalText(source.OmissionReason, $"{path}.omissionReason", GovernedLoopSequentialContractLimits.MaxReasonCharacters, errors);
            ValidateHash(source.ContentHash, $"{path}.contentHash", errors);
            if (IsHash(source.ContentHash) && !CustomLoopTraceContentHash.Matches(source.Content ?? string.Empty, source.ContentHash))
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.HashMismatch, $"{path}.contentHash");
            }

            if (!string.IsNullOrEmpty(source.SourceId) && !sourceIds.Add(source.SourceId))
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, $"{path}.sourceId");
            }

            if (index < workspaceSources.Length)
            {
                var expected = workspaceSources[index];
                if (!string.Equals(source.SourceId, expected.Id, StringComparison.Ordinal)
                    || !HasPathSuffix(source.SourcePath, expected.PathSuffix)
                    || source.SourceType != expected.Source
                    || source.Provenance != expected.Provenance
                    || source.TrustClass != expected.Trust
                    || source.Role != expected.Role)
                {
                    Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, path);
                }

                if (source.UsedCharacterCount > CustomLoopLimits.MaxInstructionCharacters)
                {
                    Add(errors, GovernedLoopSequentialValidationErrorCode.CollectionTooLarge, $"{path}.usedCharacterCount");
                }
            }
            else if (source.SourceType != CustomLoopContextSource.InvokingConversation
                || source.Provenance != CustomLoopContextProvenance.LogicalConversation
                || source.TrustClass != CustomLoopContextTrustClass.UntrustedData
                || source.Role != LlmMessageRole.User)
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, path);
            }

            ValidateUtc(source.CapturedAtUtc, $"{path}.capturedAtUtc", errors);
            if (IsUtc(source.CapturedAtUtc) && IsUtc(capturedAtUtc) && source.CapturedAtUtc != capturedAtUtc)
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidTimestamp, $"{path}.capturedAtUtc");
            }

            var usedCharacters = source.Content?.Length ?? 0;
            if (source.OriginalCharacterCount < 0 || source.UsedCharacterCount != usedCharacters || source.OriginalCharacterCount < source.UsedCharacterCount)
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, $"{path}.usedCharacterCount");
            }

            if (source.OmissionReason is not null)
            {
                if (usedCharacters != 0 || source.UsedCharacterCount != 0 || source.Truncated || source.TruncationReason is not null)
                {
                    Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, path);
                }
            }
            else if (source.Truncated != (source.OriginalCharacterCount > source.UsedCharacterCount)
                || source.Truncated != (source.TruncationReason is not null))
            {
                Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidComposition, path);
            }

            totalCharacters += usedCharacters;
            if (source.SourceType == CustomLoopContextSource.InvokingConversation)
            {
                if (source.OmissionReason is null)
                {
                    conversationSources++;
                    conversationCharacters += usedCharacters;
                }
                else
                {
                    omittedConversationSources++;
                }
            }
        }

        if (totalCharacters > GovernedLoopSequentialContractLimits.MaxContextCharacters)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.CollectionTooLarge, "$.contextManifest");
        }

        if (conversationSources > GovernedLoopSequentialContractLimits.MaxInvokingConversationSources
            || conversationCharacters > GovernedLoopSequentialContractLimits.MaxInvokingConversationCharacters
            || omittedConversationSources > 1)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.CollectionTooLarge, "$.contextManifest");
        }
    }

    private static void ValidateSchema(int value, string path, List<GovernedLoopSequentialValidationError> errors)
    {
        if (value != GovernedLoopSequentialContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.UnsupportedSchemaVersion, path);
        }
    }

    private static void ValidateToken(string? value, string path, List<GovernedLoopSequentialValidationError> errors)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > GovernedLoopSequentialContractLimits.MaxIdentifierCharacters
            || value[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || value[^1] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || value.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_' and not '.'))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidateText(
        string? value,
        string path,
        int maximumCharacters,
        bool required,
        List<GovernedLoopSequentialValidationError> errors)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidText, path);
            return;
        }

        if (value.Length > maximumCharacters || !IsSafeNormalizedText(value))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidText, path);
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string path,
        int maximumCharacters,
        List<GovernedLoopSequentialValidationError> errors)
    {
        if (value is not null)
        {
            ValidateText(value, path, maximumCharacters, required: false, errors);
        }
    }

    private static void ValidateHash(string? value, string path, List<GovernedLoopSequentialValidationError> errors)
    {
        if (!IsHash(value))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidHash, path);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<GovernedLoopSequentialValidationError> errors)
    {
        if (!IsUtc(value))
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidTimestamp, path);
        }
    }

    private static void ValidateEnumeration<TEnum>(TEnum value, string path, List<GovernedLoopSequentialValidationError> errors)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            Add(errors, GovernedLoopSequentialValidationErrorCode.InvalidEnumeration, path);
        }
    }

    private static bool IsSafeNormalizedText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character) || char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            {
                return false;
            }
        }

        return value.IsNormalized(NormalizationForm.FormC);
    }

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopSequentialContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool HasPathSuffix(string? path, string expectedSuffix)
        => path?.Replace('\\', '/').EndsWith(expectedSuffix, StringComparison.Ordinal) == true;

    private static GovernedLoopSequentialValidationResult Result(IEnumerable<GovernedLoopSequentialValidationError> errors)
        => GovernedLoopSequentialValidationResult.FromErrors(errors);

    private static void Add(
        ICollection<GovernedLoopSequentialValidationError> errors,
        GovernedLoopSequentialValidationErrorCode code,
        string path)
        => errors.Add(GovernedLoopSequentialValidationError.Create(code, path));
}
