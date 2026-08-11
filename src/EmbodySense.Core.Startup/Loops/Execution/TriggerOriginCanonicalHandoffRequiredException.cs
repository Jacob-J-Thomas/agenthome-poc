namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Identifies a trigger-origin resume that cannot prove its canonical durable hand-off.</summary>
internal sealed class TriggerOriginCanonicalHandoffRequiredException : InvalidOperationException
{
    /// <summary>Creates the stable fail-closed trigger-origin resume failure.</summary>
    public TriggerOriginCanonicalHandoffRequiredException()
        : base("Trigger-origin resume requires the exact durable canonical adapter binding and invocation snapshot; legacy execution is not permitted.")
    {
    }
}
