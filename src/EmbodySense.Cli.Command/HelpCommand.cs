using EmbodySense.Cli.Command;

namespace EmbodySense.Cli.Command;

/// <summary>
/// Writes root CLI usage for the currently implemented commands.
/// </summary>
public static class HelpCommand
{
    /// <summary>
    /// Writes root usage and examples to standard output.
    /// </summary>
    public static void PrintRoot()
    {
        Console.WriteLine("""
            EmbodySense POC CLI

            usage:
              embodysense init [root]
              embodysense run [--model model] [--workdir path] [--verbose]
              embodysense status [root]
              embodysense audit [tail] [root] [--limit count]

            example:
              embodysense init ./scratch
              embodysense run
              embodysense audit tail ./scratch --limit 10
            """);
    }
}
