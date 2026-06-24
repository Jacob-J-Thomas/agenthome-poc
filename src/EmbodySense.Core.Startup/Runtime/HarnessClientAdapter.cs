using EmbodySense.Core.Application.Harness;

namespace EmbodySense.Core.Startup.Runtime;

internal sealed class HarnessClientAdapter : IHarnessClient
{
    private readonly IAgentRuntimeConsole _console;

    public HarnessClientAdapter(IAgentRuntimeConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);

        _console = console;
    }

    public string? ReadLine()
    {
        return _console.ReadLine();
    }

    public void Clear()
    {
        _console.Clear();
    }

    public void Write(string value)
    {
        _console.Write(value);
    }

    public void WriteLine(string value = "")
    {
        _console.WriteLine(value);
    }
}
