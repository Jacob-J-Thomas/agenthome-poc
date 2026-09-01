using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Builds exact Human Input supersede candidates from canonical state and active grant evidence.</summary>
/// <remarks>Surface proposals provide only successor data. The current binding, eligible respondents, continuation, and
/// grant reference are copied from the canonical aggregate and never reconstructed from browser input.</remarks>
public sealed class HumanInputSupersedeCandidatePreparer : IHumanInputSupersedeCandidatePreparer
{
    private readonly IHumanInputRequestCatalog _catalog;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IHumanInputSupersedeCandidateRegistry _registry;
    private readonly string _workspaceId;
    private readonly string _actor;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a preparer over one canonical request catalog, grant resolver, candidate registry, actor, and clock.</summary>
    /// <param name="catalog">The canonical request catalog.</param>
    /// <param name="grantResolver">The canonical grant resolver.</param>
    /// <param name="registry">The bounded process-local candidate registry.</param>
    /// <param name="workspaceId">The server-owned workspace identity.</param>
    /// <param name="actor">The server-owned actor attribution.</param>
    /// <param name="timeProvider">The trusted preparation clock.</param>
    public HumanInputSupersedeCandidatePreparer(IHumanInputRequestCatalog catalog, IAuthorityGrantResolver grantResolver, IHumanInputSupersedeCandidateRegistry registry, string workspaceId, string actor, TimeProvider timeProvider)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        _workspaceId = workspaceId;
        _actor = actor;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<HumanInputSupersedePreparationResult> PrepareAsync(HumanInputSupersedePreparationInput? input, CancellationToken cancellationToken = default)
    {
        if (!IsShapeValid(input))
        {
            return Result(input?.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_input");
        }

        var proposal = input!;
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        if (now == default || proposal.ExpiresAtUtc <= now || proposal.ExpiresAtUtc - now > HumanInputLimits.MaxResponseWindow)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, "invalid_expiry");
        }

        HumanInputRequestCatalogReadResult read;
        try
        {
            read = await _catalog.ReadAsync(proposal.RequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "catalog_unavailable");
        }

