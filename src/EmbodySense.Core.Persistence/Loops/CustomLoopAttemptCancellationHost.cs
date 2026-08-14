using EmbodySense.Core.Common.Loops.Custom;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Owns authenticated local and cross-process routing for cancellation of the active provider attempt.
/// </summary>
/// <remarks>
/// The host publishes a version-1 owner descriptor containing a random secret and bounded named-pipe endpoint. Remote requests
/// authenticate the owner, run, and operation binding; acknowledgements are bounded by time and frame size. Signaling a token
/// is not reported as provider interruption until the registered attempt confirms that outcome.
/// </remarks>
internal sealed class CustomLoopAttemptCancellationHost : IDisposable
{
    private static readonly TimeSpan _acknowledgementTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _connectionIoTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions _wireJsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxWireUtf8Bytes = 4 * 1024;

    private readonly WorkspacePaths _paths;
    private readonly string _ownerId;
    private readonly string _encodedSecret;
    private readonly byte[] _secret;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _server;
    private readonly Dictionary<string, ActiveAttempt> _activeAttempts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _attemptGeneration;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopAttemptCancellationHost"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="workspaceKey">The workspace key.</param>
    public CustomLoopAttemptCancellationHost(WorkspacePaths paths, string workspaceKey)
    {
        _paths = paths;
        _ownerId = "owner-" + Guid.NewGuid().ToString("N");
        _secret = RandomNumberGenerator.GetBytes(32);
        _encodedSecret = Convert.ToBase64String(_secret);
        _pipeName = "es-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceKey + "\n" + _ownerId))).ToLowerInvariant()[..16];
        WriteOwnerDescriptor();
        _server = Task.Run(RunServerAsync);
    }

    /// <summary>
    /// Registers the single active provider attempt for a run and returns its generation-bound lifetime handle.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellation">The cancellation.</param>
    /// <param name="competingCancellationToken">The competing cancellation token.</param>
    /// <returns>The custom loop attempt cancellation registration.</returns>
    public ICustomLoopAttemptCancellationRegistration RegisterActiveAttempt(string runId, CancellationTokenSource cancellation, CancellationToken competingCancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_activeAttempts.ContainsKey(runId))
            {
                throw new InvalidOperationException("An active provider attempt is already registered for this run in the workspace host.");
            }

