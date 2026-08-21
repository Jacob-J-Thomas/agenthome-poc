namespace EmbodySense.Core.Common.Loops.Posture;

/// <summary>Defines the finite schema-1 bounds shared by operational posture and control contracts.</summary>
public static class GovernedLoopOperationalPostureLimits
{
    /// <summary>Gets the only supported contract schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the largest page admitted for any operational family.</summary>
    public const int MaxPageItems = 100;

    /// <summary>Gets the largest persisted control batch.</summary>
    public const int MaxControlBatchItems = 100;

    /// <summary>Gets the largest supported operation identity.</summary>
    public const int MaxOperationIdCharacters = 128;

    /// <summary>Gets the largest supported target identity.</summary>
    public const int MaxTargetIdCharacters = 256;

    /// <summary>Gets the largest supported workspace identity.</summary>
    public const int MaxWorkspaceIdCharacters = 128;

    /// <summary>Gets the largest supported actor identity.</summary>
    public const int MaxActorIdCharacters = 128;

    /// <summary>Gets the largest supported caller-surface identity.</summary>
    public const int MaxSurfaceIdCharacters = 64;

    /// <summary>Gets the lowercase SHA-256 character count.</summary>
    public const int Sha256HexCharacters = 64;
}
