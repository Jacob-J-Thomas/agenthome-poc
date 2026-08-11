using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;
using EmbodySense.Core.Persistence.Loops.Revisions;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority;

/// <summary>Persists one bounded, authenticated, append-only effect-authority decision ledger.</summary>
/// <remarks>
/// Mutable workspace artifacts are untrusted evidence. A server-owned monotonic trust provider authenticates the
/// complete ledger against its physical workspace, while a shared authority transaction and retained-handle path
/// session serialize every append. Decisions are keyed by their globally stable effect-operation identity; exact
/// replays never append twice and identity reuse with any different immutable content fails closed as a conflict.
/// Historical evidence is never rewritten or evicted automatically.
/// </remarks>
public sealed class GovernedLoopEffectAuthorityEvidenceStore : IGovernedLoopEffectAuthorityEvidenceStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly GovernedLoopEffectAuthorityEvidenceStorePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly GovernedLoopEffectAuthorityEvidenceStoreOptions _options;

    /// <summary>Creates an evidence store with the default server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="options">Optional bounded schema-1 persistence settings.</param>
    /// <param name="durabilityBarrier">An optional trusted filesystem durability adapter.</param>
    /// <param name="authorityTransaction">The optional shared authority transaction.</param>
    public GovernedLoopEffectAuthorityEvidenceStore(
        WorkspacePaths paths,
        GovernedLoopEffectAuthorityEvidenceStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), options, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates an evidence store over an explicit server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="trustProvider">The server-owned trust provider.</param>
    /// <param name="options">Optional bounded schema-1 persistence settings.</param>
    /// <param name="durabilityBarrier">An optional trusted filesystem durability adapter.</param>
    /// <param name="authorityTransaction">The optional shared authority transaction.</param>
    public GovernedLoopEffectAuthorityEvidenceStore(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        GovernedLoopEffectAuthorityEvidenceStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new GovernedLoopEffectAuthorityEvidenceStoreOptions());
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _paths = new GovernedLoopEffectAuthorityEvidenceStorePaths(paths);
        _pathGuard = new CapabilityCatalogPathGuard(
            paths.RootPath,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver);
        _trustProvider = trustProvider;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendAsync(
        GovernedLoopEffectAuthorityDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
        }

        var callbackEntered = false;
        GovernedLoopEffectAuthorityEvidenceStoreResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await AppendCoreAsync(decision, token, cancellationToken);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && callbackResult is null)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? Result(callbackEntered
                ? GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous
                : GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendCoreAsync(
        GovernedLoopEffectAuthorityDecision decision,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var decisionMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireForAppendAsync(cancellationToken);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
            GovernedLoopEffectAuthorityEvidenceDocument current;
            if (loaded.Disposition == GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Pending)
            {
                var pendingDecision = loaded.Pending!.Decisions[^1];
                var sameIdentity = SameIdentity(pendingDecision, decision);
                if (sameIdentity)
                {
                    decisionMayHaveCommitted = true;
                }

                trust = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken);
                current = loaded.Pending;
                if (sameIdentity)
                {
                    return Result(
                        SameDecision(pendingDecision, decision)
                            ? GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
                            : GovernedLoopEffectAuthorityEvidenceStoreStatus.Conflict,
                        pendingDecision.ContentHash);
                }
            }
            else
            {
                if (loaded.Disposition == GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered)
                {
                    return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous);
                }

                if (loaded.Document is null)
                {
                    return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
                }

                current = loaded.Document;
            }

            var existing = FindDecision(current, decision.EffectOperationId);
            if (existing is not null)
            {
                return Result(
                    SameDecision(existing, decision)
                        ? GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
                        : GovernedLoopEffectAuthorityEvidenceStoreStatus.Conflict,
                    existing.ContentHash);
            }

            if (current.Decisions.Count >= _options.MaxDecisions)
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            var candidate = CreateCandidate(current, decision, workspaceIdentity);
            if (!ValidateDocument(candidate, workspaceIdentity) || !IsDirectSuccessor(current, candidate))
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            var currentDigest = ComputeContentDigest(current).Value;
            trust ??= await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
            if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            GovernedLoopEffectAuthorityEvidenceSerializedDocument serializedCandidate;
            try
            {
                serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken);
            }
            catch (GovernedLoopEffectAuthorityEvidenceStoreLimitException)
            {
                return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
            }

            var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken);
            await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished, cancellationToken);
            decisionMayHaveCommitted = true;
            await session.WriteTextAtomicallyAsync(_paths.PrimaryPath, serializedCandidate.Json, cancellationToken);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished, cancellationToken);
            var advanced = await _trustProvider.AdvanceAsync(
                workspaceIdentity,
                trust.CurrentGeneration,
                trust.CurrentContentDigest,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                cancellationToken);
            RequireExactTrustSuccessor(
                advanced,
                workspaceIdentity,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                trust.CurrentGeneration,
                trust.CurrentContentDigest);
            await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, cancellationToken);
            return Result(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, decision.ContentHash);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !decisionMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return Result(decisionMayHaveCommitted
                ? GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous
                : GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectAuthorityEvidenceStoreLoadResult> LoadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        CapabilityCatalogTrustState? trust,
        CancellationToken cancellationToken)
    {
        var primaryExists = session.FileExists(_paths.PrimaryPath);
        var proofExists = session.FileExists(_paths.ProofPath);
        var empty = EmptyDocument(workspaceIdentity);
        if (trust is null)
        {
            return primaryExists || proofExists
                ? new(null, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Unavailable)
                : new(empty, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Current);
        }

        if (!string.Equals(trust.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal))
        {
            return new(null, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Unavailable);
        }

        var primary = primaryExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.PrimaryPath, cancellationToken)
            : null;
        var proof = proofExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.ProofPath, cancellationToken)
            : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new(primary, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Current);
        }

        var currentBase = proof is not null && MatchesCurrent(proof, trust)
            ? proof
            : !primaryExists && !proofExists && MatchesCurrent(empty, trust)
                ? empty
                : null;
        if (primary is not null
            && currentBase is not null
            && IsAuthenticatedDirectSuccessor(currentBase, primary, trust))
        {
            return new(currentBase, primary, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Pending);
        }

        if (!primaryExists && currentBase is not null)
        {
            return new(currentBase, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Current);
        }

        if (currentBase is not null)
        {
            return new(currentBase, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered);
        }

        if (proof is not null && MatchesPrevious(proof, trust))
        {
            return new(proof, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered);
        }

        if (primary is not null && MatchesPrevious(primary, trust))
        {
            return new(primary, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Recovered);
        }

        return new(null, null, GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition.Unavailable);
    }

    private async Task<GovernedLoopEffectAuthorityEvidenceDocument?> TryReadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaxArtifactUtf8Bytes, cancellationToken);
            if (bytes is null || HasUtf8Bom(bytes))
            {
                return null;
            }

            var text = _strictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (!GovernedLoopEffectAuthorityEvidenceStoreJson.IsStrictBoundedDocument(json.RootElement, _options.MaxDecisions))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<GovernedLoopEffectAuthorityEvidenceDocument>(text, _jsonOptions);
            if (document is null
                || !ValidateDocument(document, workspaceIdentity)
                || !string.Equals(CanonicalJson(document), text, StringComparison.Ordinal)
                || string.IsNullOrEmpty(document.AuthenticationTag)
                || _strictUtf8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
                || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var digest, out _)
                || !digest!.FixedTimeEquals(ComputeContentDigest(document))
                || !await _trustProvider.VerifyArtifactAsync(
                    workspaceIdentity,
                    document.Generation,
                    document.ContentDigest,
                    document.AuthenticationTag,
                    cancellationToken))
            {
                return null;
            }

            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<CapabilityCatalogTrustState> FinalizePendingAsync(
        string workspaceIdentity,
        CapabilityCatalogTrustState trust,
        GovernedLoopEffectAuthorityEvidenceStoreLoadResult loaded,
        CancellationToken cancellationToken)
    {
        var pending = loaded.Pending ?? throw new InvalidOperationException("A pending effect-authority successor is required.");
        var advanced = await _trustProvider.AdvanceAsync(
            workspaceIdentity,
            trust.CurrentGeneration,
            trust.CurrentContentDigest,
            pending.Generation,
            pending.ContentDigest,
            cancellationToken);
        RequireExactTrustSuccessor(
            advanced,
            workspaceIdentity,
            pending.Generation,
            pending.ContentDigest,
            trust.CurrentGeneration,
            trust.CurrentContentDigest);
        await ObserveAsync(GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced, cancellationToken);
        return advanced;
    }

    private static GovernedLoopEffectAuthorityEvidenceDocument CreateCandidate(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityDecision decision,
        string workspaceIdentity)
        => new(
            GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            current.Decisions.Append(decision).ToArray(),
            string.Empty,
            string.Empty);

    private bool ValidateDocument(GovernedLoopEffectAuthorityEvidenceDocument document, string workspaceIdentity)
    {
        if (document.SchemaVersion != GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion
            || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || document.Generation < 0
            || document.Decisions is null
            || document.Generation != document.Decisions.Count
            || document.Decisions.Count > _options.MaxDecisions)
        {
            return false;
        }

        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in document.Decisions.Take(_options.MaxDecisions + 1))
        {
            if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid
                || !operations.Add(decision.EffectOperationId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDirectSuccessor(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityEvidenceDocument candidate)
    {
        return current.Generation < long.MaxValue
            && candidate.Generation == current.Generation + 1
            && candidate.SchemaVersion == current.SchemaVersion
            && string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            && candidate.Decisions.Count == current.Decisions.Count + 1
            && candidate.Decisions.Take(current.Decisions.Count).Zip(current.Decisions).All(pair => SameDecision(pair.First, pair.Second));
    }

    private static bool IsAuthenticatedDirectSuccessor(
        GovernedLoopEffectAuthorityEvidenceDocument current,
        GovernedLoopEffectAuthorityEvidenceDocument pending,
        CapabilityCatalogTrustState trust)
        => MatchesCurrent(current, trust)
            && pending.Generation == trust.CurrentGeneration + 1
            && IsDirectSuccessor(current, pending);

    private async Task<GovernedLoopEffectAuthorityEvidenceSerializedDocument> SerializeAsync(
        string workspaceIdentity,
        GovernedLoopEffectAuthorityEvidenceDocument document,
        CancellationToken cancellationToken)
    {
        var digest = ComputeContentDigest(document).Value;
        var authenticationTag = await _trustProvider.AuthenticateArtifactAsync(
            workspaceIdentity,
            document.Generation,
            digest,
            cancellationToken);
        if (string.IsNullOrEmpty(authenticationTag)
            || _strictUtf8.GetByteCount(authenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The trust provider returned an authentication tag outside its declared bound.");
        }

        var authenticated = document with { ContentDigest = digest, AuthenticationTag = authenticationTag };
        var json = CanonicalJson(authenticated);
        if (_strictUtf8.GetByteCount(json) > _options.MaxArtifactUtf8Bytes)
        {
            throw new GovernedLoopEffectAuthorityEvidenceStoreLimitException();
        }

        return new(json, digest, authenticationTag);
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(GovernedLoopEffectAuthorityEvidenceDocument document)
    {
        var content = JsonSerializer.Serialize(
            document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty },
            _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private static GovernedLoopEffectAuthorityEvidenceDocument EmptyDocument(string workspaceIdentity)
    {
        var empty = new GovernedLoopEffectAuthorityEvidenceDocument(
            GovernedLoopEffectAuthorityEvidenceDocument.CurrentSchemaVersion,
            workspaceIdentity,
            0,
            [],
            string.Empty,
            string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private async Task<CapabilityCatalogPathSession> AcquireForAppendAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.LockPath, createRoot: false, cancellationToken)
            ?? throw new IOException("The effect-authority evidence workspace is unavailable.");

    private static string WorkspaceIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(
            "embodysense-governed-loop-effect-authority-evidence-v1\n" + session.PhysicalIdentityMaterial);

    private async ValueTask ObserveAsync(
        GovernedLoopEffectAuthorityPersistenceBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (_options.DurableBoundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken);
        }
    }

    private static GovernedLoopEffectAuthorityEvidenceStoreOptions ValidateOptions(
        GovernedLoopEffectAuthorityEvidenceStoreOptions options)
    {
        if (options.MaxDecisions is < 1 or > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumDecisions
            || options.MaxArtifactUtf8Bytes is < 1 or > GovernedLoopEffectAuthorityEvidenceStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effect-authority evidence-store options must remain within schema-1 bounds.");
        }

        return options;
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };
        options.Converters.Add(new GovernedLoopRevisionReferenceJsonConverter());
        options.Converters.Add(new AuthorityGrantIdJsonConverter());
        options.Converters.Add(new AuthorityGrantRevisionJsonConverter());
        options.Converters.Add(new AuthorityProfileIdJsonConverter());
        options.Converters.Add(new AuthorityProfileRevisionJsonConverter());
        options.Converters.Add(new AuthorityProfileHashJsonConverter());
        options.Converters.Add(new CapabilityDataClassJsonConverter());
        return options;
    }

    private static string CanonicalJson(GovernedLoopEffectAuthorityEvidenceDocument document)
        => JsonSerializer.Serialize(document, _jsonOptions) + "\n";

    private static GovernedLoopEffectAuthorityDecision? FindDecision(
        GovernedLoopEffectAuthorityEvidenceDocument document,
        string effectOperationId)
        => document.Decisions.SingleOrDefault(decision => SameIdentity(decision, effectOperationId));

    private static bool SameIdentity(
        GovernedLoopEffectAuthorityDecision left,
        GovernedLoopEffectAuthorityDecision right)
        => SameIdentity(left, right.EffectOperationId);

    private static bool SameIdentity(GovernedLoopEffectAuthorityDecision decision, string effectOperationId)
        => string.Equals(decision.EffectOperationId, effectOperationId, StringComparison.Ordinal);

    private static bool SameDecision(
        GovernedLoopEffectAuthorityDecision left,
        GovernedLoopEffectAuthorityDecision right)
        => FixedTimeHashEquals(left.ContentHash, right.ContentHash);

    private static bool MatchesCurrent(
        GovernedLoopEffectAuthorityEvidenceDocument document,
        CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && document.Generation == trust.CurrentGeneration
            && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(
        GovernedLoopEffectAuthorityEvidenceDocument document,
        CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && document.Generation == trust.PreviousGeneration
            && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);

    private static void RequireExactTrustSuccessor(
        CapabilityCatalogTrustState? advanced,
        string workspaceIdentity,
        long candidateGeneration,
        string candidateContentDigest,
        long previousGeneration,
        string previousContentDigest)
    {
        if (advanced is null
            || !string.Equals(advanced.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || advanced.CurrentGeneration != candidateGeneration
            || !string.Equals(advanced.CurrentContentDigest, candidateContentDigest, StringComparison.Ordinal)
            || advanced.PreviousGeneration != previousGeneration
            || !string.Equals(advanced.PreviousContentDigest, previousContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The effect-authority trust provider did not return the exact committed successor state.");
        }
    }

    private static bool FixedTimeHashEquals(string? left, string? right)
    {
        return left is { Length: GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters }
            && right is { Length: GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters }
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static GovernedLoopEffectAuthorityEvidenceStoreResult Result(
        GovernedLoopEffectAuthorityEvidenceStoreStatus status,
        string? contentHash = null)
        => new(status, contentHash);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or OverflowException
            or DecoderFallbackException
            or CryptographicException
            or NotSupportedException
            or InvalidOperationException;
}
