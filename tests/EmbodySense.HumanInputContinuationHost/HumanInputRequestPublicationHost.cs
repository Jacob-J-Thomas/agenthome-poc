using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Publication.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.HumanInputContinuationHost;

internal static class HumanInputRequestPublicationHost
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var workspaceRoot, var runId, var checkpointId, var checkpointHash, var utcTicksText, var grantPath, var crashBoundaryText, var crashOrdinalText, var resultPath]
            || !long.TryParse(utcTicksText, out var utcTicks)
            || !int.TryParse(crashOrdinalText, out var crashOrdinal)
            || crashOrdinal < 1
            || !AuthorityGrantJson.TryDeserialize(await File.ReadAllTextAsync(grantPath).ConfigureAwait(false), out var grant, out _)
            || grant is null)
        {
            return 2;
        }

        var now = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        var clock = new HumanInputResponseContinuationHostClock(now);
        var paths = new WorkspacePaths(workspaceRoot);
        var crash = new HumanInputRequestPublicationHostCrashObserver(crashBoundaryText, crashOrdinal);
        using var runs = new CustomLoopRunStore(paths, clock);
        var requests = new HumanInputRequestStore(paths, new HumanInputRequestStoreOptions
        {
            DurableBoundaryObserver = crash.ObserveAsync
        });
        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var publication = new HumanInputRequestPublicationService(
            runs,
            requests,
            new HumanInputRequestPublicationHostGrantResolver(grant, now),
            authorityTransaction,
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            clock);
        var result = await publication.PublishAsync(new HumanInputRequestPublicationRequest(runId, checkpointId, checkpointHash)).ConfigureAwait(false);
        await File.WriteAllTextAsync(resultPath, result.Status.ToString()).ConfigureAwait(false);
        return result.Status is HumanInputRequestPublicationStatus.Published or HumanInputRequestPublicationStatus.Replayed ? 0 : 3;
    }
}
