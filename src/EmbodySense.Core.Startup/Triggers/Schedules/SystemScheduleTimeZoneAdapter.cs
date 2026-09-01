using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>
/// Resolves schedule wall-clock values through an immutable, composition-owned snapshot of system
/// time-zone rules.
/// </summary>
/// <remarks>
/// The adapter never consults current time and does not treat a process-global mutable cache as
/// authority. Composition supplies the exact <see cref="TimeZoneInfo"/> instances admitted for the
/// runtime lifetime. Each result fingerprints the complete selected rule set so callers can reject
/// stale schedule evidence.
/// </remarks>
public sealed class SystemScheduleTimeZoneAdapter : IScheduleTimeZonePort
{
    private const string FingerprintDomain = "embodysense-schedule-time-zone-rules-v1";
    private const long InvalidTimeSearchTicks = 36 * TimeSpan.TicksPerHour;
    private const long InvalidTimeProbeTicks = TimeSpan.TicksPerMinute;
    private const int MaxAdjustmentRules = 16_384;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private static readonly long _maximumSupportedTicks = new DateTime(
        ScheduleContractLimits.MaximumSupportedYear,
        12,
        31,
        23,
        59,
        59,
        999,
        DateTimeKind.Unspecified).AddTicks(TimeSpan.TicksPerMillisecond - 1).Ticks;

    private readonly IReadOnlyDictionary<string, TimeZoneInfo> _timeZones;

    /// <summary>Maximum number of exact time-zone identifiers admitted into one server snapshot.</summary>
    public const int MaximumSupportedTimeZoneIds = 1024;

    /// <summary>Creates an adapter over one composition-owned time-zone snapshot.</summary>
    /// <param name="timeZones">
    /// The exact immutable system rules admitted by composition, normally captured once with
    /// <see cref="TimeZoneInfo.GetSystemTimeZones()"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="timeZones"/> or one of its entries is null.</exception>
    /// <exception cref="ArgumentException">The snapshot contains a malformed or duplicate exact identifier.</exception>
    public SystemScheduleTimeZoneAdapter(IReadOnlyCollection<TimeZoneInfo> timeZones)
    {
        ArgumentNullException.ThrowIfNull(timeZones);
        if (timeZones.Count > MaximumSupportedTimeZoneIds)
        {
            throw new ArgumentException("The composition-owned time-zone snapshot exceeds the bounded identifier catalog.", nameof(timeZones));
        }

        var captured = new Dictionary<string, TimeZoneInfo>(timeZones.Count, StringComparer.Ordinal);
        foreach (var timeZone in timeZones)
        {
            ArgumentNullException.ThrowIfNull(timeZone);
            if (!IsValidTimeZoneId(timeZone.Id))
            {
                throw new ArgumentException("The composition-owned time-zone snapshot contains a malformed identifier.", nameof(timeZones));
            }

            if (!captured.TryAdd(timeZone.Id, timeZone))
            {
                throw new ArgumentException("The composition-owned time-zone snapshot contains a duplicate exact identifier.", nameof(timeZones));
            }
        }

        _timeZones = captured;
    }

