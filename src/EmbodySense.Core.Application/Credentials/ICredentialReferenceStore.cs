using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reads safe public credential metadata; concrete persistence is intentionally outside this contract slice.</summary>
public interface ICredentialReferenceStore
{
    /// <summary>Gets safe public metadata for one exact reference.</summary>
    ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken);
}
