namespace EmbodySense.Cli.Harness;

public interface IHarnessTerminal
{
    string? ReadLine();

    void Write(string value);

    void WriteLine(string value = "");
}
