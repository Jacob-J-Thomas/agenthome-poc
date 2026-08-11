using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures the bounded exact authority intersection that definitively denied admission.</summary>
/// <param name="SchemaVersion">The proof schema version, which must be 1.</param>
/// <param name="CandidateCeiling">The exact monotone candidate ceiling before denial was applied.</param>
/// <param name="EffectiveCeiling">The canonical empty ceiling retained after denial.</param>
/// <param name="BoundaryReceipt">The exact closed denial decision and contributing profile revisions.</param>
public sealed record GovernedLoopAdmissionAuthorityDenialProof(
    int SchemaVersion,
    AuthorityCeiling CandidateCeiling,
    AuthorityCeiling EffectiveCeiling,
    AuthorityBoundaryReceipt BoundaryReceipt)
{
    /// <summary>Gets the only supported experimental proof schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensively copied candidate ceiling.</summary>
    public AuthorityCeiling CandidateCeiling { get; } = GovernedLoopAdmissionContractCopy.Copy(CandidateCeiling);

    /// <summary>Gets a defensively copied effective ceiling.</summary>
    public AuthorityCeiling EffectiveCeiling { get; } = GovernedLoopAdmissionContractCopy.Copy(EffectiveCeiling);

    /// <summary>Gets a defensively copied boundary receipt.</summary>
    public AuthorityBoundaryReceipt BoundaryReceipt { get; } = GovernedLoopAdmissionContractCopy.Copy(BoundaryReceipt);
}
