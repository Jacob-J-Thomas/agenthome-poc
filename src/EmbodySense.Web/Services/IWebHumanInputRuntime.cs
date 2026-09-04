using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Web.Services;

/// <summary>Provides authenticated Web controls over one retained Startup Human Input facade.</summary>
/// <remarks>Implementations must retain one process runtime and pass all lifecycle truth to Startup. Browser payloads
/// carry no actor, role, workspace, binding, grant, or authority evidence.</remarks>
public interface IWebHumanInputRuntime
{
    /// <summary>Lists one bounded detached posture page.</summary>
    Task<HumanInputRequestPosturePage> ListAsync(HumanInputRequestPosturePageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact detached posture.</summary>
    Task<HumanInputRequestPostureReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>Submits one primitive-only lifecycle intent.</summary>
    Task<HumanInputOperationResult> SubmitLifecycleAsync(HumanInputSurfaceLifecycleOperationInput input, CancellationToken cancellationToken = default);

    /// <summary>Submits one primitive-only response intent.</summary>
    Task<HumanInputOperationResult> SubmitResponseAsync(HumanInputSurfaceResponseOperationInput input, CancellationToken cancellationToken = default);

    /// <summary>Prepares one opaque supersede candidate in Startup.</summary>
    Task<HumanInputSupersedePreparationResult> PrepareSupersedeAsync(HumanInputSupersedePreparationInput input, CancellationToken cancellationToken = default);

    /// <summary>Prepares bounded opaque server-generated reroute candidates in Startup.</summary>
    Task<HumanInputReroutePreparationResult> PrepareRerouteAsync(HumanInputReroutePreparationInput input, CancellationToken cancellationToken = default);

    /// <summary>Prepares one opaque server-generated amend candidate in Startup.</summary>
    Task<HumanInputAmendPreparationResult> PrepareAmendAsync(HumanInputAmendPreparationInput input, CancellationToken cancellationToken = default);
}
