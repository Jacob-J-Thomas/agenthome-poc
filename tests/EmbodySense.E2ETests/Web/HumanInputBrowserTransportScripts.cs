using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class HumanInputBrowserTransportScripts
{
    internal static string InstallPostCommitResponseLoss(string actionPath)
    {
        var route = JsonSerializer.Serialize(actionPath);
        return $$"""
            (() => {
              const route = {{route}};
              const originalFetch = window.fetch.bind(window);
              const state = { mode: "armed", attempts: 0, networkPosts: 0, payloads: [], statuses: [] };
              window.__humanInputResponseLoss = state;
              window.fetch = async (input, init = {}) => {
                const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                const resolvedUrl = new URL(rawUrl, location.href);
                const url = resolvedUrl.pathname + resolvedUrl.search;
                const method = String(init?.method ?? (typeof input === "object" ? input?.method : "GET")).toUpperCase();
                if (url !== route || method !== "POST") return await originalFetch(input, init);
                state.attempts += 1;
                const body = typeof init?.body === "string" ? init.body : "";
                state.payloads.push(body);
                const response = await originalFetch(input, init);
                state.networkPosts += 1;
                state.statuses.push(response.status);
                if (state.attempts === 1) {
                  state.mode = "post-commit-lost";
                  throw new TypeError("Simulated committed Human Input response loss.");
                }
                state.mode = "off";
                return response;
              };
              return true;
            })()
            """;
    }

    internal static string InstallPostCapture(string actionPath)
    {
        var route = JsonSerializer.Serialize(actionPath);
        return $$"""
            (() => {
              const route = {{route}};
              const originalFetch = window.fetch.bind(window);
              const state = { mode: "on", attempts: 0, payloads: [], statuses: [] };
              window.__humanInputPostCapture = state;
              window.fetch = async (input, init = {}) => {
                const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                const resolvedUrl = new URL(rawUrl, location.href);
                const url = resolvedUrl.pathname + resolvedUrl.search;
                const method = String(init?.method ?? (typeof input === "object" ? input?.method : "GET")).toUpperCase();
                if (url !== route || method !== "POST") return await originalFetch(input, init);
                state.attempts += 1;
                state.payloads.push(typeof init?.body === "string" ? init.body : "");
                const response = await originalFetch(input, init);
                state.statuses.push(response.status);
                return response;
              };
              return true;
            })()
            """;
    }

}