            var attempt = new ActiveAttempt(cancellation, competingCancellationToken, ++_attemptGeneration);
            _activeAttempts.Add(runId, attempt);
            return new ActiveAttemptRegistration(this, runId, attempt);
        }
    }

    /// <summary>
    /// Signals a locally registered attempt and waits for a bounded confirmation of provider interruption.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop attempt cancellation result.</returns>
    public async Task<CustomLoopAttemptCancellationResult> RequestCancellationAsync(string runId, CancellationToken cancellationToken)
    {
        ActiveAttempt? attempt;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return Owned(CustomLoopAttemptCancellationStatus.OwnerUnavailable, "The workspace-host owner exited before cancellation could be routed.");
            }

            _activeAttempts.TryGetValue(runId, out attempt);
        }

        if (attempt is null)
        {
            return Owned(CustomLoopAttemptCancellationStatus.NoActiveAttempt, "The workspace-host owner has no active provider attempt for this run.");
        }

        attempt.Signal();

        try
        {
            var result = await attempt.Completion.Task.WaitAsync(_acknowledgementTimeout, cancellationToken).ConfigureAwait(false);
            return result with { OwnerId = _ownerId, OwnerProcessId = Environment.ProcessId };
        }
        catch (TimeoutException)
        {
            var result = attempt.CreateUnconfirmedResult();
            return result with { OwnerId = _ownerId, OwnerProcessId = Environment.ProcessId };
        }
    }

    /// <summary>
    /// Authenticates and sends a bounded cancellation request to the process that owns workspace hosting.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="runId">The run ID.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop attempt cancellation result.</returns>
    public static async Task<CustomLoopAttemptCancellationResult> RequestRemoteCancellationAsync(WorkspacePaths paths, string runId, string operationId, CancellationToken cancellationToken)
    {
        CancellationOwnerDescriptor descriptor;
        try
        {
            descriptor = await ReadOwnerDescriptorAsync(paths, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.OwnerUnavailable, $"The workspace-host owner descriptor could not be read safely: {exception.GetType().Name}.");
        }

        var request = new CancellationWireRequest(1, descriptor.OwnerId, runId, operationId, ComputeAuthenticationTag(descriptor.Secret, descriptor.OwnerId, runId, operationId));
        try
        {
            using var client = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_acknowledgementTimeout + TimeSpan.FromSeconds(1));
            await client.ConnectAsync(timeout.Token);
            await WriteFrameAsync(client, request, timeout.Token);
            var response = await ReadFrameAsync<CancellationWireResponse>(client, timeout.Token);
            if (response.SchemaVersion != 1 || !Enum.IsDefined(response.Status) || response.Status == CustomLoopAttemptCancellationStatus.Unknown || string.IsNullOrWhiteSpace(response.Detail))
            {
                return new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.Invalid, "The workspace-host owner returned an invalid cancellation acknowledgement.");
            }

            if (!string.Equals(response.OwnerId, descriptor.OwnerId, StringComparison.Ordinal) || response.OwnerProcessId != descriptor.ProcessId)
            {
                return new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.Invalid, "The cancellation acknowledgement did not match the authenticated workspace-host generation.");
            }

            return new CustomLoopAttemptCancellationResult(response.Status, response.Detail, response.OwnerId, response.OwnerProcessId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or ArgumentException)
        {
            return new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.OwnerUnavailable, $"The workspace-host owner could not be reached within the bounded cancellation window: {exception.GetType().Name}.", descriptor.OwnerId, descriptor.ProcessId);
        }
    }

    /// <summary>
    /// Completes and removes the exact generation of a registered attempt.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="generation">The generation.</param>
    /// <param name="interrupted">Whether the provider confirmed interruption caused by the routed cancellation.</param>
    public void CompleteAttempt(string runId, long generation, bool interrupted)
    {
        ActiveAttempt? attempt = null;
        lock (_gate)
        {
            if (_activeAttempts.TryGetValue(runId, out var current) && current.Generation == generation)
            {
                attempt = current;
                _activeAttempts.Remove(runId);
            }
        }

        if (attempt is null)
        {
            return;
        }

        if (interrupted)
        {
            attempt.ConfirmProviderInterruption();
        }
        else
        {
            attempt.CompleteWithoutConfirmedInterruption();
        }
    }

    /// <summary>
    /// Stops the cancellation endpoint and completes all registered attempts as owner unavailable.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ActiveAttempt[] attempts;
        lock (_gate)
        {
            attempts = _activeAttempts.Values.ToArray();
            _activeAttempts.Clear();
        }

        foreach (var attempt in attempts)
        {
            attempt.CompleteOwnerUnavailable();
        }

        _shutdown.Cancel();
        DeleteOwnerDescriptor();
        _ = _server.ContinueWith(
            _ =>
            {
                _shutdown.Dispose();
                CryptographicOperations.ZeroMemory(_secret);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunServerAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options);
                await server.WaitForConnectionAsync(_shutdown.Token);
                await HandleConnectionAsync(server, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // An incomplete client frame or blocked response is abandoned at its bounded I/O deadline.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                // A malformed or disconnected caller cannot terminate the bounded owner broker.
            }
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(_connectionIoTimeout);
        var request = await ReadFrameAsync<CancellationWireRequest>(stream, readTimeout.Token);
        var result = !IsAuthenticated(request)
            ? new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.Invalid, "The cancellation request did not authenticate to the current workspace-host generation.")
            : await RequestCancellationAsync(request.RunId, cancellationToken);
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeTimeout.CancelAfter(_connectionIoTimeout);
        await WriteFrameAsync(stream, new CancellationWireResponse(1, result.Status, result.Detail, _ownerId, Environment.ProcessId), writeTimeout.Token);
    }

    private CustomLoopAttemptCancellationResult Owned(CustomLoopAttemptCancellationStatus status, string detail)
    {
        return new CustomLoopAttemptCancellationResult(status, detail, _ownerId, Environment.ProcessId);
    }

    private bool IsAuthenticated(CancellationWireRequest request)
    {
        if (request.SchemaVersion != 1
            || !string.Equals(request.OwnerId, _ownerId, StringComparison.Ordinal)
            || !CustomLoopArtifactIdentifier.IsValid(request.RunId)
            || !CustomLoopArtifactIdentifier.IsValid(request.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            || request.AuthenticationTag is not { Length: 64 })
        {
            return false;
        }

        var expected = ComputeAuthenticationTag(_encodedSecret, _ownerId, request.RunId, request.OperationId);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(request.AuthenticationTag));
    }

    private void WriteOwnerDescriptor()
    {
        var pathGuard = new CustomLoopArtifactPathGuard(_paths.RootPath);
        pathGuard.PrepareRoot(_paths.LoopRunsPath);
        var path = pathGuard.GetFilePath(_paths.LoopRunsPath, Path.GetFileName(_paths.CustomLoopCancellationOwnerPath));
        var descriptor = new CancellationOwnerDescriptor(1, _ownerId, _pipeName, _encodedSecret, Environment.ProcessId, DateTimeOffset.UtcNow.ToUniversalTime());
        var payload = JsonSerializer.SerializeToUtf8Bytes(descriptor, _wireJsonOptions);
        if (payload.Length > MaxWireUtf8Bytes)
        {
            throw new FormatException("The workspace-host owner descriptor exceeds its bounded size.");
        }

        var tempPath = pathGuard.GetFilePath(_paths.LoopRunsPath, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(tempPath, options))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private void DeleteOwnerDescriptor()
    {
        try
        {
            var pathGuard = new CustomLoopArtifactPathGuard(_paths.RootPath);
            var path = pathGuard.GetFilePath(_paths.LoopRunsPath, Path.GetFileName(_paths.CustomLoopCancellationOwnerPath));
            if (!File.Exists(path))
            {
                return;
            }

            var payload = File.ReadAllBytes(path);
            var descriptor = payload.Length <= MaxWireUtf8Bytes ? JsonSerializer.Deserialize<CancellationOwnerDescriptor>(payload, _wireJsonOptions) : null;
            if (string.Equals(descriptor?.OwnerId, _ownerId, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // A stale descriptor is generation-bound and cannot authenticate after this secret is destroyed.
        }
    }

    private static async Task<CancellationOwnerDescriptor> ReadOwnerDescriptorAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        var pathGuard = new CustomLoopArtifactPathGuard(paths.RootPath);
        var path = pathGuard.GetFilePath(paths.LoopRunsPath, Path.GetFileName(paths.CustomLoopCancellationOwnerPath));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaxWireUtf8Bytes)
        {
            throw new FormatException("The workspace-host owner descriptor is outside its bounded size.");
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var descriptor = JsonSerializer.Deserialize<CancellationOwnerDescriptor>(bytes, _wireJsonOptions) ?? throw new FormatException("The workspace-host owner descriptor is empty.");
        ValidateOwnerDescriptor(descriptor);
        return descriptor;
    }

    private static void ValidateOwnerDescriptor(CancellationOwnerDescriptor descriptor)
    {
        if (descriptor.SchemaVersion != 1
            || !CustomLoopArtifactIdentifier.IsValid(descriptor.OwnerId)
            || descriptor.ProcessId <= 0
            || string.IsNullOrWhiteSpace(descriptor.PipeName)
            || descriptor.PipeName.Length > 120
            || descriptor.PipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
            || !HasValidSecret(descriptor.Secret)
            || descriptor.StartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("The workspace-host owner descriptor is invalid.");
        }
    }

    private static string ComputeAuthenticationTag(string encodedSecret, string ownerId, string runId, string operationId)
    {
        if (!TryDecodeSecret(encodedSecret, out var secret))
        {
            throw new FormatException("The workspace-host cancellation secret is invalid.");
        }

        var content = Encoding.UTF8.GetBytes($"{ownerId}\n{runId}\n{operationId}");
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(secret, content)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static bool HasValidSecret(string encodedSecret)
    {
        if (!TryDecodeSecret(encodedSecret, out var secret))
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(secret);
        return true;
    }

    private static bool TryDecodeSecret(string encodedSecret, out byte[] secret)
    {
        try
        {
            secret = Convert.FromBase64String(encodedSecret);
            return secret.Length == 32;
        }
        catch (FormatException)
        {
            secret = [];
            return false;
        }
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, _wireJsonOptions);
        if (payload.Length <= 0 || payload.Length > MaxWireUtf8Bytes)
        {
            throw new FormatException("The cancellation IPC payload is outside its bounded size.");
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, cancellationToken);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (payloadLength <= 0 || payloadLength > MaxWireUtf8Bytes)
        {
            throw new FormatException("The cancellation IPC payload length is invalid.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, _wireJsonOptions) ?? throw new FormatException("The cancellation IPC payload is empty.");
    }

    private sealed record CancellationOwnerDescriptor(int SchemaVersion, string OwnerId, string PipeName, string Secret, int ProcessId, DateTimeOffset StartedAtUtc);

    private sealed record CancellationWireRequest(int SchemaVersion, string OwnerId, string RunId, string OperationId, string? AuthenticationTag);

    private sealed record CancellationWireResponse(int SchemaVersion, CustomLoopAttemptCancellationStatus Status, string Detail, string OwnerId, int OwnerProcessId);
}
