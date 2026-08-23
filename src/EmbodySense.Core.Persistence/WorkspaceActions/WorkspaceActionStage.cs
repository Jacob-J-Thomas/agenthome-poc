using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Owns one authenticated private same-filesystem stage until publication or cleanup.</summary>
internal sealed class WorkspaceActionStage(
    SafeFileHandle directory,
    SafeFileHandle file,
    string name,
    WorkspaceActionNativeFileStamp identity,
    SafeFileHandle marker,
    string markerName,
    WorkspaceActionNativeFileStamp markerIdentity) : IDisposable
{
    private SafeFileHandle? _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private SafeFileHandle? _file = file ?? throw new ArgumentNullException(nameof(file));
    private SafeFileHandle? _marker = marker ?? throw new ArgumentNullException(nameof(marker));

    public SafeFileHandle Directory => _directory ?? throw new ObjectDisposedException(nameof(WorkspaceActionStage));

    public SafeFileHandle File => _file ?? throw new ObjectDisposedException(nameof(WorkspaceActionStage));

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public WorkspaceActionNativeFileStamp Identity { get; } = identity;

    public SafeFileHandle Marker => _marker ?? throw new ObjectDisposedException(nameof(WorkspaceActionStage));

    public string MarkerName { get; } = markerName ?? throw new ArgumentNullException(nameof(markerName));

    public WorkspaceActionNativeFileStamp MarkerIdentity { get; } = markerIdentity;

    public bool Published { get; set; }
    public bool HasRetainedFileHandle => _file is not null;

    public void ReleaseFileHandle()
    {
        _file?.Dispose();
        _file = null;
    }


    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        _marker?.Dispose();
        _marker = null;
        _directory?.Dispose();
        _directory = null;
    }
}
