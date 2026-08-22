using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

internal static class ModelUsageLedgerReadAuthentication
{
    internal static bool TryAuthenticate(
        GovernedModelUsageLedgerReadResult? value,
        GovernedModelUsageLedgerIdentity identity,
        out GovernedModelUsageLedgerReadResult? authenticated)
    {
        authenticated = null;
        try
        {
            if (value is null || !Enum.IsDefined(value.Status) || value.Status == 0 || value.Generation < 0)
            {
                return false;
            }

            var entries = ModelProfileApplicationContractCopy.Snapshot(
                value.Entries,
                GovernedModelContractLimits.MaxUsageLedgerEntries,
                nameof(value.Entries));
            var valid = value.Status switch
            {
                GovernedModelUsageLedgerReadStatus.NotFound => value.Generation == 0 && entries.Count == 0,
                GovernedModelUsageLedgerReadStatus.Unavailable => value.Generation == 0 && entries.Count == 0,
                GovernedModelUsageLedgerReadStatus.Found => GovernedModelUsageLedgerHistoryValidator.IsValid(entries, identity, value.Generation),
                _ => false
            };
            if (!valid)
            {
                return false;
            }

            authenticated = new GovernedModelUsageLedgerReadResult(value.Status, entries, value.Generation);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
