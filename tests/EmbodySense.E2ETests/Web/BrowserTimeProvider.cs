namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