        if (read is null || read.Status == HumanInputRequestCatalogReadStatus.NotFound)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.NotFound, "request_not_found");
        }

        if (read.Status != HumanInputRequestCatalogReadStatus.Ready || read.Entry is null)
        {
            return Result(proposal.RequestId, MapReadStatus(read?.Status), "request_unavailable");
        }

        var head = read.Entry.Lifecycle.Head;
        var expectedReference = ToReference(proposal.ExpectedRequest!);
        if (head is null || head.CurrentRequest is null || head.Status != HumanInputRequestLifecycleStatus.Pending || expectedReference is null || !Equals(head.CurrentRequest, expectedReference) || head.LifecycleVersion != proposal.ExpectedLifecycleVersion || !string.Equals(proposal.ExpectedLifecycleStatus, HumanInputRequestLifecycleStatus.Pending.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Conflict, "request_state_conflict");
        }

        HumanInputRequest[] current;
        try
        {
            current = read.Entry.Lifecycle.RequestVersions
                .Where(request => request is not null && Matches(request, head.CurrentRequest))
                .Take(2)
                .ToArray();
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "request_evidence_ambiguous");
        }

        var currentIsValid = false;
        try
        {
            currentIsValid = current.Length == 1 && HumanInputRequestHash.Matches(current[0]);
        }
        catch (Exception)
        {
            currentIsValid = false;
        }

        if (!currentIsValid)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "request_evidence_ambiguous");
        }

        HumanInputRequestLifecycleOperationEvidence[] grantEvidence;
        try
        {
            grantEvidence = read.Entry.Lifecycle.Operations
                .Where(operation => operation is not null && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                    && operation.GrantReference is not null
                    && Equals(operation.ResultHead, head))
                .OrderByDescending(operation => operation.RecordedAtUtc)
                .ThenByDescending(operation => operation.OperationId, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "grant_evidence_ambiguous");
        }
        if (grantEvidence.Length != 1 || grantEvidence[0].GrantReference is null)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Ambiguous, "grant_evidence_ambiguous");
        }

        AuthorityGrantResolution grant;
        try
        {
            grant = await _grantResolver.ResolveAsync(grantEvidence[0].GrantReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "grant_unavailable");
        }

        if (grant is null || grant.Status != AuthorityGrantResolutionStatus.Active || !Equals(grant.RequestedReference, grantEvidence[0].GrantReference))
        {
            return Result(proposal.RequestId, grant?.Status is AuthorityGrantResolutionStatus.NotFound or AuthorityGrantResolutionStatus.Invalid
                ? HumanInputSupersedePreparationStatus.NotFound
                : HumanInputSupersedePreparationStatus.Denied, "grant_inactive");
        }

        if (!TryParseSuccessor(proposal, current[0], now, out var candidate, out var failure))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Invalid, failure);
        }

        var grantReference = grantEvidence[0].GrantReference!;
        var registration = new HumanInputSupersedeCandidateRegistration(
            _workspaceId,
            _actor,
            proposal.OperationId,
            proposal.RequestId,
            head.LifecycleVersion,
            expectedReference,
            candidate!,
            grantReference,
            proposal.ExpiresAtUtc);
        if (!_registry.TryRegister(registration, out var candidateKey))
        {
            return Result(proposal.RequestId, HumanInputSupersedePreparationStatus.Unavailable, "candidate_registry_unavailable");
        }

        return new HumanInputSupersedePreparationResult(HumanInputSupersedePreparationStatus.Ready, proposal.RequestId, candidateKey, proposal.ExpiresAtUtc, null);
    }

    private bool TryParseSuccessor(HumanInputSupersedePreparationInput input, HumanInputRequest current, DateTimeOffset now, out HumanInputRequest? candidate, out string failure)
    {
        candidate = null;
        failure = "invalid_successor";
        if (input.ResponseSchema.ValueKind != JsonValueKind.Object || input.ResponsePolicy.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var options = JsonOptions();
            var schema = JsonSerializer.Deserialize<HumanInputResponseSchema>(input.ResponseSchema.GetRawText(), options);
            var policy = JsonSerializer.Deserialize<HumanInputResponsePolicy>(input.ResponsePolicy.GetRawText(), options);
            if (schema is null || policy is null || !TryParsePrivacy(input.PrivacyClass, out var privacy))
            {
                return false;
            }

            var successor = current with
            {
                RequestId = $"supersede-{Guid.NewGuid():N}",
                RequestVersionId = $"version-{Guid.NewGuid():N}",
                Purpose = input.Purpose,
                Prompt = input.Prompt,
                ResponseSchema = schema,
                PrivacyClass = privacy,
                Timing = new HumanInputTiming(now, input.ExpiresAtUtc),
                ResponsePolicy = policy,
                RequestHash = string.Empty
            };
            candidate = HumanInputRequestHash.Apply(successor);
            if (!HumanInputRequestSnapshot.TryCapture(candidate, out candidate, out _)
                || candidate is null
                || !HumanInputRequestHash.Matches(candidate)
                || !string.Equals(candidate.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            {
                candidate = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            failure = "invalid_successor_json";
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static bool TryParsePrivacy(string? value, out HumanInputPrivacyClass privacy)
        => Enum.TryParse(value, ignoreCase: true, out privacy)
            && Enum.IsDefined(privacy)
            && privacy != HumanInputPrivacyClass.Unknown
            && string.Equals(value, privacy.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsShapeValid(HumanInputSupersedePreparationInput? input)
        => input is not null
            && HumanInputIdentifier.IsValid(input.OperationId)
            && !string.IsNullOrWhiteSpace(input.RequestId)
            && input.ExpectedRequest is not null
            && string.Equals(input.ExpectedRequest.RequestId, input.RequestId, StringComparison.Ordinal)
            && input.ExpectedLifecycleVersion >= 1
            && !string.IsNullOrWhiteSpace(input.ExpectedLifecycleStatus)
            && !string.IsNullOrWhiteSpace(input.Purpose)
            && !string.IsNullOrWhiteSpace(input.Prompt)
            && !string.IsNullOrWhiteSpace(input.PrivacyClass);

    private static bool Matches(HumanInputRequest request, HumanInputRequestReference reference)
        => request.SchemaVersion == reference.SchemaVersion
            && string.Equals(request.RequestId, reference.RequestId, StringComparison.Ordinal)
            && string.Equals(request.RequestVersionId, reference.RequestVersionId, StringComparison.Ordinal)
            && string.Equals(request.RequestHash, reference.RequestHash, StringComparison.Ordinal);

    private static HumanInputRequestReference? ToReference(HumanInputSurfaceRequestReference? reference)
        => reference is null ? null : new HumanInputRequestReference(HumanInputRequestReference.CurrentSchemaVersion, reference.RequestId, reference.RequestVersionId, reference.RequestHash);

    private static HumanInputSupersedePreparationStatus MapReadStatus(HumanInputRequestCatalogReadStatus? status)
        => status switch
        {
            HumanInputRequestCatalogReadStatus.Invalid => HumanInputSupersedePreparationStatus.Invalid,
            HumanInputRequestCatalogReadStatus.NotFound => HumanInputSupersedePreparationStatus.NotFound,
            HumanInputRequestCatalogReadStatus.Unavailable => HumanInputSupersedePreparationStatus.Unavailable,
            _ => HumanInputSupersedePreparationStatus.Ambiguous,
        };

    private static HumanInputSupersedePreparationResult Result(string? requestId, HumanInputSupersedePreparationStatus status, string error)
        => new(status, requestId ?? string.Empty, null, null, error);
}
