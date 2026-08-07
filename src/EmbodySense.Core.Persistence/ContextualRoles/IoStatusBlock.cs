using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    public IntPtr Status;
    public IntPtr Information;
}
