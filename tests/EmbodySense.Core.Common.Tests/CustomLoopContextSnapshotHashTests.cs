using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Tests;

public sealed class CustomLoopContextSnapshotHashTests
{
    [Fact]
    public void Context_identity_ignores_capture_time_but_preserves_canonical_context_shape()
    {
        var first = CustomLoopContextSnapshot.CreateEmpty(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var second = CustomLoopContextSnapshot.CreateEmpty(first.CapturedAtUtc.AddMinutes(1));

        Assert.NotEqual(first.ManifestHash, second.ManifestHash);
        Assert.Equal(CustomLoopContextSnapshotHash.ComputeIdentity(first), CustomLoopContextSnapshotHash.ComputeIdentity(second));
        Assert.NotEqual(CustomLoopContextSnapshotHash.ComputeIdentity(first), CustomLoopContextSnapshotHash.ComputeIdentity(second with { SchemaVersion = second.SchemaVersion + 1 }));
    }
}
