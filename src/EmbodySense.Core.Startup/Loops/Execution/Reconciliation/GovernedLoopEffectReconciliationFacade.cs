using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using AppModels = EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using SurfaceModels = EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

/// <summary>Exposes bounded, redacted effect-reconciliation attention and operations through Core.Startup.</summary>
/// <remarks>
/// This facade borrows the runtime's canonical effect and case stores. It creates no worker, queue, retry, recovery,
/// Human Review authority, or alternate inbox, and every returned projection omits raw execution bindings and inputs.
/// </remarks>
public sealed class GovernedLoopEffectReconciliationFacade
{
    private readonly IGovernedLoopEffectReconciliationCaseStore _cases;
    private readonly IGovernedLoopEffectReconciliationProbeRegistry _probes;
    private readonly IGovernedLoopEffectReconciliationResolutionReader _resolutions;
    private readonly IGovernedLoopEffectReconciliationService _service;

    internal GovernedLoopEffectReconciliationFacade(
        IGovernedLoopEffectReconciliationCaseStore cases,
        IGovernedLoopEffectReconciliationService service,
        IGovernedLoopEffectReconciliationProbeRegistry probes,
        IGovernedLoopEffectReconciliationResolutionReader resolutions)
    {
        _cases = cases ?? throw new ArgumentNullException(nameof(cases));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _resolutions = resolutions ?? throw new ArgumentNullException(nameof(resolutions));
    }

    /// <summary>Reads one bounded deterministic page of reconciliation attention items.</summary>
    /// <param name="cancellationToken">A token that cancels the canonical read.</param>
    /// <returns>The detached first page or a fail-closed status.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public Task<SurfaceModels.GovernedLoopEffectReconciliationPage> ListAsync(CancellationToken cancellationToken = default)
        => ListAsync(new SurfaceModels.GovernedLoopEffectReconciliationPageRequest(), cancellationToken);

