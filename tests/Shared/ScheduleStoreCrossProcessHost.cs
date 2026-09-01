using System.Diagnostics;
using System.Globalization;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Persistence.Triggers.Schedules.Models;

namespace EmbodySense.CancellationHost.Persistence;

/// <summary>
/// Runs the schedule-store worker from either the canonical VSTest inventory adapter or the
/// authenticated cancellation-host apphost. The operation is deliberately protocol-only so the
/// apphost cannot become a second test inventory or persistence authority.
/// </summary>
internal static class ScheduleStoreCrossProcessHost
{
    internal const string WorkspaceVariable = "EMBODYSENSE_SCHEDULE_STORE_WORKSPACE";
    internal const string GateVariable = "EMBODYSENSE_SCHEDULE_STORE_GATE";
    internal const string ReadyVariable = "EMBODYSENSE_SCHEDULE_STORE_READY";
    internal const string OutputVariable = "EMBODYSENSE_SCHEDULE_STORE_OUTPUT";
    internal const string ScheduleIdVariable = "EMBODYSENSE_SCHEDULE_STORE_ID";
    internal const string CrashBoundaryVariable = "EMBODYSENSE_SCHEDULE_STORE_CRASH_BOUNDARY";
    internal const string OperationVariable = "EMBODYSENSE_SCHEDULE_STORE_OPERATION";
    internal const string VariantVariable = "EMBODYSENSE_SCHEDULE_STORE_VARIANT";
    internal const string MaxDurabilityArtifactsVariable = "EMBODYSENSE_SCHEDULE_STORE_MAX_DURABILITY_ARTIFACTS";

    private static readonly TimeSpan _gateTimeout = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(
        string workspace,
        string gate,
        string ready,
        string output,
        string scheduleIdText,
        string operation,
        string variantText,
        string maxDurabilityArtifactsText,
        string crashBoundaryText)
    {
        if (string.IsNullOrWhiteSpace(workspace)
            || string.IsNullOrWhiteSpace(gate)
            || string.IsNullOrWhiteSpace(ready)
            || string.IsNullOrWhiteSpace(output)
            || !ScheduleId.TryParse(scheduleIdText, out var scheduleId)
            || operation is not ("create" or "compare-exchange" or "compare-exchange-current")
            || !int.TryParse(variantText, NumberStyles.None, CultureInfo.InvariantCulture, out var variant)
            || variant is < 1 or > 2
            || !TryParseOptionalPositiveInt(maxDurabilityArtifactsText, out var maxDurabilityArtifacts)
            || !TryParseOptionalBoundary(crashBoundaryText, out var crashBoundary))
        {
            return 2;
        }

        var options = new ScheduleStoreOptions
        {
            MaxSchedules = 1,
            MaxDurabilityArtifacts = maxDurabilityArtifacts ?? ScheduleStoreOptions.DefaultMaximumDurabilityArtifacts,
            DurableBoundaryObserver = crashBoundary is null
                ? null
                : boundary =>
                {
                    if (boundary == crashBoundary)
                    {
                        TerminateProcess();
                    }
                },
        };
        var store = new ScheduleStore(new WorkspacePaths(workspace), options);
        var request = CreateRequest(scheduleId!);
        ScheduleState? expectedState = null;
        if (operation == "compare-exchange")
        {
            var read = await store.ReadAsync(scheduleId!);
            if (read.Status != ScheduleStoreReadStatus.Found || read.State is null)
            {
                return 3;
            }

            expectedState = read.State;
        }

        await File.WriteAllTextAsync(ready, "ready");
        await WaitForPathAsync(gate);

        ScheduleStoreMutationResult result;
        if (operation == "compare-exchange-current")
        {
            var read = await store.ReadAsync(scheduleId!);
            if (read.Status != ScheduleStoreReadStatus.Found || read.State is null)
            {
                return 3;
            }

            result = await store.CompareExchangeAsync(new(read.State, Replacement(read.State, variant)));
        }
        else if (operation == "compare-exchange")
        {
            result = await store.CompareExchangeAsync(new(expectedState!, Replacement(expectedState!, variant)));
        }
        else
        {
            result = await store.CreateAsync(request);
        }

        await File.WriteAllTextAsync(output, result.Status.ToString());
        return 0;
    }

