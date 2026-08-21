namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Identifies the durable phase of one operational-control receipt.</summary>
public enum GovernedLoopOperationalControlReceiptState
{
    /// <summary>The request and authority binding are durably reserved before target mutation.</summary>
    Pending = 1,

    /// <summary>The exact bounded target set is durably captured and may be reconciling mutations.</summary>
    Mutating = 2,

    /// <summary>The closed outcome is durable and safe to replay.</summary>
    Complete = 3,

    /// <summary>Ambiguous retained evidence requires explicit operator disposition.</summary>
    NeedsReview = 4
}
