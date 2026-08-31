using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Admission.Models;

namespace EmbodySense.Tests.Support;

internal static class GovernedLoopAdmissionCrossProcessWriterHost
{
    private const int GateTimeoutSeconds = 75;
    private const int RetryWindowSeconds = 15;
    private static readonly DateTimeOffset _grantRecordedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _capabilityAdmittedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _evaluatedAtUtc = _capabilityAdmittedAtUtc.AddMinutes(1);
    private static readonly DateTimeOffset _recordedAtUtc = _evaluatedAtUtc.AddMinutes(1);

    internal static async Task<int> RunAsync(
        string workspace,
        string trustRoot,
        string gate,
        string ready,
        string output,
        string operation)
    {
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForGateAsync(gate);
        var paths = new WorkspacePaths(workspace);
        var store = new GovernedLoopAdmissionStore(paths, new FileCapabilityCatalogTrustProvider(trustRoot));
        var mutation = CreateMutation(paths, operation, operation.EndsWith("two", StringComparison.Ordinal) ? '4' : '1');
        var retryWindow = Stopwatch.StartNew();
        GovernedLoopAdmissionStoreCommitResult result;
        do
        {
            result = await store.CommitAsync(mutation);
            if (result.Status != GovernedLoopAdmissionStoreCommitStatus.Unavailable
                || retryWindow.Elapsed >= TimeSpan.FromSeconds(RetryWindowSeconds))
            {
                break;
            }

            await Task.Delay(50);
        }
        while (true);
        await File.WriteAllTextAsync(output, result.Status.ToString());
        return 0;
    }

    internal static GovernedLoopAdmissionStoreMutation CreateMutation(WorkspacePaths paths, string operationId, char requestHash, long expectedStoreGeneration = 0)
    {
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var grant = CreateGrant();
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            workspaceId,
            operationId,
            Hash(requestHash),
            grant.Binding.Loop,
            new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            grant.Binding.Role,
            ParseActor("user-owner"),
            "cli",
            Hash('2'),
            Hash('3'));
        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(
            LoopCapabilityRequirements.CreateDefaultConversationManifest(),
            _capabilityAdmittedAtUtc) with
        {
            WorkspaceScopeId = workspaceId
        };
        var authority = new AuthorityCeiling(
            [],
            [ParseDataClass("workspace-content")],
            2,
            CapabilitySideEffectClass.ReadOnly,
            false,
            false,
            false);
        var binding = GovernedLoopExecutionBinding.Create(1, "run-1", intent.Publication.Revision, 1);
        var modelRoutingAdmission = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
            intent,
            binding,
            grant.Binding.Profile,
            grant.Boundary,
            Hash('9'),
            authority,
            capabilityAdmission,
            _evaluatedAtUtc);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            binding,
            grant.Binding.Profile,
            grant.Boundary,
            Hash('9'),
            authority,
            capabilityAdmission,
            modelRoutingAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, authority, capabilityAdmission, modelRoutingAdmission),
            _evaluatedAtUtc,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            intent,
            evidence,
            _recordedAtUtc,
            string.Empty));
        var outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            intent,
            GovernedLoopAdmissionDisposition.Admitted,
            receipt,
            null,
            _recordedAtUtc,
            string.Empty));
        var validation = GovernedLoopAdmissionValidator.Validate(outcome);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The cross-process admission writer mutation is invalid: " + string.Join(',', validation.Errors));
        }

        return new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            operationId,
            intent.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            expectedStoreGeneration,
            outcome);
    }

    private static AuthorityGrant CreateGrant()
    {
        var profile = new AuthorityGrantProfilePin(
            new AuthorityProfileReference(ParseProfileId("default-profile"), ParseProfileRevision("3")),
            ParseProfileHash("sha256:" + new string('a', 64)));
        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("bounded-helper", 4), new string('b', 64));
        var revision = GovernedLoopRevisionReference.Create(1, "governed-loop", "revision-7", new string('c', 64));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, revision, "publish-7", new string('d', 64));
        var binding = new AuthorityGrantBinding(profile, role, publication);
        return AuthorityGrantHash.Apply(new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            ParseGrantId("workspace-helper"),
            ParseGrantRevision("1"),
            null,
            null,
            AuthorityGrantLifecycleStatus.Active,
            binding,
            new AuthorityCeiling([], [ParseDataClass("workspace-content")], 5, CapabilitySideEffectClass.ReadOnly, false, false, false),
            new AuthorityGrantBoundary(_grantRecordedAtUtc.AddMinutes(-5), _grantRecordedAtUtc.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            ParseActor("user-owner"),
            ParsePurpose("Delegate bounded work for one governed loop revision."),
            _grantRecordedAtUtc,
            string.Empty));
    }

    private static async Task WaitForGateAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(GateTimeoutSeconds))
            {
                throw new TimeoutException($"Cross-process admission writer did not observe gate `{path}`.");
            }

            await Task.Delay(10);
        }
    }

    private static string Hash(char value) => new(value, GovernedLoopAdmissionLimits.Sha256HexCharacters);

    private static AuthorityActorId ParseActor(string value)
        => AuthorityActorId.TryParse(value, out var actor, out _) ? actor! : throw new InvalidOperationException();

    private static AuthorityPurpose ParsePurpose(string value)
        => AuthorityPurpose.TryParse(value, out var purpose, out _) ? purpose! : throw new InvalidOperationException();

    private static AuthorityProfileId ParseProfileId(string value)
        => AuthorityProfileId.TryParse(value, out var id, out _) ? id! : throw new InvalidOperationException();

    private static AuthorityProfileRevision ParseProfileRevision(string value)
        => AuthorityProfileRevision.TryParse(value, out var revision, out _) ? revision! : throw new InvalidOperationException();

    private static AuthorityProfileHash ParseProfileHash(string value)
        => AuthorityProfileHash.TryParse(value, out var hash, out _) ? hash! : throw new InvalidOperationException();

    private static AuthorityGrantId ParseGrantId(string value)
        => AuthorityGrantId.TryParse(value, out var id, out _) ? id! : throw new InvalidOperationException();

    private static AuthorityGrantRevision ParseGrantRevision(string value)
        => AuthorityGrantRevision.TryParse(value, out var revision, out _) ? revision! : throw new InvalidOperationException();

    private static CapabilityDataClass ParseDataClass(string value)
        => CapabilityDataClass.TryParse(value, out var dataClass, out _) ? dataClass! : throw new InvalidOperationException();
}
