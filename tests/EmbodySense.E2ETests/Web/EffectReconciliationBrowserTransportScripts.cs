using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class EffectReconciliationBrowserTransportScripts
{
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
