using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFileTime
{
    public uint Low;
    public uint High;
}
