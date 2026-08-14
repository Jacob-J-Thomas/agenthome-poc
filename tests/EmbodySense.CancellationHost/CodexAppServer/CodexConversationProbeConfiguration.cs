namespace EmbodySense.CancellationHost.CodexAppServer;

internal sealed record CodexConversationProbeConfiguration(
    string Version,
    string[] AdvertisedModels,
    string ResponsePrefix,
    string? TurnFailureMessage,
    bool WaitForTurnRelease,
    bool RequestGovernedTool,
    string? TurnReadyMarkerPath,
    string? TurnReleaseMarkerPath,
    string? ToolResponsePath);
