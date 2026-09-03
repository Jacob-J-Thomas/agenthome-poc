using System.Text.Json;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Web.Controllers;
using EmbodySense.Web.Models;
using EmbodySense.Web;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Tests;

public sealed class HumanInputControllerTests
{
    [Fact]
    public async Task List_validates_bounds_and_maps_every_closed_page_status()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);

        Assert.Throws<ArgumentNullException>(() => new HumanInputController(null!));
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.List(0)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.List(51)).ResultStatusCode());

        foreach (var (status, expected) in new[]
        {
            (HumanInputRequestPosturePageStatus.Ready, StatusCodes.Status200OK),
            (HumanInputRequestPosturePageStatus.Invalid, StatusCodes.Status400BadRequest),
            (HumanInputRequestPosturePageStatus.Stale, StatusCodes.Status409Conflict),
            (HumanInputRequestPosturePageStatus.Unavailable, StatusCodes.Status503ServiceUnavailable),
            (HumanInputRequestPosturePageStatus.Ambiguous, StatusCodes.Status503ServiceUnavailable),
            (HumanInputRequestPosturePageStatus.Unknown, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.PageResponse = new HumanInputRequestPosturePage(status, 1, [], null);
            Assert.Equal(expected, (await controller.List(5, "opaque-cursor")).ResultStatusCode());
        }

        Assert.Equal(5, runtime.LastPageRequest!.MaximumCount);
        Assert.Equal("opaque-cursor", runtime.LastPageRequest.Cursor);
    }

    [Fact]
    public async Task Get_rejects_blank_ids_and_maps_exact_read_statuses()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);

        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Get(" ")).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Get(string.Empty)).ResultStatusCode());

        var posture = new HumanInputRequestPosture(1, "request-1", 1, default, null!, null!, 0, null, null, DateTimeOffset.UnixEpoch, 0, 0, 0, false, null);
        foreach (var (status, request, expected) in new[]
        {
            (HumanInputRequestPostureReadStatus.Ready, posture, StatusCodes.Status200OK),
            (HumanInputRequestPostureReadStatus.Ready, (HumanInputRequestPosture?)null, StatusCodes.Status503ServiceUnavailable),
            (HumanInputRequestPostureReadStatus.Invalid, (HumanInputRequestPosture?)null, StatusCodes.Status400BadRequest),
            (HumanInputRequestPostureReadStatus.NotFound, (HumanInputRequestPosture?)null, StatusCodes.Status404NotFound),
            (HumanInputRequestPostureReadStatus.Unavailable, (HumanInputRequestPosture?)null, StatusCodes.Status503ServiceUnavailable),
            (HumanInputRequestPostureReadStatus.Ambiguous, (HumanInputRequestPosture?)null, StatusCodes.Status503ServiceUnavailable),
            (HumanInputRequestPostureReadStatus.Unknown, (HumanInputRequestPosture?)null, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.ReadResponse = new HumanInputRequestPostureReadResult(status, 1, request);
            Assert.Equal(expected, (await controller.Get("request-1")).ResultStatusCode());
        }

        Assert.Equal("request-1", runtime.LastReadRequestId);
    }

    [Fact]
    public async Task Answer_requires_exact_route_reference_and_maps_every_operation_status()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);
        var expectedRequest = Reference();
        var valid = new HumanInputWebResponseRequest("operation-1", 3, "Pending", expectedRequest, "response-1", Json("{\"value\":true}"), "bounded explanation");

        Assert.Equal(StatusCodes.Status409Conflict, (await controller.Answer("request-1", null)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.Answer("other-request", valid)).ResultStatusCode());

        foreach (var (status, expected) in OperationStatuses())
        {
            runtime.OperationResponse = new HumanInputOperationResult(status, "operation-1", null, null, []);
            Assert.Equal(expected, (await controller.Answer("request-1", valid)).ResultStatusCode());
        }

        Assert.Equal("Submit", runtime.LastResponseInput!.Kind);
        Assert.Equal("request-1", runtime.LastResponseInput.RequestId);
        Assert.Equal("hash-1", runtime.LastResponseInput.ExpectedRequest!.RequestHash);
    }

    [Fact]
    public async Task Lifecycle_routes_enforce_exact_reference_candidate_and_operation_statuses()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);
        var expectedRequest = Reference();
        var valid = new HumanInputWebLifecycleRequest("operation-1", 3, "Pending", expectedRequest, "bounded reason");

        Assert.Equal(StatusCodes.Status409Conflict, (await controller.Reject("request-1", null)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.Cancel("other-request", valid)).ResultStatusCode());
        foreach (var (status, expected) in OperationStatuses())
        {
            runtime.OperationResponse = new HumanInputOperationResult(status, "operation-1", null, null, []);
            Assert.Equal(expected, (await controller.Reject("request-1", valid)).ResultStatusCode());
        }

        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Supersede("request-1", valid)).ResultStatusCode());
        var supersede = valid with { CandidateKey = "opaque-candidate" };
        foreach (var (status, expected) in OperationStatuses())
        {
            runtime.OperationResponse = new HumanInputOperationResult(status, "operation-1", null, null, []);
            Assert.Equal(expected, (await controller.Supersede("request-1", supersede)).ResultStatusCode());
        }

        Assert.Equal("Supersede", runtime.LastLifecycleInput!.Kind);
        Assert.Equal("opaque-candidate", runtime.LastLifecycleInput.CandidateKey);
    }

    [Fact]
    public async Task Remind_reroute_and_amend_routes_preserve_exact_terms_and_candidate_requirements()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);
        var expectedRequest = Reference();
        var lifecycle = new HumanInputWebLifecycleRequest("operation-1", 3, "Pending", expectedRequest, "operator-reason");

        runtime.OperationResponse = new HumanInputOperationResult(HumanInputOperationStatus.Committed, "operation-1", null, null, []);
        Assert.Equal(StatusCodes.Status200OK, (await controller.Remind("request-1", lifecycle)).ResultStatusCode());
        Assert.Equal("Remind", runtime.LastLifecycleInput!.Kind);
        Assert.Null(runtime.LastLifecycleInput.CandidateKey);
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Remind("request-1", lifecycle with { CandidateKey = "unexpected" })).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Reject("request-1", lifecycle with { CandidateKey = "unexpected" })).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Cancel("request-1", lifecycle with { CandidateKey = "unexpected" })).ResultStatusCode());

        var candidateExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var reroutePreparation = new HumanInputWebReroutePreparationRequest("operation-2", 3, "Pending", expectedRequest, candidateExpiry);
        runtime.ReroutePreparationResponse = new HumanInputReroutePreparationResult(
            HumanInputSupersedePreparationStatus.Ready,
            "request-1",
            [new HumanInputRerouteCandidateOption("candidate-1", "Alternative respondent set", 2, DateTimeOffset.UtcNow.AddMinutes(5))],
            candidateExpiry,
            null);
        Assert.Equal(StatusCodes.Status200OK, (await controller.PrepareReroute("request-1", reroutePreparation)).ResultStatusCode());
        Assert.Equal("operation-2", runtime.LastReroutePreparationInput!.OperationId);
        Assert.Equal("hash-1", runtime.LastReroutePreparationInput.ExpectedRequest!.RequestHash);

        var reroute = lifecycle with { OperationId = "operation-3", CandidateKey = "candidate-1" };
        Assert.Equal(StatusCodes.Status200OK, (await controller.Reroute("request-1", reroute)).ResultStatusCode());
        Assert.Equal("Reroute", runtime.LastLifecycleInput.Kind);
        Assert.Equal("candidate-1", runtime.LastLifecycleInput.CandidateKey);
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Reroute("request-1", lifecycle)).ResultStatusCode());

        var amendmentExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var amendPreparation = new HumanInputWebAmendPreparationRequest("operation-4", 3, "Pending", expectedRequest, "new purpose", "new prompt", "Public", amendmentExpiry, candidateExpiry);
        runtime.AmendPreparationResponse = new HumanInputAmendPreparationResult(HumanInputSupersedePreparationStatus.Ready, "request-1", "candidate-2", candidateExpiry, null);
        Assert.Equal(StatusCodes.Status200OK, (await controller.PrepareAmend("request-1", amendPreparation)).ResultStatusCode());
        Assert.Equal("new purpose", runtime.LastAmendPreparationInput!.Purpose);
        Assert.Equal("new prompt", runtime.LastAmendPreparationInput.Prompt);
        Assert.Equal("Public", runtime.LastAmendPreparationInput.PrivacyClass);
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Amend("request-1", lifecycle)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status200OK, (await controller.Amend("request-1", lifecycle with { OperationId = "operation-5", CandidateKey = "candidate-2" })).ResultStatusCode());
        Assert.Equal("Amend", runtime.LastLifecycleInput.Kind);
    }

    [Fact]
    public async Task Candidate_preparation_maps_statuses_and_rejects_route_mismatch()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);
        var expectedRequest = Reference();
        var candidateExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var reroute = new HumanInputWebReroutePreparationRequest("operation-1", 3, "Pending", expectedRequest, candidateExpiry);
        var amend = new HumanInputWebAmendPreparationRequest("operation-2", 3, "Pending", expectedRequest, "purpose", "prompt", "Public", DateTimeOffset.UtcNow.AddMinutes(5), candidateExpiry);

        Assert.Equal(StatusCodes.Status409Conflict, (await controller.PrepareReroute("other-request", reroute)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.PrepareAmend("other-request", amend)).ResultStatusCode());

        foreach (var (status, expected) in new[]
        {
            (HumanInputSupersedePreparationStatus.Ready, StatusCodes.Status200OK),
            (HumanInputSupersedePreparationStatus.Invalid, StatusCodes.Status400BadRequest),
            (HumanInputSupersedePreparationStatus.NotFound, StatusCodes.Status404NotFound),
            (HumanInputSupersedePreparationStatus.Conflict, StatusCodes.Status409Conflict),
            (HumanInputSupersedePreparationStatus.Ambiguous, StatusCodes.Status409Conflict),
            (HumanInputSupersedePreparationStatus.LimitExceeded, StatusCodes.Status409Conflict),
            (HumanInputSupersedePreparationStatus.Denied, StatusCodes.Status403Forbidden),
            (HumanInputSupersedePreparationStatus.Unavailable, StatusCodes.Status503ServiceUnavailable),
            (HumanInputSupersedePreparationStatus.Unknown, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.ReroutePreparationResponse = new HumanInputReroutePreparationResult(status, "request-1", [], null, null);
            Assert.Equal(expected, (await controller.PrepareReroute("request-1", reroute)).ResultStatusCode());
            runtime.AmendPreparationResponse = new HumanInputAmendPreparationResult(status, "request-1", null, null, null);
            Assert.Equal(expected, (await controller.PrepareAmend("request-1", amend)).ResultStatusCode());
        }
    }

    [Fact]
    public async Task Prepare_supersede_requires_exact_route_successor_and_maps_every_preparation_status()
    {
        var runtime = new HumanInputControllerTestRuntime();
        var controller = new HumanInputController(runtime);
        var expectedRequest = Reference();
        var valid = new HumanInputWebSupersedePreparationRequest(
            "operation-1",
            3,
            "Pending",
            expectedRequest,
            new HumanInputWebSuccessorDraft("purpose", "prompt", Json("{}"), "Public", DateTimeOffset.UtcNow.AddMinutes(1), Json("{}")));

        Assert.Equal(StatusCodes.Status409Conflict, (await controller.PrepareSupersede("request-1", null)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.PrepareSupersede("other-request", valid)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.PrepareSupersede("request-1", valid with { Successor = null })).ResultStatusCode());

        foreach (var (status, expected) in new[]
        {
            (HumanInputSupersedePreparationStatus.Ready, StatusCodes.Status200OK),
            (HumanInputSupersedePreparationStatus.Invalid, StatusCodes.Status400BadRequest),
            (HumanInputSupersedePreparationStatus.NotFound, StatusCodes.Status404NotFound),
            (HumanInputSupersedePreparationStatus.Conflict, StatusCodes.Status409Conflict),
            (HumanInputSupersedePreparationStatus.Ambiguous, StatusCodes.Status409Conflict),
            (HumanInputSupersedePreparationStatus.LimitExceeded, StatusCodes.Status409Conflict),
            (HumanInputSupersedePreparationStatus.Denied, StatusCodes.Status403Forbidden),
            (HumanInputSupersedePreparationStatus.Unavailable, StatusCodes.Status503ServiceUnavailable),
            (HumanInputSupersedePreparationStatus.Unknown, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.PreparationResponse = new HumanInputSupersedePreparationResult(status, "request-1", status == HumanInputSupersedePreparationStatus.Ready ? "candidate" : null, null, null);
            Assert.Equal(expected, (await controller.PrepareSupersede("request-1", valid)).ResultStatusCode());
        }

        Assert.Equal("operation-1", runtime.LastPreparationInput!.OperationId);
        Assert.Equal("request-1", runtime.LastPreparationInput.RequestId);
        Assert.Equal("purpose", runtime.LastPreparationInput.Purpose);
    }

    [Fact]
    public void Controller_is_authenticated_and_no_store()
    {
        var type = typeof(HumanInputController);
        var authorize = Assert.Single(type.CustomAttributes, attribute => attribute.AttributeType == typeof(AuthorizeAttribute));
        Assert.Equal(WebAuthPolicies.LocalSession, authorize.NamedArguments.Single(argument => argument.MemberName == nameof(AuthorizeAttribute.Policy)).TypedValue.Value);
        var cache = Assert.Single(type.CustomAttributes, attribute => attribute.AttributeType == typeof(ResponseCacheAttribute));
        Assert.Equal(true, cache.NamedArguments.Single(argument => argument.MemberName == nameof(ResponseCacheAttribute.NoStore)).TypedValue.Value);
        Assert.Equal(ResponseCacheLocation.None, (ResponseCacheLocation)cache.NamedArguments.Single(argument => argument.MemberName == nameof(ResponseCacheAttribute.Location)).TypedValue.Value!);
        var requestSize = Assert.Single(type.CustomAttributes, attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(16_384L, requestSize.ConstructorArguments.Single().Value);
    }

    [Fact]
    public async Task Runtime_failures_are_redacted_as_service_unavailable_and_cancellation_is_preserved()
    {
        var runtime = new HumanInputControllerTestRuntime
        {
            ListException = new InvalidOperationException("private list detail"),
            ReadException = new InvalidOperationException("private read detail"),
            OperationException = new InvalidOperationException("private operation detail"),
            PreparationException = new InvalidOperationException("private preparation detail")
        };
        var controller = new HumanInputController(runtime);
        var reference = Reference();
        var lifecycle = new HumanInputWebLifecycleRequest("operation", 1, "Pending", reference, "reason");
        var response = new HumanInputWebResponseRequest("operation", 1, "Pending", reference, "response", Json("true"), null);
        var preparation = new HumanInputWebSupersedePreparationRequest("operation", 1, "Pending", reference, new HumanInputWebSuccessorDraft("purpose", "prompt", Json("{}"), "Private", DateTimeOffset.UtcNow.AddMinutes(1), Json("{}")));

        AssertUnavailable(await controller.List());
        AssertUnavailable(await controller.Get("request-1"));
        AssertUnavailable(await controller.Reject("request-1", lifecycle));
        AssertUnavailable(await controller.Answer("request-1", response));
        AssertUnavailable(await controller.PrepareSupersede("request-1", preparation));

        runtime.ListException = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.List(cancellationToken: cancellation.Token));
    }

    private static HumanInputWebRequestReference Reference() => new("request-1", "version-1", "hash-1");

    private static void AssertUnavailable<T>(ActionResult<T> result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        Assert.DoesNotContain("private", objectResult.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<(HumanInputOperationStatus Status, int Expected)> OperationStatuses() =>
    [
        (HumanInputOperationStatus.Committed, StatusCodes.Status200OK),
        (HumanInputOperationStatus.Replayed, StatusCodes.Status200OK),
        (HumanInputOperationStatus.Invalid, StatusCodes.Status400BadRequest),
        (HumanInputOperationStatus.NotFound, StatusCodes.Status404NotFound),
        (HumanInputOperationStatus.Denied, StatusCodes.Status403Forbidden),
        (HumanInputOperationStatus.Conflict, StatusCodes.Status409Conflict),
        (HumanInputOperationStatus.Late, StatusCodes.Status409Conflict),
        (HumanInputOperationStatus.Ambiguous, StatusCodes.Status409Conflict),
        (HumanInputOperationStatus.LimitExceeded, StatusCodes.Status409Conflict),
        (HumanInputOperationStatus.Unavailable, StatusCodes.Status503ServiceUnavailable),
        (HumanInputOperationStatus.Unknown, StatusCodes.Status503ServiceUnavailable)
    ];
}
