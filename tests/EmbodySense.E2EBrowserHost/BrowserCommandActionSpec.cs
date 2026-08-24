namespace EmbodySense.E2EBrowserHost;

/// <summary>Transfers only the current-platform artifact pin needed to reconstruct the fixed browser command registration.</summary>
/// <param name="ArtifactDigest">The exact staged executable digest.</param>
/// <param name="EntryPoint">The exact single-file staged entry point.</param>
public sealed record BrowserCommandActionSpec(string ArtifactDigest, string EntryPoint);
