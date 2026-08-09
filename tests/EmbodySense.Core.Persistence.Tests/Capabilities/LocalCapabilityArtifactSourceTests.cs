using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class LocalCapabilityArtifactSourceTests
{
    [Fact]
    public async Task Contained_regular_file_is_read_as_defensive_bounded_content()
    {
        using var workspace = new TestWorkspace();
        var sourceRoot = workspace.File("sources");
        Directory.CreateDirectory(sourceRoot);
        var path = Path.Combine(sourceRoot, "artifact.bin");
        await File.WriteAllBytesAsync(path, "artifact"u8.ToArray());
        var source = new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, new Uri(path).AbsoluteUri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned);

        var content = await new LocalCapabilityArtifactSource(sourceRoot).ReadAsync(source);
        var first = content.ToArray();
        first[0] = 0;

        Assert.Equal("artifact"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task Path_escape_and_wrong_source_kind_are_rejected()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.File("sources");
        Directory.CreateDirectory(root);
        var outside = workspace.File("outside.bin");
        await File.WriteAllTextAsync(outside, "outside");
        var local = new LocalCapabilityArtifactSource(root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => local.ReadAsync(new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, new Uri(outside).AbsoluteUri, "rev", CapabilityArtifactUpdatePolicy.Pinned)));
        await Assert.ThrowsAsync<ArgumentException>(() => local.ReadAsync(new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Remote, "https://example.test/a", "rev", CapabilityArtifactUpdatePolicy.Pinned)));
    }

    [Fact]
    public async Task Symbolic_link_is_rejected_when_platform_can_create_it()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.File("sources");
        Directory.CreateDirectory(root);
        var target = workspace.File("target.bin");
        var link = Path.Combine(root, "linked.bin");
        await File.WriteAllTextAsync(target, "outside");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<IOException>(() => new LocalCapabilityArtifactSource(root).ReadAsync(new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, new Uri(link).AbsoluteUri, "rev", CapabilityArtifactUpdatePolicy.Pinned)));
    }

    [Fact]
    public async Task Missing_contained_file_is_not_treated_as_empty_content()
    {
        using var workspace = new TestWorkspace();
        var root = workspace.File("sources");
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "missing.bin");
        var source = new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, new Uri(missing).AbsoluteUri, "rev", CapabilityArtifactUpdatePolicy.Pinned);

        await Assert.ThrowsAsync<FileNotFoundException>(() => new LocalCapabilityArtifactSource(root).ReadAsync(source));
    }

}
