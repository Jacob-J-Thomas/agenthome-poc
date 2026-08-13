using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Wait;

public sealed class GovernedLoopWaitDescriptorContractTests
{
    [Fact]
    public void Closed_catalog_is_ordinal_bounded_and_exact()
    {
        Assert.Equal([GovernedLoopWaitVocabulary.AuthenticatedEvent, GovernedLoopWaitVocabulary.Timestamp], GovernedLoopWaitVocabulary.DescriptorTypeIds);
        Assert.True(GovernedLoopWaitVocabulary.IsSupported(GovernedLoopWaitVocabulary.Timestamp));
        Assert.True(GovernedLoopWaitVocabulary.IsSupported(GovernedLoopWaitVocabulary.AuthenticatedEvent));
        Assert.False(GovernedLoopWaitVocabulary.IsSupported("wait-cron"));
        Assert.False(GovernedLoopWaitVocabulary.IsSupported(null));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)GovernedLoopWaitVocabulary.DescriptorTypeIds).Clear());
    }

    [Fact]
    public void Timestamp_descriptor_admits_only_one_exact_canonical_utc_parameter()
    {
        var descriptor = GovernedLoopWaitContractTestFixture.TimestampDescriptor();
        var exact = GovernedLoopWaitContractTestFixture.TimestampParameters();
        var invalid = new IReadOnlyDictionary<string, string>?[]
        {
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.EventReferenceParameter] = "event-1" },
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = "2026-08-13T01:02:03Z" },
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = "2026-08-13T01:02:03.4567890+00:00" },
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = "2026-08-13T01:02:03.4567890z" },
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = "2026-08-13T01:02:03.4567890Z", ["extra"] = "value" }
        };

        Assert.True(GovernedLoopWaitContractValidator.ValidateDescriptor(descriptor, exact).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(descriptor, exact, out var condition, out var validation));
        Assert.True(validation.IsValid);
        Assert.Equal(GovernedLoopWaitParameterKind.UtcTimestamp, condition!.ParameterKind);
        Assert.Equal(GovernedLoopWaitContractTestFixture.DeadlineUtc, condition.WakeDeadlineUtc);
        Assert.Null(condition.AuthenticatedEventReference);
        Assert.All(invalid, parameters => Assert.False(GovernedLoopWaitContractValidator.ValidateDescriptor(descriptor, parameters).IsValid));
    }

    [Fact]
    public void Authenticated_event_descriptor_admits_only_one_bounded_canonical_reference()
    {
        var descriptor = GovernedLoopWaitContractTestFixture.EventDescriptor();
        var exact = GovernedLoopWaitContractTestFixture.EventParameters();
        var atLimit = GovernedLoopWaitContractTestFixture.EventParameters(new string('a', GovernedLoopWaitContractLimits.MaxEventReferenceCharacters));
        var invalid = new IReadOnlyDictionary<string, string>?[]
        {
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.EventReferenceParameter] = "" },
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.EventReferenceParameter] = "Event-1" },
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.EventReferenceParameter] = "event/1" },
            GovernedLoopWaitContractTestFixture.EventParameters(new string('a', GovernedLoopWaitContractLimits.MaxEventReferenceCharacters + 1)),
            new Dictionary<string, string> { [GovernedLoopWaitVocabulary.EventReferenceParameter] = "event-1", ["extra"] = "value" }
        };

        Assert.True(GovernedLoopWaitContractValidator.ValidateDescriptor(descriptor, exact).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateDescriptor(descriptor, atLimit).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(descriptor, exact, out var condition, out _));
        Assert.Equal(GovernedLoopWaitParameterKind.AuthenticatedEventReference, condition!.ParameterKind);
        Assert.Equal("governed-event-1", condition.AuthenticatedEventReference);
        Assert.Null(condition.WakeDeadlineUtc);
        Assert.All(invalid, parameters => Assert.False(GovernedLoopWaitContractValidator.ValidateDescriptor(descriptor, parameters).IsValid));
    }

    [Theory]
    [MemberData(nameof(UnsupportedDescriptors))]
    public void Unsupported_descriptor_kind_type_or_version_fails_closed(GovernedLoopNodeDescriptor? descriptor)
    {
        Assert.False(GovernedLoopWaitContractValidator.ValidateDescriptor(descriptor, GovernedLoopWaitContractTestFixture.TimestampParameters()).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.TryCreateCondition(descriptor, GovernedLoopWaitContractTestFixture.TimestampParameters(), out var condition, out _));
        Assert.Null(condition);
    }

    public static TheoryData<GovernedLoopNodeDescriptor?> UnsupportedDescriptors()
        => new()
        {
            null,
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, GovernedLoopWaitVocabulary.Timestamp, 1),
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, "wait-cron", 1),
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, GovernedLoopWaitVocabulary.Timestamp, 2)
        };

    [Fact]
    public void Admission_is_culture_independent_and_deterministic()
    {
        using var cultures = new CultureScope("ar-SA");
        var first = GovernedLoopWaitContractTestFixture.TimestampCondition();
        var second = GovernedLoopWaitContractTestFixture.TimestampCondition();

        Assert.Equal(first, second);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.True(GovernedLoopWaitContractHash.Matches(first));
    }

    [Fact]
    public void Admission_snapshots_scalar_parameter_evidence_from_mutable_input()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GovernedLoopWaitVocabulary.EventReferenceParameter] = "governed-event-1"
        };

        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(GovernedLoopWaitContractTestFixture.EventDescriptor(), parameters, out var condition, out _));
        parameters[GovernedLoopWaitVocabulary.EventReferenceParameter] = "governed-event-2";

        Assert.Equal("governed-event-1", condition!.AuthenticatedEventReference);
        Assert.True(GovernedLoopWaitContractValidator.Validate(condition).IsValid);
    }

    [Fact]
    public void Admission_requires_ordinal_parameter_keys_and_uses_one_captured_scalar()
    {
        var uppercase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EVENT-REFERENCE"] = "governed-event-1"
        };
        var switching = new SwitchingParameters(
            GovernedLoopWaitVocabulary.EventReferenceParameter,
            "governed-event-1",
            "INVALID EVENT");

        Assert.False(GovernedLoopWaitContractValidator.ValidateDescriptor(
            GovernedLoopWaitContractTestFixture.EventDescriptor(),
            uppercase).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.TryCreateCondition(
            GovernedLoopWaitContractTestFixture.EventDescriptor(),
            switching,
            out var condition,
            out var validation));
        Assert.True(validation.IsValid);
        Assert.Equal("governed-event-1", condition!.AuthenticatedEventReference);
        Assert.True(GovernedLoopWaitContractValidator.Validate(condition).IsValid);
        Assert.Equal(0, switching.IndexerReads);
        Assert.Equal(1, switching.EnumerationReads);
    }

    private sealed class SwitchingParameters(string key, string capturedValue, string indexerValue) : IReadOnlyDictionary<string, string>
    {
        public int Count => 1;

        public IEnumerable<string> Keys => [key];

        public IEnumerable<string> Values => [capturedValue];

        internal int IndexerReads { get; private set; }

        internal int EnumerationReads { get; private set; }

        public string this[string requestedKey]
        {
            get
            {
                IndexerReads++;
                return indexerValue;
            }
        }

        public bool ContainsKey(string requestedKey) => string.Equals(requestedKey, key, StringComparison.Ordinal);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            EnumerationReads++;
            yield return new KeyValuePair<string, string>(key, capturedValue);
        }

        public bool TryGetValue(string requestedKey, out string value)
        {
            value = indexerValue;
            return ContainsKey(requestedKey);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;
        private readonly System.Globalization.CultureInfo _uiCulture = System.Globalization.CultureInfo.CurrentUICulture;

        internal CultureScope(string name)
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(name);
        }

        public void Dispose()
        {
            System.Globalization.CultureInfo.CurrentCulture = _culture;
            System.Globalization.CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
