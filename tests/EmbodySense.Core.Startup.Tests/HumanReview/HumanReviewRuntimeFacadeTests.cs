using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using ApplicationDecisionResult = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionServiceResult;
using ApplicationDecisionStatus = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionServiceStatus;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using CommonDecisionOperationDisposition = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionOperationDisposition;
using CommonRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using StartupDecisionKind = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionKind;
using StartupDecisionOperationDisposition = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionOperationDisposition;
using StartupFrontierStatus = EmbodySense.Core.Startup.HumanReview.Models.GovernedLoopFrontierStatus;
using StartupLifecycleStatus = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewLifecycleStatus;
using StartupPreviewKind = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewPreviewKind;
using StartupPurpose = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewPurpose;
using StartupRunStatus = EmbodySense.Core.Startup.HumanReview.Models.CustomLoopRunStatus;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

public sealed class HumanReviewRuntimeFacadeTests
{
    private static readonly DateTimeOffset _now = new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public async Task List_rejects_unbounded_requests_and_preserves_opaque_cursor()
    {
        var store = new RecordingRunStore { Page = new CustomLoopRunPage([], "cursor-next") };
        var facade = CreateFacade(store);

        var invalid = await facade.ListAsync(new HumanReviewPageRequest(0));
        var tooLarge = await facade.ListAsync(new HumanReviewPageRequest(HumanReviewRuntimeFacade.MaxPageSize + 1));
        var tooLong = await facade.ListAsync(new HumanReviewPageRequest(1, new string('x', CustomLoopLimits.MaxRunPageCursorCharacters + 1)));
        var page = await facade.ListAsync(new HumanReviewPageRequest(1, "cursor-before"));

        Assert.Equal(HumanReviewPageStatus.Invalid, invalid.Status);
        Assert.Equal(HumanReviewPageStatus.Invalid, tooLarge.Status);
        Assert.Equal(HumanReviewPageStatus.Invalid, tooLong.Status);
        Assert.Equal(HumanReviewPageStatus.Ready, page.Status);
        Assert.Equal("cursor-next", page.ContinuationCursor);
        Assert.Equal(["cursor-before"], store.Cursors);
    }

    [Fact]
    public async Task List_skips_tombstones_but_missing_non_deleted_record_is_ambiguous()
    {
        var tombstone = Summary("run-deleted", isDeleted: true);
        var missing = Summary("run-missing", isDeleted: false);
        var store = new RecordingRunStore { Page = new CustomLoopRunPage([tombstone, missing], null) };
        var facade = CreateFacade(store);

        var result = await facade.ListAsync(new HumanReviewPageRequest(2));

        Assert.Equal(HumanReviewPageStatus.Ambiguous, result.Status);
        Assert.Equal(["run-missing"], store.GetIds);
    }

    [Fact]
    public async Task List_maps_unavailable_and_malformed_canonical_pages_fail_closed()
    {
        var unavailable = CreateFacade(new RecordingRunStore { PageException = new IOException("offline") });
        var unavailableResult = await unavailable.ListAsync();
        var malformed = CreateFacade(new RecordingRunStore { Page = new CustomLoopRunPage([null!], null) });
        var malformedResult = await malformed.ListAsync();
        var overlongCursor = CreateFacade(new RecordingRunStore { Page = new CustomLoopRunPage([], new string('x', CustomLoopLimits.MaxRunPageCursorCharacters + 1)) });
        var overlongResult = await overlongCursor.ListAsync();

        Assert.Equal(HumanReviewPageStatus.Unavailable, unavailableResult.Status);
        Assert.Equal(HumanReviewPageStatus.Ambiguous, malformedResult.Status);
        Assert.Equal(HumanReviewPageStatus.Ambiguous, overlongResult.Status);
    }