    /// <summary>Reads one bounded deterministic page of reconciliation attention items.</summary>
    /// <param name="request">The finite page request.</param>
    /// <param name="cancellationToken">A token that cancels the canonical read.</param>
    /// <returns>A detached redacted page or a fail-closed status.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public async Task<SurfaceModels.GovernedLoopEffectReconciliationPage> ListAsync(SurfaceModels.GovernedLoopEffectReconciliationPageRequest? request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Invalid, []);
        }

        try
        {
            var result = await _cases.ListAsync(new AppModels.GovernedLoopEffectReconciliationCaseListRequest(request.MaximumCount, request.Cursor), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                AppModels.GovernedLoopEffectReconciliationCaseListStatus.Ready => new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Ready, result.Cases.Select(GovernedLoopEffectReconciliationProjectionMapper.Summary).ToArray(), result.NextCursor),
                AppModels.GovernedLoopEffectReconciliationCaseListStatus.Invalid => new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Invalid, []),
                AppModels.GovernedLoopEffectReconciliationCaseListStatus.Corrupt => new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Corrupt, []),
                _ => new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Unavailable, []),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Corrupt, []);
        }
        catch
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationPageStatus.Unavailable, []);
        }
    }

    /// <summary>Reads one exact immutable reconciliation case without following a newer case version.</summary>
    /// <param name="reference">The redacted exact case version and binding hash.</param>
    /// <param name="cancellationToken">A token that cancels the canonical read.</param>
    /// <returns>The detached case when found or a fail-closed read status without a partial payload.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public async Task<SurfaceModels.GovernedLoopEffectReconciliationReadResult> ReadAsync(SurfaceModels.GovernedLoopEffectReconciliationCaseReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is null)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Invalid, null);
        }

        try
        {
            var result = await _cases.ReadAsync(new AppModels.GovernedLoopEffectReconciliationCaseReadRequest(GovernedLoopEffectReconciliationProjectionMapper.Reference(reference)), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Found when result.Case is not null => new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Found, GovernedLoopEffectReconciliationProjectionMapper.Detail(result.Case)),
                AppModels.GovernedLoopEffectReconciliationCaseReadStatus.NotFound => new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.NotFound, null),
                AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Invalid => new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Invalid, null),
                AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Corrupt => new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Corrupt, null),
                _ => new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Unavailable, null),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Corrupt, null);
        }
        catch
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationReadStatus.Unavailable, null);
        }
    }

    /// <summary>Lists the bounded registered read-only reconciliation contracts.</summary>
    /// <param name="cancellationToken">A token that cancels the bounded registry read.</param>
    /// <returns>The detached first registry page or a fail-closed status.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public Task<SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogPage> ListProbeContractsAsync(CancellationToken cancellationToken = default)
        => ListProbeContractsAsync(new SurfaceModels.GovernedLoopEffectReconciliationPageRequest(), cancellationToken);

    /// <summary>Lists one bounded page of registered read-only reconciliation contracts.</summary>
    /// <param name="request">The finite page request.</param>
    /// <param name="cancellationToken">A token that cancels the bounded registry read.</param>
    /// <returns>The detached registry page or a fail-closed status.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public async Task<SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogPage> ListProbeContractsAsync(SurfaceModels.GovernedLoopEffectReconciliationPageRequest? request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, []);
        }

        try
        {
            var result = await _probes.ListAsync(new AppModels.GovernedLoopEffectReconciliationProbeRegistryListRequest(request.MaximumCount, request.Cursor), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                AppModels.GovernedLoopEffectReconciliationProbeRegistryListStatus.Ready => new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Ready, result.Contracts.Select(GovernedLoopEffectReconciliationProjectionMapper.Contract).ToArray(), result.NextCursor),
                AppModels.GovernedLoopEffectReconciliationProbeRegistryListStatus.Invalid => new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, []),
                AppModels.GovernedLoopEffectReconciliationProbeRegistryListStatus.Corrupt => new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Corrupt, []),
                _ => new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Unavailable, []),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Corrupt, []);
        }
        catch
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationProbeCatalogStatus.Unavailable, []);
        }
    }

    /// <summary>Invokes one exact registered read-only probe under an independent operation identity.</summary>
    /// <remarks>The probe may append value-free observation evidence, but it cannot dispatch or retry the original actuator.</remarks>
    /// <param name="operationId">The caller's stable idempotency identity for this probe operation.</param>
    /// <param name="reference">The exact current redacted case reference.</param>
    /// <param name="cancellationToken">A token that cancels work before the probe's durable boundary.</param>
    /// <returns>The closed operation status and a detached case only when safely available.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public Task<SurfaceModels.GovernedLoopEffectReconciliationOperationResult> ProbeAsync(string? operationId, SurfaceModels.GovernedLoopEffectReconciliationCaseReference? reference, CancellationToken cancellationToken = default)
        => OperateAsync(operationId, reference, (id, value) => _service.ProbeAsync(new AppModels.GovernedLoopEffectReconciliationProbeRequest(id, value), cancellationToken), cancellationToken);

    /// <summary>Derives one immutable assessment from current registered observations.</summary>
    /// <param name="operationId">The caller's stable idempotency identity for this assessment.</param>
    /// <param name="reference">The exact current redacted case reference.</param>
    /// <param name="safeDetail">Optional bounded operator context that is never treated as evidence and is omitted from returned projections.</param>
    /// <param name="cancellationToken">A token that cancels work before the durable assessment boundary.</param>
    /// <returns>The closed operation status and a detached case only when safely available.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public Task<SurfaceModels.GovernedLoopEffectReconciliationOperationResult> AssessAsync(string? operationId, SurfaceModels.GovernedLoopEffectReconciliationCaseReference? reference, string? safeDetail = null, CancellationToken cancellationToken = default)
        => OperateAsync(operationId, reference, (id, value) => _service.AssessAsync(new AppModels.GovernedLoopEffectReconciliationAssessmentRequest(id, value, safeDetail), cancellationToken), cancellationToken, safeDetail);

    /// <summary>Applies one legal disposition to the exact current assessment.</summary>
    /// <remarks>A disposition records operator intent but cannot authorize pre-dispatch work or redispatch the original effect.</remarks>
    /// <param name="operationId">The caller's stable idempotency identity for this disposition.</param>
    /// <param name="reference">The exact current redacted case reference.</param>
    /// <param name="kind">The closed disposition requested for the current assessment.</param>
    /// <param name="safeDetail">Optional bounded operator context that is never treated as evidence and is omitted from returned projections.</param>
    /// <param name="cancellationToken">A token that cancels work before the durable disposition boundary.</param>
    /// <returns>The closed operation status and a detached case only when safely available.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public Task<SurfaceModels.GovernedLoopEffectReconciliationOperationResult> DisposeAsync(string? operationId, SurfaceModels.GovernedLoopEffectReconciliationCaseReference? reference, SurfaceModels.GovernedLoopEffectReconciliationDispositionKind kind, string? safeDetail = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind == SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.Unknown || !Enum.IsDefined(kind))
        {
            return Task.FromResult(new SurfaceModels.GovernedLoopEffectReconciliationOperationResult(SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Invalid, null));
        }

        return OperateAsync(operationId, reference, (id, value) => _service.DisposeAsync(new AppModels.GovernedLoopEffectReconciliationDispositionRequest(id, value, GovernedLoopEffectReconciliationProjectionMapper.DispositionKind(kind), safeDetail), cancellationToken), cancellationToken, safeDetail);
    }

    /// <summary>Reads one exact immutable resolution without invoking recovery or changing dispatch eligibility.</summary>
    /// <param name="reference">The redacted exact case version and binding hash.</param>
    /// <param name="cancellationToken">A token that cancels the canonical read.</param>
    /// <returns>The detached immutable resolution when found or a fail-closed status.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token was canceled.</exception>
    public async Task<SurfaceModels.GovernedLoopEffectReconciliationResolutionReadResult> ReadResolutionAsync(SurfaceModels.GovernedLoopEffectReconciliationCaseReference? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is null)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Invalid, null);
        }

        try
        {
            var applicationReference = GovernedLoopEffectReconciliationProjectionMapper.Reference(reference);
            var current = await _cases.ReadAsync(new AppModels.GovernedLoopEffectReconciliationCaseReadRequest(applicationReference), cancellationToken).ConfigureAwait(false);
            if (current.Status == AppModels.GovernedLoopEffectReconciliationCaseReadStatus.NotFound)
            {
                return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, null);
            }
            if (current.Status == AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Invalid)
            {
                return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Invalid, null);
            }
            if (current.Status == AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Corrupt || current.Status == AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Found && current.Case is null)
            {
                return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, null);
            }
            if (current.Status != AppModels.GovernedLoopEffectReconciliationCaseReadStatus.Found)
            {
                return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, null);
            }

            var result = await _resolutions.ReadAsync(new AppModels.GovernedLoopEffectReconciliationResolutionReadRequest(applicationReference, current.Case!.Binding), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                AppModels.GovernedLoopEffectReconciliationResolutionReadStatus.Found when result.Resolution is not null => new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Found, GovernedLoopEffectReconciliationProjectionMapper.Resolution(result.Resolution)),
                AppModels.GovernedLoopEffectReconciliationResolutionReadStatus.NotFound => new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, null),
                AppModels.GovernedLoopEffectReconciliationResolutionReadStatus.Invalid => new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Invalid, null),
                AppModels.GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt => new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, null),
                _ => new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, null),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, null);
        }
        catch
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, null);
        }
    }

    private async Task<SurfaceModels.GovernedLoopEffectReconciliationOperationResult> OperateAsync(
        string? operationId,
        SurfaceModels.GovernedLoopEffectReconciliationCaseReference? reference,
        Func<string, AppModels.GovernedLoopEffectReconciliationCaseReference, Task<AppModels.GovernedLoopEffectReconciliationOperationResult>> operation,
        CancellationToken cancellationToken,
        string? safeDetail = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is null)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Invalid, null);
        }

        string id;
        try
        {
            id = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(operationId, nameof(operationId));
            _ = GovernedLoopEffectReconciliationSurfaceGuard.Detail(safeDetail, nameof(safeDetail));
        }
        catch (ArgumentException)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Invalid, null);
        }

        try
        {
            var result = await operation(id, GovernedLoopEffectReconciliationProjectionMapper.Reference(reference)).ConfigureAwait(false);
            var status = GovernedLoopEffectReconciliationProjectionMapper.OperationStatus(result.Status);
            SurfaceModels.GovernedLoopEffectReconciliationCaseDetail? detail = null;
            if (result.Case is not null)
            {
                detail = GovernedLoopEffectReconciliationProjectionMapper.Detail(result.Case);
            }
            return new(status, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Corrupt, null);
        }
        catch
        {
            return new(SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Unavailable, null);
        }
    }
}
