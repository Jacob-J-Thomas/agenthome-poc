using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Accepts bounded, value-free credential-use evidence.</summary>
public interface ICredentialUseEvidenceSink
{
    /// <summary>Appends one validated, value-free use-evidence record.</summary>
    ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken);
}
