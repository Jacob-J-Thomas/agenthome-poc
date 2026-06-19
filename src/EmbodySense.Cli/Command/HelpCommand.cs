using EmbodySense.Cli.Command.Models;

namespace EmbodySense.Cli.Command;

public static class HelpCommand
{
    public static void PrintRoot()
    {
        Console.WriteLine("""
            EmbodySense POC CLI

            usage:
              embodysense init [root]
              embodysense run [--model model] [--workdir path]
              embodysense status [root]
              embodysense audit [tail] [root] [--limit count]

            example:
              embodysense init ./scratch
              embodysense run
              embodysense audit tail ./scratch --limit 10
            """);
    }
}
