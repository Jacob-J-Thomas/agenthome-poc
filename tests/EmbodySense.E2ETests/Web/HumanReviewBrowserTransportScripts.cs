using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class HumanReviewBrowserTransportScripts
{
    internal static string InstallPreSendFailure(string actionPath)
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
                    mode: "pre-send",
                };
                window.__humanReviewTransport = state;
                window.fetch = async (input, init = {}) => {
                    const url = typeof input === "string" ? input : input?.url ?? "";
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    if (method === "POST" && url.includes(actionPath)) {
                        state.attempts += 1;
                        state.payloads.push(typeof init?.body === "string" ? init.body : "");
                        if (state.mode === "pre-send") {
                            state.mode = "pre-send-failed";
                            return Promise.reject(new TypeError("simulated disconnected transport"));
                        }
                        state.networkPosts += 1;
                    }
                    const response = await originalFetch(input, init);
                    if (method === "POST" && url.includes(actionPath))
                        state.statuses.push(response.status);
                    return response;
                };
            })()
            """;
    }

    internal static string InstallPostCommitResponseLoss(string actionPath, string runId)
    {
        var actionPathJson = JsonSerializer.Serialize(actionPath);
        var encodedRunIdJson = JsonSerializer.Serialize(Uri.EscapeDataString(runId));
        return $$"""
            (async () => {
                const actionPath = {{actionPathJson}};
                const encodedRunId = {{encodedRunIdJson}};
                const originalFetch = window.fetch.bind(window);
                const snapshotUrls = [
                    "/api/human-reviews?maximumCount=50",
                    `/api/human-reviews/${encodedRunId}`,
                    `/api/human-reviews/${encodedRunId}/evidence`,
                    `/api/human-reviews/${encodedRunId}/posture`,
                ];
                const snapshots = new Map();
                for (const url of snapshotUrls) {
                    const response = await originalFetch(url, { cache: "no-store", credentials: "same-origin" });
                    snapshots.set(url, { status: response.status, body: await response.clone().text() });
                }
                const state = {
                    attempts: 0,
                    networkPosts: 0,
                    payloads: [],
                    statuses: [],
                    syntheticLossStatus: null,
                    commitObserved: false,
                    mode: "armed",
                    snapshotsReady: true,
                };
                window.__humanReviewTransport = state;
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    const resolvedUrl = new URL(rawUrl, location.href);
                    const url = resolvedUrl.pathname + resolvedUrl.search;
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    if (method === "GET" && state.mode === "stale-reads" && snapshots.has(url)) {
                        const snapshot = snapshots.get(url);
                        return new Response(snapshot.body, {
                            status: snapshot.status,
                            headers: { "Content-Type": "application/json" },
                        });
                    }
                    if (method === "POST" && url.includes(actionPath)) {
                        state.attempts += 1;
                        state.payloads.push(typeof init?.body === "string" ? init.body : "");
                        state.networkPosts += 1;
                        const response = await originalFetch(input, init);
                        state.statuses.push(response.status);
                        if (state.mode === "armed") {
                            state.mode = "stale-reads";
                            state.commitObserved = true;
                            state.syntheticLossStatus = 503;
                            return new Response("", { status: 503, statusText: "simulated response loss" });
                        }
                        state.mode = "off";
                        return response;
                    }
                    return originalFetch(input, init);
                };
            })()
            """;
    }

    internal static string InstallStaleReadConflict(string actionPath, string runId)
    {
        var actionPathJson = JsonSerializer.Serialize(actionPath);
        var encodedRunIdJson = JsonSerializer.Serialize(Uri.EscapeDataString(runId));
        return $$"""
            (async () => {
                const actionPath = {{actionPathJson}};
                const encodedRunId = {{encodedRunIdJson}};
                const originalFetch = window.fetch.bind(window);
                const snapshotUrls = [
                    "/api/human-reviews?maximumCount=50",
                    `/api/human-reviews/${encodedRunId}`,
                    `/api/human-reviews/${encodedRunId}/evidence`,
                    `/api/human-reviews/${encodedRunId}/posture`,
                ];
                const snapshots = new Map();
                for (const url of snapshotUrls) {
                    const response = await originalFetch(url, { cache: "no-store", credentials: "same-origin" });
                    snapshots.set(url, { status: response.status, body: await response.clone().text() });
                }
                const state = {
                    attempts: 0,
                    statuses: [],
                    payloads: [],
                    conflictFeedbackObserved: false,
                    snapshotsReady: true,
                    mode: "stale-reads",
                };
                window.__humanReviewTransport = state;
                const actionStatus = document.getElementById("humanReviewActionStatus");
                const conflictFeedbackObserver = new MutationObserver(() => {
                    const text = actionStatus?.textContent?.toLowerCase() ?? "";
                    if (text.includes("changed") || text.includes("conflicted")) {
                        state.conflictFeedbackObserved = true;
                        conflictFeedbackObserver.disconnect();
                    }
                });
                actionStatus && conflictFeedbackObserver.observe(actionStatus, { childList: true, characterData: true, subtree: true });
                window.fetch = async (input, init = {}) => {
                    const rawUrl = typeof input === "string" ? input : input?.url ?? "";
                    const resolvedUrl = new URL(rawUrl, location.href);
                    const url = resolvedUrl.pathname + resolvedUrl.search;
                    const method = String(init?.method ?? input?.method ?? "GET").toUpperCase();
                    if (method === "GET" && state.mode === "stale-reads" && snapshots.has(url)) {
                        const snapshot = snapshots.get(url);
                        return new Response(snapshot.body, {
                            status: snapshot.status,
                            headers: { "Content-Type": "application/json" },
                        });
                    }
                    if (method === "POST" && url.includes(actionPath)) {
                        state.attempts += 1;
                        state.payloads.push(typeof init?.body === "string" ? init.body : "");
                        const response = await originalFetch(input, init);
                        state.statuses.push(response.status);
                        state.mode = "off";
                        return response;
                    }
                    return originalFetch(input, init);
                };
            })()
            """;
    }
}
