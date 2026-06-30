using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web;

public sealed class WebAgentRuntimeHost : IAsyncDisposable
{
    private readonly WebRunOptions _options;
    private readonly WebApprovalCoordinator _approvalCoordinator;
    private readonly IWorkspaceInitializer _workspaceInitializer;
    private readonly WorkspaceStatusReader _statusReader;
    private readonly WorkspaceConfigurationReader _configurationReader;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _turnCancellationGate = new();
    private CancellationTokenSource? _turnCancellation;
    private AgentRuntime? _runtime;

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
    }

    public WebStatus GetStatus()
    {
        return WebStatusFactory.Create(_options, _statusReader.Read(_options.WorkingDirectory));
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
        SetTurnCancellation(turnCancellation);
        try
        {
            if (AgentRuntime.TryHandleStaticRuntimeCommand(message, out var staticCommandResult))
            {
                await WriteCommandResultAsync(staticCommandResult, writeEventAsync, turnCancellation.Token);
                return;
            }

            var runtime = await GetRuntimeAsync(turnCancellation.Token);
            var commandResult = await runtime.TryHandleRuntimeCommandAsync(message, turnCancellation.Token);
            if (commandResult.Handled)
            {
                await WriteCommandResultAsync(commandResult, writeEventAsync, turnCancellation.Token);
                return;
            }

            var responseText = await runtime.SendUserMessageAsync(message, (chunk, token) =>
            {
                return string.IsNullOrEmpty(chunk)
                    ? Task.CompletedTask
                    : writeEventAsync(WebStreamEvent.AssistantDelta(chunk), token);
            }, (context, token) => writeEventAsync(WebStreamEvent.VerboseContext(context), token), turnCancellation.Token);
            await writeEventAsync(WebStreamEvent.AssistantFinal(responseText), turnCancellation.Token);
        }
        finally
        {
            ClearTurnCancellation(turnCancellation);
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
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }

        _runtimeGate.Dispose();
        _turnGate.Dispose();
    }

    private async Task<AgentRuntime> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        if (!_statusReader.Read(_options.WorkingDirectory).IsInitialized)
        {
            throw new InvalidOperationException("Workspace is not initialized. Initialize it from the web client before starting a session.");
        }

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
                "web",
                cancellationToken);
            return _runtime;
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private WorkspaceRuntimeConfiguration CreateRuntimeConfiguration()
    {
        var model = string.IsNullOrWhiteSpace(_options.Model) ? "configured externally" : _options.Model;
        var codexPath = string.IsNullOrWhiteSpace(_options.CodexExecutablePath) ? "codex from PATH" : _options.CodexExecutablePath;
        return new WorkspaceRuntimeConfiguration(
            "web",
            _options.Url,
            model,
            codexPath,
            _options.CodexSandbox,
            "Localhost web client is the primary browser surface; CLI remains available for verification.");
    }

    private static async Task WriteCommandResultAsync(
        AgentRuntimeCommandResult result,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        CancellationToken cancellationToken)
    {
        if (result.ReplaceTranscript)
        {
            var messages = result.RestoredMessages.Select(message => new WebTranscriptMessage(message.Role, message.Content)).ToArray();
            await writeEventAsync(WebStreamEvent.HistoryLoaded(messages), cancellationToken);
        }

        var output = result.ExitRequested
            ? "The web client is still connected. Close the browser tab or stop the web server to leave."
            : result.Output;
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
