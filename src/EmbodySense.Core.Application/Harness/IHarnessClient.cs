namespace EmbodySense.Core.Application.Harness;

public interface IHarnessClient
{
    string? ReadLine();

    void Clear();

    void Write(string value);

    void WriteLine(string value = "");
}
