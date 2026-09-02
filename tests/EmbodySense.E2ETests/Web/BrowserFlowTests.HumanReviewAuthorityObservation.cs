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
                    completedByRoute: Object.create(null),
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
                        observation.completedByRoute[pathname] = (observation.completedByRoute[pathname] ?? 0) + 1;
                    }
                };
                return true;
            })()
            """);
    }

    private static Task RefreshHumanReviewCanonicalRereadAsync(HeadlessBrowserSession browser)
        => browser.EvaluateWithUserGestureAsync("(() => { const button = document.querySelector('[data-testid=\"human-review-detail-refresh\"]'); if (!button || button.disabled) throw new Error('The Human Review detail refresh was unavailable.'); button.click(); })()");

    private static Task WaitForHumanReviewCanonicalRereadIdleAsync(HeadlessBrowserSession browser, string runId)
    {
        var runIdJson = JsonSerializer.Serialize(runId);
        var detailRouteJson = JsonSerializer.Serialize($"/api/human-reviews/{Uri.EscapeDataString(runId)}");
        var evidenceRouteJson = JsonSerializer.Serialize($"/api/human-reviews/{Uri.EscapeDataString(runId)}/evidence");
        var postureRouteJson = JsonSerializer.Serialize($"/api/human-reviews/{Uri.EscapeDataString(runId)}/posture");
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
                    && observation.completedByRoute?.[{{detailRouteJson}}] > 0
                    && observation.completedByRoute?.[{{evidenceRouteJson}}] > 0
                    && observation.completedByRoute?.[{{postureRouteJson}}] > 0
                    && detailStatus?.textContent.includes("Canonical state reread") === true
                    && identity?.textContent.includes({{runIdJson}}) === true
                    && lifecycle?.textContent.toLowerCase().includes("approved") === true
                    && document.querySelectorAll("#humanReviewDecisionHistory .human-review-decision-item").length === 1
                    && actions.every(element => element?.disabled === true);
            })()
            """, TimeSpan.FromSeconds(15));
    }
}
