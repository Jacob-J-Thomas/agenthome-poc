using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal sealed class AuthenticatedModelPersistenceStore<TDocument> where TDocument : class, IAuthenticatedModelPersistenceDocument<TDocument>
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private readonly string _domain;
    private readonly string _primaryPath;
    private readonly string _proofPath;
    private readonly string _lockPath;
    private readonly int _maximumArtifactUtf8Bytes;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly Func<string, TDocument> _emptyFactory;
    private readonly Func<TDocument, string, bool> _validator;
    private readonly Func<TDocument, TDocument, bool> _directSuccessor;

    internal AuthenticatedModelPersistenceStore(
        string rootPath,
        string primaryPath,
        string proofPath,
        string lockPath,
        string domain,
        int maximumArtifactUtf8Bytes,
        ICapabilityCatalogTrustProvider trustProvider,
        ICapabilityCatalogDurabilityBarrier durabilityBarrier,
        ICapabilityCatalogPathObserver? pathObserver,
        Func<string, TDocument> emptyFactory,
        Func<TDocument, string, bool> validator,
        Func<TDocument, TDocument, bool> directSuccessor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(proofPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(durabilityBarrier);
        ArgumentNullException.ThrowIfNull(emptyFactory);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(directSuccessor);
        if (maximumArtifactUtf8Bytes < 1 || trustProvider.MaximumAuthenticationTagUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactUtf8Bytes), "Authenticated model persistence requires positive bounded artifact and authentication-tag sizes.");
        }

        trustProvider.RequireDisjointWorkspace(rootPath);
        _domain = domain;
        _primaryPath = primaryPath;
        _proofPath = proofPath;
        _lockPath = lockPath;
        _maximumArtifactUtf8Bytes = maximumArtifactUtf8Bytes;
        _pathGuard = new CapabilityCatalogPathGuard(rootPath, durabilityBarrier, pathObserver);
        _trustProvider = trustProvider;
        _emptyFactory = emptyFactory;
        _validator = validator;
        _directSuccessor = directSuccessor;
    }

    internal Task<CapabilityCatalogPathSession?> AcquireForReadAsync(CancellationToken cancellationToken)
        => _pathGuard.TryAcquireExclusiveSessionAsync(_lockPath, createRoot: false, cancellationToken, createLockParent: false);

    internal async Task<CapabilityCatalogPathSession> AcquireForMutationAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_lockPath, createRoot: true, cancellationToken)
            ?? throw new IOException("Authenticated model persistence is unavailable.");

    internal string CreatePhysicalIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(_domain + "\n" + session.PhysicalIdentityMaterial);

    internal async Task<AuthenticatedModelPersistenceLoadResult<TDocument>> LoadAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var workspaceIdentity = CreatePhysicalIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
        var primaryExists = session.FileExists(_primaryPath);
        var proofExists = session.FileExists(_proofPath);
        var empty = PrepareEmpty(workspaceIdentity);
        if (trust is null)
        {
            return primaryExists || proofExists
                ? new(null, null, null, AuthenticatedModelPersistenceDisposition.Unavailable)
                : new(empty, null, null, AuthenticatedModelPersistenceDisposition.Current);
        }

        var primary = primaryExists ? await TryReadAsync(session, workspaceIdentity, _primaryPath, cancellationToken) : null;
        var proof = proofExists ? await TryReadAsync(session, workspaceIdentity, _proofPath, cancellationToken) : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new(primary, null, trust, AuthenticatedModelPersistenceDisposition.Current);
        }

        var currentBase = proof is not null && MatchesCurrent(proof, trust)
            ? proof
            : !primaryExists && !proofExists && MatchesCurrent(empty, trust)
                ? empty
                : null;
        if (primary is not null && currentBase is not null && IsAuthenticatedDirectSuccessor(currentBase, primary, trust))
        {
            return new(currentBase, primary, trust, AuthenticatedModelPersistenceDisposition.Pending);
        }
        if (!primaryExists && currentBase is not null)
        {
            return new(currentBase, null, trust, AuthenticatedModelPersistenceDisposition.Current);
        }
        if (currentBase is not null || proof is not null && MatchesPrevious(proof, trust) || primary is not null && MatchesPrevious(primary, trust))
        {
            return new(currentBase ?? proof ?? primary, null, trust, AuthenticatedModelPersistenceDisposition.Recovered);
        }

        return new(null, null, trust, AuthenticatedModelPersistenceDisposition.Unavailable);
    }

    internal async Task<CapabilityCatalogTrustState> FinalizePendingAsync(AuthenticatedModelPersistenceLoadResult<TDocument> loaded, CancellationToken cancellationToken)
    {
        var current = loaded.Document ?? throw new InvalidOperationException("A current authenticated model document is required.");
        var pending = loaded.Pending ?? throw new InvalidOperationException("A pending authenticated model document is required.");
        var trust = loaded.Trust ?? throw new InvalidOperationException("A trust anchor is required to finalize a pending document.");
        var advanced = await _trustProvider.AdvanceAsync(CreateExpectedIdentity(current), trust.CurrentGeneration, trust.CurrentContentDigest, pending.Generation, pending.ContentDigest, cancellationToken);
        RequireExactTrustSuccessor(advanced, pending, current);
        return advanced;
    }

    internal async Task<CapabilityCatalogTrustState> CommitAsync(CapabilityCatalogPathSession session, AuthenticatedModelPersistenceLoadResult<TDocument> loaded, TDocument candidate, Func<AuthenticatedModelPersistenceCommitStage, CancellationToken, ValueTask>? observer, CancellationToken cancellationToken)
    {
        var current = loaded.Document ?? throw new InvalidOperationException("A current authenticated model document is required.");
        var workspaceIdentity = CreateExpectedIdentity(current);
        if (!_validator(candidate, workspaceIdentity) || !_directSuccessor(current, candidate))
        {
            throw new IOException("The authenticated model document is not an exact direct successor.");
        }

        var currentDigest = ComputeContentDigest(current).Value;
        var canonicalCurrent = current.WithAuthentication(currentDigest, string.Empty);
        var trust = loaded.Trust ?? await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
        if (!MatchesCurrent(canonicalCurrent, trust))
        {
            throw new IOException("The authenticated model trust anchor does not match current state.");
        }

        var serializedCandidate = await SerializeAsync(candidate, workspaceIdentity, cancellationToken);
        var serializedCurrent = await SerializeAsync(current, workspaceIdentity, cancellationToken);
        await session.WriteTextAtomicallyAsync(_proofPath, serializedCurrent.Json, cancellationToken);
        await ObserveAsync(observer, AuthenticatedModelPersistenceCommitStage.ProofPublished, cancellationToken);
        await session.WriteTextAtomicallyAsync(_primaryPath, serializedCandidate.Json, cancellationToken);
        await ObserveAsync(observer, AuthenticatedModelPersistenceCommitStage.PrimaryPublished, cancellationToken);
        var advanced = await _trustProvider.AdvanceAsync(workspaceIdentity, trust.CurrentGeneration, trust.CurrentContentDigest, candidate.Generation, serializedCandidate.ContentDigest, cancellationToken);
        RequireExactTrustSuccessor(advanced, candidate.WithAuthentication(serializedCandidate.ContentDigest, serializedCandidate.AuthenticationTag), canonicalCurrent);
        await ObserveAsync(observer, AuthenticatedModelPersistenceCommitStage.TrustAdvanced, cancellationToken);
        return advanced;
    }

    internal Task<TDocument?> TryReadAuthenticatedSnapshotAsync(CapabilityCatalogPathSession session, string path, CancellationToken cancellationToken)
        => TryReadAsync(session, CreatePhysicalIdentity(session), path, cancellationToken);

    internal async Task WriteAuthenticatedSnapshotOnceAsync(CapabilityCatalogPathSession session, TDocument document, string path, CancellationToken cancellationToken)
    {
        var workspaceIdentity = CreatePhysicalIdentity(session);
        var serialized = await SerializeAsync(document, workspaceIdentity, cancellationToken);
        if (session.FileExists(path))
        {
            var existing = await TryReadAsync(session, workspaceIdentity, path, cancellationToken);
            if (existing is not null
                && existing.Generation == document.Generation
                && string.Equals(existing.ContentDigest, serialized.ContentDigest, StringComparison.Ordinal))
            {
                return;
            }

            throw new IOException("An authenticated model persistence snapshot already exists with different content.");
        }

        await session.WriteTextAtomicallyAsync(path, serialized.Json, cancellationToken);
        var retained = await TryReadAsync(session, workspaceIdentity, path, cancellationToken);
        if (retained is null
            || retained.Generation != document.Generation
            || !string.Equals(retained.ContentDigest, serialized.ContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The authenticated model persistence snapshot was not retained exactly.");
        }
    }

    private async Task<TDocument?> TryReadAsync(CapabilityCatalogPathSession session, string workspaceIdentity, string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.TryReadAllBytesBoundAsync(path, _maximumArtifactUtf8Bytes, cancellationToken);
            if (bytes is null || HasUtf8Bom(bytes))
            {
                return null;
            }
            var text = _strictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(json.RootElement))
            {
                return null;
            }
            var document = JsonSerializer.Deserialize<TDocument>(text, _jsonOptions);
            if (document is null
                || !_validator(document, workspaceIdentity)
                || !string.Equals(CanonicalJson(document), text, StringComparison.Ordinal)
                || string.IsNullOrEmpty(document.AuthenticationTag)
                || _strictUtf8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
                || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var digest, out _)
                || !digest!.FixedTimeEquals(ComputeContentDigest(document))
                || !await _trustProvider.VerifyArtifactAsync(workspaceIdentity, document.Generation, document.ContentDigest, document.AuthenticationTag, cancellationToken))
            {
                return null;
            }
            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<(string Json, string ContentDigest, string AuthenticationTag)> SerializeAsync(TDocument document, string workspaceIdentity, CancellationToken cancellationToken)
    {
        var digest = ComputeContentDigest(document).Value;
        var tag = await _trustProvider.AuthenticateArtifactAsync(workspaceIdentity, document.Generation, digest, cancellationToken);
        if (string.IsNullOrEmpty(tag) || _strictUtf8.GetByteCount(tag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The model persistence trust provider returned an invalid authentication tag.");
        }
        var authenticated = document.WithAuthentication(digest, tag);
        var json = CanonicalJson(authenticated);
        if (_strictUtf8.GetByteCount(json) > _maximumArtifactUtf8Bytes)
        {
            throw new AuthenticatedModelPersistenceLimitException();
        }
        return (json, digest, tag);
    }

    private TDocument PrepareEmpty(string workspaceIdentity)
    {
        var empty = _emptyFactory(workspaceIdentity);
        return empty.WithAuthentication(ComputeContentDigest(empty).Value, string.Empty);
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
        => new(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private static string CanonicalJson(TDocument document) => JsonSerializer.Serialize(document, _jsonOptions) + "\n";

    private static CapabilityIntegrityDigest ComputeContentDigest(TDocument document)
    {
        var content = JsonSerializer.Serialize(document.WithAuthentication(string.Empty, string.Empty), _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private bool IsAuthenticatedDirectSuccessor(TDocument current, TDocument pending, CapabilityCatalogTrustState trust)
        => MatchesCurrent(current, trust) && pending.Generation == trust.CurrentGeneration + 1 && _directSuccessor(current, pending);

    private static bool MatchesCurrent(TDocument document, CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && document.Generation == trust.CurrentGeneration
            && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(TDocument document, CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && trust.PreviousGeneration is not null
            && document.Generation == trust.PreviousGeneration
            && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);

    private static string CreateExpectedIdentity(TDocument document) => document.WorkspaceIdentity;

    private static void RequireExactTrustSuccessor(CapabilityCatalogTrustState advanced, TDocument successor, TDocument previous)
    {
        if (!string.Equals(advanced.WorkspaceIdentity, successor.WorkspaceIdentity, StringComparison.Ordinal)
            || advanced.CurrentGeneration != successor.Generation
            || !string.Equals(advanced.CurrentContentDigest, successor.ContentDigest, StringComparison.Ordinal)
            || advanced.PreviousGeneration != previous.Generation
            || !string.Equals(advanced.PreviousContentDigest, previous.ContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The model persistence trust provider did not retain the exact direct successor.");
        }
    }

    private static async ValueTask ObserveAsync(Func<AuthenticatedModelPersistenceCommitStage, CancellationToken, ValueTask>? observer, AuthenticatedModelPersistenceCommitStage stage, CancellationToken cancellationToken)
    {
        if (observer is not null)
        {
            await observer(stage, cancellationToken);
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes) => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
}
