using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class HumanInputLifecycleBrowserScripts
{
    internal static string InstallFixedOperationIdentity(string seed)
    {
        var serializedSeed = JsonSerializer.Serialize(seed);
        return $$"""
            (() => {
              const seed = {{serializedSeed}};
              const cryptoObject = globalThis.crypto;
              const fixed = () => seed;
              if (!globalThis.__humanInputOriginalRandomUUID)
                globalThis.__humanInputOriginalRandomUUID = cryptoObject.randomUUID.bind(cryptoObject);
              try {
                Object.defineProperty(cryptoObject, "randomUUID", { configurable: true, value: fixed });
              } catch (_) {
                try { cryptoObject.randomUUID = fixed; } catch (_) { }
              }
              if (typeof cryptoObject.randomUUID !== "function" || cryptoObject.randomUUID() !== seed)
                throw new Error("The browser did not accept the deterministic idempotency fixture.");
              return true;
            })()
            """;
    }

    internal static string RestoreOperationIdentity()
        => "(() => { const original = globalThis.__humanInputOriginalRandomUUID; if (!original) return false; Object.defineProperty(globalThis.crypto, \"randomUUID\", { configurable: true, value: original }); delete globalThis.__humanInputOriginalRandomUUID; return true; })()";
}
