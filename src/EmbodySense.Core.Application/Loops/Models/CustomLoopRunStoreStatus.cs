namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop run store status values.
/// </summary>
public enum CustomLoopRunStoreStatus
{
    /// <summary>
    /// Identifies the created custom loop run store status.
    /// </summary>
    Created = 1,
    /// <summary>
    /// Identifies the updated custom loop run store status.
    /// </summary>
    Updated = 2,
    /// <summary>
    /// Identifies the already created custom loop run store status.
    /// </summary>
    AlreadyCreated = 3,
    /// <summary>
    /// Identifies the conflict custom loop run store status.
    /// </summary>
    Conflict = 4,
    /// <summary>
    /// Identifies the operation conflict custom loop run store status.
    /// </summary>
    OperationConflict = 5,
    /// <summary>
    /// Identifies the nonterminal run exists custom loop run store status.
    /// </summary>
    NonterminalRunExists = 6,
    /// <summary>
    /// Identifies the not found custom loop run store status.
    /// </summary>
    NotFound = 7,
    /// <summary>
    /// Identifies the limit exceeded custom loop run store status.
    /// </summary>
    LimitExceeded = 8,
    /// <summary>
    /// Identifies the terminal immutable custom loop run store status.
    /// </summary>
    TerminalImmutable = 9,
    /// <summary>
    /// Identifies the deleted identity conflict custom loop run store status.
    /// </summary>
    DeletedIdentityConflict = 10
}