    [Fact]
    public async Task Exact_reads_distinguish_invalid_missing_corrupt_and_unavailable_runs()
    {
        var invalid = CreateFacade(new RecordingRunStore());
        var invalidResult = await invalid.ReadAsync("not valid!");
        var missing = CreateFacade(new RecordingRunStore { Run = null });
        var missingResult = await missing.ReadAsync("run-missing");
        var corrupt = CreateFacade(new RecordingRunStore { Run = MalformedRun("run-corrupt") });
        var corruptResult = await corrupt.ReadAsync("run-corrupt");
        var unavailable = CreateFacade(new RecordingRunStore { GetException = new IOException("offline") });
        var unavailableResult = await unavailable.ReadAsync("run-unavailable");

        Assert.Equal(HumanReviewReadStatus.Invalid, invalidResult.Status);
        Assert.Null(invalidResult.Detail);
        Assert.Equal(HumanReviewReadStatus.NotFound, missingResult.Status);
        Assert.Null(missingResult.Detail);
        Assert.Equal(HumanReviewReadStatus.Corrupt, corruptResult.Status);
        Assert.Null(corruptResult.Detail);
        Assert.Equal(HumanReviewReadStatus.Unavailable, unavailableResult.Status);
        Assert.Null(unavailableResult.Detail);
    }

    [Fact]
    public async Task Evidence_and_posture_reads_never_return_ready_with_a_null_record()
    {
        var facade = CreateFacade(new RecordingRunStore { Run = null });

        var evidence = await facade.ReadEvidenceAsync("run-missing");
        var posture = await facade.ReadRuntimePostureAsync("run-missing");

        Assert.Equal(HumanReviewEvidenceReadStatus.NotFound, evidence.Status);
        Assert.Empty(evidence.Evidence);
        Assert.Null(evidence.EffectEvidence);
        Assert.Equal(HumanReviewReadStatus.NotFound, posture.Status);
        Assert.Null(posture.Posture);
    }

    [Theory]
    [InlineData(ApplicationDecisionStatus.Accepted, HumanReviewDecisionStatus.Accepted)]
    [InlineData(ApplicationDecisionStatus.InformationRequested, HumanReviewDecisionStatus.InformationRequested)]
    [InlineData(ApplicationDecisionStatus.Denied, HumanReviewDecisionStatus.Denied)]
    [InlineData(ApplicationDecisionStatus.Conflict, HumanReviewDecisionStatus.Conflict)]
    [InlineData(ApplicationDecisionStatus.Expired, HumanReviewDecisionStatus.Expired)]
    [InlineData(ApplicationDecisionStatus.Replayed, HumanReviewDecisionStatus.Replayed)]
    [InlineData(ApplicationDecisionStatus.NotFound, HumanReviewDecisionStatus.NotFound)]
    [InlineData(ApplicationDecisionStatus.Invalid, HumanReviewDecisionStatus.Invalid)]
    [InlineData(ApplicationDecisionStatus.Unavailable, HumanReviewDecisionStatus.Unavailable)]
    [InlineData(ApplicationDecisionStatus.LimitExceeded, HumanReviewDecisionStatus.LimitExceeded)]
    public async Task Decide_maps_every_canonical_service_status(ApplicationDecisionStatus source, HumanReviewDecisionStatus expected)
    {
        var decisions = new RecordingDecisionService { Result = new ApplicationDecisionResult(source, null) };
        var facade = CreateFacade(new RecordingRunStore(), decisions);

        var result = await facade.DecideAsync(new HumanReviewDecisionOperationInput("run-one", 2, "operation-one", StartupDecisionKind.Approve, null));

        Assert.Equal(expected, result.Status);
        Assert.Equal("operation-one", result.OperationId);
        Assert.Null(result.Evidence);
        Assert.NotNull(decisions.Command);
        Assert.Equal("run-one", decisions.Command!.RunId);
        Assert.Equal(2, decisions.Command.ExpectedLifecycleVersion);
    }

    [Fact]
    public async Task Decide_rejects_malformed_input_before_calling_canonical_service()
    {
        var decisions = new RecordingDecisionService();
        var facade = CreateFacade(new RecordingRunStore(), decisions);

        var result = await facade.DecideAsync(new HumanReviewDecisionOperationInput("run-one", 0, "operation-one", StartupDecisionKind.Approve, null));

        Assert.Equal(HumanReviewDecisionStatus.Invalid, result.Status);
        Assert.Null(decisions.Command);
    }

