using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority;

/// <summary>
/// Projects only revalidated boundary receipts into bounded, value-free application evidence.
/// </summary>
public static class AuthorityBoundaryProjectionFactory
{
    /// <summary>
    /// Creates a projection only when the complete receipt remains closed, bounded, and internally consistent.
    /// </summary>
    public static bool TryCreate(AuthorityBoundaryReceipt? receipt, out AuthorityBoundaryProjection? projection, out AuthorityContractValidationResult validation)
    {
        validation = AuthorityBoundaryReceiptFactory.Validate(receipt);
        if (!validation.IsValid)
        {
            projection = null;
            return false;
        }

        var reasons = receipt!.Conditions.Select(condition => condition.Reason).Distinct().OrderBy(reason => reason).ToArray();
        projection = new AuthorityBoundaryProjection(receipt.Decision, Array.AsReadOnly(reasons), receipt.EvaluatedAtUtc);
        return true;
    }
}
