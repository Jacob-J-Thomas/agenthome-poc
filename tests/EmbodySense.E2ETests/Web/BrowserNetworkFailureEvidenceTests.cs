namespace EmbodySense.E2ETests.Web;

public sealed class BrowserNetworkFailureEvidenceTests
{
    [Fact]
    public void Rejected_loading_failures_capture_bounded_redacted_lifecycle_provenance()
    {
        const string Authority = "127.0.0.1:5001";
        const string SafeRoute = "/api/loop-runs?maximumCount=50";
        var tracker = new ExpectedServerRestartRequestTracker(Authority);
        tracker.DeclareReadOnlyGetTargets([SafeRoute]);
        Assert.Equal(["rejectedNetworkFailures retainedCount=0; truncated=False"], tracker.ReadRejectedLoadingFailureEvidence());

        tracker.Track("old-generation", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50&secret=never-retained", "GET");
        tracker.PrepareExpectedServerRestart();
        tracker.FreezeExpectedServerRestart();
        Assert.False(tracker.ProcessLoadingFailed("old-generation", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));

        tracker.Track("during-outage", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "POST");
        Assert.False(tracker.ProcessLoadingFailed("during-outage", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));
        tracker.Track("during-outage-session", "https://127.0.0.1:5001/api/session?workspace=never-retained", "GET");
        Assert.False(tracker.ProcessLoadingFailed("during-outage-session", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));

        tracker.MarkExpectedReplacementServerStarting();
        tracker.Track("after-replacement", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "GET");
        Assert.False(tracker.ProcessLoadingFailed("after-replacement", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));
        Assert.False(tracker.ProcessLoadingFailed("missing\r\ncorrelation;field=value", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));
        tracker.EndExpectedServerRestart();
        tracker.Track("post-replacement-ready", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "GET");
        Assert.False(tracker.ProcessLoadingFailed("post-replacement-ready", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));

        var evidence = tracker.ReadRejectedLoadingFailureEvidence();
        Assert.Equal(7, evidence.Count);
        Assert.Equal("rejectedNetworkFailures retainedCount=6; truncated=False", evidence[0]);
        AssertEvidenceRow(evidence[1], 1, "old-generation", "GET", sameAuthority: true, "redacted-unknown", "idle", "active-outage", capturedAtFreeze: true, beganDuringOutage: false, replacementStarted: false, provenanceExisted: true);
        AssertEvidenceRow(evidence[2], 2, "during-outage", "other", sameAuthority: true, "redacted-unknown", "active-outage", "active-outage", capturedAtFreeze: false, beganDuringOutage: true, replacementStarted: false, provenanceExisted: true);
        AssertEvidenceRow(evidence[3], 3, "during-outage-session", "GET", sameAuthority: true, "built-in-session", "active-outage", "active-outage", capturedAtFreeze: false, beganDuringOutage: true, replacementStarted: false, provenanceExisted: true);
        AssertEvidenceRow(evidence[4], 4, "after-replacement", "GET", sameAuthority: true, "route-0", "replacement-started", "replacement-started", capturedAtFreeze: false, beganDuringOutage: false, replacementStarted: true, provenanceExisted: true);
        AssertEvidenceRow(evidence[5], 5, "missing__correlation_field_value", "missing", sameAuthority: false, "redacted-unknown", "unknown", "replacement-started", capturedAtFreeze: false, beganDuringOutage: false, replacementStarted: true, provenanceExisted: false);
        AssertEvidenceRow(evidence[6], 6, "post-replacement-ready", "GET", sameAuthority: true, "route-0", "idle", "idle", capturedAtFreeze: false, beganDuringOutage: false, replacementStarted: false, provenanceExisted: true);
        Assert.DoesNotContain(evidence, row => row.Contains("secret=never-retained", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence, row => row.Contains("/api/loop-runs", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejected_loading_failure_evidence_is_deterministically_bounded_and_never_retains_unrecognized_error_text()
    {
        var tracker = new ExpectedServerRestartRequestTracker("127.0.0.1:5001");
        for (var index = 0; index < 33; index++)
        {
            var requestId = "request-" + index;
            tracker.Track(requestId, "https://127.0.0.1:5001/api/unrecognized?token=private-" + index, "PATCH");
            Assert.False(tracker.ProcessLoadingFailed(requestId, canceled: false, "arbitrary error text private-" + index));
        }

        var evidence = tracker.ReadRejectedLoadingFailureEvidence();
        Assert.Equal(34, evidence.Count);
        Assert.Equal("rejectedNetworkFailures retainedCount=32; truncated=True", evidence[0]);
        Assert.Equal("rejectedNetworkFailure=truncated", evidence[^1]);
        for (var index = 0; index < 32; index++)
        {
            Assert.Contains("rejectedNetworkFailure sequence=" + (index + 1) + "; ", evidence[index + 1], StringComparison.Ordinal);
            Assert.Contains("errorCode=unrecognized", evidence[index + 1], StringComparison.Ordinal);
            Assert.DoesNotContain("private-", evidence[index + 1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Rejected_loading_failure_evidence_uses_retained_terminal_correlation_when_live_request_provenance_is_gone()
    {
        var tracker = new ExpectedServerRestartRequestTracker("127.0.0.1:5001");
        tracker.Track("terminal-correlation", "https://127.0.0.1:5001/api/session?workspace=never-retained", "GET");
        tracker.BeginExpectedServerRestart();

        Assert.False(tracker.ProcessLoadingFailed("terminal-correlation", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));
        Assert.False(tracker.ProcessLoadingFailed("terminal-correlation", canceled: false, "net::ERR_INVALID_HTTP_RESPONSE"));

        var retainedCorrelationEvidence = tracker.ReadRejectedLoadingFailureEvidence()[^1];
        AssertEvidenceRow(retainedCorrelationEvidence, 2, "terminal-correlation", "GET", sameAuthority: true, "built-in-session", "unknown", "active-outage", capturedAtFreeze: true, beganDuringOutage: false, replacementStarted: false, provenanceExisted: true);
    }

    private static void AssertEvidenceRow(string row, int sequence, string requestId, string method, bool sameAuthority, string declaredSafeRoute, string creationPhase, string failurePhase, bool capturedAtFreeze, bool beganDuringOutage, bool replacementStarted, bool provenanceExisted)
    {
        Assert.Equal($"rejectedNetworkFailure sequence={sequence}; requestId={requestId}; method={method}; sameAuthority={sameAuthority}; declaredSafeRoute={declaredSafeRoute}; restartGeneration=1; creationPhase={creationPhase}; failurePhase={failurePhase}; capturedAtFreeze={capturedAtFreeze}; beganDuringOutage={beganDuringOutage}; replacementStarted={replacementStarted}; canceled=False; errorCode=net::ERR_INVALID_HTTP_RESPONSE; provenanceExisted={provenanceExisted}", row);
    }
}
