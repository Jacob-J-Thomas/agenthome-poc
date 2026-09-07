namespace EmbodySense.E2ETests.Web;

internal static class EffectReconciliationCatalogRecoveryContract
{
    internal const string TemporaryUnavailableMessage = "Effect Reconciliation is temporarily unavailable. Refresh after the runtime is healthy.";

    internal static bool CanRefresh(bool viewVisible, string? listBusy, string? listStatus, bool refreshVisible, bool refreshDisabled)
        => viewVisible
            && string.Equals(listBusy, "false", StringComparison.Ordinal)
            && string.Equals(listStatus, TemporaryUnavailableMessage, StringComparison.Ordinal)
            && refreshVisible
            && !refreshDisabled;
}
