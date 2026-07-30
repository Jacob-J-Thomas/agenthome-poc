using System.Text;

namespace EmbodySense.E2ETests.Web;

internal sealed class ProcessOutputBuffer
{
    private const int MaxCharacters = 64_000;
    private readonly StringBuilder _builder = new();

    public string Text
    {
        get
        {
            lock (_builder)
            {
                return _builder.ToString();
            }
        }
    }

    public void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_builder)
        {
            _builder.AppendLine(line);
            if (_builder.Length > MaxCharacters)
            {
                _builder.Remove(0, _builder.Length - MaxCharacters);
            }
        }
    }
}
