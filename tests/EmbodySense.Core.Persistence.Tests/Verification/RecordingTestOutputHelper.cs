using Xunit.Abstractions;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed class RecordingTestOutputHelper : ITestOutputHelper
{
    public List<string> Lines { get; } = [];

    public void WriteLine(string message)
    {
        Lines.Add(message);
    }

    public void WriteLine(string format, params object[] args)
    {
        Lines.Add(string.Format(format, args));
    }
}
