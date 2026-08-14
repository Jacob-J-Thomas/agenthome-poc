using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Prepares server-owned canonical invocation evidence and delegates all admission and execution to the shared coordinator.</summary>
internal sealed class GovernedLoopRuntimeFacade : IDisposable
{
    private const string PendingDetail = "The canonical invocation is pending before immutable context binding.";
    private readonly IGovernedLoopGraphRevisionStore _graphStore;
    private readonly ICustomLoopRunStore _runStore;
    private readonly ICustomLoopInvocationOperationStore _operationStore;
    private readonly CustomLoopInvocationReceiptWriter _receiptWriter;
    private readonly GovernedLoopSequentialInvocationCoordinator _coordinator;
    private readonly ICustomLoopWorkspaceExecutionGate _executionGate;
    private readonly CustomLoopRuntimeFacade _legacyRuntime;
    private readonly CustomLoopRuntimeContext _runtimeContext;
    private readonly AuthorityActorId _actor;
    private readonly string _surface;
    private readonly CustomLoopModelSnapshot _modelSnapshot;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable _ownedResource;
    private int _disposed;

    public GovernedLoopRuntimeFacade(
        IGovernedLoopGraphRevisionStore graphStore,
        ICustomLoopRunStore runStore,
        ICustomLoopInvocationOperationStore operationStore,
        CustomLoopInvocationReceiptWriter receiptWriter,
        GovernedLoopSequentialInvocationCoordinator coordinator,
        ICustomLoopWorkspaceExecutionGate executionGate,
        CustomLoopRuntimeFacade legacyRuntime,
        CustomLoopRuntimeContext runtimeContext,
        string actor,
        string surface,
        CustomLoopModelSnapshot modelSnapshot,
        IDisposable ownedResource,
        TimeProvider? timeProvider = null)
    {
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
        _receiptWriter = receiptWriter ?? throw new ArgumentNullException(nameof(receiptWriter));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _executionGate = executionGate ?? throw new ArgumentNullException(nameof(executionGate));
        _legacyRuntime = legacyRuntime ?? throw new ArgumentNullException(nameof(legacyRuntime));
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        if (!AuthorityActorId.TryParse(actor, out var parsedActor, out _))
        {
            throw new ArgumentException("The runtime actor must be canonical.", nameof(actor));
        }

        _actor = parsedActor!;
        _surface = CustomLoopArtifactIdentifier.Require(surface, nameof(surface));
        _modelSnapshot = modelSnapshot ?? throw new ArgumentNullException(nameof(modelSnapshot));
        _ownedResource = ownedResource ?? throw new ArgumentNullException(nameof(ownedResource));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Invokes one exact published governed-loop revision through canonical admission and the shared ordered runtime.</summary>
    public async Task<GovernedLoopRunInvocationResponse> InvokeAsync(
        GovernedLoopRunInvocationInput? input,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsValid(input))
        {
            return Failure(GovernedLoopSequentialInvocationStatus.Invalid.ToString(), "The governed-loop invocation coordinates are malformed.");
        }

        GovernedLoopGraphRevisionArtifact? artifact;
        try
        {
            var read = await _graphStore.ReadArtifactAsync(input!.Publication.Revision, cancellationToken).ConfigureAwait(false);
            if (read.Status != GovernedLoopRevisionStoreReadStatus.Ready || read.Artifact is null)
            {
                return Failure(MapReadStatus(read.Status), "The exact immutable governed-loop graph artifact is not available.");
            }

            artifact = read.Artifact;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(GovernedLoopSequentialInvocationStatus.Unavailable.ToString(), $"The exact graph artifact could not be read safely: {exception.GetType().Name}.");
        }

        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        if (planResult.Status != GovernedLoopSequentialPlanBuildStatus.Ready || planResult.Plan is null)
        {
            return Failure(
                GovernedLoopSequentialInvocationStatus.Invalid.ToString(),
                $"The exact graph cannot execute through the sequential runtime: {planResult.Status} at `{planResult.FailurePath}`.");
        }

        CustomLoopInvocationOperation? existing;
        try
        {
            existing = await _operationStore.GetAsync(input!.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(GovernedLoopSequentialInvocationStatus.Unavailable.ToString(), $"The invocation receipt could not be read safely: {exception.GetType().Name}.");
        }

        GovernedLoopSequentialInvocationSnapshot snapshot;
        try
        {
            snapshot = existing?.SequentialInvocationSnapshot
                ?? await CaptureAsync(input!, existing?.CreatedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(GovernedLoopSequentialInvocationStatus.Unavailable.ToString(), $"The immutable invocation snapshot could not be captured safely: {exception.GetType().Name}.");
        }

        GovernedLoopAdmissionRequest admissionRequest;
        CustomLoopInvocationOperation pending;
        try
        {
            admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
                GovernedLoopAdmissionRequest.CurrentSchemaVersion,
                input!.OperationId,
                snapshot.ContentHash,
                string.Empty,
                input.Publication,
                input.AuthorityGrant,
                _actor,
                _surface));
            var projection = GovernedLoopSequentialLegacyDefinitionProjector.ProjectPrepared(
                input.OperationId,
                snapshot,
                planResult.Plan,
                artifact);
            if (projection.Status != GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready || projection.Definition is null)
            {
                return Failure(GovernedLoopSequentialInvocationStatus.Invalid.ToString(), $"The exact graph could not form an ordered-runtime projection: {projection.Status}.");
            }

            var now = snapshot.ContextCapturedAtUtc;
            pending = CustomLoopInvocationRequestHash.ApplySequential(new CustomLoopInvocationOperation(
                CustomLoopInvocationOperation.CurrentSchemaVersion,
                input.OperationId,
                string.Empty,
                artifact.Graph.GraphId,
                projection.Definition.DefinitionVersion,
                projection.Definition.ContentHash,
                _actor.Value,
                _surface,
                artifact.Graph.OwningRole.Identity.RoleId,
                CustomLoopInvocationRequestHash.ComputePromptHash(snapshot.TriggerPrompt),
                snapshot.ModelSnapshot.Provider,
                snapshot.ModelSnapshot.Model,
                CustomLoopInvocationBindingState.Unbound,
                null,
                null,
                now,
                now,
                CustomLoopInvocationOperationState.Pending,
                CustomLoopInvocationOutcome.Unknown,
                string.Empty,
                null,
                [],
                PendingDetail)
            {
                SequentialAdmissionRequestHash = admissionRequest.RequestHash,
                SequentialArtifactHash = artifact.ArtifactHash,
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(GovernedLoopSequentialInvocationStatus.Invalid.ToString(), $"The canonical invocation envelope is invalid: {exception.Message}");
        }

        var request = new GovernedLoopSequentialInvocationRequest(
            GovernedLoopSequentialInvocationRequest.CurrentSchemaVersion,
            admissionRequest,
            artifact,
            planResult.Plan,
            snapshot);

        var operation = existing;
        if (operation?.SequentialInvocationSnapshot is null)
        {
            CustomLoopInvocationOperationStoreResult begun;
            try
            {
                begun = await _receiptWriter.BeginAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Failure(GovernedLoopSequentialInvocationStatus.Unavailable.ToString(), $"The canonical Begin receipt could not be written safely: {exception.GetType().Name}.");
            }

            if (begun.Status is not (CustomLoopInvocationOperationStoreStatus.Created or CustomLoopInvocationOperationStoreStatus.Replayed)
                || begun.Operation is null)
            {
                return Failure(MapReceiptStatus(begun.Status), $"The canonical Begin receipt returned `{begun.Status}`.");
            }

            operation = begun.Operation;
        }

        if (!MatchesPreparedEnvelope(operation, pending))
        {
            return Map(await _coordinator.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
        }

        if (await CannotDispatchAsync(operation!, cancellationToken).ConfigureAwait(false))
        {
            return Map(await _coordinator.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
        }

        var availability = await _legacyRuntime.EnsureCustomExecutionAvailableAsync(_actor.Value, cancellationToken).ConfigureAwait(false);
        if (!availability.Available)
        {
            return Failure(availability.Status, availability.Detail);
        }

        var ownership = _executionGate.TryAcquire(input!.OperationId, pending.RequestHash);
        if (ownership.Status != CustomLoopExecutionLeaseStatus.Acquired || ownership.Lease is null)
        {
            var reconciled = await TryReadOperationAsync(input.OperationId).ConfigureAwait(false);
            if (MatchesPreparedEnvelope(reconciled, pending)
                && await CannotDispatchAsync(reconciled!, CancellationToken.None).ConfigureAwait(false))
            {
                return Map(await _coordinator.InvokeAsync(request, CancellationToken.None).ConfigureAwait(false));
            }

            return Failure(ownership.Status.ToString(), ownership.Detail);
        }

        using (ownership.Lease)
        {
            return Map(await _coordinator.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ownedResource.Dispose();
        }
    }

    private async Task<GovernedLoopSequentialInvocationSnapshot> CaptureAsync(
        GovernedLoopRunInvocationInput input,
        DateTimeOffset? durableCaptureInstant,
        CancellationToken cancellationToken)
    {
        var capture = durableCaptureInstant is { } capturedAtUtc
            ? await _runtimeContext.CaptureAsync(includeInvokingConversation: true, capturedAtUtc, cancellationToken).ConfigureAwait(false)
            : await _runtimeContext.CaptureAsync(includeInvokingConversation: true, cancellationToken).ConfigureAwait(false);
        return GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            input.InvocationPrompt,
            _modelSnapshot,
            capture.ConversationReference,
            capture.Snapshot.CapturedAtUtc,
            capture.Snapshot.SourceManifest,
            string.Empty));
    }

    private async Task<bool> CannotDispatchAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken)
    {
        if (operation.State == CustomLoopInvocationOperationState.Complete
            && operation.Outcome == CustomLoopInvocationOutcome.Rejected)
        {
            return true;
        }

        if (operation.RunId is null)
        {
            return false;
        }

        try
        {
            var run = await _runStore.GetAsync(operation.RunId, cancellationToken).ConfigureAwait(false);
            return run is not null && run.Status != CustomLoopRunStatus.Admitted;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CustomLoopInvocationOperation?> TryReadOperationAsync(string operationId)
    {
        try
        {
            return await _operationStore.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private DateTimeOffset UtcNow(DateTimeOffset minimum)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < minimum ? minimum : now;
    }

    private static bool IsValid(GovernedLoopRunInvocationInput? input)
        => input is not null
            && CustomLoopArtifactIdentifier.IsValid(input.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            && GovernedLoopRevisionContractValidator.Validate(input.Publication).IsValid
            && IsValid(input.AuthorityGrant)
            && !string.IsNullOrWhiteSpace(input.InvocationPrompt)
            && input.InvocationPrompt.Length <= GovernedLoopSequentialContractLimits.MaxTriggerPromptCharacters;

    private static bool IsValid(AuthorityGrantReference? reference)
        => reference?.GrantId is not null
            && reference.Revision is not null
            && AuthorityGrantId.TryParse(reference.GrantId.Value, out _, out _)
            && AuthorityGrantRevision.TryParse(reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _)
            && reference.ContentHash is { Length: 71 }
            && reference.ContentHash.StartsWith("sha256:", StringComparison.Ordinal)
            && reference.ContentHash[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool MatchesPreparedEnvelope(CustomLoopInvocationOperation? actual, CustomLoopInvocationOperation expected)
        => actual is not null
            && string.Equals(actual.RequestHash, expected.RequestHash, StringComparison.Ordinal)
            && string.Equals(actual.OperationId, expected.OperationId, StringComparison.Ordinal)
            && string.Equals(actual.SequentialAdmissionRequestHash, expected.SequentialAdmissionRequestHash, StringComparison.Ordinal)
            && string.Equals(actual.SequentialArtifactHash, expected.SequentialArtifactHash, StringComparison.Ordinal)
            && string.Equals(actual.LoopId, expected.LoopId, StringComparison.Ordinal)
            && actual.ExpectedDefinitionVersion == expected.ExpectedDefinitionVersion
            && string.Equals(actual.ExpectedDefinitionHash, expected.ExpectedDefinitionHash, StringComparison.Ordinal)
            && string.Equals(actual.Actor, expected.Actor, StringComparison.Ordinal)
            && string.Equals(actual.Surface, expected.Surface, StringComparison.Ordinal)
            && string.Equals(actual.CurrentRoleId, expected.CurrentRoleId, StringComparison.Ordinal)
            && string.Equals(actual.InvocationPromptHash, expected.InvocationPromptHash, StringComparison.Ordinal)
            && string.Equals(actual.Provider, expected.Provider, StringComparison.Ordinal)
            && string.Equals(actual.Model, expected.Model, StringComparison.Ordinal);

    private static GovernedLoopRunInvocationResponse Map(GovernedLoopSequentialInvocationResult result)
        => new(
            result.Status.ToString(),
            result.Admission?.Status.ToString(),
            result.Admission?.Outcome?.Rejection?.FailureCode.ToString(),
            result.Materialization?.Status.ToString(),
            result.Execution?.Status.ToString(),
            result.ProviderWasInvoked(),
            result.Run is null ? null : CustomLoopRuntimeFacade.Map(result.Run),
            result.Detail);

    private static GovernedLoopRunInvocationResponse Failure(string status, string detail)
        => new(status, null, null, null, null, false, null, detail);

    private static string MapReadStatus(GovernedLoopRevisionStoreReadStatus status)
        => status switch
        {
            GovernedLoopRevisionStoreReadStatus.NotFound => GovernedLoopSequentialInvocationStatus.NotFound.ToString(),
            GovernedLoopRevisionStoreReadStatus.Unavailable => GovernedLoopSequentialInvocationStatus.Unavailable.ToString(),
            _ => GovernedLoopSequentialInvocationStatus.Invalid.ToString(),
        };

    private static string MapReceiptStatus(CustomLoopInvocationOperationStoreStatus status)
        => status switch
        {
            CustomLoopInvocationOperationStoreStatus.Conflict => GovernedLoopSequentialInvocationStatus.Conflict.ToString(),
            CustomLoopInvocationOperationStoreStatus.LimitExceeded or CustomLoopInvocationOperationStoreStatus.RetentionRequired => GovernedLoopSequentialInvocationStatus.LimitExceeded.ToString(),
            CustomLoopInvocationOperationStoreStatus.RetentionAuditUnavailable => GovernedLoopSequentialInvocationStatus.AuditUnavailable.ToString(),
            _ => GovernedLoopSequentialInvocationStatus.Unavailable.ToString(),
        };
}
