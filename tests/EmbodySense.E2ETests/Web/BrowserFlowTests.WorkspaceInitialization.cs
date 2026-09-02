using System.Diagnostics;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    private static readonly TimeSpan _workspaceInitializationTimeout = TimeSpan.FromSeconds(30);

    private const string WorkspaceInitializedExpression = "document.getElementById('workspaceStatus').textContent.includes('Initialized')";
    private const string CanonicalWorkspaceInitializationExpression = """
        (async () => {
          const abortController = new AbortController();
          const timeoutId = window.setTimeout(() => abortController.abort(), 2000);
          try {
            const response = await fetch('/api/status', { cache: 'no-store', signal: abortController.signal });
            if (!response.ok) return `http-${response.status}`;
            const status = await response.json();
            if (!status || typeof status !== 'object') return 'malformed-payload';
            if (status.initialized === true && status.initializationState === 'initialized') return 'initialized';
            if (status.initialized === false && status.initializationState === 'uninitialized') return 'pending-uninitialized';
            if (status.initialized === false && status.initializationState === 'partial') return 'pending-partial';
            return 'inconsistent-status';
          } catch (error) {
            return error?.name === 'AbortError' ? 'request-timeout' : 'request-failed';
          } finally {
            window.clearTimeout(timeoutId);
          }
        })()
        """;

    private static async Task InitializeWorkspaceAsync(HeadlessBrowserSession browser)
    {
        await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Needs initialization')");
        await browser.WaitForExpressionAsync("!document.getElementById('initButton').disabled");
        await ClickAsync(browser, "#initButton");
        await WaitForCommittedWorkspaceInitializationAsync(browser);
        await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
    }

    private static async Task HideCommittedWorkspaceInitializationUntilReloadAsync(HeadlessBrowserSession browser)
    {
        const string Expression = """
            (() => {
              const status = document.getElementById('workspaceStatus');
              if (!status) throw new Error('The workspace status projection is unavailable.');
              const observer = new MutationObserver(() => {
                if (status.textContent.includes('Initialized')) status.textContent = 'Needs initialization';
              });
              observer.observe(status, { childList: true, characterData: true, subtree: true });
            })()
            """;
        await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus') !== null");
        await browser.EvaluateAsync(Expression);
    }

    private static async Task WaitForCommittedWorkspaceInitializationAsync(HeadlessBrowserSession browser)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(_workspaceInitializationTimeout);
        var canonicalObservation = "not-read";
        var reloadedCommittedState = false;

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                if (await browser.EvaluateStringAsync($"String(Boolean({WorkspaceInitializedExpression}))", timeout.Token) == "true")
                {
                    return;
                }

                canonicalObservation = await browser.EvaluateStringAsync(CanonicalWorkspaceInitializationExpression, timeout.Token);
                if (canonicalObservation == "initialized")
                {
                    await browser.ReloadAsync(cancellationToken: timeout.Token);
                    reloadedCommittedState = true;
                    var remaining = _workspaceInitializationTimeout - Stopwatch.GetElapsedTime(startedAt);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    try
                    {
                        await browser.WaitForExpressionAsync(WorkspaceInitializedExpression, remaining);
                    }
                    catch (TimeoutException exception)
                    {
                        throw new TimeoutException("Canonical workspace initialization committed, but the visible projection did not recover after one reload within the original initialization deadline.", exception);
                    }

                    return;
                }

                await Task.Delay(250, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"The visible workspace initialization did not converge within {_workspaceInitializationTimeout.TotalSeconds:F0} seconds. Canonical observation: {canonicalObservation}; reloaded committed state: {reloadedCommittedState}.");
    }
}
