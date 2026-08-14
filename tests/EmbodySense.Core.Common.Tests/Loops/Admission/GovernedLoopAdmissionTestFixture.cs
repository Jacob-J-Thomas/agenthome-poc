using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
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
        AuthorityGrantProfilePin? grantProfile = null,
        AuthorityGrantBoundary? grantBoundary = null,
        string? grantDependencyEvidenceHash = null,
        AuthorityCeiling? effectiveAuthority = null,
        CapabilityAdmissionSnapshot? capabilityAdmission = null,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        DateTimeOffset? evaluatedAtUtc = null,
        string? intentHash = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var exactGrant = AuthorityGrantTestFixture.Grant();
        var authority = effectiveAuthority ?? EffectiveAuthority();
        var capabilities = capabilityAdmission ?? CapabilityAdmission() with { WorkspaceScopeId = exactIntent.WorkspaceId };
        var exactReferences = references ?? GovernedLoopAdmissionContractHash.CreateEvidenceReferences(exactIntent, authority, capabilities);
        var candidate = new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            intentHash ?? GovernedLoopAdmissionContractHash.ComputeIntentHash(exactIntent),
            binding ?? GovernedLoopExecutionBinding.Create(1, "run-1", exactIntent.Publication.Revision, 1),
            grantProfile ?? exactGrant.Binding.Profile,
            grantBoundary ?? exactGrant.Boundary,
            grantDependencyEvidenceHash ?? Hash('9'),
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
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial = null,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial = null,
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references = null,
        DateTimeOffset? rejectedAtUtc = null,
        bool applyHash = true)
    {
        var exactIntent = intent ?? Intent();
        var rejectedAt = rejectedAtUtc ?? RecordedAtUtc;
        var exactAuthorityDenial = failureCode == GovernedLoopAdmissionFailureCode.AuthorityDenied
            ? authorityDenial ?? AuthorityDenialProof(rejectedAt)
            : authorityDenial;
        var exactCapabilityDenial = failureCode == GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied
            ? capabilityDenial ?? CapabilityDenialProof(evaluatedAtUtc: rejectedAt)
            : capabilityDenial;
        var exactReferences = references ?? (Enum.IsDefined(failureCode) && failureCode != GovernedLoopAdmissionFailureCode.None
            ? GovernedLoopAdmissionContractHash.CreateRejectionEvidenceReferences(
                exactIntent,
                failureCode,
                exactAuthorityDenial,
                exactCapabilityDenial)
            : [RoleReference(exactIntent)]);
        var candidate = new GovernedLoopAdmissionRejection(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            exactIntent,
            failureCode,
            exactAuthorityDenial,
            exactCapabilityDenial,
            exactReferences,
            rejectedAt,
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

    internal static GovernedLoopAdmissionAuthorityDenialProof AuthorityDenialProof(DateTimeOffset? evaluatedAtUtc = null)
    {
        var evaluatedAt = evaluatedAtUtc ?? RecordedAtUtc;
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            AuthorityBoundaryReceipt.CurrentSchemaVersion,
            AuthorityBoundaryDecision.Deny,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.ProfileRetired)],
            [],
            evaluatedAt,
            out var receipt,
            out var validation), string.Join(',', validation.Errors));
        return new GovernedLoopAdmissionAuthorityDenialProof(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            EffectiveAuthority(),
            AuthorityCeilingIntersection.EmptyCeiling(),
            receipt!);
    }

    internal static GovernedLoopAdmissionCapabilityDenialProof CapabilityDenialProof(
        CapabilityDependencyManifest? requirements = null,
        AuthorityCeiling? effectiveAuthority = null,
        IReadOnlyList<GovernedLoopAdmissionCapabilityDenialViolation>? violations = null,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        var exactRequirements = requirements ?? LoopCapabilityRequirements.CreateDefaultConversationManifest();
        var authority = effectiveAuthority ?? EffectiveAuthority();
        Assert.True(CapabilityDependencyManifestHash.TryCompute(exactRequirements, out var requirementsHash, out _));
        var exactViolations = violations ?? exactRequirements.Required
            .Where(dependency => !authority.Capabilities.Any(identity => identity.Id.Equals(dependency.CapabilityId) && dependency.CompatibleVersionRange.Contains(identity.Version)))
            .OrderBy(dependency => dependency.CapabilityId.Value, StringComparer.Ordinal)
            .Select(dependency => new GovernedLoopAdmissionCapabilityDenialViolation(
                dependency.CapabilityId,
                dependency.CompatibleVersionRange,
                GovernedLoopAdmissionCapabilityDenialReason.RequiredCapabilityOutsideEffectiveAuthority))
            .ToArray();
        return new GovernedLoopAdmissionCapabilityDenialProof(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            exactRequirements,
            requirementsHash!.Value,
            authority,
            exactViolations,
            evaluatedAtUtc ?? RecordedAtUtc);
    }
}
