using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web;

public sealed class WebAgentRuntimeHost : IAsyncDisposable, IWebLoopRuntimeInvoker
{
    private readonly WebRunOptions _options;
    private readonly WebApprovalCoordinator _approvalCoordinator;
    private readonly IWorkspaceInitializer _workspaceInitializer;
    private readonly WorkspaceStatusReader _statusReader;
    private readonly WorkspaceConfigurationReader _configurationReader;
    private readonly LoopRunInspectionFacade _loopRuns;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _turnCancellationGate = new();
    private readonly CancellationTokenSource _hostLifetimeCancellation = new();
    private CancellationTokenSource? _turnCancellation;
    private AgentRuntime? _runtime;
    private int _disposed;

    public WebAgentRuntimeHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator)
        : this(options, approvalCoordinator, WorkspaceInitializer.ForWeb())
    {
    }

    public WebAgentRuntimeHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator, IWorkspaceInitializer workspaceInitializer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(approvalCoordinator);
        ArgumentNullException.ThrowIfNull(workspaceInitializer);

        _options = options;
        _approvalCoordinator = approvalCoordinator;
        _workspaceInitializer = workspaceInitializer;
        _statusReader = new WorkspaceStatusReader();
        _configurationReader = new WorkspaceConfigurationReader();
        _loopRuns = new LoopRunInspectionFacade(options.WorkingDirectory, WorkspaceActors.Web, AgentRuntimeSurface.Web.Id);
    }

    public WebStatus GetStatus()
    {
        return WebStatusFactory.Create(_options, _statusReader.Read(_options.WorkingDirectory));
    }

    public LoopRunModelSnapshot GetCustomLoopModel()
    {
        return new LoopRunModelSnapshot("OpenAiCodex", string.IsNullOrWhiteSpace(_options.Model) ? null : _options.Model);
    }

    public async Task<WebStatus> InitializeWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var status = _statusReader.Read(_options.WorkingDirectory);
        if (!status.IsInitialized)
        {
            await _workspaceInitializer.InitializeAsync(status.RootPath, cancellationToken);
        }

        return GetStatus();
    }

    public async Task<WorkspaceConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return await _configurationReader.ReadAsync(_options.WorkingDirectory, CreateRuntimeConfiguration(), cancellationToken);
    }

    public async Task<IReadOnlyList<LoopRunSummarySnapshot>> GetLoopRunsAsync(int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop run evidence");
        return await _loopRuns.ListRecentAsync(maximumCount, cancellationToken);
    }

    public async Task<LoopRunSnapshot?> GetLoopRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop run evidence");
        return await _loopRuns.GetAsync(runId, cancellationToken);
    }

    public async Task<LoopTraceInspectionSnapshot?> GetLoopTraceAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop trace evidence");
        return await _loopRuns.GetTraceAsync(runId, cancellationToken);
    }

    public async Task<LoopTraceQuotaSnapshot> GetLoopTraceQuotaAsync(CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop trace quota");
        return await _loopRuns.GetTraceQuotaAsync(cancellationToken);
    }

    public async Task<LoopTraceDeletionResponse> DeleteLoopTraceAsync(string runId, string expectedTraceHash, string operationId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("deleting custom-loop trace content");
        return await _loopRuns.DeleteTraceAsync(runId, expectedTraceHash, operationId, cancellationToken);
    }

    public async Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var runtime = await GetRuntimeAsync(executionCancellation.Token);
        return await runtime.InvokeCustomLoopAsync(input, executionCancellation.Token);
    }

    public async Task<LoopRunControlResponse> PauseLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtime = await GetRuntimeAsync(cancellationToken);
        return await runtime.PauseCustomLoopAsync(input, cancellationToken);
    }

    public async Task<LoopRunControlResponse> CancelLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtime = await GetRuntimeAsync(cancellationToken);
        return await runtime.CancelCustomLoopAsync(input, cancellationToken);
    }

    public async Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var runtime = await GetRuntimeAsync(executionCancellation.Token);
        return await runtime.ResumeCustomLoopAsync(input, executionCancellation.Token);
    }

    public async Task SendMessageAsync(
        string message,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        string? ownerConnectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(writeEventAsync);

        await _turnGate.WaitAsync(cancellationToken);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var discardRuntime = false;
        SetTurnCancellation(turnCancellation);
        try
        {
            if (AgentRuntime.TryHandleStaticRuntimeCommand(message, out var staticCommandResult))
            {
                await WriteTurnResultAsync(staticCommandResult, writeEventAsync, cancellationToken);
                return;
            }

            var runtime = await GetRuntimeAsync(turnCancellation.Token);
            var turnResult = await runtime.RunTurnAsync(
                message,
                (chunk, token) =>
                {
                    return string.IsNullOrEmpty(chunk)
                        ? Task.CompletedTask
                        : writeEventAsync(WebStreamEvent.AssistantDelta(chunk), token);
                },
                (context, token) => writeEventAsync(WebStreamEvent.VerboseContext(context), token),
                cancellationToken: turnCancellation.Token);

            discardRuntime = turnResult.IsCancelled;
            await WriteTurnResultAsync(turnResult, writeEventAsync, cancellationToken);
        }
        catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            discardRuntime = true;
            await writeEventAsync(WebStreamEvent.Cancelled("Message cancelled."), cancellationToken);
        }
        finally
        {
            ClearTurnCancellation(turnCancellation);
            if (discardRuntime)
            {
                await DiscardRuntimeAsync();
            }

            _turnGate.Release();
        }
    }

    public async Task SetVerboseModeAsync(
        bool enabled,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeEventAsync);

        var runtime = await GetRuntimeAsync(cancellationToken);
        var result = runtime.SetVerbose(enabled);
        await writeEventAsync(WebStreamEvent.System(result.Output), cancellationToken);
    }

    public bool CancelCurrentTurn()
    {
        lock (_turnCancellationGate)
        {
            if (_turnCancellation is null || _turnCancellation.IsCancellationRequested)
            {
                return false;
            }

            _turnCancellation.Cancel();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _hostLifetimeCancellation.Cancel();
        await DiscardRuntimeAsync();

        _runtimeGate.Dispose();
        _turnGate.Dispose();
        _hostLifetimeCancellation.Dispose();
    }

    private async Task<AgentRuntime> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        EnsureWorkspaceInitialized("starting a runtime session");

        if (_runtime is not null)
        {
            return _runtime;
        }

        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            _runtime ??= await new AgentRuntimeFactory(_approvalCoordinator).CreateAsync(
                _options.Model,
                _options.WorkingDirectory,
                _options.CodexExecutablePath,
                _options.CodexSandbox,
                AgentRuntimeSurface.Web,
                cancellationToken);
            return _runtime;
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private void EnsureWorkspaceInitialized(string operation)
    {
        if (!_statusReader.Read(_options.WorkingDirectory).IsInitialized)
        {
            throw new InvalidOperationException($"Workspace is not initialized. Initialize it from the web client before {operation}.");
        }
    }

    private async Task DiscardRuntimeAsync()
    {
        AgentRuntime? runtime;
        await _runtimeGate.WaitAsync(CancellationToken.None);
        try
        {
            runtime = _runtime;
            _runtime = null;
        }
        finally
        {
            _runtimeGate.Release();
        }

        if (runtime is not null)
        {
            await runtime.DisposeAsync();
        }
    }

    private WorkspaceRuntimeConfiguration CreateRuntimeConfiguration()
    {
        var model = string.IsNullOrWhiteSpace(_options.Model) ? "configured externally" : _options.Model;
        var codexPath = string.IsNullOrWhiteSpace(_options.CodexExecutablePath) ? "codex from PATH" : _options.CodexExecutablePath;
        return new WorkspaceRuntimeConfiguration(
            AgentRuntimeSurface.Web.Id,
            _options.Url,
            model,
            codexPath,
            _options.CodexSandbox,
            "Localhost web client is the primary browser surface; CLI remains available for verification.");
    }

    private static async Task WriteTurnResultAsync(
        AgentRuntimeTurnResult result,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        CancellationToken cancellationToken)
    {
        var commandOutputParts = new List<string>();
        foreach (var turnEvent in result.Events)
        {
            switch (turnEvent.Kind)
            {
                case AgentRuntimeTurnEventKind.TranscriptReplacement:
                    var messages = turnEvent.TranscriptMessages.Select(message => new WebTranscriptMessage(message.Role, message.Content)).ToArray();
                    await writeEventAsync(WebStreamEvent.HistoryLoaded(messages), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.CommandOutput:
                case AgentRuntimeTurnEventKind.Prompt:
                    commandOutputParts.Add(turnEvent.Text);
                    break;

                case AgentRuntimeTurnEventKind.AssistantMessage:
                    await writeEventAsync(WebStreamEvent.AssistantFinal(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.Failure:
                    await writeEventAsync(WebStreamEvent.Failure(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.Cancellation:
                    await writeEventAsync(WebStreamEvent.Cancelled(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.ExitRequested:
                    commandOutputParts.Add("The web client is still connected. Close the browser tab or stop the web server to leave.");
                    break;
            }
        }

        var output = string.Join(Environment.NewLine, commandOutputParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(output))
        {
            await writeEventAsync(WebStreamEvent.AssistantFinal(output), cancellationToken);
        }
    }

    private void SetTurnCancellation(CancellationTokenSource cancellation)
    {
        lock (_turnCancellationGate)
        {
            _turnCancellation = cancellation;
        }
    }

    private void ClearTurnCancellation(CancellationTokenSource cancellation)
    {
        lock (_turnCancellationGate)
        {
            if (ReferenceEquals(_turnCancellation, cancellation))
            {
                _turnCancellation = null;
            }
        }
    }
}
