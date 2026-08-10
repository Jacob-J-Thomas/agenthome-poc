namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Defines finite schema-version-1 bounds for durable authenticated Human Input responses.</summary>
public static class HumanInputResponseContractLimits
{
    /// <summary>The only supported experimental response contract schema version.</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>The maximum number of retained responses for one request.</summary>
    public const int MaxResponsesPerRequest = 64;
    /// <summary>The maximum number of response-operation records retained for one request.</summary>
    public const int MaxOperationsPerRequest = 256;
    /// <summary>The maximum number of response references in one explicit selection.</summary>
    public const int MaxSelectedResponses = HumanInputLimits.MaxEligibleRespondents;
    /// <summary>The maximum number of structured validation errors returned by one call.</summary>
    public const int MaxValidationErrors = 64;
    /// <summary>The maximum number of characters in one safe schema-relative error path.</summary>
    public const int MaxErrorPathCharacters = 192;
}
