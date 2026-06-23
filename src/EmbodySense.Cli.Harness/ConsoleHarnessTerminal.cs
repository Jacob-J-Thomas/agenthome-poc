using EmbodySense.Core.Application.Harness;

namespace EmbodySense.Cli.Harness;

public sealed class ConsoleHarnessTerminal : IHarnessClient
{
    public static ConsoleHarnessTerminal Instance { get; } = new();

    private ConsoleHarnessTerminal()
    {
    }

    public string? ReadLine()
    {
        return Console.ReadLine();
    }

    public void Write(string value)
    {
        Console.Write(value);
    }

    public void WriteLine(string value = "")
    {
        Console.WriteLine(value);
    }
}
