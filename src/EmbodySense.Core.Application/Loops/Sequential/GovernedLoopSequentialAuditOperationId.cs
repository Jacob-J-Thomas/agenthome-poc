using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Derives stable, domain-separated operation identities for append-once sequential audit records.</summary>
public static class GovernedLoopSequentialAuditOperationId
{
    private const string Prefix = "sequential-audit-";

    /// <summary>Derives an admission-audit identity from the exact committed receipt and adapter binding.</summary>
    public static string ForAdmission(string admissionReceiptHash, string adapterBindingHash)
    {
        RequireHash(admissionReceiptHash, nameof(admissionReceiptHash));
        RequireHash(adapterBindingHash, nameof(adapterBindingHash));
        return Derive("governed-loop-sequential-admission-audit-v1", admissionReceiptHash, adapterBindingHash);
    }

    /// <summary>Derives a node-outcome-audit identity from the exact terminal Common evidence hash.</summary>
    public static string ForNodeOutcome(string evidenceHash)
    {
        RequireHash(evidenceHash, nameof(evidenceHash));
        return Derive("governed-loop-sequential-node-outcome-audit-v1", evidenceHash);
    }

    /// <summary>Derives a node-start-audit identity from the exact durable dispatch evidence hash.</summary>
    public static string ForNodeStart(string evidenceHash)
    {
        RequireHash(evidenceHash, nameof(evidenceHash));
        return Derive("governed-loop-sequential-node-start-audit-v1", evidenceHash);
    }

    /// <summary>Derives a terminal-lifecycle-audit identity from the exact durable terminal event artifact.</summary>
    public static string ForTerminalLifecycle(string terminalArtifactHash)
    {
        RequireHash(terminalArtifactHash, nameof(terminalArtifactHash));
        return Derive("governed-loop-sequential-terminal-lifecycle-audit-v1", terminalArtifactHash);
    }

    private static string Derive(params string[] values)
    {
        var canonical = string.Join('\n', values);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return Prefix + digest;
    }

    private static void RequireHash(string? value, string parameterName)
    {
        if (value is not { Length: 64 }
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        }
    }
}
