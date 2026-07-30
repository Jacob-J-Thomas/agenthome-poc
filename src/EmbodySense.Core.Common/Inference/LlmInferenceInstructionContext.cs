using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Inference;

/// <summary>
/// Carries fixed governance instructions and ordered trusted workspace instructions into one inference request.
/// </summary>
public sealed record LlmInferenceInstructionContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LlmInferenceInstructionContext"/> type.
    /// </summary>
    /// <param name="governance">The fixed, hash-bound EmbodySense governance snapshot.</param>
    /// <param name="trustedInstructions">Trusted instruction blocks copied in their supplied order.</param>
    /// <param name="preserveExactLogicalContext">Whether the provider request must preserve these logical instruction boundaries exactly.</param>
    /// <exception cref="ArgumentNullException">Thrown when either required instruction input is <see langword="null"/>.</exception>
    public LlmInferenceInstructionContext(
        EmbodySenseDeveloperInstructionSet governance,
        IReadOnlyList<EmbodySenseTrustedInstruction> trustedInstructions,
        bool preserveExactLogicalContext = true)
    {
        ArgumentNullException.ThrowIfNull(governance);
        ArgumentNullException.ThrowIfNull(trustedInstructions);

        Governance = governance;
        TrustedInstructions = trustedInstructions.ToArray();
        PreserveExactLogicalContext = preserveExactLogicalContext;
    }

    /// <summary>
    /// Gets the fixed governance instruction snapshot.
    /// </summary>
    /// <value>The governance EmbodySense developer instruction set.</value>
    public EmbodySenseDeveloperInstructionSet Governance { get; }

    /// <summary>
    /// Gets the EmbodySense trusted instructions.
    /// </summary>
    /// <value>The EmbodySense trusted instructions.</value>
    public IReadOnlyList<EmbodySenseTrustedInstruction> TrustedInstructions { get; }

    /// <summary>
    /// Gets a value indicating whether logical instruction boundaries must remain exact.
    /// </summary>
    /// <value><see langword="true"/> when adapters must preserve the fixed-first governance block and ordered trusted blocks without flattening; otherwise, <see langword="false"/>.</value>
    public bool PreserveExactLogicalContext { get; }
}
