namespace EmbodySense.Cli.Command;

public sealed class ConsoleRuntimeTerminal : IAgentRuntimeConsole
{
    public static ConsoleRuntimeTerminal Instance { get; } = new();

    private ConsoleRuntimeTerminal()
    {
    }

    public string? ReadLine()
    {
        return Console.ReadLine();
    }

    public void Clear()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
        }
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
