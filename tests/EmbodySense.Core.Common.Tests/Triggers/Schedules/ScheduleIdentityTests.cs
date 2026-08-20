using System.Globalization;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Tests.Triggers.Schedules;

public sealed class ScheduleIdentityTests
{
    [Fact]
    public void Schedule_and_claim_identifiers_are_exactly_bounded_and_canonical()
    {
        Assert.True(ScheduleId.TryParse(new string('a', ScheduleContractLimits.MaxScheduleIdCharacters), out var schedule));
        Assert.True(ScheduleClaimId.TryParse(new string('b', ScheduleContractLimits.MaxClaimIdCharacters), out var claim));
        Assert.Equal(new string('a', ScheduleContractLimits.MaxScheduleIdCharacters), schedule!.Value);
        Assert.Equal(new string('b', ScheduleContractLimits.MaxClaimIdCharacters), claim!.Value);

        foreach (var value in new string?[] { null, "", "Uppercase", "con", "path/child", ".leading", "trailing." })
        {
            Assert.False(ScheduleId.TryParse(value, out _));
            Assert.False(ScheduleClaimId.TryParse(value, out _));
        }

        Assert.False(ScheduleId.TryParse(new string('a', ScheduleContractLimits.MaxScheduleIdCharacters + 1), out _));
        Assert.False(ScheduleClaimId.TryParse(new string('b', ScheduleContractLimits.MaxClaimIdCharacters + 1), out _));
    }

