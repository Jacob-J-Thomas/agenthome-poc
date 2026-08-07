using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reports value-free evidence persistence acceptance or rejection.</summary>
public sealed class CredentialEvidenceWriteResult
{
    private CredentialEvidenceWriteResult(bool succeeded, CredentialFailure? failure)
    {
        Succeeded = succeeded;
        Failure = failure;
    }

    /// <summary>Gets whether the evidence was durably accepted.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the closed value-free failure.</summary>
    public CredentialFailure? Failure { get; }

    /// <summary>Creates a successful append result.</summary>
    public static CredentialEvidenceWriteResult Success() => new(true, null);

    /// <summary>Creates a failed append result.</summary>
    public static CredentialEvidenceWriteResult Failed(CredentialFailure failure) => new(false, failure ?? throw new ArgumentNullException(nameof(failure)));
}
