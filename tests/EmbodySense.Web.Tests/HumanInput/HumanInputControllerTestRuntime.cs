using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

internal sealed class HumanInputControllerTestRuntime : IWebHumanInputRuntime
{
    internal HumanInputRequestPosturePage? PageResponse { get; set; } = new(HumanInputRequestPosturePageStatus.Ready, 1, [], null);

    internal HumanInputRequestPostureReadResult? ReadResponse { get; set; } = new(HumanInputRequestPostureReadStatus.NotFound, 1, null);

    internal HumanInputOperationResult? OperationResponse { get; set; } = new(HumanInputOperationStatus.NotFound, "operation", null, null, []);

    internal HumanInputSupersedePreparationResult? PreparationResponse { get; set; } = new(HumanInputSupersedePreparationStatus.NotFound, "request-1", null, null, "request_not_found");

    internal HumanInputReroutePreparationResult? ReroutePreparationResponse { get; set; } = new(HumanInputSupersedePreparationStatus.NotFound, "request-1", [], null, "request_not_found");

    internal HumanInputAmendPreparationResult? AmendPreparationResponse { get; set; } = new(HumanInputSupersedePreparationStatus.NotFound, "request-1", null, null, "request_not_found");

    internal Exception? ListException { get; set; }

    internal Exception? ReadException { get; set; }

    internal Exception? OperationException { get; set; }

    internal Exception? PreparationException { get; set; }

    internal HumanInputRequestPosturePageRequest? LastPageRequest { get; private set; }

    internal string? LastReadRequestId { get; private set; }

    internal HumanInputSurfaceLifecycleOperationInput? LastLifecycleInput { get; private set; }

    internal HumanInputSurfaceResponseOperationInput? LastResponseInput { get; private set; }

    internal HumanInputSupersedePreparationInput? LastPreparationInput { get; private set; }

    internal HumanInputReroutePreparationInput? LastReroutePreparationInput { get; private set; }

    internal HumanInputAmendPreparationInput? LastAmendPreparationInput { get; private set; }

    public Task<HumanInputRequestPosturePage> ListAsync(HumanInputRequestPosturePageRequest request, CancellationToken cancellationToken = default)
    {
        if (ListException is not null)
        {
            throw ListException;
        }

        LastPageRequest = request;
        return Task.FromResult(PageResponse!);
    }

    public Task<HumanInputRequestPostureReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (ReadException is not null)
        {
            throw ReadException;
        }

        LastReadRequestId = requestId;
        return Task.FromResult(ReadResponse!);
    }

    public Task<HumanInputOperationResult> SubmitLifecycleAsync(HumanInputSurfaceLifecycleOperationInput input, CancellationToken cancellationToken = default)
    {
        if (OperationException is not null)
        {
            throw OperationException;
        }

        LastLifecycleInput = input;
        return Task.FromResult(OperationResponse!);
    }

    public Task<HumanInputOperationResult> SubmitResponseAsync(HumanInputSurfaceResponseOperationInput input, CancellationToken cancellationToken = default)
    {
        if (OperationException is not null)
        {
            throw OperationException;
        }

        LastResponseInput = input;
        return Task.FromResult(OperationResponse!);
    }

    public Task<HumanInputSupersedePreparationResult> PrepareSupersedeAsync(HumanInputSupersedePreparationInput input, CancellationToken cancellationToken = default)
    {
        if (PreparationException is not null)
        {
            throw PreparationException;
        }

        LastPreparationInput = input;
        return Task.FromResult(PreparationResponse!);
    }

    public Task<HumanInputReroutePreparationResult> PrepareRerouteAsync(HumanInputReroutePreparationInput input, CancellationToken cancellationToken = default)
    {
        if (PreparationException is not null)
        {
            throw PreparationException;
        }

        LastReroutePreparationInput = input;
        return Task.FromResult(ReroutePreparationResponse!);
    }

    public Task<HumanInputAmendPreparationResult> PrepareAmendAsync(HumanInputAmendPreparationInput input, CancellationToken cancellationToken = default)
    {
        if (PreparationException is not null)
        {
            throw PreparationException;
        }

        LastAmendPreparationInput = input;
        return Task.FromResult(AmendPreparationResponse!);
    }
}
