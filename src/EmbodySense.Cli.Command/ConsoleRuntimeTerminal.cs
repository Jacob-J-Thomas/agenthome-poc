namespace EmbodySense.Cli.Command;

/// <summary>
/// Adapts the process console to the runtime console boundary.
/// </summary>
public sealed class ConsoleRuntimeTerminal : IAgentRuntimeConsole
{
    /// <summary>
    /// Gets the shared stateless console adapter.
    /// </summary>
    public static ConsoleRuntimeTerminal Instance { get; } = new();

    private ConsoleRuntimeTerminal()
    {
    }

    /// <inheritdoc />
    public string? ReadLine()
    {
        return Console.ReadLine();
    }

    /// <summary>
    /// Clears an interactive console when supported.
    /// </summary>
    /// <remarks>Redirected output and console-specific <see cref="IOException"/> failures leave existing output intact.</remarks>
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

    /// <inheritdoc />
    public void Write(string value)
    {
        Console.Write(value);
    }

    /// <inheritdoc />
    public void WriteLine(string value = "")
    {
        Console.WriteLine(value);
    }
}
