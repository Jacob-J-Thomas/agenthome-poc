using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Persists authenticated capability-catalog trust anchors in a server-owned root outside workspaces.</summary>
/// <remarks>
/// The configured root is the security boundary and must be writable only by the server account. Workspace actors never
/// receive its key or paths through catalog projections. Deployments requiring a stronger machine or remote boundary can
/// inject another <see cref="ICapabilityCatalogTrustProvider"/> without changing catalog orchestration. Schema-1 anchors
/// are retained monotonically and never evicted automatically; the provider fails closed at its count or byte quota, and
/// administrative reinitialization of the server-owned root is the only lifecycle reset.
/// </remarks>
public sealed class FileCapabilityCatalogTrustProvider : ICapabilityCatalogTrustProvider
{
    private const int AuthenticationKeyByteCount = 32;
    private const string AuthenticationTagPrefix = "hmac-sha256:";
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _authenticationOptions = CreateJsonOptions(writeIndented: false);
    private readonly CapabilityCatalogPathGuard _guard;

    /// <summary>Creates a provider rooted in one server-owned directory.</summary>
    /// <param name="rootPath">The server-owned trust-root path.</param>
    /// <param name="durabilityBarrier">The optional trusted post-rename durability adapter.</param>
    public FileCapabilityCatalogTrustProvider(string rootPath, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        _guard = new CapabilityCatalogPathGuard(RootPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
    }

    /// <summary>Gets the server-owned trust root.</summary>
    public string RootPath { get; }

    /// <summary>Gets the server-owned root authentication key path.</summary>
    public string AuthenticationKeyPath => Path.Combine(RootPath, "capability-catalog-root.key");

    /// <summary>Gets the trust-anchor directory.</summary>
    public string AnchorsPath => Path.Combine(RootPath, "anchors");

    /// <summary>Gets the cross-process trust-root lock path.</summary>
    public string TrustLockPath => Path.Combine(RootPath, ".capability-catalog-trust.lock");

    /// <summary>Creates the default provider beneath the current server account's local application data.</summary>
    public static FileCapabilityCatalogTrustProvider CreateDefault()
    {
        // TODO(#275): Reject any normalized overlap between the governed workspace and capability trust root.
        // https://github.com/Jacob-J-Thomas/agenthome-poc/issues/275
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new IOException("The server-owned local application-data root is unavailable.");
        }

        return new FileCapabilityCatalogTrustProvider(Path.Combine(localData, "EmbodySense", "server-state", "capability-catalog"));
    }

    /// <summary>Gets the canonical anchor path for a validated workspace identity.</summary>
    public string GetAnchorPath(string workspaceIdentity) => Path.Combine(AnchorsPath, RequireWorkspaceIdentity(workspaceIdentity)["sha256:".Length..] + ".json");

