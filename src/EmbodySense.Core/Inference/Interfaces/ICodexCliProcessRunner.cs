using System.Diagnostics;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Interfaces;

public interface ICodexCliProcessRunner
{
    Task<CodexCliProcessResult> RunAsync(ProcessStartInfo startInfo, string standardInput, CancellationToken cancellationToken = default);
}
