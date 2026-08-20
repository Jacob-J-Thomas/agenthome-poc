using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleDefinitionContractTests
{
    [Fact]
    public void Definition_accepts_only_the_closed_recurrence_catalog_and_exact_parameter_matrix()
    {
        AssertValid(ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.Once, misfireKind: ScheduleMisfirePolicyKind.Skip, catchUpLimit: 0));
        AssertValid(ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.FixedInterval, fixedIntervalSeconds: 1, misfireKind: ScheduleMisfirePolicyKind.FireLatestOnce, catchUpLimit: 0));
        AssertValid(ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.FixedInterval, fixedIntervalSeconds: ScheduleContractLimits.MaxFixedIntervalSeconds));
        AssertValid(ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.Daily));
        AssertValid(ScheduleContractTestData.Definition(recurrenceKind: ScheduleRecurrenceKind.Weekly));

        AssertInvalid(ScheduleContractTestData.Definition() with { Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Unknown, ScheduleContractTestData.FirstLocal, null) }, "recurrence.kind", "unsupported_recurrence");
        AssertInvalid(ScheduleContractTestData.Definition() with { Recurrence = new ScheduleRecurrenceRule((ScheduleRecurrenceKind)99, ScheduleContractTestData.FirstLocal, null) }, "recurrence.kind", "unsupported_recurrence");
        AssertInvalid(ScheduleContractTestData.Definition() with { Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.FixedInterval, ScheduleContractTestData.FirstLocal, 0) }, "recurrence.fixedIntervalSeconds", "invalid_fixed_interval");
        AssertInvalid(ScheduleContractTestData.Definition() with { Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.FixedInterval, ScheduleContractTestData.FirstLocal, ScheduleContractLimits.MaxFixedIntervalSeconds + 1) }, "recurrence.fixedIntervalSeconds", "invalid_fixed_interval");
        AssertInvalid(ScheduleContractTestData.Definition() with { Recurrence = new ScheduleRecurrenceRule(ScheduleRecurrenceKind.Daily, ScheduleContractTestData.FirstLocal, 1) }, "recurrence.fixedIntervalSeconds", "unexpected_fixed_interval");
    }

    [Fact]
    public void Definition_enforces_every_schema_revision_and_misfire_bound_at_limit_and_limit_plus_one()
    {
        AssertValid(ScheduleContractTestData.Definition(revision: ScheduleContractLimits.MaxRevision));
        AssertValid(ScheduleContractTestData.Definition(catchUpLimit: ScheduleContractLimits.MaxCatchUpOccurrences));

        AssertInvalid(ScheduleContractTestData.Definition() with { SchemaVersion = 0 }, "schemaVersion", "unsupported_schema_version");
        AssertInvalid(ScheduleContractTestData.Definition(revision: 0), "revision", "revision_out_of_range");
        AssertInvalid(ScheduleContractTestData.Definition(revision: ScheduleContractLimits.MaxRevision + 1), "revision", "revision_out_of_range");
        AssertInvalid(ScheduleContractTestData.Definition(catchUpLimit: 0), "misfire.catchUpLimit", "invalid_catch_up_limit");
        AssertInvalid(ScheduleContractTestData.Definition(catchUpLimit: ScheduleContractLimits.MaxCatchUpOccurrences + 1), "misfire.catchUpLimit", "invalid_catch_up_limit");
        AssertInvalid(ScheduleContractTestData.Definition(misfireKind: ScheduleMisfirePolicyKind.Skip, catchUpLimit: 1), "misfire.catchUpLimit", "unexpected_catch_up_limit");
        AssertInvalid(ScheduleContractTestData.Definition() with { Misfire = new ScheduleMisfirePolicy((ScheduleMisfirePolicyKind)99, 0) }, "misfire.kind", "unsupported_misfire_policy");
    }

    [Fact]
    public void Definition_rejects_null_default_unknown_and_malformed_required_bindings()
    {
        var valid = ScheduleContractTestData.Definition();
        AssertInvalid(null, "$", "required");
        AssertInvalid(valid with { ScheduleId = null! }, "scheduleId", "invalid_schedule_id");
        AssertInvalid(valid with { Target = null! }, "target", "governed_target_required");
        AssertInvalid(valid with { Target = TriggerDeliveryTestData.Loop() }, "target", "governed_target_required");
        AssertInvalid(valid with { TimeAdapter = null! }, "timeAdapter", "invalid_time_adapter");
        AssertInvalid(valid with { ActorId = null! }, "actorId", "invalid_actor");
        AssertInvalid(valid with { AuthorityProfile = null! }, "authorityProfile", "invalid_authority_profile");
        AssertInvalid(valid with { Payload = null! }, "payload", "invalid_payload_reference");
        AssertInvalid(valid with { Recurrence = null! }, "recurrence", "required");
        AssertInvalid(valid with { TimeZone = null! }, "timeZone", "required");
        AssertInvalid(valid with { DaylightSaving = null! }, "daylightSaving", "invalid_daylight_saving_policy");
        AssertInvalid(valid with { Misfire = null! }, "misfire", "required");
        AssertInvalid(valid with { Priority = SchedulePriority.Unknown }, "priority", "unsupported_priority");
        AssertInvalid(valid with { Priority = (SchedulePriority)99 }, "priority", "unsupported_priority");
        AssertInvalid(valid with { Overlap = ScheduleOverlapPolicy.Unknown }, "overlap", "unsupported_overlap_policy");
        AssertInvalid(valid with { Overlap = (ScheduleOverlapPolicy)99 }, "overlap", "unsupported_overlap_policy");
        AssertInvalid(valid with { DaylightSaving = new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.Unknown, ScheduleAmbiguousLocalTimePolicy.EarlierUtc) }, "daylightSaving", "invalid_daylight_saving_policy");
        AssertInvalid(valid with { DaylightSaving = new ScheduleDaylightSavingPolicy(ScheduleInvalidLocalTimePolicy.Skip, (ScheduleAmbiguousLocalTimePolicy)99) }, "daylightSaving", "invalid_daylight_saving_policy");
    }

    [Fact]
    public void Definition_enforces_identifier_timezone_hash_and_local_time_bounds()
    {
        var valid = ScheduleContractTestData.Definition();
        AssertValid(valid with { SurfaceId = new string('s', 64), WorkspaceId = new string('w', 120), RoleId = new string('r', 120) });
        AssertValid(valid with { TimeZone = ScheduleContractTestData.TimeZone(new string('z', ScheduleContractLimits.MaxTimeZoneIdCharacters)) });

        AssertInvalid(valid with { SurfaceId = new string('s', 65) }, "surfaceId", "invalid_identifier");
        AssertInvalid(valid with { WorkspaceId = new string('w', 121) }, "workspaceId", "invalid_identifier");
        AssertInvalid(valid with { RoleId = new string('r', 121) }, "roleId", "invalid_identifier");
        AssertInvalid(valid with { TimeZone = ScheduleContractTestData.TimeZone(new string('z', ScheduleContractLimits.MaxTimeZoneIdCharacters + 1)) }, "timeZone.timeZoneId", "invalid_time_zone_id");
        AssertInvalid(valid with { TimeZone = ScheduleContractTestData.TimeZone("../Chicago") }, "timeZone.timeZoneId", "invalid_time_zone_id");
        AssertInvalid(valid with { TimeZone = ScheduleContractTestData.TimeZone("America\\Chicago") }, "timeZone.timeZoneId", "invalid_time_zone_id");
        AssertInvalid(valid with { TimeZone = ScheduleContractTestData.TimeZone(rulesFingerprint: new string('E', 64)) }, "timeZone.rulesFingerprint", "invalid_hash");
        AssertInvalid(valid with { Recurrence = valid.Recurrence with { FirstLocalOccurrence = DateTime.SpecifyKind(ScheduleContractTestData.FirstLocal, DateTimeKind.Utc) } }, "recurrence.firstLocalOccurrence", "invalid_local_time");
        AssertInvalid(valid with { Recurrence = valid.Recurrence with { FirstLocalOccurrence = new DateTime(ScheduleContractLimits.MinimumSupportedYear - 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) } }, "recurrence.firstLocalOccurrence", "invalid_local_time");
        AssertInvalid(valid with { Recurrence = valid.Recurrence with { FirstLocalOccurrence = new DateTime(ScheduleContractLimits.MaximumSupportedYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) } }, "recurrence.firstLocalOccurrence", "invalid_local_time");
    }

    [Fact]
    public void Occurrence_accepts_exact_temporal_bounds_and_rejects_malformed_shapes()
    {
        Assert.True(ScheduleContractValidator.ValidateOccurrence(ScheduleContractTestData.Occurrence(ordinal: ScheduleContractLimits.MaxOccurrenceOrdinal)).IsValid);
        Assert.True(ScheduleContractValidator.ValidateOccurrence(ScheduleContractTestData.Occurrence(
            scheduledLocal: new DateTime(ScheduleContractLimits.MinimumSupportedYear, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            scheduledAtUtc: new DateTimeOffset(ScheduleContractLimits.MinimumSupportedYear, 1, 1, 0, 0, 0, TimeSpan.Zero))).IsValid);
        Assert.True(ScheduleContractValidator.ValidateOccurrence(ScheduleContractTestData.Occurrence(
            scheduledLocal: new DateTime(ScheduleContractLimits.MaximumSupportedYear, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            scheduledAtUtc: new DateTimeOffset(ScheduleContractLimits.MaximumSupportedYear, 1, 1, 0, 0, 0, TimeSpan.Zero))).IsValid);

        AssertOccurrenceInvalid(null, "$", "required");
        AssertOccurrenceInvalid(ScheduleContractTestData.Occurrence() with { SchemaVersion = 2 }, "schemaVersion", "unsupported_schema_version");
        AssertOccurrenceInvalid(ScheduleContractTestData.Occurrence(ordinal: 0), "ordinal", "ordinal_out_of_range");
        AssertOccurrenceInvalid(ScheduleContractTestData.Occurrence(ordinal: ScheduleContractLimits.MaxOccurrenceOrdinal + 1), "ordinal", "ordinal_out_of_range");
        AssertOccurrenceInvalid(ScheduleContractTestData.Occurrence(scheduledLocal: DateTime.SpecifyKind(ScheduleContractTestData.FirstLocal, DateTimeKind.Local)), "scheduledLocal", "invalid_local_time");
        AssertOccurrenceInvalid(ScheduleContractTestData.Occurrence(scheduledAtUtc: ScheduleContractTestData.FirstUtc.ToOffset(TimeSpan.FromHours(1))), "scheduledAtUtc", "utc_required");
        AssertOccurrenceInvalid(ScheduleContractTestData.Occurrence(timeZone: null!) with { TimeZone = null! }, "timeZone", "required");
    }

    private static void AssertValid(ScheduleDefinition definition)
    {
        var validation = ScheduleContractValidator.ValidateDefinition(definition);
        Assert.True(validation.IsValid, ScheduleContractTestData.Errors(validation));
    }

    private static void AssertInvalid(ScheduleDefinition? definition, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateDefinition(definition);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }

    private static void AssertOccurrenceInvalid(ScheduleOccurrence? occurrence, string path, string code)
    {
        var validation = ScheduleContractValidator.ValidateOccurrence(occurrence);
        Assert.Contains(validation.Errors, error => error.Path == path && error.Code == code);
    }
}
