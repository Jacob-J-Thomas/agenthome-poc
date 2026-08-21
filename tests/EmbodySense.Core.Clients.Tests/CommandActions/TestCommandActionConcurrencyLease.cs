namespace EmbodySense.Core.Clients.Tests.CommandActions;

internal sealed class TestCommandActionConcurrencyLease : IAsyncDisposable
{
    internal static TestCommandActionConcurrencyLease Instance { get; } = new();

    private TestCommandActionConcurrencyLease()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
