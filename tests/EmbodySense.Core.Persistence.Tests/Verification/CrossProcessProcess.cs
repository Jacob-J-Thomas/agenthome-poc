using System.Diagnostics;
using System.IO;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed class CrossProcessProcess : IDisposable
{
    private readonly CrossProcessProcessOwnership _ownership;

    internal CrossProcessProcess(Process process, CrossProcessProcessOwnership ownership)
    {
        Process = process;
        _ownership = ownership;
    }

    internal Process Process { get; }

    internal CrossProcessProcessOwnership Ownership => _ownership;

    internal StreamReader StandardOutput => _ownership.StandardOutput;

    internal StreamReader StandardError => _ownership.StandardError;

    internal StreamWriter StandardInput => _ownership.StandardInput;

    internal bool HasExited => Process.HasExited;

    internal int ExitCode => Process.ExitCode;

    internal int Id => Process.Id;

    internal Task WaitForExitAsync() => Process.WaitForExitAsync();

    public void Dispose()
    {
        _ownership.Dispose();
        Process.Dispose();
    }
}
