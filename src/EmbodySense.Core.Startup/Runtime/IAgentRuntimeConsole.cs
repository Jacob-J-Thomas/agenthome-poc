namespace EmbodySense.Core.Startup.Runtime;

public interface IAgentRuntimeConsole
{
    string? ReadLine();

    void Write(string value);

    void WriteLine(string value = "");
}
