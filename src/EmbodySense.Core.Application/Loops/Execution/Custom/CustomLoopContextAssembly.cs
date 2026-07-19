using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopContextAssembly(
    LlmInferenceRequest Request,
    CustomLoopContextBlock[] Blocks,
    CustomLoopContextOutputPolicy ResolvedOutputPolicy)
{
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
