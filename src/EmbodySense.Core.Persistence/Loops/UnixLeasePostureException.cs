namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Identifies an initial Unix lease posture that may belong to a peer still completing creation.</summary>
internal sealed class UnixLeasePostureException(string message) : IOException(message);
