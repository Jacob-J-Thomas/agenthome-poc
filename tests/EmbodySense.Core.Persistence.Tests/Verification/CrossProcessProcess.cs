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

    internal bool HasExited => _ownership.HasExited;

    internal int ExitCode => _ownership.ExitCode;

    internal int Id => _ownership.Id;

    internal Task WaitForExitAsync(CancellationToken cancellationToken = default) => _ownership.WaitForExitAsync(cancellationToken);

    internal Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
        => _ownership.ReadStandardOutputToEndAsync(cancellationToken);

    internal Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
        => _ownership.ReadStandardErrorToEndAsync(cancellationToken);

    public void Dispose()
    {
        _ownership.Dispose();
        Process.Dispose();
    }
}