    [Fact]
    public async Task Decide_projects_only_detached_receipt_fields()
    {
        var receipt = new HumanReviewDecisionOperationReceipt(
            1,
            "operation-one",
            Hash,
            new HumanReviewRequestReference("request-one", Hash),
            CommonDecisionOperationDisposition.Accepted,
            new HumanReviewDecisionReference("decision-one", "operation-one", CommonDecisionKind.Approve, Hash),
            _now,
            null!,
            Hash);
        var decisions = new RecordingDecisionService { Result = new ApplicationDecisionResult(ApplicationDecisionStatus.Accepted, receipt) };
        var facade = CreateFacade(new RecordingRunStore(), decisions);

        var result = await facade.DecideAsync(new HumanReviewDecisionOperationInput("run-one", 1, "operation-one", StartupDecisionKind.Approve, null));

        Assert.Equal(HumanReviewDecisionStatus.Accepted, result.Status);
        Assert.Equal("operation-one", result.Evidence!.OperationId);
        Assert.Equal("request-one", result.Evidence.RequestId);
        Assert.Equal(StartupDecisionOperationDisposition.Accepted, result.Evidence.Disposition);
        Assert.Equal(StartupDecisionKind.Approve, result.Evidence.DecisionKind);
        Assert.Equal(Hash, result.Evidence.ProposalHash);
        Assert.Equal(Hash, result.Evidence.ReceiptHash);
    }

    [Fact]
    public void Public_projections_make_defensive_copies_and_redact_authority_shape()
    {
        var item = new HumanReviewSummary("run-one", "request-one", Hash, StartupPurpose.Continuation, [StartupDecisionKind.Approve], StartupLifecycleStatus.Pending, StartupRunStatus.Paused, StartupFrontierStatus.ReviewBlocked, 2, _now, _now.AddMinutes(5));
        var sourceItems = new List<HumanReviewSummary> { item };
        var page = new HumanReviewPage(HumanReviewPageStatus.Ready, sourceItems, "cursor");
        sourceItems.Clear();
        var sourcePreviews = new List<HumanReviewPreview> { new(StartupPreviewKind.Action, "Action", "safe", Hash) };
        var detail = new HumanReviewDetail(item, sourcePreviews, [], [], new HumanReviewRuntimePosture(StartupRunStatus.Paused, StartupFrontierStatus.ReviewBlocked, StartupLifecycleStatus.Pending, HumanReviewContinuationStatus.NotReserved, 2, 0, 0, 0, _now), null);
        sourcePreviews.Clear();

        Assert.Single(page.Items);
        Assert.Single(detail.Previews);
        Assert.DoesNotContain("Binding", string.Join('|', detail.GetType().GetProperties().Select(property => property.Name)), StringComparison.Ordinal);
        Assert.DoesNotContain("Grant", string.Join('|', detail.GetType().GetProperties().Select(property => property.Name)), StringComparison.Ordinal);
    }

    private static HumanReviewRuntimeFacade CreateFacade(RecordingRunStore store, RecordingDecisionService? decisions = null)
        => HumanReviewRuntimeFacadeTestFactory.Create(store, decisions ?? new RecordingDecisionService());

    private static CustomLoopRunSummary Summary(string id, bool isDeleted)
        => new(id, "loop-one", "admission-one", 1, 1, isDeleted ? CommonRunStatus.Completed : CommonRunStatus.Paused, _now, _now, isDeleted ? _now : null, 0, 0, null, isDeleted);

    private static CustomLoopRunRecord MalformedRun(string id)
        => new(CustomLoopRunRecord.CurrentSchemaVersion, id, "loop-one", 1, CommonRunStatus.Admitted, _now, _now, null, "web", new CustomLoopModelSnapshot("provider", "model"), "admission-one", "actor-one", Hash, CustomLoopDefinition.CreateSeed("loop-one", "role-one", "step-one", "operation-one", _now), "prompt", null, CustomLoopContextSnapshot.CreateEmpty(_now), CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [], null, null, null);

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}
