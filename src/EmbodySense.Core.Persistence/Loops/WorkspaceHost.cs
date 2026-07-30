using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class WorkspaceHost : IDisposable
{
    public WorkspaceHost(WorkspacePaths paths, string workspaceKey, FileStream ownership)
    {
        Ownership = ownership;
        CancellationHost = new CustomLoopAttemptCancellationHost(paths, workspaceKey);
    }

    public FileStream Ownership { get; }

    public CustomLoopAttemptCancellationHost CancellationHost { get; }

    public int ReferenceCount { get; set; } = 1;

    public string? ActiveOperationId { get; set; }

    public string? ActiveRequestHash { get; set; }

    public long Generation { get; set; }

    public long BusyOutcomeGeneration { get; set; }

    public Dictionary<string, BusyOutcomeReservation> BusyOutcomeReservations { get; } = new(StringComparer.Ordinal);

    public void Dispose()
    {
        CancellationHost.Dispose();
        Ownership.Dispose();
    }
}
