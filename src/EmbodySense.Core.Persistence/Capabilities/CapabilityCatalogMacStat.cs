using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogMacStat
{
    public uint Device;
    public ushort Mode;
    public ushort LinkCount;
    public ulong Inode;
    public uint UserId;
    public uint GroupId;
    public uint RawDevice;
    public CapabilityCatalogMacTimespec AccessTime;
    public CapabilityCatalogMacTimespec ModificationTime;
    public CapabilityCatalogMacTimespec ChangeTime;
    public CapabilityCatalogMacTimespec BirthTime;
    public long Size;
    public long Blocks;
    public int BlockSize;
    public uint Flags;
    public uint Generation;
    public int Spare;
    public long Reserved1;
    public long Reserved2;
}
