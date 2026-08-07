using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Provides the Windows handle-bound delete disposition payload.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TriggerQueueFileDispositionInformation
{
    /// <summary>Gets or sets whether the exact open file is deleted when its final handle closes.</summary>
    [MarshalAs(UnmanagedType.U1)]
    public bool DeleteFile;
}
