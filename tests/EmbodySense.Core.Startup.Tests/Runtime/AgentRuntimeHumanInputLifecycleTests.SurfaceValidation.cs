using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeHumanInputLifecycleTests
{
    [Fact]
    public async Task Human_input_surface_validates_lifecycle_and_response_dtos_before_authority_resolution()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await AgentRuntimeFactoryTests.CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var reference = new HumanInputSurfaceRequestReference("request-surface", "version-surface", HumanInputRequestStoreTestData.HashA);

        var nullLifecycle = await runtime.HumanInput.SubmitSurfaceLifecycleAsync(null);
        var invalidLifecycleKind = await runtime.HumanInput.SubmitSurfaceLifecycleAsync(new HumanInputSurfaceLifecycleOperationInput("surface-lifecycle-kind", "not-an-operation", "request-surface", 1, "Pending", reference, null, "reject"));
        var invalidLifecycleStatus = await runtime.HumanInput.SubmitSurfaceLifecycleAsync(new HumanInputSurfaceLifecycleOperationInput("surface-lifecycle-status", "Reject", "request-surface", 1, "not-a-status", reference, null, "reject"));
        var invalidLifecycleOperation = await runtime.HumanInput.SubmitSurfaceLifecycleAsync(new HumanInputSurfaceLifecycleOperationInput("surface/lifecycle", "Reject", "request-surface", 1, "Pending", reference, null, "reject"));
        var validLifecycle = await runtime.HumanInput.SubmitSurfaceLifecycleAsync(new HumanInputSurfaceLifecycleOperationInput("surface-lifecycle-valid", "Reject", "request-surface", 1, "Pending", reference, null, "reject"));
        var nullResponse = await runtime.HumanInput.SubmitSurfaceResponseAsync(null);
        var invalidResponseKind = await runtime.HumanInput.SubmitSurfaceResponseAsync(new HumanInputSurfaceResponseOperationInput("surface-response-kind", "not-an-operation", "request-surface", 1, "Pending", reference, "response-surface", JsonSerializer.SerializeToElement(new { kind = "text", text = "value" }), null));
        var missingResponseReference = await runtime.HumanInput.SubmitSurfaceResponseAsync(new HumanInputSurfaceResponseOperationInput("surface-response-reference", "Submit", "request-surface", 1, "Pending", null, "response-surface", JsonSerializer.SerializeToElement(new { kind = "text", text = "value" }), null));
        var invalidResponseValue = await runtime.HumanInput.SubmitSurfaceResponseAsync(new HumanInputSurfaceResponseOperationInput("surface-response-value", "Submit", "request-surface", 1, "Pending", reference, "response-surface", default, null));
        var validResponse = await runtime.HumanInput.SubmitSurfaceResponseAsync(new HumanInputSurfaceResponseOperationInput("surface-response-valid", "Submit", "request-surface", 1, "Pending", reference, "response-surface", JsonSerializer.SerializeToElement(new { kind = "text", text = "value" }), null));

        Assert.Equal(HumanInputOperationStatus.Invalid, nullLifecycle.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycleKind.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycleStatus.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycleOperation.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, validLifecycle.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, nullResponse.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidResponseKind.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, missingResponseReference.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidResponseValue.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, validResponse.Status);
    }
}
