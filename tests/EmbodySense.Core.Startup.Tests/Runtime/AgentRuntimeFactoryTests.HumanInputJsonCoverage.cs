using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Human_input_surface_response_rejects_nested_duplicate_json_properties()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Cli);
        using var document = JsonDocument.Parse("{\"value\":{\"kind\":\"text\",\"text\":\"one\",\"text\":\"two\"}}");
        var input = new HumanInputSurfaceResponseOperationInput(
            "operation-json-duplicate",
            HumanInputResponseOperationKind.Submit.ToString(),
            "request-one",
            1,
            HumanInputRequestLifecycleStatus.Pending.ToString(),
            new HumanInputSurfaceRequestReference("request-one", "version-one", HumanInputRequestStoreTestData.HashA),
            "response-one",
            document.RootElement.Clone(),
            null);

        var result = await runtime.HumanInput.SubmitSurfaceResponseAsync(input);

        Assert.Equal(HumanInputOperationStatus.Invalid, result.Status);
        var invalidOperation = await runtime.HumanInput.SubmitSurfaceResponseAsync(input with { OperationId = "operation/invalid" });
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidOperation.Status);
    }
}