    internal static async Task RunFromEnvironmentAsync()
    {
        var workspace = Environment.GetEnvironmentVariable(WorkspaceVariable);
        if (string.IsNullOrEmpty(workspace))
        {
            return;
        }

        var exitCode = await RunAsync(
            workspace,
            RequireEnvironment(GateVariable),
            RequireEnvironment(ReadyVariable),
            RequireEnvironment(OutputVariable),
            RequireEnvironment(ScheduleIdVariable),
            Environment.GetEnvironmentVariable(OperationVariable) ?? "create",
            Environment.GetEnvironmentVariable(VariantVariable) ?? "1",
            Environment.GetEnvironmentVariable(MaxDurabilityArtifactsVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(CrashBoundaryVariable) ?? string.Empty);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"The schedule-store cross-process host exited with status {exitCode}.");
        }
    }

    private static ScheduleStoreCreateRequest CreateRequest(ScheduleId scheduleId)
    {
        var definition = new ScheduleDefinition(
            ScheduleDefinition.CurrentSchemaVersion,
            scheduleId,
            1,
            CreateTarget(),
            CreateAdapter(),
            ParseActor("owner"),
            "scheduler",
            "workspace-1",
            "operator",
            new AuthorityProfileReference(ParseProfileId("trigger-operator"), ParseProfileRevision(7)),
            new SchedulePayloadReference("payload/" + scheduleId.Value, CapabilityIntegrityDigest.Compute([1, 2, 3, 4])),
            SchedulePriority.Normal,
            new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Daily, new DateTime(2026, 8, 12, 9, 30, 0, DateTimeKind.Unspecified), null),
            new ScheduleTimeZoneReference("America/Chicago", new string('e', 64)),
            new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.ShiftForward, ScheduleAmbiguousLocalTimePolicy.EarlierUtc),
            new ScheduleMisfirePolicy(ScheduleMisfirePolicyKind.CatchUp, 3),
            ScheduleOverlapPolicy.DeferOne,
            true);
        if (!ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out var validation))
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors.Select(error => error.Code)));
        }

        var occurrence = new ScheduleOccurrence(
            ScheduleOccurrence.CurrentSchemaVersion,
            1,
            new DateTime(2026, 8, 12, 9, 30, 0, DateTimeKind.Unspecified),
            new DateTimeOffset(2026, 8, 12, 14, 30, 0, TimeSpan.Zero),
            definition.TimeZone);
        var state = new ScheduleState(
            ScheduleState.CurrentSchemaVersion,
            scheduleId,
            definition.Revision,
            definitionHash!,
            1,
            definition.Enabled,
            occurrence,
            null,
            null,
            occurrence.ScheduledAtUtc.AddSeconds(5),
            null,
            [],
            []);
        return new ScheduleStoreCreateRequest(definition, state, definitionHash!);
    }

    private static TriggerLoopReference CreateTarget()
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-3", new string('c', 64));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, revision, "publish-3", new string('d', 64));
        var grant = new AuthorityGrantReference(ParseGrantId("grant-1"), ParseGrantRevision(2), "sha256:" + new string('e', 64));
        if (!TriggerDeliveryFactory.TryCreateGovernedLoopReference(publication, grant, out var target, out var validation))
        {
            throw new InvalidOperationException(string.Join(',', validation.Errors.Select(error => error.Code)));
        }

        return target!;
    }

    private static TriggerAdapterReference CreateAdapter()
    {
        if (!CapabilityId.TryParse("org.embodysense/triggers/time", out var capabilityId, out _)
            || !CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _)
            || !CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var descriptorHash, out _)
            || !CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _))
        {
            throw new InvalidOperationException("The schedule-store adapter identity is invalid.");
        }

        return new TriggerAdapterReference(new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!), new CapabilityImplementationIdentity(providerId!, "triggers/time"));
    }

    private static AuthorityActorId ParseActor(string value)
        => AuthorityActorId.TryParse(value, out var actor, out _) ? actor! : throw new InvalidOperationException($"The actor identity `{value}` is invalid.");

    private static AuthorityProfileId ParseProfileId(string value)
        => AuthorityProfileId.TryParse(value, out var profile, out _) ? profile! : throw new InvalidOperationException($"The profile identity `{value}` is invalid.");

    private static AuthorityProfileRevision ParseProfileRevision(int value)
        => AuthorityProfileRevision.TryParse(value.ToString(CultureInfo.InvariantCulture), out var revision, out _) ? revision! : throw new InvalidOperationException($"The profile revision `{value}` is invalid.");

    private static AuthorityGrantId ParseGrantId(string value)
        => AuthorityGrantId.TryParse(value, out var grant, out _) ? grant! : throw new InvalidOperationException($"The grant identity `{value}` is invalid.");

    private static AuthorityGrantRevision ParseGrantRevision(int value)
        => AuthorityGrantRevision.TryParse(value.ToString(CultureInfo.InvariantCulture), out var revision, out _) ? revision! : throw new InvalidOperationException($"The grant revision `{value}` is invalid.");

    private static ScheduleState Replacement(ScheduleState state, int variant)
        => state with { StateRevision = state.StateRevision + 1, LastClockObservedAtUtc = state.LastClockObservedAtUtc!.Value.AddSeconds(variant) };

    private static bool TryParseOptionalPositiveInt(string value, out int? parsed)
    {
        if (string.IsNullOrEmpty(value))
        {
            parsed = null;
            return true;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var candidate) && candidate > 0)
        {
            parsed = candidate;
            return true;
        }

        parsed = null;
        return false;
    }

    private static bool TryParseOptionalBoundary(string value, out ScheduleStorePersistenceBoundary? boundary)
    {
        if (string.IsNullOrEmpty(value))
        {
            boundary = null;
            return true;
        }

        if (Enum.TryParse<ScheduleStorePersistenceBoundary>(value, out var candidate))
        {
            boundary = candidate;
            return true;
        }

        boundary = null;
        return false;
    }

    private static string RequireEnvironment(string variable)
        => Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"The schedule-store host variable `{variable}` is required.");

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (wait.Elapsed >= _gateTimeout)
            {
                throw new TimeoutException($"The schedule-store release marker `{path}` was not published within {_gateTimeout}.");
            }

            await Task.Delay(10);
        }
    }

    private static void TerminateProcess()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
}
