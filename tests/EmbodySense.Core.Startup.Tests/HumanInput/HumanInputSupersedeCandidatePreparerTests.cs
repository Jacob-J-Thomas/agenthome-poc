using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

public sealed class HumanInputSupersedeCandidatePreparerTests
{
    [Fact]
    public async Task Invalid_shape_expiry_and_cancellation_fail_closed_before_catalog_access()
    {
        var catalog = new HumanInputSupersedeCandidatePreparerTestCatalog();
        var preparer = CreatePreparer(catalog, new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Unknown, null, null!, EmptyCeiling(), string.Empty, default));

        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await preparer.PrepareAsync(null)).Status);
        var valid = Input(DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await preparer.PrepareAsync(valid with { Purpose = string.Empty })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await preparer.PrepareAsync(valid with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) })).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await preparer.PrepareAsync(valid with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(31) })).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparer.PrepareAsync(valid, cancellation.Token));
    }

    [Fact]
    public async Task Catalog_dispositions_and_failures_are_mapped_without_reading_private_state()
    {
        var catalog = new HumanInputSupersedeCandidatePreparerTestCatalog();
        var preparer = CreatePreparer(catalog, new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Unknown, null, null!, EmptyCeiling(), string.Empty, default));
        var input = Input(DateTimeOffset.UtcNow.AddMinutes(2));

        catalog.ReadException = new InvalidOperationException("private catalog detail");
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, (await preparer.PrepareAsync(input)).Status);
        catalog.ReadException = null;
        foreach (var (status, expected) in new[]
        {
            (HumanInputRequestCatalogReadStatus.NotFound, HumanInputSupersedePreparationStatus.NotFound),
            (HumanInputRequestCatalogReadStatus.Invalid, HumanInputSupersedePreparationStatus.Invalid),
            (HumanInputRequestCatalogReadStatus.Unavailable, HumanInputSupersedePreparationStatus.Unavailable),
            (HumanInputRequestCatalogReadStatus.Ambiguous, HumanInputSupersedePreparationStatus.Ambiguous),
            (HumanInputRequestCatalogReadStatus.Unknown, HumanInputSupersedePreparationStatus.Ambiguous)
        })
        {
            catalog.ReadResponse = new HumanInputRequestCatalogReadResult(status, 1, null);
            Assert.Equal(expected, (await preparer.PrepareAsync(input)).Status);
        }

        catalog.ReadResponse = new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.Ready, 1, null);
        Assert.Equal(HumanInputSupersedePreparationStatus.Ambiguous, (await preparer.PrepareAsync(input)).Status);
    }

    [Fact]
    public async Task Exact_pending_catalog_and_active_grant_produce_one_opaque_replayable_candidate()
    {
        var mutation = HumanInputRequestStoreTestData.CreateMutation("request-preparer", "version-preparer", "create-preparer");
        var lifecycle = new HumanInputRequestLifecycleStoreSnapshot(mutation.PrimaryHeadToWrite!, [mutation.RequestToAppend!], [mutation.Operation]);
        var catalog = new HumanInputSupersedeCandidatePreparerTestCatalog
        {
            ReadResponse = new HumanInputRequestCatalogReadResult(
                HumanInputRequestCatalogReadStatus.Ready,
                1,
                new HumanInputRequestCatalogEntry(lifecycle, null!))
        };
        var grant = mutation.Operation.GrantReference!;
        var resolver = new HumanInputSupersedeCandidatePreparerTestGrantResolver(new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Active, grant, null!, EmptyCeiling(), "grant-evidence", mutation.Operation.RecordedAtUtc));
        var registry = new HumanInputSupersedeCandidateRegistry();
        var preparer = new HumanInputSupersedeCandidatePreparer(catalog, resolver, registry, mutation.RequestToAppend!.Binding.WorkspaceId, "user-one", TimeProvider.System);
        var input = new HumanInputSupersedePreparationInput(
            "prepare-operation",
            mutation.RequestToAppend.RequestId,
            new HumanInputSurfaceRequestReference(mutation.RequestToAppend.RequestId, mutation.RequestToAppend.RequestVersionId, mutation.RequestToAppend.RequestHash),
            mutation.PrimaryHeadToWrite!.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Pending.ToString(),
            "successor-purpose",
            "successor-prompt",
            JsonSerializer.SerializeToElement(mutation.RequestToAppend.ResponseSchema, JsonOptions()),
            HumanInputPrivacyClass.Private.ToString(),
            DateTimeOffset.UtcNow.AddMinutes(2),
            JsonSerializer.SerializeToElement(mutation.RequestToAppend.ResponsePolicy, JsonOptions()));

        var result = await preparer.PrepareAsync(input);

        Assert.Equal(HumanInputSupersedePreparationStatus.Ready, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.CandidateKey));
        Assert.Equal(input.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.True(registry.TryResolve(result.CandidateKey!, mutation.RequestToAppend.Binding.WorkspaceId, "user-one", input.OperationId, input.RequestId, input.ExpectedLifecycleVersion, input.ExpectedRequest!.RequestVersionId, input.ExpectedRequest.RequestHash, DateTimeOffset.UtcNow, out var resolution));
        Assert.NotNull(resolution);
        Assert.NotEqual(input.RequestId, resolution!.CandidateRequest.RequestId);
        Assert.Equal(input.Purpose, resolution.CandidateRequest.Purpose);
        Assert.Equal(input.Prompt, resolution.CandidateRequest.Prompt);

        var duplicateNestedProperty = input with { ResponseSchema = Json("""{"kind":"text","choices":[{"choiceId":"choice","displayText":"one","displayText":"two"}]}""") };
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await preparer.PrepareAsync(duplicateNestedProperty)).Status);
        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, (await preparer.PrepareAsync(input with { ResponseSchema = Json("[]") })).Status);
        foreach (var (grantStatus, expectedStatus) in new[]
        {
            (AuthorityGrantResolutionStatus.NotFound, HumanInputSupersedePreparationStatus.NotFound),
            (AuthorityGrantResolutionStatus.Invalid, HumanInputSupersedePreparationStatus.NotFound),
            (AuthorityGrantResolutionStatus.Revoked, HumanInputSupersedePreparationStatus.Denied),
            (AuthorityGrantResolutionStatus.Unavailable, HumanInputSupersedePreparationStatus.Denied)
        })
        {
            resolver.Resolution = new AuthorityGrantResolution(grantStatus, grant, null!, EmptyCeiling(), string.Empty, mutation.Operation.RecordedAtUtc);
            Assert.Equal(expectedStatus, (await preparer.PrepareAsync(input with { OperationId = $"prepare-grant-{grantStatus.ToString().ToLowerInvariant()}" })).Status);
        }

        resolver.ResolveException = new InvalidOperationException("private grant detail");
        Assert.Equal(HumanInputSupersedePreparationStatus.Unavailable, (await preparer.PrepareAsync(input with { OperationId = "prepare-grant-error" })).Status);
    }

    [Fact]
    public async Task Invalid_operation_id_is_rejected_before_catalog_access()
    {
        var catalog = new HumanInputSupersedeCandidatePreparerTestCatalog();
        var preparer = CreatePreparer(catalog, new AuthorityGrantResolution(AuthorityGrantResolutionStatus.Unknown, null, null!, EmptyCeiling(), string.Empty, default));

        var result = await preparer.PrepareAsync(Input(DateTimeOffset.UtcNow.AddMinutes(2)) with { OperationId = "operation/invalid" });

        Assert.Equal(HumanInputSupersedePreparationStatus.Invalid, result.Status);
        Assert.Equal(0, catalog.ReadCount);
    }

    private static HumanInputSupersedeCandidatePreparer CreatePreparer(HumanInputSupersedeCandidatePreparerTestCatalog catalog, AuthorityGrantResolution resolution)
        => new(catalog, new HumanInputSupersedeCandidatePreparerTestGrantResolver(resolution), new HumanInputSupersedeCandidateRegistry(), "workspace", "user-one", TimeProvider.System);

    private static HumanInputSupersedePreparationInput Input(DateTimeOffset expiresAtUtc)
        => new("operation", "request", new HumanInputSurfaceRequestReference("request", "version", HumanInputRequestStoreTestData.HashA), 1, "Pending", "purpose", "prompt", Json("{}"), "Private", expiresAtUtc, Json("{}"));

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static AuthorityCeiling EmptyCeiling() => new([], [], 0, CapabilitySideEffectClass.None, false, false, false);
}
