namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the persisted lifecycle projection of one contextual role.</summary>
public enum ContextualRoleLifecycleState
{
    /// <summary>An undefined state that is never valid.</summary>
    Unknown = 0,
    /// <summary>The current revision remains eligible for later policy admission.</summary>
    Active = 1,
    /// <summary>The current revision is explicitly ineligible for later policy admission.</summary>
    Disabled = 2,
    /// <summary>The stable role identity is permanently removed while its history remains attributable.</summary>
    Tombstoned = 3,
    /// <summary>No primary lifecycle projection exists for the requested stable role identity.</summary>
    Absent = 4
}
