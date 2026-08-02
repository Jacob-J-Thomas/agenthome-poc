using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.ContextualRoles;

// The leading 4-byte field is the documented BOOLEAN/ULONG union. FileName retains the native
// flexible-array placeholder so Marshal.SizeOf includes the complete FILE_RENAME_INFO minimum size.
[StructLayout(LayoutKind.Sequential)]
internal struct FileRenameInformationHeader
{
    public uint ReplaceIfExistsOrFlags;
    public IntPtr RootDirectory;
    public uint FileNameLength;
    public ushort FileName;
}