    /// <summary>Creates a canonical reference to one time-zone rule snapshot retained by this adapter.</summary>
    /// <remarks>
    /// Callers provide only the exact bounded time-zone identifier. The adapter derives the fingerprint from its
    /// composition-owned immutable rule snapshot so interface surfaces cannot select or forge scheduling rules.
    /// </remarks>
    /// <param name="timeZoneId">The exact case-sensitive identifier selected from the host snapshot.</param>
    /// <param name="reference">The canonical identifier and derived rules fingerprint when available.</param>
    /// <returns><see langword="true"/> when the snapshot contains a trustworthy matching rule set.</returns>
    public bool TryCreateReference(string? timeZoneId, out ScheduleTimeZoneReference? reference)
    {
        reference = null;
        if (!IsValidTimeZoneId(timeZoneId) || !_timeZones.TryGetValue(timeZoneId!, out var timeZone))
        {
            return false;
        }

        try
        {
            reference = new ScheduleTimeZoneReference(timeZone.Id, ComputeFingerprint(timeZone, CancellationToken.None));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (Exception exception) when (IsRuleFailure(exception))
        {
            return false;
        }
    }

    /// <summary>Returns the exact identifiers admitted by this composition-owned rules snapshot.</summary>
    /// <remarks>
    /// The identifiers are returned in a deterministic order and are detached from the adapter's internal lookup.
    /// Interface surfaces should use this list when selecting a time zone instead of deriving an identifier from a
    /// browser or another host's time-zone database.
    /// </remarks>
    /// <returns>A detached, ordinal-sorted list of server-supported identifiers.</returns>
    public IReadOnlyList<string> GetSupportedTimeZoneIds()
        => Array.AsReadOnly(_timeZones.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray());

    /// <inheritdoc />
    public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
        ScheduleTimeZoneReference timeZone,
        DateTime scheduledLocal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ResolveLocal(timeZone, scheduledLocal, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ScheduleTimeZoneResolution>(cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<ScheduleInstantResolution> ResolveInstantAsync(
        ScheduleTimeZoneReference timeZone,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ResolveInstant(timeZone, scheduledAtUtc, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ScheduleInstantResolution>(cancellationToken);
        }
    }

    private ScheduleTimeZoneResolution ResolveLocal(
        ScheduleTimeZoneReference? reference,
        DateTime scheduledLocal,
        CancellationToken cancellationToken)
    {
        if (!IsValidReference(reference) || !IsSupportedLocal(scheduledLocal))
        {
            return LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
        }

        if (!_timeZones.TryGetValue(reference!.TimeZoneId, out var timeZone))
        {
            return LocalFailure(ScheduleTimeZoneResolutionStatus.Unavailable);
        }

        try
        {
            var fingerprint = ComputeFingerprint(timeZone, cancellationToken);
            if (timeZone.IsInvalidTime(scheduledLocal))
            {
                var firstValid = FindFirstValidLocal(timeZone, scheduledLocal, cancellationToken);
                if (firstValid is null || timeZone.IsAmbiguousTime(firstValid.Value))
                {
                    return LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
                }

                var firstValidUtcValue = TimeZoneInfo.ConvertTimeToUtc(firstValid.Value, timeZone);
                var firstValidUtc = new DateTimeOffset(DateTime.SpecifyKind(firstValidUtcValue, DateTimeKind.Utc));
                return IsSupportedUtc(firstValidUtc) && RoundTrips(timeZone, firstValidUtc, firstValid.Value)
                    ? new ScheduleTimeZoneResolution(
                        ScheduleTimeZoneResolutionStatus.InvalidLocalTime,
                        fingerprint,
                        firstValid.Value,
                        firstValidUtc,
                        null)
                    : LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
            }

            if (timeZone.IsAmbiguousTime(scheduledLocal))
            {
                return ResolveAmbiguous(timeZone, scheduledLocal, fingerprint);
            }

            var utc = TimeZoneInfo.ConvertTimeToUtc(scheduledLocal, timeZone);
            var scheduledAtUtc = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            return IsSupportedUtc(scheduledAtUtc) && RoundTrips(timeZone, scheduledAtUtc, scheduledLocal)
                ? new ScheduleTimeZoneResolution(
                    ScheduleTimeZoneResolutionStatus.Unique,
                    fingerprint,
                    scheduledLocal,
                    scheduledAtUtc,
                    null)
                : LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException)
        {
            return LocalFailure(ScheduleTimeZoneResolutionStatus.Unavailable);
        }
        catch (Exception exception) when (IsRuleFailure(exception))
        {
            return LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
        }
    }

    private ScheduleInstantResolution ResolveInstant(
        ScheduleTimeZoneReference? reference,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken)
    {
        if (!IsValidReference(reference) || !IsSupportedUtc(scheduledAtUtc))
        {
            return InstantFailure(ScheduleInstantResolutionStatus.Corrupt);
        }

        if (!_timeZones.TryGetValue(reference!.TimeZoneId, out var timeZone))
        {
            return InstantFailure(ScheduleInstantResolutionStatus.Unavailable);
        }

        try
        {
            var fingerprint = ComputeFingerprint(timeZone, cancellationToken);
            var mapped = TimeZoneInfo.ConvertTimeFromUtc(scheduledAtUtc.UtcDateTime, timeZone);
            var scheduledLocal = DateTime.SpecifyKind(mapped, DateTimeKind.Unspecified);
            return IsSupportedLocal(scheduledLocal) && RoundTrips(timeZone, scheduledAtUtc, scheduledLocal)
                ? new ScheduleInstantResolution(ScheduleInstantResolutionStatus.Resolved, fingerprint, scheduledLocal)
                : InstantFailure(ScheduleInstantResolutionStatus.Corrupt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException)
        {
            return InstantFailure(ScheduleInstantResolutionStatus.Unavailable);
        }
        catch (Exception exception) when (IsRuleFailure(exception))
        {
            return InstantFailure(ScheduleInstantResolutionStatus.Corrupt);
        }
    }

    private static ScheduleTimeZoneResolution ResolveAmbiguous(
        TimeZoneInfo timeZone,
        DateTime scheduledLocal,
        string fingerprint)
    {
        var offsets = timeZone.GetAmbiguousTimeOffsets(scheduledLocal);
        if (offsets.Length != 2
            || !TryCreateUtc(scheduledLocal, offsets[0], out var first)
            || !TryCreateUtc(scheduledLocal, offsets[1], out var second)
            || first == second)
        {
            return LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
        }

        var earlier = first < second ? first : second;
        var later = first < second ? second : first;
        return IsSupportedUtc(earlier)
            && IsSupportedUtc(later)
            && RoundTrips(timeZone, earlier, scheduledLocal)
            && RoundTrips(timeZone, later, scheduledLocal)
            ? new ScheduleTimeZoneResolution(
                ScheduleTimeZoneResolutionStatus.AmbiguousLocalTime,
                fingerprint,
                scheduledLocal,
                earlier,
                later)
            : LocalFailure(ScheduleTimeZoneResolutionStatus.Corrupt);
    }

    private static DateTime? FindFirstValidLocal(
        TimeZoneInfo timeZone,
        DateTime invalidLocal,
        CancellationToken cancellationToken)
    {
        var searchLimit = Math.Min(_maximumSupportedTicks, invalidLocal.Ticks + InvalidTimeSearchTicks);
        var invalidTicks = invalidLocal.Ticks;
        while (invalidTicks < searchLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probeTicks = Math.Min(searchLimit, invalidTicks + InvalidTimeProbeTicks);
            var probe = new DateTime(probeTicks, DateTimeKind.Unspecified);
            if (!timeZone.IsInvalidTime(probe))
            {
                var firstValidTicks = FindBoundary(timeZone, invalidTicks, probeTicks, cancellationToken);
                var firstValid = new DateTime(firstValidTicks, DateTimeKind.Unspecified);
                var preceding = new DateTime(firstValidTicks - 1, DateTimeKind.Unspecified);
                return !timeZone.IsInvalidTime(firstValid) && timeZone.IsInvalidTime(preceding)
                    ? firstValid
                    : null;
            }

            invalidTicks = probeTicks;
        }

        return null;
    }

    private static long FindBoundary(
        TimeZoneInfo timeZone,
        long invalidTicks,
        long validTicks,
        CancellationToken cancellationToken)
    {
        while (validTicks - invalidTicks > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateTicks = invalidTicks + ((validTicks - invalidTicks) / 2);
            var candidate = new DateTime(candidateTicks, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(candidate))
            {
                invalidTicks = candidateTicks;
            }
            else
            {
                validTicks = candidateTicks;
            }
        }

        return validTicks;
    }

    private static bool RoundTrips(TimeZoneInfo timeZone, DateTimeOffset scheduledAtUtc, DateTime scheduledLocal)
    {
        var mapped = TimeZoneInfo.ConvertTimeFromUtc(scheduledAtUtc.UtcDateTime, timeZone);
        if (DateTime.SpecifyKind(mapped, DateTimeKind.Unspecified) != scheduledLocal
            || timeZone.IsInvalidTime(scheduledLocal))
        {
            return false;
        }

        if (timeZone.IsAmbiguousTime(scheduledLocal))
        {
            return timeZone.GetAmbiguousTimeOffsets(scheduledLocal)
                .Any(offset => TryCreateUtc(scheduledLocal, offset, out var candidate) && candidate == scheduledAtUtc);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(scheduledLocal, timeZone);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) == scheduledAtUtc;
    }

    private static bool TryCreateUtc(DateTime scheduledLocal, TimeSpan offset, out DateTimeOffset scheduledAtUtc)
    {
        var ticks = (decimal)scheduledLocal.Ticks - offset.Ticks;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            scheduledAtUtc = default;
            return false;
        }

        scheduledAtUtc = new DateTimeOffset((long)ticks, TimeSpan.Zero);
        return true;
    }

    private static string ComputeFingerprint(TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var rules = timeZone.GetAdjustmentRules();
        if (rules.Length > MaxAdjustmentRules)
        {
            throw new InvalidTimeZoneException("The selected time zone exceeds the bounded adjustment-rule count.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, FingerprintDomain);
        Append(hash, timeZone.Id);
        Append(hash, timeZone.BaseUtcOffset.Ticks);
        Append(hash, timeZone.SupportsDaylightSavingTime);
        Append(hash, rules.Length);
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, rule.DateStart.Ticks);
            Append(hash, (int)rule.DateStart.Kind);
            Append(hash, rule.DateEnd.Ticks);
            Append(hash, (int)rule.DateEnd.Kind);
            Append(hash, rule.DaylightDelta.Ticks);
            Append(hash, rule.BaseUtcOffsetDelta.Ticks);
            Append(hash, rule.DaylightTransitionStart);
            Append(hash, rule.DaylightTransitionEnd);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, TimeZoneInfo.TransitionTime transition)
    {
        Append(hash, transition.IsFixedDateRule);
        Append(hash, transition.Month);
        Append(hash, transition.Week);
        Append(hash, transition.Day);
        Append(hash, (int)transition.DayOfWeek);
        Append(hash, transition.TimeOfDay.Ticks);
        Append(hash, (int)transition.TimeOfDay.Kind);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = _strictUtf8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, bool value)
        => hash.AppendData(value ? [1] : [0]);

    private static bool IsValidReference(ScheduleTimeZoneReference? reference)
        => reference is not null
            && IsValidTimeZoneId(reference.TimeZoneId)
            && reference.RulesFingerprint?.Length == ScheduleContractLimits.Sha256HexCharacters
            && reference.RulesFingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidTimeZoneId(string? value)
    {
        if (!IsSafeNormalized(value, ScheduleContractLimits.MaxTimeZoneIdCharacters)
            || char.IsWhiteSpace(value![0])
            || char.IsWhiteSpace(value[^1])
            || value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsSafeNormalized(string? value, int maximumCharacters)
    {
        if (value is null || value.Length is 0 || value.Length > maximumCharacters)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            Rune rune;
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !Rune.TryCreate(value[index], value[index + 1], out rune))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
            else
            {
                rune = new Rune(value[index]);
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control
                || rune.Value is >= 0xfdd0 and <= 0xfdef
                || (rune.Value & 0xffff) is 0xfffe or 0xffff)
            {
                return false;
            }
        }

        return value.IsNormalized(NormalizationForm.FormC);
    }

    private static bool IsSupportedLocal(DateTime value)
        => value.Kind == DateTimeKind.Unspecified && IsSupportedYear(value.Year);

    private static bool IsSupportedUtc(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero && IsSupportedYear(value.UtcDateTime.Year);

    private static bool IsSupportedYear(int year)
        => year is >= ScheduleContractLimits.MinimumSupportedYear and <= ScheduleContractLimits.MaximumSupportedYear;

    private static bool IsRuleFailure(Exception exception)
        => exception is ArgumentException or InvalidOperationException or InvalidTimeZoneException or NotSupportedException or OverflowException;

    private static ScheduleTimeZoneResolution LocalFailure(ScheduleTimeZoneResolutionStatus status)
        => new(status, null, default, null, null);

    private static ScheduleInstantResolution InstantFailure(ScheduleInstantResolutionStatus status)
        => new(status, null, default);
}
