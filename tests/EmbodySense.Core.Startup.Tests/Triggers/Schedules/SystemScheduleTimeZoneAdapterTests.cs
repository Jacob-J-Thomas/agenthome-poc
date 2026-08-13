using System.Globalization;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

public sealed class SystemScheduleTimeZoneAdapterTests
{
    private const string PlaceholderFingerprint = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly DateTime _uniqueLocal = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task Utc_resolves_both_directions_with_one_stable_exact_rules_fingerprint()
    {
        var adapter = Adapter(TimeZoneInfo.Utc);
        var reference = Reference(TimeZoneInfo.Utc);
        var expectedUtc = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

        var local = await adapter.ResolveLocalAsync(reference, _uniqueLocal);
        var instant = await adapter.ResolveInstantAsync(reference, expectedUtc);
        var repeated = await adapter.ResolveLocalAsync(reference, _uniqueLocal);

        Assert.Equal(ScheduleTimeZoneResolutionStatus.Unique, local.Status);
        Assert.Equal(_uniqueLocal, local.ResolvedLocal);
        Assert.Equal(expectedUtc, local.EarlierUtc);
        Assert.Null(local.LaterUtc);
        Assert.Equal(ScheduleInstantResolutionStatus.Resolved, instant.Status);
        Assert.Equal(_uniqueLocal, instant.ScheduledLocal);
        Assert.Equal(DateTimeKind.Unspecified, instant.ScheduledLocal.Kind);
        Assert.Matches("^[0-9a-f]{64}$", local.RulesFingerprint);
        Assert.Equal(local.RulesFingerprint, instant.RulesFingerprint);
        Assert.Equal(local, repeated);
    }

    [Fact]
    public async Task Gap_returns_the_exact_first_valid_local_and_fold_returns_canonical_utc_order()
    {
        var zone = CreateDstZone("Test/GapFold");
        var adapter = Adapter(zone);
        var reference = Reference(zone);
        var invalid = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
        var ambiguous = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);

        var gap = await adapter.ResolveLocalAsync(reference, invalid);
        var fold = await adapter.ResolveLocalAsync(reference, ambiguous);

