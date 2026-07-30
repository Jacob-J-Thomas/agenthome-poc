using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Runtime;
using System.Collections.Concurrent;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.State;

/// <summary>
/// Owns the ordered in-memory conversation projection and serializes turns against optional workspace-wide ownership.
/// </summary>
/// <remarks>
/// Startup context is retained separately from the mutable transcript. Durable synchronization must either replace the transcript
/// under exclusive ownership or prove that the in-memory transcript is a prefix before extending it.
/// </remarks>
public sealed class ConversationRuntimeState
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceExclusiveAccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly IResettableInferenceClient? _resettableInferenceClient;
    private readonly List<RuntimeContextMessage> _messages;
    private readonly object _messagesSync = new();
    private readonly SemaphoreSlim _exclusiveAccess;
    private readonly IConversationWorkspaceLease? _workspaceLease;
    private string? _durableConversationVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationRuntimeState"/> type.
    /// </summary>
    /// <param name="initialMessages">The initial messages.</param>
    /// <param name="resettableInferenceClient">The resettable inference client.</param>
    /// <param name="exclusiveAccessScope">The exclusive access scope.</param>
    /// <param name="workspaceLease">The workspace lease.</param>
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
            : _workspaceExclusiveAccess.GetOrAdd(exclusiveAccessScope.Trim(), _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Gets an immutable snapshot of model messages.
    /// </summary>
    /// <value>The LLM messages.</value>
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

    /// <summary>
    /// Gets an immutable snapshot of messages with runtime provenance.
    /// </summary>
    /// <value>The runtime context messages.</value>
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

    /// <summary>
    /// Gets the durable conversation version synchronized into this runtime projection.
    /// </summary>
    /// <value>The durable conversation version.</value>
    public string? DurableConversationVersion
    {
        get
        {
            lock (_messagesSync)
            {
                return _durableConversationVersion;
            }
        }
    }

    /// <summary>
    /// Acquires both in-process conversation ownership and, when configured, the cross-process workspace lease.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A lease that releases workspace ownership before in-process ownership.</returns>
    public async Task<IDisposable> AcquireExclusiveAccessAsync(CancellationToken cancellationToken = default)
    {
        await _exclusiveAccess.WaitAsync(cancellationToken);
        try
        {
            // Acquire cross-process ownership only after the in-process semaphore. Every caller uses
            // this order, avoiding lease inversion while keeping one durable transcript writer.
            var workspaceLease = _workspaceLease is null ? null : await _workspaceLease.AcquireAsync(cancellationToken);
            return new ExclusiveAccessLease(_exclusiveAccess, workspaceLease);
        }
        catch
        {
            _exclusiveAccess.Release();
            throw;
        }
    }

    /// <summary>
    /// Appends a message to the in-memory projection with source provenance.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="source">The source.</param>
    public void AppendMessage(LlmMessage message, RuntimeContextSource source = RuntimeContextSource.SessionTranscript)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_messagesSync)
        {
            _messages.Add(CreateContextMessage(message, source));
        }
    }

    /// <summary>
    /// Replaces the projection and resets any inference client that retains provider conversation state.
    /// </summary>
    /// <param name="messages">The messages.</param>
    /// <param name="startupContextCount">The startup context count.</param>
    /// <param name="remainingSource">The remaining source.</param>
    /// <param name="remainingDetail">The remaining detail.</param>
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

    /// <summary>
    /// Records the version of durable conversation content represented in memory.
    /// </summary>
    /// <param name="version">The version.</param>
    public void SetDurableConversationVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_messagesSync)
        {
            _durableConversationVersion = version;
        }
    }

    /// <summary>
    /// Replaces non-startup messages with the authoritative durable transcript.
    /// </summary>
    /// <param name="transcript">The transcript.</param>
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

    /// <summary>
    /// Extends the projection from a durable transcript only when the in-memory transcript is its prefix.
    /// </summary>
    /// <param name="transcript">The transcript.</param>
    /// <returns><see langword="true"/> when the transcript already matched or was safely extended; otherwise, <see langword="false"/>.</returns>
    public bool TrySynchronizeConversationTranscript(IReadOnlyList<LlmMessage> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var changed = false;
        lock (_messagesSync)
        {
            var currentTranscript = _messages.Where(message => message.Source != RuntimeContextSource.StartupContext).Select(message => message.Message).ToArray();
            if (currentTranscript.Length > 0 && !IsPrefix(currentTranscript, transcript))
            {
                return false;
            }

            if (MessagesEqual(currentTranscript, transcript))
            {
                return true;
            }

            _messages.RemoveAll(message => message.Source != RuntimeContextSource.StartupContext);
            _messages.AddRange(transcript.Select(message => CreateContextMessage(message, RuntimeContextSource.RestoredConversationHistory, "Synchronized from the durable workspace conversation before turn context assembly.")));
            changed = true;
        }

        if (changed)
        {
            _resettableInferenceClient?.ResetConversation();
        }

        return true;
    }

    private static bool MessagesEqual(IReadOnlyList<LlmMessage> left, IReadOnlyList<LlmMessage> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
    }

    private static bool IsPrefix(IReadOnlyList<LlmMessage> prefix, IReadOnlyList<LlmMessage> messages)
    {
        return prefix.Count <= messages.Count && prefix.Zip(messages).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
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

}
