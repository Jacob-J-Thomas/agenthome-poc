namespace EmbodySense.Cli.Command;

/// <summary>
/// Abstracts interactive text input, output, and transcript replacement for the CLI runtime host.
/// </summary>
public interface IAgentRuntimeConsole
{
    /// <summary>
    /// Reads the next input line.
    /// </summary>
    /// <returns>The line without its terminator, or <see langword="null"/> at end-of-input.</returns>
    string? ReadLine();

    /// <summary>
    /// Clears previously projected content when the backing terminal supports it.
    /// </summary>
    void Clear();

    /// <summary>
    /// Writes text without adding a line terminator.
    /// </summary>
    /// <param name="value">The text to write.</param>
    void Write(string value);

    /// <summary>
    /// Writes text followed by the terminal line terminator.
    /// </summary>
    /// <param name="value">The text to write, or an empty string for a blank line.</param>
    void WriteLine(string value = "");
}
