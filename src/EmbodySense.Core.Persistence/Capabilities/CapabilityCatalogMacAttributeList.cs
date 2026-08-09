using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogMacAttributeList
{
    public ushort BitmapCount;
    public ushort Reserved;
    public uint CommonAttributes;
    public uint VolumeAttributes;
    public uint DirectoryAttributes;
    public uint FileAttributes;
    public uint ForkAttributes;
}
