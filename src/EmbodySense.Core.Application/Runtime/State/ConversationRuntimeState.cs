using System.Collections.Concurrent;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.State;

public sealed class ConversationRuntimeState
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceExclusiveAccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly IResettableInferenceClient? _resettableInferenceClient;
    private readonly List<RuntimeContextMessage> _messages;
    private readonly object _messagesSync = new();
    private readonly SemaphoreSlim _exclusiveAccess;
    private readonly IConversationWorkspaceLease? _workspaceLease;

    public ConversationRuntimeState(
        IReadOnlyList<LlmMessage>? initialMessages = null,
        IResettableInferenceClient? resettableInferenceClient = null,
        string? exclusiveAccessScope = null,
        IConversationWorkspaceLease? workspaceLease = null)
    {
        _resettableInferenceClient = resettableInferenceClient;
        _workspaceLease = workspaceLease;
        _messages = initialMessages?.Select(message => CreateContextMessage(message, RuntimeContextSource.StartupContext)).ToList() ?? [];
        _exclusiveAccess = string.IsNullOrWhiteSpace(exclusiveAccessScope)
            ? new SemaphoreSlim(1, 1)
            : WorkspaceExclusiveAccess.GetOrAdd(exclusiveAccessScope.Trim(), _ => new SemaphoreSlim(1, 1));
    }

    public IReadOnlyList<LlmMessage> Messages
    {
        get
        {
            lock (_messagesSync)
            {
                return _messages.Select(message => message.Message).ToArray();
            }
        }
    }

    public IReadOnlyList<RuntimeContextMessage> ContextMessages
    {
        get
        {
            lock (_messagesSync)
            {
                return _messages.ToArray();
            }
        }
    }

    public async Task<IDisposable> AcquireExclusiveAccessAsync(CancellationToken cancellationToken = default)
    {
        await _exclusiveAccess.WaitAsync(cancellationToken);
        try
        {
            var workspaceLease = _workspaceLease is null ? null : await _workspaceLease.AcquireAsync(cancellationToken);
            return new ExclusiveAccessLease(_exclusiveAccess, workspaceLease);
        }
        catch
        {
            _exclusiveAccess.Release();
            throw;
        }
    }

    public void AppendMessage(LlmMessage message, RuntimeContextSource source = RuntimeContextSource.SessionTranscript)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_messagesSync)
        {
            _messages.Add(CreateContextMessage(message, source));
        }
    }

    public void ReplaceMessages(
        IReadOnlyList<LlmMessage> messages,
        int startupContextCount = 0,
        RuntimeContextSource remainingSource = RuntimeContextSource.SessionTranscript,
        string? remainingDetail = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (startupContextCount < 0 || startupContextCount > messages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startupContextCount), startupContextCount, "Startup context count must fit the replacement message list.");
        }

        lock (_messagesSync)
        {
            _messages.Clear();
            for (var i = 0; i < messages.Count; i++)
            {
                var source = i < startupContextCount ? RuntimeContextSource.StartupContext : remainingSource;
                var detail = i < startupContextCount ? null : remainingDetail;
                _messages.Add(CreateContextMessage(messages[i], source, detail));
            }
        }

        _resettableInferenceClient?.ResetConversation();
    }

    public void SynchronizeConversationTranscript(IReadOnlyList<LlmMessage> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var changed = false;
        lock (_messagesSync)
        {
            var currentTranscript = _messages.Where(message => message.Source != RuntimeContextSource.StartupContext).Select(message => message.Message).ToArray();
            if (currentTranscript.Length == transcript.Count && currentTranscript.Zip(transcript).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal)))
            {
                return;
            }

            _messages.RemoveAll(message => message.Source != RuntimeContextSource.StartupContext);
            _messages.AddRange(transcript.Select(message => CreateContextMessage(message, RuntimeContextSource.RestoredConversationHistory, "Synchronized from the durable workspace conversation before turn context assembly.")));
            changed = true;
        }

        if (changed)
        {
            _resettableInferenceClient?.ResetConversation();
        }
    }

    private static RuntimeContextMessage CreateContextMessage(LlmMessage message, RuntimeContextSource source, string? detail = null)
    {
        return new RuntimeContextMessage(message, source, detail ?? GetDefaultDetail(source));
    }

    private static string GetDefaultDetail(RuntimeContextSource source)
    {
        return source switch
        {
            RuntimeContextSource.StartupContext => "Loaded during runtime bootstrap from workspace and agent context documents.",
            RuntimeContextSource.RestoredConversationHistory => "Restored from conversation history at the user's request.",
            RuntimeContextSource.SessionTranscript => "Accepted during this runtime session and retained in conversation state.",
            RuntimeContextSource.CurrentTurnInput => "Current user input being evaluated by the active loop before provider dispatch.",
            _ => "Context source is not classified."
        };
    }

    private sealed class ExclusiveAccessLease : IDisposable
    {
        private SemaphoreSlim? _gate;
        private IDisposable? _workspaceLease;

        public ExclusiveAccessLease(SemaphoreSlim gate, IDisposable? workspaceLease)
        {
            _gate = gate;
            _workspaceLease = workspaceLease;
        }

        public void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _workspaceLease, null)?.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
