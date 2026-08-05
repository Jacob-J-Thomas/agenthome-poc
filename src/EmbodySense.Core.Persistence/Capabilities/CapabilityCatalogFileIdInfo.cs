using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogFileIdInfo
{
    public ulong VolumeSerialNumber;
    public Guid FileId;
}
