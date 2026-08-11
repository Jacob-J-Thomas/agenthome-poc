using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;
using EmbodySense.Core.Persistence.Loops.Admission.Models;
using EmbodySense.Core.Persistence.Loops.Revisions;

namespace EmbodySense.Core.Persistence.Loops.Admission;

/// <summary>Persists one bounded, authenticated, append-only governed-loop admission ledger.</summary>
/// <remarks>
/// Workspace artifacts are untrusted projections. A server-owned monotonic trust provider binds the exact ledger to
/// the physical workspace, while a shared authority transaction and retained-handle path session serialize mutations.
/// A published direct successor is finalized only by an exact terminal-outcome retry. Historical outcomes are never
/// evicted or rewritten, and the ledger contains no invocation payloads, secrets, credentials, or diagnostics.
/// </remarks>
public sealed class GovernedLoopAdmissionStore : IGovernedLoopAdmissionStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly GovernedLoopAdmissionStorePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly GovernedLoopAdmissionStoreOptions _options;
    private readonly string _workspaceId;

    /// <summary>Creates an admission store with the default server-owned trust provider.</summary>
    public GovernedLoopAdmissionStore(
        WorkspacePaths paths,
        GovernedLoopAdmissionStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), options, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates an admission store over an explicit server-owned trust provider.</summary>
    public GovernedLoopAdmissionStore(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        GovernedLoopAdmissionStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new GovernedLoopAdmissionStoreOptions());
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > GovernedLoopAdmissionStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        if (!ContextualRoleWorkspaceId.IsValid(_workspaceId))
        {
            throw new InvalidOperationException("The physical workspace did not produce a canonical workspace scope.");
        }

        _paths = new GovernedLoopAdmissionStorePaths(paths);
        _pathGuard = new CapabilityCatalogPathGuard(
            paths.RootPath,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver);
        _trustProvider = trustProvider;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopAdmissionStoreReadResult> ReadByOperationAsync(
        string workspaceId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(workspaceId, _workspaceId, StringComparison.Ordinal) || !IsIdentifier(operationId))
        {
            return ReadResult(GovernedLoopAdmissionStoreReadStatus.Unavailable);
        }

        GovernedLoopAdmissionStoreReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadCoreAsync(operationId, token);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (callbackResult is not null)
            {
                return callbackResult;
            }

            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult ?? ReadResult(GovernedLoopAdmissionStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopAdmissionStoreCommitResult> CommitAsync(
        GovernedLoopAdmissionStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidMutation(mutation))
        {
            return CommitResult(GovernedLoopAdmissionStoreCommitStatus.Unavailable);
        }

        var callbackEntered = false;
        GovernedLoopAdmissionStoreCommitResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await CommitCoreAsync(mutation, token, cancellationToken);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (callbackResult is null && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return callbackResult ?? CommitResult(callbackEntered
                ? GovernedLoopAdmissionStoreCommitStatus.Ambiguous
                : GovernedLoopAdmissionStoreCommitStatus.Unavailable);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult ?? CommitResult(callbackEntered
                ? GovernedLoopAdmissionStoreCommitStatus.Ambiguous
                : GovernedLoopAdmissionStoreCommitStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopAdmissionStoreReadResult> ReadCoreAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var session = await AcquireForReadAsync(cancellationToken);
        if (session is null)
        {
            return new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 0, null);
        }

        var workspaceIdentity = WorkspaceIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
        var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
        if (loaded.Disposition == GovernedLoopAdmissionStoreLoadDisposition.Pending)
        {
            var pendingOutcome = loaded.Pending!.Outcomes[^1];
            return string.Equals(pendingOutcome.Intent.OperationId, operationId, StringComparison.Ordinal)
                ? new GovernedLoopAdmissionStoreReadResult(
                    GovernedLoopAdmissionStoreReadStatus.Recoverable,
                    loaded.Pending.Generation,
                    pendingOutcome)
                : ReadResult(GovernedLoopAdmissionStoreReadStatus.Ambiguous);
        }

        if (loaded.Disposition == GovernedLoopAdmissionStoreLoadDisposition.Recovered)
        {
            return ReadResult(GovernedLoopAdmissionStoreReadStatus.Ambiguous);
        }

        if (loaded.Document is null)
        {
            return ReadResult(GovernedLoopAdmissionStoreReadStatus.Unavailable);
        }

        var outcome = FindOutcome(loaded.Document, operationId);
        return new GovernedLoopAdmissionStoreReadResult(
            outcome is null ? GovernedLoopAdmissionStoreReadStatus.NotFound : GovernedLoopAdmissionStoreReadStatus.Found,
            loaded.Document.Generation,
            outcome);
    }

    private async Task<GovernedLoopAdmissionStoreCommitResult> CommitCoreAsync(
        GovernedLoopAdmissionStoreMutation mutation,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var outcomeMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireForCommitAsync(cancellationToken);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
            if (loaded.Disposition == GovernedLoopAdmissionStoreLoadDisposition.Pending)
            {
                var pendingOutcome = loaded.Pending!.Outcomes[^1];
                if (!string.Equals(pendingOutcome.Intent.OperationId, mutation.OperationId, StringComparison.Ordinal))
                {
                    return CommitResult(GovernedLoopAdmissionStoreCommitStatus.Ambiguous);
                }

                if (!SameOutcome(pendingOutcome, mutation.Outcome))
                {
                    return new GovernedLoopAdmissionStoreCommitResult(
                        GovernedLoopAdmissionStoreCommitStatus.OperationConflict,
                        loaded.Pending.Generation,
                        pendingOutcome);
                }

                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken);
                var recovered = FindOutcome(loaded.Document!, mutation.OperationId);
                return new GovernedLoopAdmissionStoreCommitResult(
                    GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted,
                    loaded.Document!.Generation,
                    recovered);
            }

            if (loaded.Disposition == GovernedLoopAdmissionStoreLoadDisposition.Recovered)
            {
                return CommitResult(GovernedLoopAdmissionStoreCommitStatus.Ambiguous);
            }

            if (loaded.Document is null)
            {
                return CommitResult(GovernedLoopAdmissionStoreCommitStatus.Unavailable);
            }

            var current = loaded.Document;
            var existing = FindOutcome(current, mutation.OperationId);
            if (existing is not null)
            {
                return new GovernedLoopAdmissionStoreCommitResult(
                    SameOutcome(existing, mutation.Outcome)
                        ? GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted
                        : GovernedLoopAdmissionStoreCommitStatus.OperationConflict,
                    current.Generation,
                    existing);
            }

            if (mutation.ExpectedStoreGeneration != current.Generation)
            {
                return new GovernedLoopAdmissionStoreCommitResult(
                    GovernedLoopAdmissionStoreCommitStatus.GenerationConflict,
                    current.Generation,
                    null);
            }

            if (current.Outcomes.Count >= _options.MaxTerminalOutcomes)
            {
                return new GovernedLoopAdmissionStoreCommitResult(
                    GovernedLoopAdmissionStoreCommitStatus.LimitExceeded,
                    current.Generation,
                    null);
            }

            var candidate = CreateCandidate(current, mutation.Outcome, workspaceIdentity);
            if (!ValidateDocument(candidate, workspaceIdentity) || !IsDirectSuccessor(current, candidate))
            {
                return new GovernedLoopAdmissionStoreCommitResult(
                    GovernedLoopAdmissionStoreCommitStatus.Unavailable,
                    current.Generation,
                    null);
            }

            var currentDigest = ComputeContentDigest(current).Value;
            trust ??= await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
            if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
            {
                return CommitResult(GovernedLoopAdmissionStoreCommitStatus.Unavailable);
            }

            GovernedLoopAdmissionStoreSerializedDocument serializedCandidate;
            try
            {
                serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken);
            }
            catch (GovernedLoopAdmissionStoreLimitException)
            {
                return new GovernedLoopAdmissionStoreCommitResult(
                    GovernedLoopAdmissionStoreCommitStatus.LimitExceeded,
                    current.Generation,
                    null);
            }

            var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken);
            await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken);
            await ObserveAsync(GovernedLoopAdmissionPersistenceBoundary.ProofPublished, cancellationToken);
            outcomeMayHaveCommitted = true;
            await session.WriteTextAtomicallyAsync(_paths.PrimaryPath, serializedCandidate.Json, cancellationToken);
            await ObserveAsync(GovernedLoopAdmissionPersistenceBoundary.PrimaryPublished, cancellationToken);
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
            await ObserveAsync(GovernedLoopAdmissionPersistenceBoundary.TrustAdvanced, cancellationToken);
            return new GovernedLoopAdmissionStoreCommitResult(
                GovernedLoopAdmissionStoreCommitStatus.Committed,
                candidate.Generation,
                mutation.Outcome);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return CommitResult(outcomeMayHaveCommitted
                ? GovernedLoopAdmissionStoreCommitStatus.Ambiguous
                : GovernedLoopAdmissionStoreCommitStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopAdmissionStoreLoadResult> LoadAsync(
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
                ? new(null, null, GovernedLoopAdmissionStoreLoadDisposition.Unavailable)
                : new(empty, null, GovernedLoopAdmissionStoreLoadDisposition.Current);
        }

        var primary = primaryExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.PrimaryPath, cancellationToken)
            : null;
        var proof = proofExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.ProofPath, cancellationToken)
            : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new(primary, null, GovernedLoopAdmissionStoreLoadDisposition.Current);
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
            return new(currentBase, primary, GovernedLoopAdmissionStoreLoadDisposition.Pending);
        }

        if (!primaryExists && currentBase is not null)
        {
            return new(currentBase, null, GovernedLoopAdmissionStoreLoadDisposition.Current);
        }

        if (currentBase is not null)
        {
            return new(currentBase, null, GovernedLoopAdmissionStoreLoadDisposition.Recovered);
        }

        if (proof is not null && MatchesPrevious(proof, trust))
        {
            return new(proof, null, GovernedLoopAdmissionStoreLoadDisposition.Recovered);
        }

        if (primary is not null && MatchesPrevious(primary, trust))
        {
            return new(primary, null, GovernedLoopAdmissionStoreLoadDisposition.Recovered);
        }

        return new(null, null, GovernedLoopAdmissionStoreLoadDisposition.Unavailable);
    }

    private async Task<GovernedLoopAdmissionStoreDocument?> TryReadAsync(
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
            if (!GovernedLoopAdmissionStoreJson.IsStrictBoundedDocument(json.RootElement, _options.MaxTerminalOutcomes))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<GovernedLoopAdmissionStoreDocument>(text, _jsonOptions);
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
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<GovernedLoopAdmissionStoreLoadResult> FinalizePendingAsync(
        string workspaceIdentity,
        CapabilityCatalogTrustState trust,
        GovernedLoopAdmissionStoreLoadResult loaded,
        CancellationToken cancellationToken)
    {
        var pending = loaded.Pending ?? throw new InvalidOperationException("A pending admission successor is required.");
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
        await ObserveAsync(GovernedLoopAdmissionPersistenceBoundary.TrustAdvanced, cancellationToken);
        return new(pending, null, GovernedLoopAdmissionStoreLoadDisposition.Current);
    }

    private static GovernedLoopAdmissionStoreDocument CreateCandidate(
        GovernedLoopAdmissionStoreDocument current,
        GovernedLoopAdmissionTerminalOutcome outcome,
        string workspaceIdentity)
        => new(
            GovernedLoopAdmissionStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            current.WorkspaceId,
            checked(current.Generation + 1),
            current.Outcomes.Append(outcome).ToArray(),
            string.Empty,
            string.Empty);

    private bool ValidateDocument(GovernedLoopAdmissionStoreDocument document, string workspaceIdentity)
    {
        if (document.SchemaVersion != GovernedLoopAdmissionStoreDocument.CurrentSchemaVersion
            || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || !string.Equals(document.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || document.Generation < 0
            || document.Outcomes is null
            || document.Generation != document.Outcomes.Count
            || document.Outcomes.Count > _options.MaxTerminalOutcomes)
        {
            return false;
        }

        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in document.Outcomes.Take(_options.MaxTerminalOutcomes + 1))
        {
            if (!GovernedLoopAdmissionValidator.Validate(outcome).IsValid
                || !string.Equals(outcome.Intent.WorkspaceId, document.WorkspaceId, StringComparison.Ordinal)
                || !operations.Add(outcome.Intent.OperationId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDirectSuccessor(
        GovernedLoopAdmissionStoreDocument current,
        GovernedLoopAdmissionStoreDocument candidate)
    {
        return current.Generation < long.MaxValue
            && candidate.Generation == current.Generation + 1
            && candidate.SchemaVersion == current.SchemaVersion
            && string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            && string.Equals(candidate.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal)
            && candidate.Outcomes.Count == current.Outcomes.Count + 1
            && candidate.Outcomes.Take(current.Outcomes.Count).Zip(current.Outcomes).All(pair => SameOutcome(pair.First, pair.Second));
    }

    private static bool IsAuthenticatedDirectSuccessor(
        GovernedLoopAdmissionStoreDocument current,
        GovernedLoopAdmissionStoreDocument pending,
        CapabilityCatalogTrustState trust)
        => MatchesCurrent(current, trust)
            && pending.Generation == trust.CurrentGeneration + 1
            && IsDirectSuccessor(current, pending);

    private async Task<GovernedLoopAdmissionStoreSerializedDocument> SerializeAsync(
        string workspaceIdentity,
        GovernedLoopAdmissionStoreDocument document,
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
            throw new GovernedLoopAdmissionStoreLimitException();
        }

        return new(json, digest, authenticationTag);
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(GovernedLoopAdmissionStoreDocument document)
    {
        var content = JsonSerializer.Serialize(
            document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty },
            _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private GovernedLoopAdmissionStoreDocument EmptyDocument(string workspaceIdentity)
    {
        var empty = new GovernedLoopAdmissionStoreDocument(
            GovernedLoopAdmissionStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            _workspaceId,
            0,
            [],
            string.Empty,
            string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private Task<CapabilityCatalogPathSession?> AcquireForReadAsync(CancellationToken cancellationToken)
        => _pathGuard.TryAcquireExclusiveSessionAsync(
            _paths.LockPath,
            createRoot: false,
            cancellationToken,
            createLockParent: false);

    private async Task<CapabilityCatalogPathSession> AcquireForCommitAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.LockPath, createRoot: false, cancellationToken)
            ?? throw new IOException("The governed-loop admission workspace is unavailable.");

    private static string WorkspaceIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(
            "embodysense-governed-loop-admissions-v1\n" + session.PhysicalIdentityMaterial);

    private async ValueTask ObserveAsync(
        GovernedLoopAdmissionPersistenceBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (_options.DurableBoundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken);
        }
    }

    private bool IsValidMutation(GovernedLoopAdmissionStoreMutation? mutation)
    {
        if (mutation?.Outcome?.Intent is null
            || mutation.ExpectedStoreGeneration < 0
            || !string.Equals(mutation.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(mutation.WorkspaceId, mutation.Outcome.Intent.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(mutation.OperationId, mutation.Outcome.Intent.OperationId, StringComparison.Ordinal)
            || !FixedTimeHashEquals(mutation.RequestHash, mutation.Outcome.Intent.RequestHash)
            || !GovernedLoopAdmissionValidator.Validate(mutation.Outcome).IsValid)
        {
            return false;
        }

        var expectedIntentHash = GovernedLoopAdmissionContractHash.ComputeIntentHash(mutation.Outcome.Intent);
        return FixedTimeHashEquals(mutation.IntentHash, expectedIntentHash);
    }

    private static GovernedLoopAdmissionStoreOptions ValidateOptions(GovernedLoopAdmissionStoreOptions options)
    {
        if (options.MaxTerminalOutcomes is < 1 or > GovernedLoopAdmissionStoreOptions.MaximumTerminalOutcomes
            || options.MaxArtifactUtf8Bytes is < 1 or > GovernedLoopAdmissionStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Governed-loop admission store options must remain within schema-1 bounds.");
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
        options.Converters.Add(new GovernedLoopExecutionBindingJsonConverter());
        options.Converters.Add(new AuthorityActorIdJsonConverter());
        options.Converters.Add(new AuthorityGrantIdJsonConverter());
        options.Converters.Add(new AuthorityGrantRevisionJsonConverter());
        options.Converters.Add(new CapabilityDataClassJsonConverter());
        return options;
    }

    private static string CanonicalJson(GovernedLoopAdmissionStoreDocument document)
        => JsonSerializer.Serialize(document, _jsonOptions) + "\n";

    private static GovernedLoopAdmissionTerminalOutcome? FindOutcome(
        GovernedLoopAdmissionStoreDocument document,
        string operationId)
        => document.Outcomes.SingleOrDefault(outcome => string.Equals(outcome.Intent.OperationId, operationId, StringComparison.Ordinal));

    private static bool SameOutcome(
        GovernedLoopAdmissionTerminalOutcome left,
        GovernedLoopAdmissionTerminalOutcome right)
        => FixedTimeHashEquals(left.ContentHash, right.ContentHash);

    private static bool MatchesCurrent(GovernedLoopAdmissionStoreDocument document, CapabilityCatalogTrustState trust)
        => string.Equals(document.WorkspaceIdentity, trust.WorkspaceIdentity, StringComparison.Ordinal)
            && document.Generation == trust.CurrentGeneration
            && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(GovernedLoopAdmissionStoreDocument document, CapabilityCatalogTrustState trust)
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
            throw new IOException("The admission trust provider did not return the exact committed successor state.");
        }
    }

    private static bool FixedTimeHashEquals(string? left, string? right)
    {
        return left is { Length: GovernedLoopAdmissionLimits.Sha256HexCharacters }
            && right is { Length: GovernedLoopAdmissionLimits.Sha256HexCharacters }
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    private static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopAdmissionLimits.MaxIdentifierCharacters);

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static GovernedLoopAdmissionStoreReadResult ReadResult(GovernedLoopAdmissionStoreReadStatus status)
        => new(status, 0, null);

    private static GovernedLoopAdmissionStoreCommitResult CommitResult(GovernedLoopAdmissionStoreCommitStatus status)
        => new(status, 0, null);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or OverflowException
            or DecoderFallbackException
            or CryptographicException
            or InvalidOperationException;
}
