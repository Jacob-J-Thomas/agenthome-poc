using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Creates exact effect-authority requests for one admitted read-only workspace-tool operation.</summary>
public static class WorkspaceToolEffectAuthorityRequestFactory
{
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private const string RequestFingerprintDomain = "embodysense-workspace-tool-request-v1";
    private const string OperationIdentityDomain = "embodysense-workspace-tool-effect-v1";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Creates one deterministic intake or actuation request bound to complete immutable admission evidence.</summary>
    /// <param name="admissionReceipt">The complete exact successful admission receipt retained by the run.</param>
    /// <param name="executionBinding">The exact run, revision, and execution generation.</param>
    /// <param name="graphArtifact">The exact immutable graph artifact retained by the run.</param>
    /// <param name="nodeId">The exact provider-Inference node identity.</param>
    /// <param name="nodeAttempt">The exact positive node-attempt number.</param>
    /// <param name="serverCorrelationId">The exact attempt-local server correlation identity.</param>
    /// <param name="toolRequest">The bounded, correlated, read-only workspace request.</param>
    /// <param name="resolvedTargetPath">The server-resolved, normalized absolute workspace target produced before this boundary.</param>
    /// <param name="boundaryKind">The workspace-tool intake or final workspace-actuation boundary.</param>
    /// <returns>A request containing the exact admitted workspace-command pin and a non-granting one-target read-only ceiling.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required evidence object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the attempt or boundary kind is unsupported.</exception>
    /// <exception cref="ArgumentException">Thrown when any retained identity, graph, authority, pin, or tool request is not exact and bounded.</exception>
    public static GovernedLoopEffectAuthorityRequest Create(
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        GovernedLoopGraphRevisionArtifact graphArtifact,
        string nodeId,
        int nodeAttempt,
        string serverCorrelationId,
        ToolRequest toolRequest,
        string resolvedTargetPath,
        GovernedLoopEffectBoundaryKind boundaryKind)
    {
        ArgumentNullException.ThrowIfNull(admissionReceipt);
        ArgumentNullException.ThrowIfNull(executionBinding);
        ArgumentNullException.ThrowIfNull(graphArtifact);
        ArgumentNullException.ThrowIfNull(toolRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedTargetPath);

        if (boundaryKind is not (GovernedLoopEffectBoundaryKind.WorkspaceToolIntake or GovernedLoopEffectBoundaryKind.WorkspaceActuation))
        {
            throw new ArgumentOutOfRangeException(nameof(boundaryKind), boundaryKind, "Workspace-tool authority supports only the intake and final actuation boundaries.");
        }

        if (nodeAttempt is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeAttempt), nodeAttempt, "The node attempt is outside the governed effect-authority bound.");
        }

        nodeId = CustomLoopArtifactIdentifier.Require(nodeId, nameof(nodeId), GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);
        serverCorrelationId = CustomLoopArtifactIdentifier.Require(serverCorrelationId, nameof(serverCorrelationId), GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);
        ValidateRetainedEvidence(admissionReceipt, executionBinding, graphArtifact);
        var node = graphArtifact.Graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null || !Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
        {
            throw new ArgumentException("Workspace-tool authority requires the exact admitted provider-Inference node.", nameof(nodeId));
        }

        var admittedPins = admissionReceipt.Evidence.CapabilityAdmission.Pins;
        var workspacePins = admittedPins.Where(item => string.Equals(item.DescriptorIdentity.Id.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal)).ToArray();
        if (workspacePins.Length != 1
            || !node.AuthorityCeiling.CapabilityIds.Contains(WorkspaceCommandCapabilityId, StringComparer.Ordinal)
            || !admissionReceipt.Evidence.EffectiveAuthority.Capabilities.Contains(workspacePins[0].DescriptorIdentity))
        {
            throw new ArgumentException("The exact Inference-node ceiling and successful admission receipt must contain one identical workspace-command pin.", nameof(admissionReceipt));
        }

        var admittedAuthority = admissionReceipt.Evidence.EffectiveAuthority;
        if (admittedAuthority.MaxTargetCount < 1 || admittedAuthority.MaxSideEffectClass < CapabilitySideEffectClass.ReadOnly)
        {
            throw new ArgumentException("The admitted authority cannot be widened to one read-only workspace target.", nameof(admissionReceipt));
        }

        var canonicalTargetPath = RequireServerResolvedTarget(resolvedTargetPath);
        var targetFingerprint = ComputeTargetFingerprint(canonicalTargetPath);
        var requestFingerprint = ComputeRequestFingerprint(toolRequest, canonicalTargetPath, executionBinding, nodeId, nodeAttempt, serverCorrelationId);
        var requiredAuthority = new AuthorityCeiling(
            [workspacePins[0].DescriptorIdentity],
            admittedAuthority.DataClasses.ToArray(),
            1,
            CapabilitySideEffectClass.ReadOnly,
            false,
            false,
            false);
        if (!AuthorityProfileValidator.ValidateCeiling(requiredAuthority).IsValid
            || !(AuthorityCeilingSubset.IsEqual(requiredAuthority, admittedAuthority) || AuthorityCeilingSubset.IsStrictSubset(requiredAuthority, admittedAuthority)))
        {
            throw new ArgumentException("The derived workspace-tool ceiling was not an exact non-granting narrowing of admitted authority.", nameof(admissionReceipt));
        }

        var operationId = CreateOperationId(
            admissionReceipt,
            executionBinding,
            graphArtifact,
            nodeId,
            nodeAttempt,
            serverCorrelationId,
            requestFingerprint,
            boundaryKind);
        return new GovernedLoopEffectAuthorityRequest(
            admissionReceipt,
            executionBinding,
            graphArtifact,
            nodeId,
            nodeAttempt,
            operationId,
            serverCorrelationId,
            boundaryKind,
            requiredAuthority,
            workspacePins,
            targetFingerprint);
    }

    private static void ValidateRetainedEvidence(
        GovernedLoopAdmissionReceipt receipt,
        GovernedLoopExecutionBinding binding,
        GovernedLoopGraphRevisionArtifact artifact)
    {
        try
        {
            if (!GovernedLoopAdmissionValidator.Validate(receipt).IsValid
                || !Equals(binding, receipt.Evidence.Binding)
                || !string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifact.ArtifactHash, receipt.Intent.GraphArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifact.LayoutHash, receipt.Intent.GraphLayoutHash, StringComparison.Ordinal)
                || !Equals(artifact.RevisionArtifact.Revision, binding.Revision)
                || !Equals(receipt.Intent.Publication.Revision, binding.Revision)
                || !Equals(artifact.Graph.OwningRole, receipt.Intent.Role))
            {
                throw new ArgumentException("The retained admission receipt, execution binding, and graph artifact do not identify one exact admitted run.", nameof(receipt));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ArgumentException("The retained admission receipt, execution binding, or graph artifact was malformed.", nameof(receipt), exception);
        }
    }

    private static string ComputeRequestFingerprint(
        ToolRequest request,
        string canonicalTargetPath,
        GovernedLoopExecutionBinding binding,
        string nodeId,
        int nodeAttempt,
        string serverCorrelationId)
    {
        if (request.Command is not (ToolCommand.List or ToolCommand.Read or ToolCommand.Search))
        {
            throw new ArgumentException("The schema-1 workspace-tool authority adapter supports only read-only list, read, and search commands.", nameof(request));
        }

        RequireBounded(request.TargetPath, nameof(request.TargetPath), CustomLoopLimits.MaxGovernedToolTargetCharacters, required: true);
        RequireBounded(request.Content, nameof(request.Content), CustomLoopLimits.MaxGovernedToolArgumentCharacters, required: false);
        RequireBounded(request.Pattern, nameof(request.Pattern), CustomLoopLimits.MaxGovernedToolArgumentCharacters, required: false);
        RequireBounded(request.CorrelationId, nameof(request.CorrelationId), CustomLoopLimits.MaxArtifactIdCharacters, required: true);
        if (request.AuditCorrelation is { } correlation)
        {
            if (!string.Equals(correlation.RunId, binding.RunId, StringComparison.Ordinal)
                || !string.Equals(correlation.StepId, nodeId, StringComparison.Ordinal)
                || correlation.Attempt != nodeAttempt
                || !string.Equals(correlation.AttemptCorrelationId, serverCorrelationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The tool audit correlation does not identify the exact admitted run, node, attempt, and server correlation.", nameof(request));
            }
        }

        var canonical = new StringBuilder(CustomLoopLimits.MaxGovernedToolArgumentCharacters * 3);
        Append(canonical, RequestFingerprintDomain);
        Append(canonical, ((int)request.Command).ToString(CultureInfo.InvariantCulture));
        Append(canonical, canonicalTargetPath);
        Append(canonical, request.Content);
        Append(canonical, request.Pattern);
        Append(canonical, request.CorrelationId);
        AppendAuditCorrelation(canonical, request.AuditCorrelation);
        byte[] bytes;
        try
        {
            bytes = _strictUtf8.GetBytes(canonical.ToString());
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The tool request contains invalid Unicode and cannot have a canonical fingerprint.", nameof(request), exception);
        }

        if (bytes.Length > CustomLoopLimits.MaxGovernedToolRequestEvidenceUtf8Bytes)
        {
            throw new ArgumentException("The canonical tool request exceeds the governed request-evidence bound.", nameof(request));
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string RequireServerResolvedTarget(string resolvedTargetPath)
    {
        RequireBounded(resolvedTargetPath, nameof(resolvedTargetPath), CustomLoopLimits.MaxGovernedToolTargetCharacters, required: true);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolvedTargetPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The workspace target was not a valid server-resolved absolute path.", nameof(resolvedTargetPath), exception);
        }

        if (!Path.IsPathFullyQualified(resolvedTargetPath)
            || !string.Equals(fullPath, resolvedTargetPath, FileSystemPathComparer.GetPathComparison()))
        {
            throw new ArgumentException("The workspace target must be the exact normalized absolute path resolved by the server.", nameof(resolvedTargetPath));
        }

        var canonical = Path.TrimEndingDirectorySeparator(fullPath);
        return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
    }

    private static string ComputeTargetFingerprint(string canonicalTargetPath)
    {
        var canonical = new StringBuilder(canonicalTargetPath.Length + 64);
        Append(canonical, "embodysense-workspace-target-v1");
        Append(canonical, canonicalTargetPath);
        return Convert.ToHexString(SHA256.HashData(_strictUtf8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void AppendAuditCorrelation(StringBuilder canonical, ToolAuditCorrelation? correlation)
    {
        if (correlation is null)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, "present");
        Append(canonical, correlation.RunId);
        Append(canonical, correlation.LoopId);
        Append(canonical, correlation.RoleId);
        Append(canonical, correlation.DefinitionVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, correlation.DefinitionHash);
        Append(canonical, correlation.Iteration.ToString(CultureInfo.InvariantCulture));
        Append(canonical, correlation.StepId);
        Append(canonical, correlation.Attempt.ToString(CultureInfo.InvariantCulture));
        Append(canonical, correlation.AttemptCorrelationId);
        Append(canonical, correlation.AdmittedCommands);
        Append(canonical, correlation.CurrentRoleCommands);
        Append(canonical, correlation.EffectiveCommands);
        Append(canonical, correlation.RoleCeilingHash);
        Append(canonical, correlation.CatalogHash);
    }

    private static string CreateOperationId(
        GovernedLoopAdmissionReceipt receipt,
        GovernedLoopExecutionBinding binding,
        GovernedLoopGraphRevisionArtifact artifact,
        string nodeId,
        int nodeAttempt,
        string serverCorrelationId,
        string requestFingerprint,
        GovernedLoopEffectBoundaryKind boundaryKind)
    {
        var canonical = new StringBuilder(1_024);
        Append(canonical, OperationIdentityDomain);
        Append(canonical, ((int)boundaryKind).ToString(CultureInfo.InvariantCulture));
        Append(canonical, receipt.ContentHash);
        Append(canonical, binding.RunId);
        Append(canonical, binding.ExecutionGeneration.ToString(CultureInfo.InvariantCulture));
        Append(canonical, binding.Revision.GraphId);
        Append(canonical, binding.Revision.RevisionId);
        Append(canonical, binding.Revision.ExecutableHash);
        Append(canonical, artifact.ArtifactHash);
        Append(canonical, artifact.LayoutHash);
        Append(canonical, nodeId);
        Append(canonical, nodeAttempt.ToString(CultureInfo.InvariantCulture));
        Append(canonical, serverCorrelationId);
        Append(canonical, requestFingerprint);
        var digest = Convert.ToHexString(SHA256.HashData(_strictUtf8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        var prefix = boundaryKind == GovernedLoopEffectBoundaryKind.WorkspaceToolIntake
            ? "workspace-tool-intake-"
            : "workspace-tool-actuation-";
        return prefix + digest;
    }

    private static void RequireBounded(string? value, string parameterName, int maximumCharacters, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The canonical tool request field is required.", parameterName);
        }

        if (value is not null && (value.Length > maximumCharacters || value.IndexOf('\0') >= 0))
        {
            throw new ArgumentException("The canonical tool request field exceeds its safe evidence bound.", parameterName);
        }
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}
