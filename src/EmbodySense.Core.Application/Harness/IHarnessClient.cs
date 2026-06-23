namespace EmbodySense.Core.Application.Harness;

public interface IHarnessClient
{
    string? ReadLine();

    void Write(string value);

    void WriteLine(string value = "");
}
