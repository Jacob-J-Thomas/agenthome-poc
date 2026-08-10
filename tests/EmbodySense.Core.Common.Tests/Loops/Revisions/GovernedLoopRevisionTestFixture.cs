using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Revisions;

internal static class GovernedLoopRevisionTestFixture
{
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 10, 4, 0, 0, TimeSpan.Zero);
    internal static readonly string RequestHash = new('a', 64);
    internal static readonly string AuthorityHash = new('b', 64);
    internal static readonly string ValidationHash = new('c', 64);

    internal static GovernedLoopRevisionReference Revision(int number, char hash = 'd', string graphId = "graph")
        => GovernedLoopRevisionReference.Create(1, graphId, $"revision-{number}", new string(hash, 64));

    internal static GovernedLoopRevisionPublicationPin Pin(
        GovernedLoopRevisionReference revision,
        string operationId = "publish-1",
        string? validationHash = null)
        => GovernedLoopRevisionPublicationPinFactory.Create(1, revision, operationId, validationHash ?? ValidationHash);

    internal static GovernedLoopRevisionLifecycleHead DraftHead(
        GovernedLoopRevisionReference? draft = null,
        long version = 1,
        string operationId = "create-1",
        DateTimeOffset? updatedAtUtc = null)
    {
        var revision = draft ?? Revision(1);
        return GovernedLoopRevisionLifecycleHeadFactory.Create(1, revision.GraphId, version, GovernedLoopRevisionLifecycleStatus.Draft, revision, null, operationId, updatedAtUtc ?? CreatedAtUtc);
    }

    internal static GovernedLoopRevisionLifecycleHead PublishedHead(
        GovernedLoopRevisionPublicationPin? publication = null,
        GovernedLoopRevisionReference? draft = null,
        long version = 2,
        string operationId = "publish-1",
        DateTimeOffset? updatedAtUtc = null)
    {
        var pin = publication ?? Pin(Revision(1));
        return GovernedLoopRevisionLifecycleHeadFactory.Create(1, pin.Revision.GraphId, version, GovernedLoopRevisionLifecycleStatus.Published, draft, pin, operationId, updatedAtUtc ?? CreatedAtUtc.AddMinutes(version));
    }

    internal static GovernedLoopRevisionLifecycleHead DisabledHead(
        GovernedLoopRevisionPublicationPin? publication = null,
        GovernedLoopRevisionReference? draft = null,
        long version = 3,
        string operationId = "disable-1",
        DateTimeOffset? updatedAtUtc = null)
    {
        var pin = publication ?? Pin(Revision(1));
        return GovernedLoopRevisionLifecycleHeadFactory.Create(1, pin.Revision.GraphId, version, GovernedLoopRevisionLifecycleStatus.Disabled, draft, pin, operationId, updatedAtUtc ?? CreatedAtUtc.AddMinutes(version));
    }

    internal static GovernedLoopRevisionOperationEvidence Evidence(
        GovernedLoopRevisionOperationKind kind,
        GovernedLoopRevisionOperationOutcome outcome,
        GovernedLoopRevisionOperationFailureCode failureCode,
        string operationId,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead? result,
        GovernedLoopRevisionReference? candidate,
        GovernedLoopRevisionReference? target,
        GovernedLoopRevisionPublicationPin? rollbackSource = null,
        string? validationHash = null)
    {
        return GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-1",
            RequestHash,
            kind,
            outcome,
            failureCode,
            previous,
            result,
            candidate,
            target,
            rollbackSource,
            AuthorityHash,
            validationHash,
            (result?.UpdatedAtUtc ?? previous?.UpdatedAtUtc ?? CreatedAtUtc).AddSeconds(1));
    }
}
