using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

// FILE_DIRECTORY_INFORMATION has one 64-byte, 8-byte-aligned fixed header on all supported Windows architectures; FileName begins immediately after it.
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct FileDirectoryInformationHeader
{
    [FieldOffset(0)]
    public uint NextEntryOffset;

    [FieldOffset(60)]
    public uint FileNameLength;
}
