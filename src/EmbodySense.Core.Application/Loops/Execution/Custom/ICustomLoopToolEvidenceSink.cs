using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopToolEvidenceSink
{
    Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default);
}

public sealed class CustomLoopToolEvidenceIntegrityException : Exception
{
    public CustomLoopToolEvidenceIntegrityException(string message) : base(message)
    {
    }

    public CustomLoopToolEvidenceIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
