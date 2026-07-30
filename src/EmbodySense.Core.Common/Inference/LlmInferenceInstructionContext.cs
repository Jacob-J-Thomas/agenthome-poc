using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Inference;

public sealed record LlmInferenceInstructionContext
{
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

    public EmbodySenseDeveloperInstructionSet Governance { get; }

    public IReadOnlyList<EmbodySenseTrustedInstruction> TrustedInstructions { get; }

    public bool PreserveExactLogicalContext { get; }
}
