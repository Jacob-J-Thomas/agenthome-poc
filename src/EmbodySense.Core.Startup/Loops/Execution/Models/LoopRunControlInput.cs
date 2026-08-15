using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Identifies an optimistic, idempotent pause, cancel, or resume request.
/// </summary>
public sealed record LoopRunControlInput
{
    private string _runId = string.Empty;
    private string _operationId = string.Empty;
    private int _expectedLifecycleVersion;

    /// <summary>
    /// Initializes one validated lifecycle-control input before any retained-runtime recovery is attempted.
    /// </summary>
    /// <param name="runId">The exact run identifier.</param>
    /// <param name="expectedLifecycleVersion">The positive expected lifecycle version.</param>
    /// <param name="operationId">The exact bounded operation identifier.</param>
    /// <exception cref="ArgumentException">Thrown when an identifier is not canonical.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the expected lifecycle version is not positive.</exception>
    public LoopRunControlInput(string runId, int expectedLifecycleVersion, string operationId)
    {
        RunId = runId;
        ExpectedLifecycleVersion = expectedLifecycleVersion;
        OperationId = operationId;
    }

    /// <summary>
    /// Gets the exact run identifier.
    /// </summary>
    public string RunId
    {
        get => _runId;
        init
        {
            CustomLoopArtifactIdentifier.Require(value, nameof(RunId));
            _runId = value;
        }
    }

    /// <summary>
    /// Gets the positive expected lifecycle version.
    /// </summary>
    public int ExpectedLifecycleVersion
    {
        get => _expectedLifecycleVersion;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(ExpectedLifecycleVersion), "Expected lifecycle version must be at least one.");
            }

            _expectedLifecycleVersion = value;
        }
    }

    /// <summary>
    /// Gets the exact bounded operation identifier.
    /// </summary>
    public string OperationId
    {
        get => _operationId;
        init
        {
            CustomLoopArtifactIdentifier.Require(value, nameof(OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
            _operationId = value;
        }
    }
}
