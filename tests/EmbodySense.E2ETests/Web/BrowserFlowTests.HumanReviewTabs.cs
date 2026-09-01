using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    private sealed partial class HeadlessBrowserSession
    {
        private readonly ConcurrentBag<HeadlessBrowserTab> _childTabs = [];

        internal async Task<HeadlessBrowserTab> OpenTabAsync(string targetUrl)
        {
            var tab = await HeadlessBrowserTab.StartAsync(_debugPort, targetUrl).ConfigureAwait(false);
            _childTabs.Add(tab);
            return tab;
        }

        internal async Task<bool> EvaluateBooleanAsync(string expression, CancellationToken cancellationToken)
        {
            var value = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
            return value.ValueKind == JsonValueKind.True;
        }

        internal async Task PressKeyAsync(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            var parameters = new { type = "keyDown", key, code = key, text = key == "Enter" ? "\r" : key };
            _ = await SendCommandAsync("Input.dispatchKeyEvent", parameters).ConfigureAwait(false);
            _ = await SendCommandAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code = key }).ConfigureAwait(false);
        }

        internal async Task<string?> ReadCookieValueAsync(string cookieName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cookieName);
            var response = await SendCommandAsync("Network.getAllCookies").ConfigureAwait(false);
            if (!response.TryGetProperty("result", out var result)
                || !result.TryGetProperty("cookies", out var cookies)
                || cookies.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var cookie in cookies.EnumerateArray())
            {
                if (cookie.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), cookieName, StringComparison.Ordinal)
                    && cookie.TryGetProperty("value", out var value))
                {
                    return value.GetString();
                }
            }

            return null;
        }

        internal async Task SetCookieValueAsync(string cookieName, string value, string url)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cookieName);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            _ = await SendCommandAsync("Network.setCookies", new
            {
                cookies = new[]
                {
                    new { name = cookieName, value, url, path = "/", httpOnly = true, sameSite = "Strict" }
                }
            }).ConfigureAwait(false);
        }

        private async Task DisposeChildTabsAsync()
        {
            foreach (var tab in _childTabs)
            {
                try
                {
                    await tab.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or WebSocketException or IOException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
