using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Represents a custom loop context assembly.
/// </summary>
/// <param name="Request">The request.</param>
/// <param name="Blocks">The blocks.</param>
/// <param name="ResolvedOutputPolicy">The resolved output policy.</param>
public sealed record CustomLoopContextAssembly(
    LlmInferenceRequest Request,
    CustomLoopContextBlock[] Blocks,
    CustomLoopContextOutputPolicy ResolvedOutputPolicy)
{
    /// <summary>
    /// Gets the logical request character count.
    /// </summary>
    /// <value>The logical request character count.</value>
    public long LogicalRequestCharacterCount
    {
        get
        {
            var messageCharacters = Request.Messages.Sum(message => (long)message.Content.Length);
            var developerInstructionCharacters = Request.InstructionContext is null
                ? 0
                : EmbodySenseDeveloperInstructions.Compose(Request.InstructionContext.Governance, Request.InstructionContext.TrustedInstructions).Length;
            return messageCharacters + developerInstructionCharacters;
        }
    }
}
