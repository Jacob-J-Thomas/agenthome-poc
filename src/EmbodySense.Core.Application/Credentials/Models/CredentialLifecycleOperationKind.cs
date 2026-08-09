namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one explicit credential lifecycle workflow.</summary>
public enum CredentialLifecycleOperationKind
{
    /// <summary>Creates new provider material and registers its value-free metadata.</summary>
    Create = 1,
    /// <summary>Imports caller-supplied material through the same callback-only provider boundary.</summary>
    Import = 2,
    /// <summary>Changes the exact capability and scope binding without granting authority.</summary>
    Bind = 3,
    /// <summary>Records an explicit authenticated user consent decision without granting authority.</summary>
    Consent = 4,
    /// <summary>Tests safe provider health without performing a credential-bearing external effect.</summary>
    Test = 5,
    /// <summary>Rotates provider material while preserving the previous proved value on failure.</summary>
    Rotate = 6,
    /// <summary>Publishes expired posture.</summary>
    Expire = 7,
    /// <summary>Publishes revoked posture.</summary>
    Revoke = 8,
    /// <summary>Replaces provider material while preserving the previous proved value on failure.</summary>
    Replace = 9,
    /// <summary>Publishes disabled posture.</summary>
    Disable = 10,
    /// <summary>Deletes provider material and irreversibly tombstones the reference.</summary>
    Delete = 11,
    /// <summary>Explicitly retries cleanup for a repair-required tombstone without automatic replay.</summary>
    Repair = 12,
    /// <summary>Conservatively terminalizes one interrupted repair intent as uncertain without invoking the provider.</summary>
    ReconcileRepair = 13
}