    /// <inheritdoc />
    public async Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default)
    {
        workspaceIdentity = RequireWorkspaceIdentity(workspaceIdentity);
        await using var fileSystem = await _guard.TryAcquireExclusiveSessionAsync(TrustLockPath, createRoot: false, cancellationToken);
        if (fileSystem is null)
        {
            return null;
        }

        ValidateAnchorRoot(fileSystem);
        var anchorPath = GetAnchorPath(workspaceIdentity);
        if (!fileSystem.FileExists(anchorPath))
        {
            return null;
        }

        var key = await ReadRequiredKeyAsync(fileSystem, cancellationToken);
        return Map(await ReadRequiredAnchorAsync(fileSystem, anchorPath, workspaceIdentity, key, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        workspaceIdentity = RequireWorkspaceIdentity(workspaceIdentity);
        ValidateGenerationAndDigest(generation, contentDigest);
        if (generation != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "The initial capability catalog trust generation must be zero.");
        }

        await using var fileSystem = await _guard.TryAcquireExclusiveSessionAsync(TrustLockPath, createRoot: true, cancellationToken) ?? throw new IOException("The server-owned capability catalog trust root could not be prepared.");
        fileSystem.PrepareDirectory(AnchorsPath);
        var key = await ReadOrCreateInitialKeyAsync(fileSystem, cancellationToken);
        ValidateAnchorRoot(fileSystem);
        var anchorPath = GetAnchorPath(workspaceIdentity);
        if (fileSystem.FileExists(anchorPath))
        {
            var existing = await ReadRequiredAnchorAsync(fileSystem, anchorPath, workspaceIdentity, key, cancellationToken);
            if (existing.CurrentGeneration == generation && string.Equals(existing.CurrentContentDigest, contentDigest, StringComparison.Ordinal) && existing.PreviousGeneration is null && existing.PreviousContentDigest is null)
            {
                return Map(existing);
            }

            throw new IOException("The capability catalog trust anchor already exists with different monotonic state.");
        }

        var anchor = new CapabilityCatalogTrustAnchorDocument(CapabilityCatalogTrustAnchorDocument.CurrentSchemaVersion, workspaceIdentity, generation, contentDigest, null, null, string.Empty);
        var authenticated = anchor with { AuthenticationTag = ComputeAnchorAuthenticationTag(anchor, key) };
        await WriteAnchorAsync(fileSystem, anchorPath, authenticated, reserveNewAnchor: true, cancellationToken);
        return Map(authenticated);
    }

    /// <inheritdoc />
    public async Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
    {
        workspaceIdentity = RequireWorkspaceIdentity(workspaceIdentity);
        ValidateGenerationAndDigest(generation, contentDigest);
        await using var fileSystem = await AcquireExistingSessionAsync(cancellationToken);
        ValidateAnchorRoot(fileSystem);
        var key = await ReadRequiredKeyAsync(fileSystem, cancellationToken);
        var anchor = await ReadRequiredAnchorAsync(fileSystem, GetAnchorPath(workspaceIdentity), workspaceIdentity, key, cancellationToken);
        var isCurrent = generation == anchor.CurrentGeneration && string.Equals(contentDigest, anchor.CurrentContentDigest, StringComparison.Ordinal);
        if (!isCurrent && generation != checked(anchor.CurrentGeneration + 1))
        {
            throw new IOException("The artifact generation is outside the server-owned current or direct-successor trust boundary.");
        }

        return ComputeArtifactAuthenticationTag(workspaceIdentity, generation, contentDigest, key);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default)
    {
        workspaceIdentity = RequireWorkspaceIdentity(workspaceIdentity);
        ValidateGenerationAndDigest(generation, contentDigest);
        await using var fileSystem = await AcquireExistingSessionAsync(cancellationToken);
        ValidateAnchorRoot(fileSystem);
        var key = await ReadRequiredKeyAsync(fileSystem, cancellationToken);
        _ = await ReadRequiredAnchorAsync(fileSystem, GetAnchorPath(workspaceIdentity), workspaceIdentity, key, cancellationToken);
        return FixedTimeEquals(authenticationTag, ComputeArtifactAuthenticationTag(workspaceIdentity, generation, contentDigest, key));
    }

    /// <inheritdoc />
    public async Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default)
    {
        workspaceIdentity = RequireWorkspaceIdentity(workspaceIdentity);
        ValidateGenerationAndDigest(expectedGeneration, expectedContentDigest);
        ValidateGenerationAndDigest(newGeneration, newContentDigest);
        if (newGeneration != checked(expectedGeneration + 1))
        {
            throw new ArgumentOutOfRangeException(nameof(newGeneration), "Capability catalog trust may advance by exactly one generation.");
        }

        await using var fileSystem = await AcquireExistingSessionAsync(cancellationToken);
        ValidateAnchorRoot(fileSystem);
        var key = await ReadRequiredKeyAsync(fileSystem, cancellationToken);
        var anchorPath = GetAnchorPath(workspaceIdentity);
        var current = await ReadRequiredAnchorAsync(fileSystem, anchorPath, workspaceIdentity, key, cancellationToken);
        if (current.CurrentGeneration != expectedGeneration || !string.Equals(current.CurrentContentDigest, expectedContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The capability catalog trust anchor changed before monotonic advancement.");
        }

        var advanced = new CapabilityCatalogTrustAnchorDocument(CapabilityCatalogTrustAnchorDocument.CurrentSchemaVersion, workspaceIdentity, newGeneration, newContentDigest, expectedGeneration, expectedContentDigest, string.Empty);
        advanced = advanced with { AuthenticationTag = ComputeAnchorAuthenticationTag(advanced, key) };
        await WriteAnchorAsync(fileSystem, anchorPath, advanced, reserveNewAnchor: false, cancellationToken);
        return Map(advanced);
    }

    private async Task<CapabilityCatalogPathSession> AcquireExistingSessionAsync(CancellationToken cancellationToken)
    {
        return await _guard.TryAcquireExclusiveSessionAsync(TrustLockPath, createRoot: false, cancellationToken) ?? throw new IOException("The server-owned capability catalog trust root is missing.");
    }

    private async Task<byte[]> ReadOrCreateInitialKeyAsync(CapabilityCatalogPathSession fileSystem, CancellationToken cancellationToken)
    {
        if (fileSystem.FileExists(AuthenticationKeyPath))
        {
            return await ReadRequiredKeyAsync(fileSystem, cancellationToken);
        }

        if (fileSystem.EnumerateRegularFiles(AnchorsPath, CapabilityCatalogLimits.MaximumTrustAnchors, CapabilityCatalogLimits.MaximumTrustAnchorRootBytes).Count > 0)
        {
            throw new IOException("The server-owned authentication key is missing and cannot be regenerated over existing trust anchors.");
        }

        var key = RandomNumberGenerator.GetBytes(AuthenticationKeyByteCount);
        await fileSystem.WriteBytesAtomicallyAsync(AuthenticationKeyPath, key, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            fileSystem.SetUserOnlyFilePermissions(AuthenticationKeyPath);
        }

        return key;
    }

    private async Task<byte[]> ReadRequiredKeyAsync(CapabilityCatalogPathSession fileSystem, CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(AuthenticationKeyPath))
        {
            throw new IOException("The server-owned capability catalog authentication key is missing.");
        }

        var key = await fileSystem.ReadAllBytesAsync(AuthenticationKeyPath, AuthenticationKeyByteCount, cancellationToken);
        return key.Length == AuthenticationKeyByteCount ? key : throw new FormatException("The server-owned capability catalog authentication key is malformed.");
    }

    private async Task<CapabilityCatalogTrustAnchorDocument> ReadRequiredAnchorAsync(CapabilityCatalogPathSession fileSystem, string path, string workspaceIdentity, byte[] key, CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(path))
        {
            throw new IOException("The server-owned capability catalog trust anchor is missing.");
        }

        var bytes = await fileSystem.ReadAllBytesAsync(path, CapabilityCatalogLimits.MaximumTrustAnchorUtf8Bytes, cancellationToken);
        var anchor = JsonSerializer.Deserialize<CapabilityCatalogTrustAnchorDocument>(bytes, _jsonOptions) ?? throw new FormatException("The server-owned capability catalog trust anchor is malformed.");
        if (!ValidateAnchor(anchor, workspaceIdentity, key))
        {
            throw new FormatException("The server-owned capability catalog trust anchor is invalid or substituted.");
        }

        return anchor;
    }

    private async Task WriteAnchorAsync(CapabilityCatalogPathSession fileSystem, string path, CapabilityCatalogTrustAnchorDocument anchor, bool reserveNewAnchor, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(anchor, _jsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > CapabilityCatalogLimits.MaximumTrustAnchorUtf8Bytes)
        {
            throw new IOException("The server-owned capability catalog trust anchor exceeds its bounded size.");
        }

        ValidateAnchorRoot(fileSystem, reserveNewAnchor ? bytes : 0);
        await fileSystem.WriteTextAtomicallyAsync(path, json, cancellationToken);
    }

    private static void ValidateAnchorRoot(CapabilityCatalogPathSession fileSystem, int additionalBytes = 0)
    {
        var reservedEntries = additionalBytes > 0 ? 1 : 0;
        var anchors = fileSystem.EnumerateRegularFiles(Path.Combine(fileSystem.Root, "anchors"), CapabilityCatalogLimits.MaximumTrustAnchors - reservedEntries, CapabilityCatalogLimits.MaximumTrustAnchorRootBytes - additionalBytes);

        foreach (var anchor in anchors)
        {
            if (!IsCanonicalAnchorFileName(anchor.Name) || anchor.Length is <= 0 or > CapabilityCatalogLimits.MaximumTrustAnchorUtf8Bytes)
            {
                throw new FormatException("The server-owned capability catalog trust-anchor root contains an invalid entry.");
            }
        }
    }

    private static bool IsCanonicalAnchorFileName(string name)
    {
        if (name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in name.AsSpan(0, 64))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateAnchor(CapabilityCatalogTrustAnchorDocument anchor, string workspaceIdentity, byte[] key)
    {
        if (anchor.SchemaVersion != CapabilityCatalogTrustAnchorDocument.CurrentSchemaVersion || !string.Equals(anchor.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal) || anchor.CurrentGeneration < 0 || !IsDigest(anchor.CurrentContentDigest))
        {
            return false;
        }

        var previousAbsent = anchor.PreviousGeneration is null && anchor.PreviousContentDigest is null;
        var previousValid = anchor.PreviousGeneration is not null && anchor.PreviousContentDigest is not null && anchor.PreviousGeneration.Value == anchor.CurrentGeneration - 1 && IsDigest(anchor.PreviousContentDigest);
        return (previousAbsent && anchor.CurrentGeneration == 0 || previousValid) && FixedTimeEquals(anchor.AuthenticationTag, ComputeAnchorAuthenticationTag(anchor, key));
    }

    private static string ComputeAnchorAuthenticationTag(CapabilityCatalogTrustAnchorDocument anchor, byte[] key)
    {
        var content = JsonSerializer.Serialize(anchor with { AuthenticationTag = string.Empty }, _authenticationOptions);
        return AuthenticationTag(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("embodysense-capability-anchor-v1\n" + content)));
    }

    private static string ComputeArtifactAuthenticationTag(string workspaceIdentity, long generation, string contentDigest, byte[] key)
    {
        var content = $"embodysense-capability-artifact-v1\n{workspaceIdentity}\n{generation}\n{contentDigest}";
        return AuthenticationTag(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(content)));
    }

    private static string AuthenticationTag(byte[] bytes) => AuthenticationTagPrefix + Convert.ToHexString(bytes).ToLowerInvariant();

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null || supplied.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected));
    }

    private static CapabilityCatalogTrustState Map(CapabilityCatalogTrustAnchorDocument anchor) => new(anchor.WorkspaceIdentity, anchor.CurrentGeneration, anchor.CurrentContentDigest, anchor.PreviousGeneration, anchor.PreviousContentDigest);

    private static string RequireWorkspaceIdentity(string value)
    {
        return IsDigest(value) ? value : throw new ArgumentException("Workspace identity must be one canonical SHA-256 digest.", nameof(value));
    }

    private static void ValidateGenerationAndDigest(long generation, string contentDigest)
    {
        if (generation < 0 || !IsDigest(contentDigest))
        {
            throw new ArgumentException("Capability catalog generation and content digest must be canonical.");
        }
    }

    private static bool IsDigest(string? value) => CapabilityIntegrityDigest.TryParse(value, out _, out _);

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
    }
}
