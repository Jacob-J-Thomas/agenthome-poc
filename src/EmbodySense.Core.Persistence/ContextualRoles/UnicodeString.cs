using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}
