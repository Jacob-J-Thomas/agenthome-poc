using System.Collections.Concurrent;

namespace EmbodySense.E2ETests.Web;

internal sealed class ExpectedServerRestartRequestTracker
{
    private const int MaxTrackedSameAuthorityRequests = 1024;
    private const int MaxDeclaredReadOnlyGetTargets = 16;
    private const int MaxProvenanceTraceEntries = 128;
    private const int MaxTraceRequestIdLength = 96;
    private const int Idle = 0;
    private const int Preparing = 1;
    private const int Active = 2;
    private const int ReplacementStarting = 3;
    private readonly string _targetAuthority;
    private readonly ConcurrentDictionary<string, byte> _expectedServerRestartRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _capturedExpectedServerRestartRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RestartRequestProvenance> _requestProvenance = new(StringComparer.Ordinal);
    private readonly List<string> _declaredReadOnlyGetTargets = [];
    private readonly Dictionary<string, RestartRequestCorrelation> _terminalCorrelations = new(StringComparer.Ordinal);
    private readonly List<string> _qualifiedReadOnlyRefusalEvidence = [];
    private readonly HashSet<string> _qualifiedReadOnlyRefusalEvidenceKeys = new(StringComparer.Ordinal);
    private readonly List<string> _provenanceTrace = [];
    private readonly object _gate = new();
    private int _expectedServerRestart;
    private long _restartGeneration;
    private long _provenanceTraceSequence;
    private bool _provenanceTraceTruncated;

