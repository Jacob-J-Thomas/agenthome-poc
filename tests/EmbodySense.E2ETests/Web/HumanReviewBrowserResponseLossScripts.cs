using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class HumanReviewBrowserResponseLossScripts
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
                    networkPosts: 0,
                    payloads: [],
                    statuses: [],
                    canonicalGets: 0,
                    mode: "armed",
                    realCommitStatus: null,
                };
                window.__humanReviewResponseLoss = state;
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    const resolvedUrl = new URL(rawUrl, location.href);
                    const url = resolvedUrl.pathname + resolvedUrl.search;
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    if (method === "GET") {
                        state.canonicalGets += 1;
                        return originalFetch(input, init);
                    }
                    if (method !== "POST" || url !== actionPath)
                        return originalFetch(input, init);

                    state.attempts += 1;
                    state.payloads.push(typeof init?.body === "string" ? init.body : "");
                    const response = await originalFetch(input, init);
                    state.networkPosts += 1;
                    state.statuses.push(response.status);
                    if (state.mode === "armed") {
                        state.mode = "response-lost";
                        state.realCommitStatus = response.status;
                        throw new TypeError("simulated response loss after committed response");
                    }
                    state.mode = "off";
                    return response;
                };
            })()
            """;
    }

    internal static string InstallRetryCapture(string actionPath)
    {
        var actionPathJson = JsonSerializer.Serialize(actionPath);
        return $$"""
            (() => {
                const actionPath = {{actionPathJson}};
                const originalFetch = window.fetch.bind(window);
                const state = {
                    attempts: 0,
                    networkPosts: 0,
                    payloads: [],
                    statuses: [],
                    mode: "armed",
                };
                window.__humanReviewRetryCapture = state;
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    const resolvedUrl = new URL(rawUrl, location.href);
                    const url = resolvedUrl.pathname + resolvedUrl.search;
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    if (method !== "POST" || url !== actionPath)
                        return originalFetch(input, init);

                    state.attempts += 1;
                    state.payloads.push(typeof init?.body === "string" ? init.body : "");
                    const response = await originalFetch(input, init);
                    state.networkPosts += 1;
                    state.statuses.push(response.status);
                    state.mode = "off";
                    return response;
                };
            })()
            """;
    }
}
