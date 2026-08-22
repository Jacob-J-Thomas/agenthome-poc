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
    string? ToolResponsePath,
    string? GovernedToolPromptMarker = null,
    string? GovernedToolPath = null,
    string? ProtocolTracePath = null,
    string? TurnFailurePromptMarker = null);
