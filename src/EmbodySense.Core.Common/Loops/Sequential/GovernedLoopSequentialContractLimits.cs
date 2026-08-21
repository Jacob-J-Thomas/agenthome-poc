using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.Sequential;

/// <summary>Defines finite schema-1 bounds for the sequential governed-loop hand-off contracts.</summary>
public static class GovernedLoopSequentialContractLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum trigger-prompt character count.</summary>
    public const int MaxTriggerPromptCharacters = CustomLoopLimits.MaxPresetPromptCharacters;

    /// <summary>Gets the maximum provider, model, conversation, or provenance-reference character count.</summary>
    public const int MaxReferenceCharacters = CustomLoopLimits.MaxTraceReferenceCharacters;

    /// <summary>Gets the maximum retained content characters for one context source.</summary>
    public const int MaxContextSourceCharacters = CustomLoopLimits.MaxLogicalProviderRequestCharacters;

    /// <summary>Gets the maximum retained context characters across one invocation snapshot.</summary>
    public const int MaxContextCharacters = CustomLoopLimits.MaxLogicalProviderRequestCharacters;

    /// <summary>Gets the maximum truncation or omission reason character count.</summary>
    public const int MaxReasonCharacters = CustomLoopLimits.MaxRunDetailCharacters;

    /// <summary>Gets the seven workspace sources plus bounded invoking-conversation sources and one aggregate omission.</summary>
    public const int MaxContextSources = 7 + CustomLoopLimits.MaxInvokingConversationEntries + 1;

    /// <summary>Gets the maximum number of included invoking-conversation sources.</summary>
    public const int MaxInvokingConversationSources = CustomLoopLimits.MaxInvokingConversationEntries;

    /// <summary>Gets the maximum included invoking-conversation character count.</summary>
    public const int MaxInvokingConversationCharacters = CustomLoopLimits.MaxInvokingConversationCharacters;

    /// <summary>Gets the maximum stable operation or run-anchor identifier length.</summary>
    public const int MaxIdentifierCharacters = 128;

    /// <summary>Gets the maximum distinct server-registered command capabilities pinned by one graph hand-off.</summary>
    public const int MaxCommandActionCapabilities = 256;

    /// <summary>Gets the maximum structured validation errors returned by one call.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>Gets the maximum safe validation-path character count.</summary>
    public const int MaxValidationPathCharacters = 256;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
}
