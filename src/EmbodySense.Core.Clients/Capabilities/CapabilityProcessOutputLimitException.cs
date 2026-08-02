namespace EmbodySense.Core.Clients.Capabilities;

internal sealed class CapabilityProcessOutputLimitException : Exception
{
    public CapabilityProcessOutputLimitException() : base("Capability process output exceeded its bound.")
    {
    }
}