        Assert.Equal(ScheduleTimeZoneResolutionStatus.InvalidLocalTime, gap.Status);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0, DateTimeKind.Unspecified), gap.ResolvedLocal);
        Assert.True(zone.IsInvalidTime(gap.ResolvedLocal.AddTicks(-1)));
        Assert.False(zone.IsInvalidTime(gap.ResolvedLocal));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), gap.EarlierUtc);
        Assert.Equal(gap.ResolvedLocal, ToUnspecified(TimeZoneInfo.ConvertTimeFromUtc(gap.EarlierUtc!.Value.UtcDateTime, zone)));
        Assert.Null(gap.LaterUtc);
        Assert.Equal(ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime, fold.Status);
        Assert.Equal(ambiguous, fold.ResolvedLocal);
        Assert.NotNull(fold.EarlierUtc);
        Assert.NotNull(fold.LaterUtc);
        Assert.True(fold.EarlierUtc < fold.LaterUtc);
        Assert.Equal(ambiguous, ToUnspecified(TimeZoneInfo.ConvertTimeFromUtc(fold.EarlierUtc!.Value.UtcDateTime, zone)));
        Assert.Equal(ambiguous, ToUnspecified(TimeZoneInfo.ConvertTimeFromUtc(fold.LaterUtc!.Value.UtcDateTime, zone)));
        Assert.Equal(gap.RulesFingerprint, fold.RulesFingerprint);
    }

    [Fact]
    public async Task Inverse_mapping_preserves_both_fixed_interval_instants_across_a_fold()
    {
        var zone = CreateDstZone("Test/FixedIntervalFold");
        var adapter = Adapter(zone);
        var reference = Reference(zone);
        var ambiguous = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var fold = await adapter.ResolveLocalAsync(reference, ambiguous);

        var earlier = await adapter.ResolveInstantAsync(reference, fold.EarlierUtc!.Value);
        var later = await adapter.ResolveInstantAsync(reference, fold.LaterUtc!.Value);

        Assert.Equal(ScheduleInstantResolutionStatus.Resolved, earlier.Status);
        Assert.Equal(ScheduleInstantResolutionStatus.Resolved, later.Status);
        Assert.Equal(ambiguous, earlier.ScheduledLocal);
        Assert.Equal(ambiguous, later.ScheduledLocal);
        Assert.Equal(earlier.RulesFingerprint, later.RulesFingerprint);
        Assert.Equal(TimeSpan.FromHours(1), fold.LaterUtc - fold.EarlierUtc);
    }

    [Fact]
    public async Task System_dst_rules_resolve_gap_and_fold_when_the_platform_exposes_them()
    {
        var candidate = FindSystemDstCandidate();
        if (candidate is null)
        {
            return;
        }

        var (zone, invalid, ambiguous) = candidate.Value;
        var adapter = Adapter(zone);
        var reference = Reference(zone);

        var gap = await adapter.ResolveLocalAsync(reference, invalid);
        var fold = await adapter.ResolveLocalAsync(reference, ambiguous);

        Assert.Equal(ScheduleTimeZoneResolutionStatus.InvalidLocalTime, gap.Status);
        Assert.True(gap.ResolvedLocal > invalid);
        Assert.False(zone.IsInvalidTime(gap.ResolvedLocal));
        Assert.Equal(ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime, fold.Status);
        Assert.True(fold.EarlierUtc < fold.LaterUtc);
        Assert.Equal(gap.RulesFingerprint, fold.RulesFingerprint);
    }

    [Fact]
    public async Task Fingerprint_changes_for_base_offset_or_any_adjustment_transition_change()
    {
        var first = CreateDstZone("Test/Fingerprint", startDay: 8);
        var changedTransition = CreateDstZone("Test/Fingerprint", startDay: 9);
        var changedBaseOffset = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Fingerprint",
            TimeSpan.FromHours(-4),
            "Test/Fingerprint",
            "Test/Fingerprint");

        var firstResult = await Adapter(first).ResolveLocalAsync(Reference(first), _uniqueLocal);
        var transitionResult = await Adapter(changedTransition).ResolveLocalAsync(Reference(changedTransition), _uniqueLocal);
        var offsetResult = await Adapter(changedBaseOffset).ResolveLocalAsync(Reference(changedBaseOffset), _uniqueLocal);

        Assert.NotEqual(firstResult.RulesFingerprint, transitionResult.RulesFingerprint);
        Assert.NotEqual(firstResult.RulesFingerprint, offsetResult.RulesFingerprint);
        Assert.NotEqual(transitionResult.RulesFingerprint, offsetResult.RulesFingerprint);
    }

    [Fact]
    public async Task Fingerprint_is_culture_independent()
    {
        var zone = CreateDstZone("Test/Culture");
        var adapter = Adapter(zone);
        var reference = Reference(zone);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = new CultureInfo("ar-SA");
            var first = await adapter.ResolveLocalAsync(reference, _uniqueLocal);
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            var second = await adapter.ResolveInstantAsync(reference, first.EarlierUtc!.Value);

            Assert.Equal(first.RulesFingerprint, second.RulesFingerprint);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task Missing_or_malformed_references_fail_closed_without_alias_fallback()
    {
        var zone = CreateDstZone("Test/ExactCase");
        var adapter = Adapter(zone);

        var missing = await adapter.ResolveLocalAsync(new ScheduleTimeZoneReference("test/exactcase", PlaceholderFingerprint), _uniqueLocal);
        var traversal = await adapter.ResolveLocalAsync(new ScheduleTimeZoneReference("Test/../ExactCase", PlaceholderFingerprint), _uniqueLocal);
        var backslash = await adapter.ResolveLocalAsync(new ScheduleTimeZoneReference("Test\\ExactCase", PlaceholderFingerprint), _uniqueLocal);
        var malformedHash = await adapter.ResolveLocalAsync(new ScheduleTimeZoneReference(zone.Id, "ABC"), _uniqueLocal);
        var malformedUnicode = await adapter.ResolveLocalAsync(new ScheduleTimeZoneReference("Test/\ud800", PlaceholderFingerprint), _uniqueLocal);
        var nullReference = await adapter.ResolveLocalAsync(null!, _uniqueLocal);

        Assert.Equal(ScheduleTimeZoneResolutionStatus.Unavailable, missing.Status);
        Assert.All([traversal, backslash, malformedHash, malformedUnicode, nullReference], result =>
        {
            Assert.Equal(ScheduleTimeZoneResolutionStatus.Corrupt, result.Status);
            Assert.Null(result.RulesFingerprint);
            Assert.Null(result.EarlierUtc);
            Assert.Null(result.LaterUtc);
        });
    }

    [Fact]
    public async Task Local_and_utc_bounds_and_kinds_fail_closed_while_edge_years_round_trip()
    {
        var adapter = Adapter(TimeZoneInfo.Utc);
        var reference = Reference(TimeZoneInfo.Utc);
        var minimumLocal = new DateTime(ScheduleContractLimits.MinimumSupportedYear, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var maximumLocal = new DateTime(ScheduleContractLimits.MaximumSupportedYear, 12, 31, 23, 59, 59, DateTimeKind.Unspecified);

        Assert.Equal(ScheduleTimeZoneResolutionStatus.Unique, (await adapter.ResolveLocalAsync(reference, minimumLocal)).Status);
        Assert.Equal(ScheduleTimeZoneResolutionStatus.Unique, (await adapter.ResolveLocalAsync(reference, maximumLocal)).Status);
        Assert.Equal(ScheduleInstantResolutionStatus.Resolved, (await adapter.ResolveInstantAsync(reference, new DateTimeOffset(minimumLocal, TimeSpan.Zero))).Status);
        Assert.Equal(ScheduleInstantResolutionStatus.Resolved, (await adapter.ResolveInstantAsync(reference, new DateTimeOffset(maximumLocal, TimeSpan.Zero))).Status);

        var localBefore = await adapter.ResolveLocalAsync(reference, new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified));
        var localAfter = await adapter.ResolveLocalAsync(reference, new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        var wrongKind = await adapter.ResolveLocalAsync(reference, DateTime.SpecifyKind(_uniqueLocal, DateTimeKind.Utc));
        var instantBefore = await adapter.ResolveInstantAsync(reference, new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero));
        var instantAfter = await adapter.ResolveInstantAsync(reference, new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var nonUtcOffset = await adapter.ResolveInstantAsync(reference, new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(1)));

        Assert.All([localBefore, localAfter, wrongKind], result => Assert.Equal(ScheduleTimeZoneResolutionStatus.Corrupt, result.Status));
        Assert.All([instantBefore, instantAfter, nonUtcOffset], result => Assert.Equal(ScheduleInstantResolutionStatus.Corrupt, result.Status));
    }

    [Fact]
    public async Task Cancellation_is_observed_before_rules_or_mappings_are_returned()
    {
        var adapter = Adapter(TimeZoneInfo.Utc);
        var reference = Reference(TimeZoneInfo.Utc);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ResolveLocalAsync(reference, _uniqueLocal, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ResolveInstantAsync(reference, new DateTimeOffset(_uniqueLocal, TimeSpan.Zero), cancellation.Token));
    }

    [Fact]
    public void Composition_snapshot_rejects_null_malformed_and_duplicate_entries()
    {
        var malformed = TimeZoneInfo.CreateCustomTimeZone(
            " Test/Malformed",
            TimeSpan.Zero,
            "Test/Malformed",
            "Test/Malformed");

        Assert.Throws<ArgumentNullException>(() => new SystemScheduleTimeZoneAdapter(null!));
        Assert.Throws<ArgumentNullException>(() => new SystemScheduleTimeZoneAdapter(new TimeZoneInfo[] { null! }));
        Assert.Throws<ArgumentException>(() => Adapter(TimeZoneInfo.Utc, TimeZoneInfo.Utc));
        Assert.Throws<ArgumentException>(() => Adapter(malformed));
    }

    private static SystemScheduleTimeZoneAdapter Adapter(params TimeZoneInfo[] timeZones)
        => new(timeZones);

    private static ScheduleTimeZoneReference Reference(TimeZoneInfo timeZone)
        => new(timeZone.Id, PlaceholderFingerprint);

    private static TimeZoneInfo CreateDstZone(string id, int startDay = 8)
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0, DateTimeKind.Unspecified),
            3,
            startDay);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0, DateTimeKind.Unspecified),
            11,
            1);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(ScheduleContractLimits.MinimumSupportedYear, 1, 1),
            new DateTime(ScheduleContractLimits.MaximumSupportedYear, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            id,
            TimeSpan.FromHours(-5),
            id,
            id,
            id + " Daylight",
            [adjustment],
            disableDaylightSavingTime: false);
    }

    private static (TimeZoneInfo Zone, DateTime Invalid, DateTime Ambiguous)? FindSystemDstCandidate()
    {
        var candidates = new[]
        {
            (Id: "America/New_York", Invalid: new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified), Ambiguous: new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified)),
            (Id: "Eastern Standard Time", Invalid: new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified), Ambiguous: new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified)),
            (Id: "Europe/London", Invalid: new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Unspecified), Ambiguous: new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Unspecified))
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(candidate.Id);
                if (zone.IsInvalidTime(candidate.Invalid) && zone.IsAmbiguousTime(candidate.Ambiguous))
                {
                    return (zone, candidate.Invalid, candidate.Ambiguous);
                }
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return null;
    }

    private static DateTime ToUnspecified(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
