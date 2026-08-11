using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Tests.Authority.Grants;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

internal static class GovernedLoopAdmissionTestFixture
{
    internal const string WorkspaceId = "workspace-sha256:1111111111111111111111111111111111111111111111111111111111111111";
    internal const string OperationId = "admit-operation-1";
    internal const string Surface = "cli";

    internal static readonly DateTimeOffset CapabilityAdmittedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset EvaluatedAtUtc = CapabilityAdmittedAtUtc.AddMinutes(1);
    internal static readonly DateTimeOffset RecordedAtUtc = EvaluatedAtUtc.AddMinutes(1);

    internal static GovernedLoopAdmissionIntent Intent(
        string? workspaceId = null,
        string? operationId = null,
        string? requestHash = null,
        GovernedLoopRevisionPublicationPin? publication = null,
        AuthorityGrantReference? authorityGrant = null,
        ContextualRoleRevisionPin? role = null,
        AuthorityActorId? actorId = null,
        string? surface = null,
        string? graphArtifactHash = null,
        string? graphLayoutHash = null)
    {
        var grant = AuthorityGrantTestFixture.Grant();
        return new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            workspaceId ?? WorkspaceId,
            operationId ?? OperationId,
            requestHash ?? Hash('1'),
            publication ?? grant.Binding.Loop,
            authorityGrant ?? new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            role ?? grant.Binding.Role,
            actorId ?? AuthorityGrantTestFixture.Actor("user-owner"),
            surface ?? Surface,
            graphArtifactHash ?? Hash('2'),
            graphLayoutHash ?? Hash('3'));
    }

    internal static CapabilityAdmissionSnapshot CapabilityAdmission(DateTimeOffset? admittedAtUtc = null)
        => TestCapabilityAdmissionFactory.Create(
            LoopCapabilityRequirements.CreateDefaultConversationManifest(),
            admittedAtUtc ?? CapabilityAdmittedAtUtc);

    internal static AuthorityCeiling EffectiveAuthority(
        int maxTargetCount = 2,
        bool allowsRecurrence = false,
        bool allowsExternalPublication = false,
        bool allowsIrreversibleAction = false)
        => AuthorityGrantTestFixture.Ceiling(
            maxTargetCount: maxTargetCount,
            allowsRecurrence: allowsRecurrence,
            allowsExternalPublication: allowsExternalPublication,
            allowsIrreversibleAction: allowsIrreversibleAction);

    internal static GovernedLoopAdmissionEvidence Evidence(
        GovernedLoopAdmissionIntent? intent = null,
        GovernedLoopExecutionBinding? binding = null,
        AuthorityCeiling? effectiveAuthority = null,
        CapabilityAdmissionSnapshot? capabilityAdmission = null,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        DateTimeOffset? evaluatedAtUtc = null,
        string? intentHash = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var authority = effectiveAuthority ?? EffectiveAuthority();
        var capabilities = capabilityAdmission ?? CapabilityAdmission();
        var exactReferences = references ?? GovernedLoopAdmissionContractHash.CreateEvidenceReferences(exactIntent, authority, capabilities);
        var candidate = new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            intentHash ?? GovernedLoopAdmissionContractHash.ComputeIntentHash(exactIntent),
            binding ?? GovernedLoopExecutionBinding.Create(1, "run-1", exactIntent.Publication.Revision, 1),
            authority,
            capabilities,
            exactReferences,
            evaluatedAtUtc ?? EvaluatedAtUtc,
            string.Empty);
        return applyHash ? GovernedLoopAdmissionContractHash.Apply(candidate) : candidate;
    }

    internal static GovernedLoopAdmissionReceipt Receipt(
        GovernedLoopAdmissionIntent? intent = null,
        GovernedLoopAdmissionEvidence? evidence = null,
        DateTimeOffset? recordedAtUtc = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var candidate = new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            exactIntent,
            evidence ?? Evidence(exactIntent),
            recordedAtUtc ?? RecordedAtUtc,
            string.Empty);
        return applyHash ? GovernedLoopAdmissionContractHash.Apply(candidate) : candidate;
    }

    internal static GovernedLoopAdmissionRejection Rejection(
        GovernedLoopAdmissionIntent? intent = null,
        GovernedLoopAdmissionFailureCode failureCode = GovernedLoopAdmissionFailureCode.RoleInactive,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        DateTimeOffset? rejectedAtUtc = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var exactReferences = references ?? DefaultRejectionReferences(exactIntent, failureCode);
        var candidate = new GovernedLoopAdmissionRejection(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            exactIntent,
            failureCode,
            exactReferences,
            rejectedAtUtc ?? RecordedAtUtc,
            string.Empty);
        return applyHash ? GovernedLoopAdmissionContractHash.Apply(candidate) : candidate;
    }

    internal static GovernedLoopAdmissionTerminalOutcome AdmittedOutcome(
        GovernedLoopAdmissionIntent? intent = null,
        GovernedLoopAdmissionReceipt? receipt = null,
        DateTimeOffset? recordedAtUtc = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var exactReceipt = receipt ?? Receipt(exactIntent, recordedAtUtc: recordedAtUtc);
        var candidate = new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            exactIntent,
            GovernedLoopAdmissionDisposition.Admitted,
            exactReceipt,
            null,
            recordedAtUtc ?? exactReceipt.RecordedAtUtc,
            string.Empty);
        return applyHash ? GovernedLoopAdmissionContractHash.Apply(candidate) : candidate;
    }

    internal static GovernedLoopAdmissionTerminalOutcome RejectedOutcome(
        GovernedLoopAdmissionIntent? intent = null,
        GovernedLoopAdmissionRejection? rejection = null,
        DateTimeOffset? recordedAtUtc = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var exactRejection = rejection ?? Rejection(exactIntent, rejectedAtUtc: recordedAtUtc);
        var candidate = new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            exactIntent,
            GovernedLoopAdmissionDisposition.Rejected,
            null,
            exactRejection,
            recordedAtUtc ?? exactRejection.RejectedAtUtc,
            string.Empty);
        return applyHash ? GovernedLoopAdmissionContractHash.Apply(candidate) : candidate;
    }

    internal static GovernedLoopAdmissionEvidenceReference RoleReference(GovernedLoopAdmissionIntent intent)
        => new(
            GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
            GovernedLoopAdmissionContractHash.ComputeContextualRoleReferenceHash(intent.Role));

    internal static GovernedLoopAdmissionEvidenceReference Reference(GovernedLoopAdmissionEvidenceKind kind, char hash)
        => new(kind, Hash(hash));

    internal static string Hash(char value) => new(value, GovernedLoopAdmissionLimits.Sha256HexCharacters);

    private static IReadOnlyList<GovernedLoopAdmissionEvidenceReference> DefaultRejectionReferences(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionFailureCode failureCode)
    {
        if (!Enum.IsDefined(failureCode) || failureCode == GovernedLoopAdmissionFailureCode.None)
        {
            return [RoleReference(intent)];
        }

        var requiredKinds = GovernedLoopAdmissionValidator.RequiredRejectionEvidenceKinds(failureCode);
        var complete = GovernedLoopAdmissionContractHash.CreateEvidenceReferences(
            intent,
            EffectiveAuthority(),
            CapabilityAdmission());
        return Array.AsReadOnly(complete.Where(reference => requiredKinds.Contains(reference.Kind)).ToArray());
    }
}
