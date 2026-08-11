using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Exposes surface-neutral governed-loop admission while owning disposable production composition.</summary>
/// <remarks>
/// Admission records exact immutable evidence only. It does not dispatch a node, grant execution authority, or create
/// Web, CLI, chat, model-tool, or HTTP semantics.
/// </remarks>
public sealed class GovernedLoopAdmissionFacade : IDisposable
{
    private readonly IGovernedLoopAdmissionService _service;
    private readonly IDisposable? _ownedResource;
    private int _disposed;

    internal GovernedLoopAdmissionFacade(IGovernedLoopAdmissionService service, IDisposable? ownedResource = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ownedResource = ownedResource;
    }

    /// <summary>Admits one server-prepared exact request through the canonical Application service.</summary>
    /// <param name="request">The bounded caller-stable request coordinates and exact authority pins.</param>
    /// <param name="cancellationToken">The token used until durable terminal evidence exists.</param>
    /// <returns>The canonical admission result without surface-specific reinterpretation.</returns>
    public Task<GovernedLoopAdmissionResult> AdmitAsync(
        GovernedLoopAdmissionRequest? request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _service.AdmitAsync(request, cancellationToken);
    }

    /// <summary>Releases production-owned retained workspace handles without mutating persisted evidence.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ownedResource?.Dispose();
        }
    }
}
