namespace EmbodySense.Cli.Command;

public interface IAgentRuntimeConsole
{
    string? ReadLine();

    void Clear();

    void Write(string value);

    void WriteLine(string value = "");
}
