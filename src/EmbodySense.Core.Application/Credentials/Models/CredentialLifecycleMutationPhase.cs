namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Correlates one provider-mutation phase with its durable lifecycle intent.</summary>
public enum CredentialLifecycleMutationPhase
{
    /// <summary>The durable repair-required intent recorded before provider mutation.</summary>
    Intent = 1,
    /// <summary>The proved successful provider-value completion.</summary>
    Complete = 2,
    /// <summary>The proved provider failure and safe-posture rollback.</summary>
    Rollback = 3,
    /// <summary>The tombstone committed after proved provider cleanup.</summary>
    TombstoneComplete = 4,
    /// <summary>The repair-required tombstone committed after uncertain provider cleanup.</summary>
    TombstoneUncertain = 5,
    /// <summary>The explicit repair completion that removes retained private cleanup state.</summary>
    RepairComplete = 6,
    /// <summary>The durable outcome-uncertain projection recorded after an ambiguous provider value mutation.</summary>
    Uncertain = 7,
    /// <summary>The durable outcome-uncertain evidence recorded after ambiguous explicit repair cleanup.</summary>
    RepairUncertain = 8,
    /// <summary>The private provider locator durably attached after create/import intent.</summary>
    LocatorPrepared = 9,
    /// <summary>The durable outcome-uncertain evidence recorded after ambiguous provider-locator creation.</summary>
    LocatorUncertain = 10,
    /// <summary>The closed durable reconciliation that terminalizes an exact interrupted repair intent without claiming provider success.</summary>
    RepairReconciledUncertain = 11
}
