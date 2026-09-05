using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class EffectReconciliationBrowserTransportScripts
{
    internal static string InstallOneCollectionUnavailable(string collectionPath)
    {
        var collectionPathJson = JsonSerializer.Serialize(collectionPath);
        return $$"""
            (() => {
                const collectionPath = {{collectionPathJson}};
                const originalFetch = window.fetch.bind(window);
                const state = {
                    exactGets: 0,
                    forwardedGets: 0,
                    injectedFailures: 0,
                    refreshClicks: 0,
                    statuses: [],
                };
                document.addEventListener("click", (event) => {
                    if (event.target?.closest?.("#effectReconciliationRefreshButton"))
                        state.refreshClicks += 1;
                }, true);
                window.__effectReconciliationCollectionRecovery = state;
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    const resolvedUrl = new URL(rawUrl, location.href);
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    const requestTarget = `${resolvedUrl.pathname}${resolvedUrl.search}`;
                    if (method !== "GET" || requestTarget !== collectionPath)
                        return originalFetch(input, init);

                    state.exactGets += 1;
                    if (state.injectedFailures === 0) {
                        state.injectedFailures = 1;
                        state.statuses.push(503);
                        return new Response(JSON.stringify({ status: "unavailable", cases: [], cursor: null }), {
                            status: 503,
                            headers: { "content-type": "application/json" },
                        });
                    }

                    const response = await originalFetch(input, init);
                    state.forwardedGets += 1;
                    state.statuses.push(response.status);
                    return response;
                };
            })()
            """;
    }

    internal static string InstallPostCommitResponseLoss(string actionPath)
    {
        var actionPathJson = JsonSerializer.Serialize(actionPath);
        return $$"""
            (() => {
                const actionPath = {{actionPathJson}};
                const originalFetch = window.fetch.bind(window);
                const state = {
                    attempts: 0,
                    payloads: [],
                    statuses: [],
                    mode: "armed",
                    realCommitStatus: null,
                };
                window.__effectReconciliationTransport = state;
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    const resolvedUrl = new URL(rawUrl, location.href);
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    if (method !== "POST" || resolvedUrl.pathname !== actionPath)
                        return originalFetch(input, init);

                    state.attempts += 1;
                    state.payloads.push(typeof init?.body === "string" ? init.body : "");
                    const response = await originalFetch(input, init);
                    state.statuses.push(response.status);
                    if (state.mode === "armed") {
                        state.mode = "response-lost";
                        state.realCommitStatus = response.status;
                        throw new TypeError("simulated response loss after committed reconciliation operation");
                    }
                    state.mode = "off";
                    return response;
                };
            })()
            """;
    }
}
