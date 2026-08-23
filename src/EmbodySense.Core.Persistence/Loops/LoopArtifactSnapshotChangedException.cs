namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Indicates that an artifact changed between the paired byte snapshots used to establish a stable read.
/// </summary>
internal sealed class LoopArtifactSnapshotChangedException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LoopArtifactSnapshotChangedException"/> class.
    /// </summary>
    public LoopArtifactSnapshotChangedException()
        : base("The artifact changed while its bounded read snapshot was being verified.")
    {
    }
}
