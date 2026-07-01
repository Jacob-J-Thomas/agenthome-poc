using EmbodySense.Core.Common.Runtime.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class RuntimeSurfaceIdTests
{
    [Fact]
    public void Create_normalizes_and_rejects_unsafe_surface_ids()
    {
        var web = RuntimeSurfaceId.Create(" Web ");
        var custom = RuntimeSurfaceId.Create("editor-panel");

        Assert.Equal("web", web.Id);
        Assert.Equal("editor-panel", custom.Id);
        Assert.Equal("cli", RuntimeSurfaceId.Cli.Id);
        Assert.Equal("runtime", RuntimeSurfaceId.Runtime.Id);
        Assert.Equal("web", web.ToString());
        Assert.Throws<ArgumentException>(() => RuntimeSurfaceId.Create(" "));
        Assert.Throws<ArgumentException>(() => RuntimeSurfaceId.Create("web/ui"));
    }
}
