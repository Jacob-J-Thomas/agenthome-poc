using System.Collections.Concurrent;

namespace EmbodySense.E2ETests.Web;

internal sealed class ExpectedServerRestartRequestTracker
{
    private const int MaxTrackedSameAuthorityRequests = 1024;
    private const int Idle = 0;
    private const int Preparing = 1;
    private const int Active = 2;
    private const int ReplacementStarting = 3;
    private readonly string _targetAuthority;
    private readonly ConcurrentDictionary<string, byte> _expectedServerRestartRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _capturedExpectedServerRestartRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RestartRequestCorrelation> _terminalCorrelations = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _expectedServerRestart;
    private long _restartGeneration;

    public ExpectedServerRestartRequestTracker(string targetAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAuthority);
        _targetAuthority = targetAuthority;
    }

    public string TargetAuthority => _targetAuthority;

    public void PrepareExpectedServerRestart()
    {
        lock (_gate)
        {
            _restartGeneration++;
            _expectedServerRestartRequests.Clear();
            _capturedExpectedServerRestartRequests.Clear();
            _terminalCorrelations.Clear();
            Interlocked.Exchange(ref _expectedServerRestart, Preparing);
        }
    }

    public void FreezeExpectedServerRestart()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _expectedServerRestart) != Preparing)
            {
                return;
            }

            _capturedExpectedServerRestartRequests.Clear();
            foreach (var request in _requestUrls
                .ToArray()
                .OrderBy(request => request.Key, StringComparer.Ordinal)
                .Take(MaxTrackedSameAuthorityRequests))
            {
                _capturedExpectedServerRestartRequests.TryAdd(request.Key, 0);
            }

            Interlocked.Exchange(ref _expectedServerRestart, Active);
        }
    }

    public void AbortExpectedServerRestart()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _expectedServerRestart) == Preparing)
            {
                _expectedServerRestartRequests.Clear();
                _capturedExpectedServerRestartRequests.Clear();
                _terminalCorrelations.Clear();
                Interlocked.Exchange(ref _expectedServerRestart, Idle);
            }
        }
    }

    public void BeginExpectedServerRestart()
    {
        PrepareExpectedServerRestart();
        FreezeExpectedServerRestart();
    }

    public void MarkExpectedReplacementServerStarting()
    {
        lock (_gate)
        {
            Interlocked.CompareExchange(ref _expectedServerRestart, ReplacementStarting, Active);
        }
    }

    public void EndExpectedServerRestart()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _expectedServerRestart, Idle);
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
                && Volatile.Read(ref _expectedServerRestart) == Active
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
            if (_terminalCorrelations.TryGetValue(requestId, out var correlation) && correlation.LogObserved)
            {
                _terminalCorrelations.Remove(requestId);
            }
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

            var hasCurrentRequest = _requestUrls.TryGetValue(requestId, out var currentRequestUrl);
            var terminalCorrelation = default(RestartRequestCorrelation);
            var hasTerminalCorrelation = _terminalCorrelations.TryGetValue(requestId, out terminalCorrelation);
            if (!hasCurrentRequest && !hasTerminalCorrelation)
            {
                return canceled;
            }

            var requestUrl = hasCurrentRequest ? currentRequestUrl! : terminalCorrelation.RequestUrl;
            var beganDuringOutage = (hasCurrentRequest && _expectedServerRestartRequests.ContainsKey(requestId))
                || (hasTerminalCorrelation && terminalCorrelation.BeganDuringOutage);
            var capturedAtRestart = (hasCurrentRequest && _capturedExpectedServerRestartRequests.ContainsKey(requestId))
                || (hasTerminalCorrelation && terminalCorrelation.CapturedAtRestart);
            RemoveUnderLock(requestId);
            if (canceled)
            {
                _terminalCorrelations.Remove(requestId);
                return true;
            }

            var expectedServerRestart = Volatile.Read(ref _expectedServerRestart) is Active or ReplacementStarting;
            var canCorrelate = expectedServerRestart || beganDuringOutage || capturedAtRestart;
            if (!canCorrelate
                || !capturedAtRestart && !ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartUrl(requestUrl, _targetAuthority))
            {
                _terminalCorrelations.Remove(requestId);
                return false;
            }

            var expected = ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(
                expectedServerRestart,
                beganDuringOutage,
                requestUrl,
                errorText,
                _targetAuthority,
                capturedAtRestart);
            var logObserved = hasTerminalCorrelation && terminalCorrelation.LogObserved;
            if (logObserved)
            {
                _terminalCorrelations.Remove(requestId);
            }
            else
            {
                _terminalCorrelations[requestId] = new RestartRequestCorrelation(
                    requestUrl,
                    beganDuringOutage,
                    capturedAtRestart,
                    FailureObserved: true,
                    LogObserved: false,
                    _restartGeneration);
                TrimTerminalCorrelationsUnderLock();
            }

            return expected;
        }
    }

    public (bool ExpectedServerRestart, bool BeganDuringOutage, bool CapturedAtRestart, string? CorrelatedRequestUrl) ReadLogContext(string? requestId)
    {
        lock (_gate)
        {
            var expectedServerRestart = Volatile.Read(ref _expectedServerRestart) is Active or ReplacementStarting;
            var beganDuringOutage = requestId is not null && _expectedServerRestartRequests.ContainsKey(requestId);
            var capturedAtRestart = requestId is not null && _capturedExpectedServerRestartRequests.ContainsKey(requestId);
            var correlatedRequestUrl = requestId is not null && _requestUrls.TryGetValue(requestId, out var requestUrl)
                ? requestUrl
                : null;
            if (requestId is not null && _terminalCorrelations.TryGetValue(requestId, out var terminalCorrelation))
            {
                beganDuringOutage |= terminalCorrelation.BeganDuringOutage;
                capturedAtRestart |= terminalCorrelation.CapturedAtRestart;
                correlatedRequestUrl ??= terminalCorrelation.RequestUrl;
            }

            return (expectedServerRestart, beganDuringOutage, capturedAtRestart, correlatedRequestUrl);
        }
    }

    public bool IsExpectedServerRestartLogEntry(string? requestId, string? source, string? text, string? url)
    {
        lock (_gate)
        {
            var expectedServerRestart = Volatile.Read(ref _expectedServerRestart) is Active or ReplacementStarting;
            var beganDuringOutage = requestId is not null && _expectedServerRestartRequests.ContainsKey(requestId);
            var capturedAtRestart = requestId is not null && _capturedExpectedServerRestartRequests.ContainsKey(requestId);
            var correlatedRequestUrl = requestId is not null && _requestUrls.TryGetValue(requestId, out var requestUrl)
                ? requestUrl
                : null;
            var terminalCorrelation = default(RestartRequestCorrelation);
            var hasTerminalCorrelation = requestId is not null && _terminalCorrelations.TryGetValue(requestId, out terminalCorrelation);
            if (hasTerminalCorrelation)
            {
                beganDuringOutage |= terminalCorrelation.BeganDuringOutage;
                capturedAtRestart |= terminalCorrelation.CapturedAtRestart;
                correlatedRequestUrl ??= terminalCorrelation.RequestUrl;
            }

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
            if (requestId is null)
            {
                return expected;
            }

            if (hasTerminalCorrelation)
            {
                _terminalCorrelations[requestId] = terminalCorrelation with { LogObserved = true };
                if (terminalCorrelation.FailureObserved)
                {
                    _terminalCorrelations.Remove(requestId);
                }
            }
            else if (_requestUrls.ContainsKey(requestId))
            {
                _terminalCorrelations[requestId] = new RestartRequestCorrelation(
                    correlatedRequestUrl!,
                    beganDuringOutage,
                    capturedAtRestart,
                    FailureObserved: false,
                    LogObserved: true,
                    _restartGeneration);
                TrimTerminalCorrelationsUnderLock();
            }

            if (expected)
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
            return Volatile.Read(ref _expectedServerRestart) is Active or ReplacementStarting;
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

    private void TrimTerminalCorrelationsUnderLock()
    {
        var requestIds = _terminalCorrelations
            .OrderBy(correlation => correlation.Key, StringComparer.Ordinal)
            .Skip(MaxTrackedSameAuthorityRequests)
            .Select(correlation => correlation.Key)
            .ToArray();
        foreach (var requestId in requestIds)
        {
            _terminalCorrelations.Remove(requestId);
        }
    }

    private void RemoveUnderLock(string requestId)
    {
        _requestUrls.TryRemove(requestId, out _);
        _expectedServerRestartRequests.TryRemove(requestId, out _);
        _capturedExpectedServerRestartRequests.TryRemove(requestId, out _);
    }

    private readonly record struct RestartRequestCorrelation(
        string RequestUrl,
        bool BeganDuringOutage,
        bool CapturedAtRestart,
        bool FailureObserved,
        bool LogObserved,
        long Generation);
}
