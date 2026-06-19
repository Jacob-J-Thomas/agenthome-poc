namespace EmbodySense.Cli.Harness;

internal sealed class ConsoleHarnessTerminal : IHarnessTerminal
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
