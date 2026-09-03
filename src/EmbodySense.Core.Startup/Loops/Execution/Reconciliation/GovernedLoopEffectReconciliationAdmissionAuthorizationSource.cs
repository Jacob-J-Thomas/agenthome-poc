using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

/// <summary>Authorizes only server-initiated publication of an already durable reconciliation-required attention case.</summary>
/// <remarks>This source is private to the automatic open-only service. It grants no probe, disposition, resolution, run-resume, or actuator authority.</remarks>
internal sealed class GovernedLoopEffectReconciliationAdmissionAuthorizationSource : IGovernedLoopEffectReconciliationAuthorizationSource
{
    private const string Purpose = "effect-reconciliation";

    public Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var valid = string.Equals(request.Purpose, Purpose, StringComparison.Ordinal)
            && GovernedLoopEffectReconciliationContractValidator.Validate(request.Binding).IsValid
            && string.Equals(request.Case.BindingHash, request.Binding.ContentHash, StringComparison.Ordinal);
        var status = valid
            ? GovernedLoopEffectReconciliationAuthorizationStatus.Ready
            : GovernedLoopEffectReconciliationAuthorizationStatus.Denied;
        var evidenceHash = valid ? Hash(request.Purpose, request.Case.CaseId, request.Case.ContentHash, request.Binding.ContentHash) : null;
        return Task.FromResult(new GovernedLoopEffectReconciliationAuthorizationResult(status, request.Purpose, request.Case, request.Binding, evidenceHash));
    }

    private static string Hash(params string[] values)
    {
        var builder = new StringBuilder("embodysense.reconciliation-attention-admission.v1\n");
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
