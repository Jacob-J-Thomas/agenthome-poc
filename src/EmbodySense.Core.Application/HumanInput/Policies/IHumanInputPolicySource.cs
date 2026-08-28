using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Application.HumanInput.Policies;

/// <summary>Reads one exact immutable Human Input policy revision without selecting a default, current, or replacement revision.</summary>
public interface IHumanInputPolicySource
{
    /// <summary>Reads one exact policy and revision identity under the source's durable consistency boundary.</summary>
    /// <param name="reference">The exact immutable policy reference.</param>
    /// <param name="cancellationToken">A token that cancels the bounded lookup.</param>
    /// <returns>A detached policy artifact only when the source proves the exact revision is available.</returns>
    Task<HumanInputPolicySourceReadResult> ReadAsync(HumanInputPolicyReference reference, CancellationToken cancellationToken = default);
}
