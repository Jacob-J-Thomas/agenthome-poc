using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Web.Controllers;
using EmbodySense.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbodySense.Web.Tests;

public sealed class HumanReviewsControllerCoverageTests
{
    [Fact]
    public async Task List_projects_null_responses_runtime_failures_and_cancellation()
    {
        var runtime = new HumanReviewControllerTestRuntime { PageResponse = null };
        var controller = CreateController(runtime);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.List()).ResultStatusCode());

        runtime.PageResponse = new HumanReviewPage(HumanReviewPageStatus.Ready, [], null);
        runtime.PageException = new InvalidOperationException("private runtime detail");
        var failed = await controller.List();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failed.ResultStatusCode());
        Assert.DoesNotContain("private", failed.RawObjectValue()?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        runtime.PageException = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.List(cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Read_projects_null_responses_runtime_failures_and_cancellation()
    {
        var runtime = new HumanReviewControllerTestRuntime { ReadResponse = null };
        var controller = CreateController(runtime);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.Read("run-1")).ResultStatusCode());

        runtime.ReadResponse = new HumanReviewReadResult(HumanReviewReadStatus.NotFound);
        runtime.ReadException = new InvalidOperationException("private runtime detail");
        var failed = await controller.Read("run-1");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failed.ResultStatusCode());
        Assert.DoesNotContain("private", failed.RawObjectValue()?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        runtime.ReadException = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Read("run-1", cancellation.Token));
    }

    [Fact]
    public async Task ReadEvidence_projects_null_responses_runtime_failures_and_cancellation()
    {
        var runtime = new HumanReviewControllerTestRuntime { EvidenceResponse = null };
        var controller = CreateController(runtime);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.ReadEvidence("run-1")).ResultStatusCode());

        runtime.EvidenceResponse = new HumanReviewEvidenceReadResult(HumanReviewEvidenceReadStatus.NotFound, [], null);
        runtime.EvidenceException = new InvalidOperationException("private runtime detail");
        var failed = await controller.ReadEvidence("run-1");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failed.ResultStatusCode());
        Assert.DoesNotContain("private", failed.RawObjectValue()?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        runtime.EvidenceException = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ReadEvidence("run-1", cancellation.Token));
    }

    [Fact]
    public async Task ReadPosture_projects_null_responses_runtime_failures_and_cancellation()
    {
        var runtime = new HumanReviewControllerTestRuntime { PostureResponse = null };
        var controller = CreateController(runtime);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.ReadPosture("run-1")).ResultStatusCode());

        runtime.PostureResponse = new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.NotFound, null);
        runtime.PostureException = new InvalidOperationException("private runtime detail");
        var failed = await controller.ReadPosture("run-1");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failed.ResultStatusCode());
        Assert.DoesNotContain("private", failed.RawObjectValue()?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        runtime.PostureException = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ReadPosture("run-1", cancellation.Token));
    }

    [Fact]
    public async Task Decisions_reject_null_body_and_project_null_response_and_cancellation()
    {
        var runtime = new HumanReviewControllerTestRuntime { DecisionResponse = null };
        var controller = CreateController(runtime);

        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Approve("run-1", null)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.Approve("run-1", new WebHumanReviewDecisionRequest(1, "operation-1", null))).ResultStatusCode());

        runtime.DecisionResponse = new HumanReviewDecisionResult(HumanReviewDecisionStatus.Invalid, "operation-2", null);
        runtime.DecisionException = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Approve("run-1", new WebHumanReviewDecisionRequest(1, "operation-2", null), cancellation.Token));
    }

    private static HumanReviewsController CreateController(HumanReviewControllerTestRuntime runtime)
        => new(runtime, new HumanReviewControllerTestNotifier(), NullLogger<HumanReviewsController>.Instance);
}
