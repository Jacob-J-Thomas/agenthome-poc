using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Accepts bounded, value-free credential-use evidence.</summary>
public interface ICredentialUseEvidenceSink
{
    /// <summary>Reserves authenticated terminal-evidence and operation capacity for one exact lease before redemption.</summary>
    /// <param name="intent">The validated exact lease identity whose terminal evidence must remain appendable.</param>
    /// <param name="cancellationToken">Stops the reservation before durable publication.</param>
    /// <returns>A fail-closed result that is successful only when the reservation already or newly exists.</returns>
    ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken);

    /// <summary>Appends one validated, value-free use-evidence record.</summary>
    ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken);
}
