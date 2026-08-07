using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogLinuxStatx
{
    public uint Mask;
    public uint BlockSize;
    public ulong Attributes;
    public uint LinkCount;
    public uint UserId;
    public uint GroupId;
    public ushort Mode;
    public ushort Spare0;
    public ulong Inode;
    public ulong Size;
    public ulong Blocks;
    public ulong AttributesMask;
    public CapabilityCatalogStatxTimestamp AccessTime;
    public CapabilityCatalogStatxTimestamp BirthTime;
    public CapabilityCatalogStatxTimestamp ChangeTime;
    public CapabilityCatalogStatxTimestamp ModificationTime;
    public uint DeviceIdMajor;
    public uint DeviceIdMinor;
    public uint DeviceMajor;
    public uint DeviceMinor;
    public ulong MountId;
    public uint DirectIoMemoryAlignment;
    public uint DirectIoOffsetAlignment;
    public ulong Spare1;
    public ulong Spare2;
    public ulong Spare3;
    public ulong Spare4;
    public ulong Spare5;
    public ulong Spare6;
    public ulong Spare7;
    public ulong Spare8;
    public ulong Spare9;
    public ulong Spare10;
    public ulong Spare11;
    public ulong Spare12;
}
