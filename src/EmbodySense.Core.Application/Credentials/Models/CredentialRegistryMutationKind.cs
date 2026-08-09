namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one safe credential-registry lifecycle transition.</summary>
public enum CredentialRegistryMutationKind
{
    /// <summary>Registers one new value-free reference and exact binding.</summary>
    Register = 1,
    /// <summary>Updates only the provider health posture.</summary>
    SetHealth = 2,
    /// <summary>Irreversibly tombstones a registered reference.</summary>
    Tombstone = 3,
    /// <summary>Rebinds an existing reference while retaining historical operation evidence.</summary>
    Bind = 4,
    /// <summary>Records an explicit consent decision without granting loop or capability authority.</summary>
    Consent = 5,
    /// <summary>Updates lifecycle and provider posture together.</summary>
    UpdatePosture = 6,
    /// <summary>Records explicit user-confirmed cleanup-repair intent for a repair-required tombstone.</summary>
    BeginRepair = 7,
    /// <summary>Appends proved cleanup-repair completion evidence and removes retained private locator state.</summary>
    CompleteRepair = 8,
    /// <summary>Appends outcome-uncertain explicit repair evidence without altering the historical tombstone.</summary>
    RecordRepairUncertain = 9,
    /// <summary>Records create/import intent before any provider-owned locator or value operation.</summary>
    BeginCreate = 10,
    /// <summary>Appends an outcome-uncertain provider-locator result without retrying the locator operation.</summary>
    RecordLocatorUncertain = 11,
    /// <summary>Conservatively resolves one exact interrupted repair intent as uncertain through the closed durable lifecycle boundary.</summary>
    ReconcileRepair = 12
}
