using System.Net.WebSockets;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

public sealed class BrowserDevToolsContractTests
{
    [Fact]
    public void Browser_devtools_response_accepts_only_exact_turnover_protocol_errors()
    {
        using var firstTurnover = JsonDocument.Parse("{\"id\":1,\"error\":{\"code\":-32000,\"message\":\"Cannot find context with specified id\"}}");
        using var secondTurnover = JsonDocument.Parse("{\"id\":2,\"error\":{\"code\":-32000,\"message\":\"Execution context was destroyed.\"}}");
        using var nearMatch = JsonDocument.Parse("{\"id\":3,\"error\":{\"code\":-32000,\"message\":\"Cannot find context with a specified id\"}}");

        var first = Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.Validate("Runtime.evaluate", firstTurnover.RootElement));
        var second = Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.Validate("Runtime.evaluate", secondTurnover.RootElement));
        var near = Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.Validate("Runtime.evaluate", nearMatch.RootElement));
        Assert.True(BrowserReadOnlyWait.IsExpectedContextTurnover(first));
        Assert.True(BrowserReadOnlyWait.IsExpectedContextTurnover(second));
        Assert.False(BrowserReadOnlyWait.IsExpectedContextTurnover(near));
        Assert.False(BrowserReadOnlyWait.IsExpectedContextTurnover(new BrowserDevToolsException("protocol-error", "Runtime.evaluate", -32001, "Cannot find context with specified id")));
        Assert.False(BrowserReadOnlyWait.IsExpectedContextTurnover(new BrowserDevToolsException("protocol-error", "Page.reload", -32000, "Cannot find context with specified id")));
        Assert.Equal("protocol-error", near.SafeMessage);
        AssertMalformedConflictingAndSecretShapedEnvelopes();
        AssertRuntimeRemoteObjectShape();
    }

    private static void AssertMalformedConflictingAndSecretShapedEnvelopes()
    {
        foreach (var envelope in new[]
        {
            "{}",
            "{\"result\":{},\"error\":null}",
            "{\"result\":{},\"error\":{\"code\":-32000,\"message\":\"Cannot find context with specified id\"}}",
            "{\"error\":{\"code\":\"-32000\",\"message\":\"message\"}}",
            "{\"error\":{\"code\":-32000,\"message\":1}}",
            "{\"result\":{\"exceptionDetails\":{\"exception\":{\"description\":\"token=secret-value\"}}}}"
        })
        {
            using var document = JsonDocument.Parse(envelope);
            var exception = Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.Validate("Runtime.evaluate", document.RootElement));
            Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
        }

        using var malformedScreenshot = JsonDocument.Parse("{\"result\":{}}");
        Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.ReadRequiredResultString("Page.captureScreenshot", malformedScreenshot.RootElement, "data"));
    }

    private static void AssertRuntimeRemoteObjectShape()
    {
        using var validBoolean = JsonDocument.Parse("{\"result\":{\"result\":{\"type\":\"boolean\",\"value\":true}}}");
        using var undefinedAction = JsonDocument.Parse("{\"result\":{\"result\":{\"type\":\"undefined\"}}}");
        using var malformedBoolean = JsonDocument.Parse("{\"result\":{\"result\":{\"type\":\"boolean\",\"value\":\"true\"}}}");
        using var malformedUndefined = JsonDocument.Parse("{\"result\":{\"result\":{\"type\":\"undefined\",\"value\":true}}}");

        Assert.Equal(JsonValueKind.True, BrowserDevToolsResponse.ReadRuntimeEvaluationValue(validBoolean.RootElement).ValueKind);
        Assert.Equal(JsonValueKind.Undefined, BrowserDevToolsResponse.ReadRuntimeEvaluationValue(undefinedAction.RootElement).ValueKind);
        Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.ReadRuntimeEvaluationValue(malformedBoolean.RootElement));
        Assert.Throws<BrowserDevToolsException>(() => BrowserDevToolsResponse.ReadRuntimeEvaluationValue(malformedUndefined.RootElement));
    }

    [Fact]
    public void Browser_pending_response_validation_precedes_barrier_callback_and_removes_terminal_handlers()
    {
        var handlers = new PendingBrowserCommandResponses();
        var callbackCount = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handlers.Add(1, response =>
        {
            BrowserDevToolsResponse.Validate("Runtime.evaluate", response);
            Assert.True(BrowserDevToolsResponse.IsTrueRuntimeEvaluation(response));
            Assert.False(completion.Task.IsCompleted);
            callbackCount++;
        });

        using var valid = JsonDocument.Parse("{\"id\":1,\"result\":{\"result\":{\"type\":\"boolean\",\"value\":true}}}");
        handlers.Handle(1, valid.RootElement);
        completion.SetResult();
        handlers.Handle(1, valid.RootElement);
        Assert.Equal(1, callbackCount);
        Assert.Equal(0, handlers.Count);

        foreach (var envelope in new[]
        {
            "{\"id\":2}",
            "{\"id\":2,\"error\":{\"code\":-32000,\"message\":\"Cannot find context with specified id\"}}",
            "{\"id\":2,\"result\":{\"exceptionDetails\":{\"text\":\"terminal\"}}}",
            "{\"id\":2,\"result\":{\"result\":{\"type\":\"boolean\",\"value\":false}}}",
            "{\"id\":2,\"result\":{\"result\":{\"type\":\"undefined\"}}}"
        })
        {
            handlers.Add(2, response =>
            {
                BrowserDevToolsResponse.Validate("Runtime.evaluate", response);
                if (!BrowserDevToolsResponse.IsTrueRuntimeEvaluation(response))
                {
                    throw new BrowserDevToolsException("invalid-barrier", "Runtime.evaluate", null, "expected-true");
                }

                callbackCount++;
            });
            using var rejected = JsonDocument.Parse(envelope);
            Assert.Throws<BrowserDevToolsException>(() => handlers.Handle(2, rejected.RootElement));
            Assert.Equal(0, handlers.Count);
        }

        handlers.Add(3, _ => callbackCount++);
        handlers.Remove(3);
        handlers.Add(4, _ => callbackCount++);
        handlers.Remove(4);
        Assert.Equal(0, handlers.Count);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task Browser_read_only_wait_retries_only_exact_turnover_and_keeps_terminal_attempts_single()
    {
        var actionSendCount = 0;
        var evaluationSendCount = 0;
        actionSendCount++;
        await BrowserReadOnlyWait.WaitForTrueAsync(_ =>
        {
            evaluationSendCount++;
            return evaluationSendCount switch
            {
                1 => Task.FromException<bool>(new BrowserDevToolsException("protocol-error", "Runtime.evaluate", -32000, "Execution context was destroyed.")),
                2 => Task.FromResult(false),
                _ => Task.FromResult(true)
            };
        }, TimeSpan.FromSeconds(1), "read-only deadline");
        Assert.Equal(1, actionSendCount);
        Assert.Equal(3, evaluationSendCount);

        await AssertTerminalOnFirstAttemptAsync(new BrowserDevToolsException("runtime-exception", "Runtime.evaluate", null, "runtime-exception"));
        await AssertTerminalOnFirstAttemptAsync(new WebSocketException());
        await AssertTerminalOnFirstAttemptAsync(new InvalidOperationException("browser exited"));
        await AssertTerminalOnFirstAttemptAsync(new JsonException("malformed"));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await AssertTerminalOnFirstAttemptAsync(new OperationCanceledException(canceled.Token));

        var turnoverAttempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => BrowserReadOnlyWait.WaitForTrueAsync(_ =>
        {
            turnoverAttempts++;
            return Task.FromException<bool>(new BrowserDevToolsException("protocol-error", "Runtime.evaluate", -32000, "Cannot find context with specified id"));
        }, TimeSpan.FromMilliseconds(220), "read-only deadline"));
        Assert.InRange(turnoverAttempts, 2, 4);
    }

    [Fact]
    public async Task Browser_failure_diagnostics_are_independently_best_effort_but_provenance_write_is_required()
    {
        var writerCalls = 0;
        foreach (var _ in Enumerable.Range(0, 5))
        {
            await BrowserFailureDiagnostics.TryWriteAsync(() =>
            {
                writerCalls++;
                return Task.FromException(new IOException("diagnostic channel unavailable"));
            });
        }

        Assert.Equal(5, writerCalls);
        var missingDirectory = Path.Combine(Path.GetTempPath(), "embodysense-missing-provenance-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => File.WriteAllLinesAsync(Path.Combine(missingDirectory, "expected-restart-qualified-refusals.txt"), []));
    }

    private static async Task AssertTerminalOnFirstAttemptAsync(Exception exception)
    {
        var attempts = 0;
        var observed = await Assert.ThrowsAsync(exception.GetType(), () => BrowserReadOnlyWait.WaitForTrueAsync(_ =>
        {
            attempts++;
            return Task.FromException<bool>(exception);
        }, TimeSpan.FromSeconds(1), "read-only deadline"));
        Assert.Same(exception, observed);
        Assert.Equal(1, attempts);
    }
}