    [Fact]
    public void Scalar_identities_use_exact_ordinal_value_semantics()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var schedule));
        Assert.True(ScheduleId.TryParse("daily-reflection", out var sameSchedule));
        Assert.True(ScheduleId.TryParse("weekly-reflection", out var laterSchedule));
        Assert.True(ScheduleClaimId.TryParse("claim-1", out var claim));
        Assert.True(ScheduleClaimId.TryParse("claim-1", out var sameClaim));
        Assert.True(ScheduleClaimId.TryParse("claim-2", out var laterClaim));

        Assert.Equal(schedule, sameSchedule);
        Assert.True(schedule!.Equals((object)sameSchedule!));
        Assert.False(schedule.Equals((object)claim!));
        Assert.Equal(schedule.GetHashCode(), sameSchedule!.GetHashCode());
        Assert.Equal("daily-reflection", schedule.ToString());
        Assert.True(schedule.CompareTo(laterSchedule) < 0);
        Assert.True(schedule.CompareTo(null) > 0);

        Assert.Equal(claim, sameClaim);
        Assert.True(claim!.Equals((object)sameClaim!));
        Assert.False(claim.Equals((object)schedule));
        Assert.Equal(claim.GetHashCode(), sameClaim!.GetHashCode());
        Assert.Equal("claim-1", claim.ToString());
        Assert.True(claim.CompareTo(laterClaim) < 0);
        Assert.True(claim.CompareTo(null) > 0);

        var occurrence = ScheduleContractTestData.Identity(ScheduleContractTestData.Occurrence()).OccurrenceId;
        Assert.True(ScheduleOccurrenceId.TryParse(occurrence.Value, out var sameOccurrence));
        Assert.True(ScheduleOccurrenceId.TryParse(ScheduleOccurrenceId.Prefix + new string('f', 64), out var laterOccurrence));
        Assert.Equal(occurrence, sameOccurrence);
        Assert.True(occurrence.Equals((object)sameOccurrence!));
        Assert.False(occurrence.Equals((object)schedule));
        Assert.Equal(occurrence.GetHashCode(), sameOccurrence!.GetHashCode());
        Assert.Equal(occurrence.Value, occurrence.ToString());
        Assert.True(occurrence.CompareTo(laterOccurrence) < 0);
        Assert.True(occurrence.CompareTo(null) > 0);
    }

    [Fact]
    public void Occurrence_identifier_accepts_only_its_exact_domain_prefix_and_lowercase_digest()
    {
        var value = ScheduleOccurrenceId.Prefix + new string('a', ScheduleContractLimits.Sha256HexCharacters);
        Assert.True(ScheduleOccurrenceId.TryParse(value, out var parsed));
        Assert.Equal(value, parsed!.Value);

        Assert.False(ScheduleOccurrenceId.TryParse(null, out _));
        Assert.False(ScheduleOccurrenceId.TryParse("occurrence-" + new string('a', 64), out _));
        Assert.False(ScheduleOccurrenceId.TryParse(ScheduleOccurrenceId.Prefix + new string('A', 64), out _));
        Assert.False(ScheduleOccurrenceId.TryParse(ScheduleOccurrenceId.Prefix + new string('a', 63), out _));
        Assert.False(ScheduleOccurrenceId.TryParse(ScheduleOccurrenceId.Prefix + new string('a', 63) + "g", out _));
    }

    [Fact]
    public void Derived_replay_identity_is_deterministic_domain_separated_and_exactly_pinned()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        var occurrence = ScheduleContractTestData.Occurrence();

        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId, 1, ScheduleContractTestData.DefinitionHash, occurrence, out var first, out var validation), ScheduleContractTestData.Errors(validation));
        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId, 1, ScheduleContractTestData.DefinitionHash, occurrence with { TimeZone = occurrence.TimeZone with { } }, out var replay, out validation), ScheduleContractTestData.Errors(validation));

        Assert.Equal(first, replay);
        Assert.NotEqual(first!.OccurrenceId.Value, first.DeliveryId.Value);
        Assert.NotEqual(first.DeliveryId.Value, first.DeduplicationId.Value);
        Assert.Equal("schedule-occurrence-cc8add1c424178cb5180857c30ae433ce77c42a171708483e3c75401753250cb", first.OccurrenceId.Value);
        Assert.Equal("schedule-delivery-f49d176be58f5559bc9d04e2e638da45fb6d3cc3823b0470936ed681d96d0af2", first.DeliveryId.Value);
        Assert.Equal("schedule-deduplication-c8f0f44c66f0a8a87923943f03de57f48b6404483171a0e676d1389389a6f02f", first.DeduplicationId.Value);
        Assert.True(ScheduleIdentityDerivation.Matches(first, scheduleId, 1, ScheduleContractTestData.DefinitionHash, occurrence));
    }

    [Fact]
    public void Every_immutable_coordinate_changes_all_domain_separated_identities()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        Assert.True(ScheduleId.TryParse("weekly-reflection", out var otherScheduleId));
        var occurrence = ScheduleContractTestData.Occurrence();
        var expected = Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence);
        var variants = new[]
        {
            Derive(otherScheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence),
            Derive(scheduleId!, 2, ScheduleContractTestData.DefinitionHash, occurrence),
            Derive(scheduleId!, 1, new string('1', 64), occurrence),
            Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence with { Ordinal = 2 }),
            Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence with { ScheduledLocal = occurrence.ScheduledLocal.AddDays(1) }),
            Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence with { ScheduledAtUtc = occurrence.ScheduledAtUtc.AddDays(1) }),
            Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence with { TimeZone = occurrence.TimeZone with { TimeZoneId = "Etc/UTC" } }),
            Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, occurrence with { TimeZone = occurrence.TimeZone with { RulesFingerprint = new string('2', 64) } }),
        };

        Assert.All(variants, variant =>
        {
            Assert.NotEqual(expected.OccurrenceId, variant.OccurrenceId);
            Assert.NotEqual(expected.DeliveryId, variant.DeliveryId);
            Assert.NotEqual(expected.DeduplicationId, variant.DeduplicationId);
        });
    }

    [Fact]
    public void Identity_derivation_is_independent_of_process_culture()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var localized = Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, ScheduleContractTestData.Occurrence());

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Assert.Equal(localized, Derive(scheduleId!, 1, ScheduleContractTestData.DefinitionHash, ScheduleContractTestData.Occurrence()));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Identity_derivation_rejects_every_boundary_outside_schema_one_coordinates()
    {
        Assert.True(ScheduleId.TryParse("daily-reflection", out var scheduleId));
        var occurrence = ScheduleContractTestData.Occurrence();

        AssertInvalid(null, 1, ScheduleContractTestData.DefinitionHash, occurrence, "scheduleId");
        AssertInvalid(scheduleId, 0, ScheduleContractTestData.DefinitionHash, occurrence, "definitionRevision");
        AssertInvalid(scheduleId, ScheduleContractLimits.MaxRevision + 1, ScheduleContractTestData.DefinitionHash, occurrence, "definitionRevision");
        AssertInvalid(scheduleId, 1, new string('A', 64), occurrence, "definitionHash");
        AssertInvalid(scheduleId, 1, new string('a', 63), occurrence, "definitionHash");
        AssertInvalid(scheduleId, 1, ScheduleContractTestData.DefinitionHash, null, "occurrence");
    }

    private static ScheduleOccurrenceIdentity Derive(ScheduleId scheduleId, long revision, string hash, ScheduleOccurrence occurrence)
    {
        Assert.True(ScheduleIdentityDerivation.TryDerive(scheduleId, revision, hash, occurrence, out var identity, out var validation), ScheduleContractTestData.Errors(validation));
        return identity!;
    }

    private static void AssertInvalid(ScheduleId? scheduleId, long revision, string? hash, ScheduleOccurrence? occurrence, string path)
    {
        Assert.False(ScheduleIdentityDerivation.TryDerive(scheduleId, revision, hash, occurrence, out var identity, out var validation));
        Assert.Null(identity);
        Assert.Contains(validation.Errors, error => error.Path.StartsWith(path, StringComparison.Ordinal));
    }
}
