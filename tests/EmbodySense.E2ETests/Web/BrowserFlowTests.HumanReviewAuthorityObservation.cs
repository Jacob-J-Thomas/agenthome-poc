using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    private static async Task InstallHumanReviewCanonicalRereadObservationAsync(HeadlessBrowserSession browser, string runId)
    {
        var runIdJson = JsonSerializer.Serialize(runId);
        await browser.EvaluateAsync($$"""
            (() => {
                const runId = {{runIdJson}};
                const current = window.__embodySenseHumanReviewCanonicalObservation;
                if (current?.installed === true && current.runId === runId) return true;

                const routes = new Set([
                    `/api/human-reviews/${encodeURIComponent(runId)}`,
                    `/api/human-reviews/${encodeURIComponent(runId)}/evidence`,
                    `/api/human-reviews/${encodeURIComponent(runId)}/posture`,
                ]);
                const originalFetch = window.fetch.bind(window);
                const observation = {
                    installed: true,
                    runId,
                    pending: 0,
                    completed: 0,
                };
                window.__embodySenseHumanReviewCanonicalObservation = observation;
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    let pathname;
                    try {
                        pathname = new URL(rawUrl, location.href).pathname;
                    } catch {
                        return originalFetch(input, init);
                    }

                    if (!routes.has(pathname)) return originalFetch(input, init);
                    observation.pending += 1;
                    try {
                        return await originalFetch(input, init);
                    } finally {
                        observation.pending -= 1;
                        observation.completed += 1;
                    }
                };
                return true;
            })()
            """);
    }

    private static Task WaitForHumanReviewCanonicalRereadIdleAsync(HeadlessBrowserSession browser, string runId)
    {
        var runIdJson = JsonSerializer.Serialize(runId);
        return browser.WaitForExpressionAsync($$"""
            (() => {
                const observation = window.__embodySenseHumanReviewCanonicalObservation;
                const detailStatus = document.getElementById("humanReviewDetailStatus");
                const identity = document.getElementById("humanReviewIdentity");
                const lifecycle = document.getElementById("humanReviewLifecycleStatus");
                const actions = ["approve", "reject", "cancel", "request-information"].map(action => document.querySelector(`[data-testid="human-review-${action}"]`));
                return observation?.installed === true
                    && observation.runId === {{runIdJson}}
                    && observation.completed > 0
                    && observation.pending === 0
                    && detailStatus?.textContent.includes("Canonical state reread") === true
                    && identity?.textContent.includes({{runIdJson}}) === true
                    && lifecycle?.textContent.toLowerCase().includes("approved") === true
                    && document.querySelectorAll("#humanReviewDecisionHistory .human-review-decision-item").length === 1
                    && actions.every(element => element?.disabled === true);
            })()
            """, TimeSpan.FromSeconds(15));
    }
}
