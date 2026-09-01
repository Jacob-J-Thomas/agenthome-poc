using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Loops.Schedules.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Schedule_authoring_rejects_malformed_intent()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var nullInput = await runtime.GovernedLoopScheduleAuthoring.CreateAsync(null);
        var invalidInput = await runtime.GovernedLoopScheduleAuthoring.CreateAsync(new GovernedLoopScheduleAuthoringInput(
            "schedule-invalid-shape",
            "graph-valid-shape",
            "revision-valid-shape",
            0,
            null,
            ScheduleRecurrenceKind.FixedInterval,
            DateTime.UtcNow,
            60,
            "UTC",
            ScheduleInvalidLocalTimePolicy.Skip,
            ScheduleAmbiguousLocalTimePolicy.EarlierUtc,
            ScheduleMisfirePolicyKind.Skip,
            0,
            ScheduleOverlapPolicy.Skip,
            SchedulePriority.Normal,
            true));

        Assert.Equal("invalid", nullInput.Status);
        Assert.Equal(string.Empty, nullInput.OperationId);
        Assert.Equal("invalid", invalidInput.Status);
        Assert.Equal("schedule-invalid-shape", invalidInput.OperationId);
    }

    [Fact]
    public async Task Schedule_authoring_reports_a_missing_graph()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var response = await runtime.GovernedLoopScheduleAuthoring.CreateAsync(new GovernedLoopScheduleAuthoringInput(
            "schedule-missing-graph",
            "graph-that-is-not-persisted",
            "revision-that-is-not-persisted",
            1,
            null,
            ScheduleRecurrenceKind.FixedInterval,
            DateTime.SpecifyKind(DateTime.UtcNow.AddHours(1), DateTimeKind.Unspecified),
            60,
            "UTC",
            ScheduleInvalidLocalTimePolicy.Skip,
            ScheduleAmbiguousLocalTimePolicy.EarlierUtc,
            ScheduleMisfirePolicyKind.Skip,
            0,
            ScheduleOverlapPolicy.Skip,
            SchedulePriority.Normal,
            true));

        Assert.Equal("not-found", response.Status);
        Assert.Equal("schedule-missing-graph", response.OperationId);
        Assert.Contains("graph", response.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Schedule);
    }
}
