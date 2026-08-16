using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Maps the Startup-owned surface contract into the canonical Application verification port.</summary>
internal sealed class AgentRuntimeAuthenticatedWakeVerificationAdapter : IGovernedLoopAuthenticatedWakeVerificationPort
{
    private readonly IAgentRuntimeAuthenticatedWakeVerifier _inner;

    internal AgentRuntimeAuthenticatedWakeVerificationAdapter(IAgentRuntimeAuthenticatedWakeVerifier inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _inner.VerifyAsync(
            new AgentRuntimeAuthenticatedWakeVerificationRequest(
                request.CheckpointId,
                request.CheckpointHash,
                request.AuthenticatedEventReference,
                request.AuthenticationEvidenceHash,
                request.CheckpointPublishedAtUtc),
            cancellationToken).ConfigureAwait(false);
        if (result is null || !Enum.IsDefined(result.Status))
        {
            return null;
        }

        return new GovernedLoopAuthenticatedWakeVerificationResult(
            Map(result.Status),
            result.Verification is null
                ? null
                : new GovernedLoopAuthenticatedWakeVerification(
                    result.Verification.CheckpointId,
                    result.Verification.CheckpointHash,
                    result.Verification.AuthenticatedEventReference,
                    result.Verification.AuthenticationEvidenceHash,
                    result.Verification.OccurredAtUtc,
                    result.Verification.AuthenticatedAtUtc,
                    result.Verification.Eligible));
    }

    private static GovernedLoopAuthenticatedWakeVerificationStatus Map(
        AgentRuntimeAuthenticatedWakeVerificationStatus status)
        => status switch
        {
            AgentRuntimeAuthenticatedWakeVerificationStatus.Verified => GovernedLoopAuthenticatedWakeVerificationStatus.Verified,
            AgentRuntimeAuthenticatedWakeVerificationStatus.Rejected => GovernedLoopAuthenticatedWakeVerificationStatus.Rejected,
            AgentRuntimeAuthenticatedWakeVerificationStatus.NotFound => GovernedLoopAuthenticatedWakeVerificationStatus.NotFound,
            AgentRuntimeAuthenticatedWakeVerificationStatus.Conflict => GovernedLoopAuthenticatedWakeVerificationStatus.Conflict,
            AgentRuntimeAuthenticatedWakeVerificationStatus.Unavailable => GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}
