using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Startup.Loops.Execution;

public static class LoopRunTransportLimits
{
    private const int MaxJsonUtf8BytesPerCharacter = 6;
    private const int SignalRInvocationEnvelopeUtf8Bytes = 8_192;

    public const long MaxSignalRInvocationMessageUtf8Bytes = (long)CustomLoopLimits.MaxPresetPromptCharacters * MaxJsonUtf8BytesPerCharacter + SignalRInvocationEnvelopeUtf8Bytes;
}
