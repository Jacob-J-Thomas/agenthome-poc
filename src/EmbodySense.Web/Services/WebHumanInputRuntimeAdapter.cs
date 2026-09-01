using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>Maps Web DTOs to Startup primitive boundary types over the retained Web runtime host.</summary>
public sealed class WebHumanInputRuntimeAdapter : IWebHumanInputRuntime
{
    private readonly WebAgentRuntimeHost _host;

    /// <summary>Initializes the adapter over the one retained Web runtime host.</summary>
    /// <param name="host">The host that brackets each operation for drain-safe runtime ownership.</param>
    public WebHumanInputRuntimeAdapter(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc />
    public Task<HumanInputRequestPosturePage> ListAsync(HumanInputRequestPosturePageRequest request, CancellationToken cancellationToken = default)
        => _host.ListHumanInputAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<HumanInputRequestPostureReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default)
        => _host.ReadHumanInputAsync(requestId, cancellationToken);

    /// <inheritdoc />
    public Task<HumanInputOperationResult> SubmitLifecycleAsync(HumanInputSurfaceLifecycleOperationInput input, CancellationToken cancellationToken = default)
        => _host.SubmitHumanInputLifecycleAsync(input, cancellationToken);

    /// <inheritdoc />
    public Task<HumanInputOperationResult> SubmitResponseAsync(HumanInputSurfaceResponseOperationInput input, CancellationToken cancellationToken = default)
        => _host.SubmitHumanInputResponseAsync(input, cancellationToken);

    /// <inheritdoc />
    public Task<HumanInputSupersedePreparationResult> PrepareSupersedeAsync(HumanInputSupersedePreparationInput input, CancellationToken cancellationToken = default)
        => _host.PrepareHumanInputSupersedeAsync(input, cancellationToken);
}
