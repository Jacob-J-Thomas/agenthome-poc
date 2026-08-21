using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityCatalogPageTests
{
    [Fact]
    public void Exact_public_page_bound_is_captured_as_a_defensive_snapshot()
    {
        var entry = CapabilityPostureTestData.Entry();
        var source = Enumerable.Repeat(entry, 100).ToArray();

        var page = new CapabilityCatalogPage(7, source, null);
        source[0] = null!;

        Assert.Equal(100, page.Entries.Count);
        Assert.Same(entry, page.Entries[0]);
    }

    [Fact]
    public void Public_page_rejects_more_than_one_hundred_entries_before_enumeration()
    {
        var entry = CapabilityPostureTestData.Entry();
        var source = new HostileList(101, Enumerable.Repeat(entry, 101).ToArray(), throwOnCount: false, throwOnEnumeration: true);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityCatalogPage(7, source, null));

        Assert.Equal("entries", exception.ParamName);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void Public_page_rejects_under_or_over_declared_entry_counts(int declaredCount, int actualCount)
    {
        var entry = CapabilityPostureTestData.Entry();
        var source = new HostileList(declaredCount, Enumerable.Repeat(entry, actualCount).ToArray(), throwOnCount: false, throwOnEnumeration: false);

        var exception = Assert.Throws<ArgumentException>(() => new CapabilityCatalogPage(7, source, null));

        Assert.Equal("entries", exception.ParamName);
    }

    [Fact]
    public void Public_page_propagates_throwing_count_without_enumerating()
    {
        var entry = CapabilityPostureTestData.Entry();
        var source = new HostileList(1, [entry], throwOnCount: true, throwOnEnumeration: true);

        var exception = Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogPage(7, source, null));

        Assert.Equal("secret-canary-count", exception.Message);
    }

    [Fact]
    public void Public_page_propagates_throwing_enumerator_after_bounded_count_read()
    {
        var entry = CapabilityPostureTestData.Entry();
        var source = new HostileList(1, [entry], throwOnCount: false, throwOnEnumeration: true);

        var exception = Assert.Throws<InvalidOperationException>(() => new CapabilityCatalogPage(7, source, null));

        Assert.Equal("secret-canary-enumerator", exception.Message);
    }

    private sealed class HostileList(
        int declaredCount,
        IReadOnlyList<CapabilityCatalogEntry> entries,
        bool throwOnCount,
        bool throwOnEnumeration) : IReadOnlyList<CapabilityCatalogEntry>
    {
        public int Count
            => throwOnCount
                ? throw new InvalidOperationException("secret-canary-count")
                : declaredCount;

        public CapabilityCatalogEntry this[int index] => entries[index];

        public IEnumerator<CapabilityCatalogEntry> GetEnumerator()
            => throwOnEnumeration
                ? throw new InvalidOperationException("secret-canary-enumerator")
                : entries.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
