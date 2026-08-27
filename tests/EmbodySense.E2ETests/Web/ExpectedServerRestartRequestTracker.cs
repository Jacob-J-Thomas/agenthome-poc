using System.Collections.Concurrent;

namespace EmbodySense.E2ETests.Web;

internal sealed class ExpectedServerRestartRequestTracker
{
    private const int MaxTrackedSameAuthorityRequests = 1024;
    private readonly string _targetAuthority;
    private readonly ConcurrentDictionary<string, byte> _expectedServerRestartRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _capturedExpectedServerRestartRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _expectedServerRestart;

    public ExpectedServerRestartRequestTracker(string targetAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAuthority);
        _targetAuthority = targetAuthority;
    }

    public string TargetAuthority => _targetAuthority;

    public void BeginExpectedServerRestart()
    {
        lock (_gate)
        {
            _expectedServerRestartRequests.Clear();
            _capturedExpectedServerRestartRequests.Clear();
            Interlocked.Exchange(ref _expectedServerRestart, 1);
            foreach (var request in _requestUrls
                .ToArray()
                .OrderBy(request => request.Key, StringComparer.Ordinal)
                .Take(MaxTrackedSameAuthorityRequests))
            {
                _capturedExpectedServerRestartRequests.TryAdd(request.Key, 0);
            }
        }
    }

    public void MarkExpectedReplacementServerStarting()
    {
        lock (_gate)
        {
            Interlocked.CompareExchange(ref _expectedServerRestart, 2, 1);
        }
    }

    public void EndExpectedServerRestart()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _expectedServerRestart, 0);
        }
    }

    public void Track(string requestId, string url)
    {
        lock (_gate)
        {
            if (!IsTargetAuthority(url))
            {
                RemoveUnderLock(requestId);
                return;
            }

            _requestUrls[requestId] = url;
            TrimUnderLock();
            if (_requestUrls.ContainsKey(requestId)
                && Volatile.Read(ref _expectedServerRestart) == 1
                && ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartUrl(url, _targetAuthority))
            {
                _expectedServerRestartRequests.TryAdd(requestId, 0);
            }
        }
    }

    public void Complete(string requestId)
    {
        lock (_gate)
        {
            RemoveUnderLock(requestId);
        }
    }

    public bool ProcessLoadingFailed(string? requestId, bool canceled, string? errorText)
    {
        lock (_gate)
        {
            if (requestId is null)
            {
                return canceled;
            }

            if (!_requestUrls.TryGetValue(requestId, out var requestUrl))
            {
                return canceled;
            }

            var beganDuringOutage = _expectedServerRestartRequests.ContainsKey(requestId);
            var capturedAtRestart = _capturedExpectedServerRestartRequests.ContainsKey(requestId);
            RemoveUnderLock(requestId);
            if (canceled)
            {
                return true;
            }

            if (Volatile.Read(ref _expectedServerRestart) == 0 && !beganDuringOutage && !capturedAtRestart
                || !capturedAtRestart && !ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartUrl(requestUrl, _targetAuthority))
            {
                return false;
            }

            return ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(
                Volatile.Read(ref _expectedServerRestart) != 0,
                beganDuringOutage,
                requestUrl,
                errorText,
                _targetAuthority,
                capturedAtRestart);
        }
    }

    public (bool ExpectedServerRestart, bool BeganDuringOutage, bool CapturedAtRestart, string? CorrelatedRequestUrl) ReadLogContext(string? requestId)
    {
        lock (_gate)
        {
            var beganDuringOutage = requestId is not null && _expectedServerRestartRequests.ContainsKey(requestId);
            var capturedAtRestart = requestId is not null && _capturedExpectedServerRestartRequests.ContainsKey(requestId);
            var correlatedRequestUrl = requestId is not null && _requestUrls.TryGetValue(requestId, out var requestUrl)
                ? requestUrl
                : null;
            return (Volatile.Read(ref _expectedServerRestart) != 0, beganDuringOutage, capturedAtRestart, correlatedRequestUrl);
        }
    }

    public bool IsExpectedServerRestartLogEntry(string? requestId, string? source, string? text, string? url)
    {
        lock (_gate)
        {
            var beganDuringOutage = requestId is not null && _expectedServerRestartRequests.ContainsKey(requestId);
            var capturedAtRestart = requestId is not null && _capturedExpectedServerRestartRequests.ContainsKey(requestId);
            var correlatedRequestUrl = requestId is not null && _requestUrls.TryGetValue(requestId, out var requestUrl)
                ? requestUrl
                : null;
            var expectedServerRestart = Volatile.Read(ref _expectedServerRestart) != 0;
            if (!expectedServerRestart && !beganDuringOutage && !capturedAtRestart)
            {
                return false;
            }

            if (!string.Equals(source, "network", StringComparison.Ordinal)
                || !ContainsTargetAuthority(text) && !ContainsTargetAuthority(url) && !ContainsTargetAuthority(correlatedRequestUrl))
            {
                return false;
            }

            var expected = ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(
                expectedServerRestart,
                beganDuringOutage,
                source,
                text,
                url,
                correlatedRequestUrl,
                _targetAuthority,
                capturedAtRestart);
            if (expected && requestId is not null)
            {
                _expectedServerRestartRequests.TryRemove(requestId, out _);
            }

            return expected;
        }
    }

    public bool IsExpectedServerRestart()
    {
        lock (_gate)
        {
            return Volatile.Read(ref _expectedServerRestart) != 0;
        }
    }

    internal void ExecuteAtomicallyForTest(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            action();
        }
    }

    private bool IsTargetAuthority(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, _targetAuthority, StringComparison.OrdinalIgnoreCase);
    }

    private bool ContainsTargetAuthority(string? value)
    {
        return value?.Contains(_targetAuthority, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void TrimUnderLock()
    {
        var requestIds = _requestUrls
            .OrderBy(request => request.Key, StringComparer.Ordinal)
            .Skip(MaxTrackedSameAuthorityRequests)
            .Select(request => request.Key)
            .ToArray();
        foreach (var requestId in requestIds)
        {
            RemoveUnderLock(requestId);
        }
    }

    private void RemoveUnderLock(string requestId)
    {
        _requestUrls.TryRemove(requestId, out _);
        _expectedServerRestartRequests.TryRemove(requestId, out _);
        _capturedExpectedServerRestartRequests.TryRemove(requestId, out _);
    }
}
