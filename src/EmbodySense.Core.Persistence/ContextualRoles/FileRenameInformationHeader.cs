using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

// The leading 4-byte field is the documented BOOLEAN/ULONG union, which preserves the HANDLE alignment required by FILE_RENAME_INFORMATION.
[StructLayout(LayoutKind.Sequential)]
internal struct FileRenameInformationHeader
{
    public uint ReplaceIfExistsOrFlags;
    public IntPtr RootDirectory;
    public uint FileNameLength;
}
