using EmbodySense.Core.Common.Governance.Tools;

namespace EmbodySense.Core.Common.Inference.Models;

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

public sealed record LlmInferenceRequest
{
    public LlmInferenceRequest(
        IReadOnlyList<LlmMessage> messages,
        LlmInferenceOptions? options = null,
        LlmInferenceInstructionContext? instructionContext = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException(
                "At least one message is required for LLM inferencing.",
                nameof(messages));
        }

        Messages = messages.ToArray();
        Options = options ?? LlmInferenceOptions.Default;
        InstructionContext = instructionContext;
    }

    public IReadOnlyList<LlmMessage> Messages { get; }

    public LlmInferenceOptions Options { get; }

    public LlmInferenceInstructionContext? InstructionContext { get; }

    public static LlmInferenceRequest FromUserText(string text, LlmInferenceOptions? options = null)
    {
        return new LlmInferenceRequest([LlmMessage.User(text)], options);
    }
}
