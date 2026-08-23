using System.Globalization;
using EmbodySense.Core.Common.LocalWorkspace.Actions;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Captures stable native identity and entry posture from a retained no-follow handle.</summary>
internal readonly record struct WorkspaceActionNativeFileStamp(
    ulong Device,
    ulong MountId,
    string FileIdentity,
    string LifetimeIdentity,
    uint LinkCount,
    uint Mode,
    uint OwnerId,
    uint GroupId,
    bool IsDirectory,
    bool IsRegularFile,
    bool IsReparsePoint)
{
    /// <summary>Gets the value-free domain-separated native identity fingerprint.</summary>
    public string Fingerprint => WorkspaceActionFingerprint.Compute(
        "embodysense.workspace-native-identity.v1",
        Device.ToString(CultureInfo.InvariantCulture),
        MountId.ToString(CultureInfo.InvariantCulture),
        FileIdentity,
        LifetimeIdentity);

    /// <summary>Returns whether two retained handles identify the same physical filesystem entry.</summary>
    public bool SameEntry(WorkspaceActionNativeFileStamp other)
        => SameMount(other)
            && string.Equals(FileIdentity, other.FileIdentity, StringComparison.Ordinal)
            && string.Equals(LifetimeIdentity, other.LifetimeIdentity, StringComparison.Ordinal);

    /// <summary>Returns whether Windows replacement retained the staged file identity and original target lifetime.</summary>
    public bool MatchesWindowsReplacementPublication(WorkspaceActionNativeFileStamp stage, WorkspaceActionNativeFileStamp originalTarget)
        => SameMount(stage)
            && SameMount(originalTarget)
            && string.Equals(FileIdentity, stage.FileIdentity, StringComparison.Ordinal)
            && string.Equals(LifetimeIdentity, originalTarget.LifetimeIdentity, StringComparison.Ordinal);

    /// <summary>Returns whether two retained handles are rooted in the same exact mounted filesystem instance.</summary>
    public bool SameMount(WorkspaceActionNativeFileStamp other) => Device == other.Device && MountId == other.MountId;
}