    public ExpectedServerRestartRequestTracker(string targetAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAuthority);
        _targetAuthority = targetAuthority;
    }

    public string TargetAuthority => _targetAuthority;

    public void DeclareReadOnlyGetTargets(IEnumerable<string> pathAndQueries)
    {
        ArgumentNullException.ThrowIfNull(pathAndQueries);
        lock (_gate)
        {
            _declaredReadOnlyGetTargets.Clear();
            foreach (var pathAndQuery in pathAndQueries)
            {
                if (TryGetExactPathAndQuery(pathAndQuery, out var exactPathAndQuery))
                {
                    if (_declaredReadOnlyGetTargets.Count == MaxDeclaredReadOnlyGetTargets)
                    {
                        break;
                    }

                    if (!_declaredReadOnlyGetTargets.Contains(exactPathAndQuery, StringComparer.Ordinal))
                    {
                        _declaredReadOnlyGetTargets.Add(exactPathAndQuery);
                    }
                }
            }

            TraceDeclarationTransitionsUnderLock();
        }
    }

    public void PrepareExpectedServerRestart()
    {
        lock (_gate)
        {
            _restartGeneration++;
            _expectedServerRestartRequests.Clear();
            _capturedExpectedServerRestartRequests.Clear();
            _terminalCorrelations.Clear();
            _qualifiedReadOnlyRefusalEvidence.Clear();
            _qualifiedReadOnlyRefusalEvidenceKeys.Clear();
            _provenanceTrace.Clear();
            _provenanceTraceSequence = 0;
            _provenanceTraceTruncated = false;
            Interlocked.Exchange(ref _expectedServerRestart, Preparing);
            RecordLifecycleTransition("prepare");
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
                if (_requestProvenance.TryGetValue(request.Key, out var provenance))
                {
                    var declaredTargetIndex = GetDeclaredTargetIndex(request.Value);
                    var currentMatch = declaredTargetIndex >= 0;
                    var isGet = IsGetMethod(provenance.Method);
                    if (provenance.IsDeclaredReadOnlyGetTarget || currentMatch)
                    {
                        RecordProvenanceTrace(request.Key, declaredTargetIndex, provenance.Method, provenance.IsDeclaredReadOnlyGetTarget, currentMatch, frozenSnapshot: true, active: false, currentMatch && isGet ? "accepted-at-freeze" : GetMethodRejectionReason(provenance.Method));
                    }

                    _requestProvenance[request.Key] = provenance with
                    {
                        Generation = _restartGeneration,
                        LiveAtSuccessfulFreeze = true,
                        IsDeclaredReadOnlyGetTarget = currentMatch && isGet,
                        DeclaredTargetIndex = declaredTargetIndex
                    };
                }
            }

            Interlocked.Exchange(ref _expectedServerRestart, Active);
            RecordLifecycleTransition("successful-freeze-active");
        }
    }

    public void AbortExpectedServerRestart()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _expectedServerRestart) != Idle)
            {
                _expectedServerRestartRequests.Clear();
                _capturedExpectedServerRestartRequests.Clear();
                _requestProvenance.Clear();
                _terminalCorrelations.Clear();
                _qualifiedReadOnlyRefusalEvidence.Clear();
                _qualifiedReadOnlyRefusalEvidenceKeys.Clear();
                Interlocked.Exchange(ref _expectedServerRestart, Idle);
                RecordLifecycleTransition("abort");
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
            if (Interlocked.CompareExchange(ref _expectedServerRestart, ReplacementStarting, Active) == Active)
            {
                RecordLifecycleTransition("replacement-start");
            }
        }
    }

    public void EndExpectedServerRestart()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _expectedServerRestart, Idle);
            RecordLifecycleTransition("end");
        }
    }

    public void Track(string requestId, string url, string? method = null)
    {
        lock (_gate)
        {
            if (!IsTargetAuthority(url))
            {
                RemoveUnderLock(requestId);
                _terminalCorrelations.Remove(requestId);
                return;
            }

            var expectedServerRestart = Volatile.Read(ref _expectedServerRestart);
            var declaredTargetIndex = GetDeclaredTargetIndex(url);
            var declaredReadOnlyGetTarget = declaredTargetIndex >= 0 && IsGetMethod(method);
            _requestUrls[requestId] = url;
            _terminalCorrelations.Remove(requestId);
            _requestProvenance[requestId] = new RestartRequestProvenance(
                method,
                _restartGeneration,
                LiveAtSuccessfulFreeze: false,
                BeganDuringActiveOutage: expectedServerRestart == Active,
                IsDeclaredReadOnlyGetTarget: declaredReadOnlyGetTarget,
                DeclaredTargetIndex: declaredTargetIndex);
            if (declaredTargetIndex >= 0)
            {
                RecordProvenanceTrace(requestId, declaredTargetIndex, method, cachedMatch: false, currentMatch: true, frozenSnapshot: false, active: expectedServerRestart == Active, expectedServerRestart == ReplacementStarting ? "replacement-start" : declaredReadOnlyGetTarget ? "tracked" : GetMethodRejectionReason(method));
            }
            TrimUnderLock();
            if (_requestUrls.ContainsKey(requestId)
                && expectedServerRestart == Active
                && (ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartUrl(url, _targetAuthority)
                    || declaredReadOnlyGetTarget))
            {
                _expectedServerRestartRequests.TryAdd(requestId, 0);
            }
        }
    }

    public void Complete(string requestId)
    {
        lock (_gate)
        {
            if (_requestProvenance.TryGetValue(requestId, out var provenance) && provenance.IsDeclaredReadOnlyGetTarget)
            {
                RecordProvenanceTrace(requestId, provenance.DeclaredTargetIndex, provenance.Method, cachedMatch: true, currentMatch: true, frozenSnapshot: provenance.LiveAtSuccessfulFreeze, active: Volatile.Read(ref _expectedServerRestart) == Active, "completed");
            }

            RemoveUnderLock(requestId);
            _terminalCorrelations.Remove(requestId);
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
            var hasCurrentProvenance = _requestProvenance.TryGetValue(requestId, out var currentProvenance);
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
            var qualifiedReadOnlyRefusal = (hasCurrentProvenance && IsQualifiedReadOnlyRefusal(currentProvenance))
                || (hasTerminalCorrelation && terminalCorrelation.QualifiedReadOnlyRefusal && terminalCorrelation.Generation == _restartGeneration);
            RemoveUnderLock(requestId);
            if (canceled)
            {
                _terminalCorrelations.Remove(requestId);
                return true;
            }

            var expectedServerRestart = Volatile.Read(ref _expectedServerRestart) is Active or ReplacementStarting;
            var canCorrelate = expectedServerRestart || beganDuringOutage || capturedAtRestart;
            if (!canCorrelate
                || !capturedAtRestart && !qualifiedReadOnlyRefusal && !ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartUrl(requestUrl, _targetAuthority))
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
                capturedAtRestart,
                qualifiedReadOnlyRefusal);
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
                    QualifiedReadOnlyRefusal: qualifiedReadOnlyRefusal,
                    RequestMethod: hasCurrentProvenance ? currentProvenance.Method : terminalCorrelation.RequestMethod,
                    PathAndQuery: GetPathAndQuery(requestUrl),
                    _restartGeneration);
                TrimTerminalCorrelationsUnderLock();
            }

            if (expected && qualifiedReadOnlyRefusal && IsExactConnectionRefused(errorText))
            {
                RecordQualifiedReadOnlyRefusalEvidence(requestId, new RestartRequestCorrelation(
                    requestUrl,
                    beganDuringOutage,
                    capturedAtRestart,
                    FailureObserved: true,
                    LogObserved: false,
                    QualifiedReadOnlyRefusal: qualifiedReadOnlyRefusal,
                    RequestMethod: hasCurrentProvenance ? currentProvenance.Method : terminalCorrelation.RequestMethod,
                    PathAndQuery: GetPathAndQuery(requestUrl),
                    _restartGeneration));
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
            var currentProvenance = default(RestartRequestProvenance);
            var hasCurrentProvenance = requestId is not null && _requestProvenance.TryGetValue(requestId, out currentProvenance);
            var qualifiedReadOnlyRefusal = hasCurrentProvenance && IsQualifiedReadOnlyRefusal(currentProvenance);
            var correlatedRequestUrl = requestId is not null && _requestUrls.TryGetValue(requestId, out var requestUrl)
                ? requestUrl
                : null;
            var terminalCorrelation = default(RestartRequestCorrelation);
            var hasTerminalCorrelation = requestId is not null && _terminalCorrelations.TryGetValue(requestId, out terminalCorrelation);
            if (hasTerminalCorrelation)
            {
                beganDuringOutage |= terminalCorrelation.BeganDuringOutage;
                capturedAtRestart |= terminalCorrelation.CapturedAtRestart;
                qualifiedReadOnlyRefusal |= terminalCorrelation.QualifiedReadOnlyRefusal && terminalCorrelation.Generation == _restartGeneration;
                correlatedRequestUrl ??= terminalCorrelation.RequestUrl;
            }

            if (!expectedServerRestart && !beganDuringOutage && !capturedAtRestart && !qualifiedReadOnlyRefusal)
            {
                return false;
            }

            if (!string.Equals(source, "network", StringComparison.Ordinal)
                || !ContainsTargetAuthority(text) && !ContainsTargetAuthority(url) && !ContainsTargetAuthority(correlatedRequestUrl))
            {
                return false;
            }

            if (qualifiedReadOnlyRefusal && !IsExactQualifiedReadOnlyGetRoute(url, correlatedRequestUrl))
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
                capturedAtRestart,
                qualifiedReadOnlyRefusal);
            if (requestId is null)
            {
                return expected;
            }

            if (hasTerminalCorrelation)
            {
                if (expected)
                {
                    if (terminalCorrelation.FailureObserved)
                    {
                        _terminalCorrelations.Remove(requestId);
                    }
                    else
                    {
                        _terminalCorrelations[requestId] = terminalCorrelation with { LogObserved = true };
                    }
                }
            }
            else if (_requestUrls.ContainsKey(requestId))
            {
                _terminalCorrelations[requestId] = new RestartRequestCorrelation(
                    correlatedRequestUrl!,
                    beganDuringOutage,
                    capturedAtRestart,
                    FailureObserved: false,
                    LogObserved: expected,
                    QualifiedReadOnlyRefusal: qualifiedReadOnlyRefusal,
                    RequestMethod: hasCurrentProvenance ? currentProvenance.Method : terminalCorrelation.RequestMethod,
                    PathAndQuery: GetPathAndQuery(correlatedRequestUrl!),
                    _restartGeneration);
                TrimTerminalCorrelationsUnderLock();
            }

            if (expected)
            {
                _expectedServerRestartRequests.TryRemove(requestId, out _);
                if (qualifiedReadOnlyRefusal && IsQualifiedConnectionRefusedLog(text))
                {
                    RecordQualifiedReadOnlyRefusalEvidence(requestId, new RestartRequestCorrelation(
                        correlatedRequestUrl!,
                        beganDuringOutage,
                        capturedAtRestart,
                        FailureObserved: false,
                        LogObserved: true,
                        QualifiedReadOnlyRefusal: qualifiedReadOnlyRefusal,
                        RequestMethod: hasCurrentProvenance ? currentProvenance.Method : terminalCorrelation.RequestMethod,
                        PathAndQuery: GetPathAndQuery(correlatedRequestUrl!),
                        _restartGeneration));
                }
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

    public IReadOnlyList<string> ReadQualifiedReadOnlyRefusalEvidence()
    {
        lock (_gate)
        {
            return _qualifiedReadOnlyRefusalEvidence.ToArray();
        }
    }

    public IReadOnlyList<string> ReadQualifiedReadOnlyRefusalEvidenceSummary()
    {
        lock (_gate)
        {
            return ["declaredReadOnlyGetTargets=" + _declaredReadOnlyGetTargets.Count, .. _qualifiedReadOnlyRefusalEvidence, .. _provenanceTrace];
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

    private int GetDeclaredTargetIndex(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Authority, _targetAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return _declaredReadOnlyGetTargets.FindIndex(target => string.Equals(target, uri.PathAndQuery, StringComparison.Ordinal));
    }

    private static bool IsGetMethod(string? method)
    {
        return string.Equals(method, "GET", StringComparison.Ordinal);
    }

    private static string GetMethodRejectionReason(string? method)
    {
        return method is null ? "missing-method" : "method-not-get";
    }

    private bool IsQualifiedReadOnlyRefusal(RestartRequestProvenance provenance)
    {
        return provenance.IsDeclaredReadOnlyGetTarget
            && provenance.Generation == _restartGeneration
            && (provenance.LiveAtSuccessfulFreeze || provenance.BeganDuringActiveOutage);
    }

    private void TraceDeclarationTransitionsUnderLock()
    {
        var phase = Volatile.Read(ref _expectedServerRestart);
        foreach (var request in _requestProvenance.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!_requestUrls.TryGetValue(request.Key, out var url))
            {
                continue;
            }

            var declaredTargetIndex = GetDeclaredTargetIndex(url);
            var currentMatch = declaredTargetIndex >= 0;
            if (request.Value.IsDeclaredReadOnlyGetTarget || currentMatch)
            {
                RecordProvenanceTrace(request.Key, declaredTargetIndex, request.Value.Method, request.Value.IsDeclaredReadOnlyGetTarget, currentMatch, request.Value.LiveAtSuccessfulFreeze, phase == Active, request.Value.LiveAtSuccessfulFreeze ? "post-freeze-declaration" : IsGetMethod(request.Value.Method) ? "declaration-updated" : GetMethodRejectionReason(request.Value.Method));
            }
        }
    }

    private void RecordLifecycleTransition(string transition)
    {
        RecordProvenanceTrace("none", -1, null, cachedMatch: false, currentMatch: false, frozenSnapshot: false, active: Volatile.Read(ref _expectedServerRestart) == Active, "lifecycle-" + transition);
    }

    private void RecordProvenanceTrace(string requestId, int declaredTargetIndex, string? method, bool cachedMatch, bool currentMatch, bool frozenSnapshot, bool active, string rejectionReason)
    {
        if (_provenanceTrace.Count >= MaxProvenanceTraceEntries)
        {
            if (!_provenanceTraceTruncated)
            {
                _provenanceTrace.Add("provenanceTrace=truncated");
                _provenanceTraceTruncated = true;
            }

            return;
        }

        _provenanceTraceSequence++;
        _provenanceTrace.Add($"provenanceTrace sequence={_provenanceTraceSequence}; phase={Volatile.Read(ref _expectedServerRestart)}; generation={_restartGeneration}; requestId={SanitizeTraceRequestId(requestId)}; declaredTargetIndex={declaredTargetIndex}; methodPresent={method is not null}; method={NormalizeTraceMethod(method)}; cachedMatch={cachedMatch}; currentMatch={currentMatch}; frozenSnapshot={frozenSnapshot}; active={active}; rejectionReason={rejectionReason}");
    }

    private static string SanitizeTraceRequestId(string requestId)
    {
        var normalized = requestId.Replace('\r', '_').Replace('\n', '_');
        return normalized.Length <= MaxTraceRequestIdLength ? normalized : normalized[..MaxTraceRequestIdLength];
    }

    private static string NormalizeTraceMethod(string? method)
    {
        return method is null ? "missing" : IsGetMethod(method) ? "GET" : "other";
    }

    private bool IsExactQualifiedReadOnlyGetRoute(string? suppliedUrl, string? correlatedRequestUrl)
    {
        if (!Uri.TryCreate(correlatedRequestUrl, UriKind.Absolute, out var correlatedUri)
            || !string.Equals(correlatedUri.Authority, _targetAuthority, StringComparison.OrdinalIgnoreCase)
            || !_declaredReadOnlyGetTargets.Contains(correlatedUri.PathAndQuery, StringComparer.Ordinal))
        {
            return false;
        }

        return suppliedUrl is null
            || Uri.TryCreate(suppliedUrl, UriKind.Absolute, out var suppliedUri)
                && string.Equals(suppliedUri.Authority, correlatedUri.Authority, StringComparison.OrdinalIgnoreCase)
                && string.Equals(suppliedUri.PathAndQuery, correlatedUri.PathAndQuery, StringComparison.Ordinal);
    }

    private static bool TryGetExactPathAndQuery(string? value, out string exactPathAndQuery)
    {
        exactPathAndQuery = string.Empty;
        return value is not null
            && value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains('#')
            && Uri.TryCreate(value, UriKind.Relative, out var uri)
            && string.Equals(uri.OriginalString, value, StringComparison.Ordinal)
            && (exactPathAndQuery = uri.OriginalString).Length > 1;
    }

    private static string GetPathAndQuery(string requestUrl)
    {
        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) ? uri.PathAndQuery : string.Empty;
    }

    private static bool IsQualifiedConnectionRefusedLog(string? value)
    {
        return string.Equals(value, "Failed to load resource: net::ERR_CONNECTION_REFUSED", StringComparison.Ordinal)
            || string.Equals(value?.Trim(), "net::ERR_CONNECTION_REFUSED", StringComparison.Ordinal);
    }

    private static bool IsExactConnectionRefused(string? value)
    {
        return string.Equals(value, "net::ERR_CONNECTION_REFUSED", StringComparison.Ordinal);
    }

    private void RecordQualifiedReadOnlyRefusalEvidence(string requestId, RestartRequestCorrelation correlation)
    {
        var key = requestId + "\n" + correlation.Generation;
        if (_qualifiedReadOnlyRefusalEvidence.Count < MaxTrackedSameAuthorityRequests
            && _qualifiedReadOnlyRefusalEvidenceKeys.Add(key))
        {
            _qualifiedReadOnlyRefusalEvidence.Add($"requestId={requestId}; generation={correlation.Generation}; method={correlation.RequestMethod}; target={correlation.PathAndQuery}");
        }
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
        _requestProvenance.Remove(requestId);
    }

    private readonly record struct RestartRequestCorrelation(
        string RequestUrl,
        bool BeganDuringOutage,
        bool CapturedAtRestart,
        bool FailureObserved,
        bool LogObserved,
        bool QualifiedReadOnlyRefusal,
        string? RequestMethod,
        string PathAndQuery,
        long Generation);

    private readonly record struct RestartRequestProvenance(
        string? Method,
        long Generation,
        bool LiveAtSuccessfulFreeze,
        bool BeganDuringActiveOutage,
        bool IsDeclaredReadOnlyGetTarget,
        int DeclaredTargetIndex);
}
