using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogFileAttributeTagInfo
{
    public uint FileAttributes;
    public uint ReparseTag;
}
