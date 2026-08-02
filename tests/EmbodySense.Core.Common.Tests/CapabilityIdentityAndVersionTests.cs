using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Tests;

public sealed class CapabilityIdentityAndVersionTests
{
    [Fact]
    public void Identity_types_require_canonical_namespaced_ascii_values()
    {
        var id = CapabilityContractTestData.Id("org.example/files/read-text");
        var same = CapabilityContractTestData.Id("org.example/files/read-text");
        var later = CapabilityContractTestData.Id("org.example/files/write-text");
        var provider = CapabilityContractTestData.Provider("org.example");

        Assert.Equal("org.example/files/read-text", id.Value);
        Assert.Equal(id.Value, id.ToString());
        Assert.Equal(id, same);
        Assert.True(id.Equals((object)same));
        Assert.NotEqual(id, later);
        Assert.True(id.CompareTo(later) < 0);
        Assert.Equal(1, id.CompareTo(null));
        Assert.Equal(id.GetHashCode(), same.GetHashCode());
        Assert.Equal("org.example", provider.Value);
        Assert.Equal(provider.Value, provider.ToString());
        Assert.Equal(provider, CapabilityContractTestData.Provider("org.example"));
        Assert.True(provider.Equals((object)CapabilityContractTestData.Provider("org.example")));
        Assert.False(provider.Equals((object)id));
        Assert.Equal(1, provider.CompareTo(null));
        Assert.True(provider.CompareTo(CapabilityContractTestData.Provider("org.zzz")) < 0);

        foreach (var value in new string?[] { null, "", "orgexample/path", "org.Example/path", "org.example/Path", "org.example/", "/path", "org.example/path/", "org.example//path", "org.exämple/path", new string('a', CapabilityContractLimits.MaxCapabilityIdCharacters + 1) })
        {
            Assert.False(CapabilityId.TryParse(value, out var rejected, out var error));
            Assert.Null(rejected);
            Assert.Equal("invalid_capability_id", error?.Code);
        }

        foreach (var value in new string?[] { null, "", "example", ".org.example", "org.example.", "org..example", "Org.example", "org.-example", "org.example-", new string('a', 64) + ".example" })
        {
            Assert.False(CapabilityProviderId.TryParse(value, out var rejected, out var error));
            Assert.Null(rejected);
            Assert.Equal("invalid_provider_id", error?.Code);
        }
    }

    [Fact]
    public void Randomized_identity_round_trips_are_culture_independent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var cultureName in new[] { "tr-TR", "ar-SA", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                var random = new Random(204);
                for (var index = 0; index < 250; index++)
                {
                    var suffix = RandomToken(random, 3 + random.Next(24));
                    var value = $"org.example/{suffix}";
                    Assert.True(CapabilityId.TryParse(value, out var id, out _));
                    Assert.Equal(value, id?.ToString());
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Semantic_versions_follow_precedence_and_exact_identity_rules()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0"
        }.Select(CapabilityContractTestData.Version).ToArray();

        for (var index = 0; index < ordered.Length - 1; index++)
        {
            Assert.True(ordered[index].ComparePrecedenceTo(ordered[index + 1]) < 0);
        }

        var firstBuild = CapabilityContractTestData.Version("1.0.0+build.1");
        var secondBuild = CapabilityContractTestData.Version("1.0.0+build.2");
        Assert.Equal(0, firstBuild.ComparePrecedenceTo(secondBuild));
        Assert.True(firstBuild.CompareTo(secondBuild) < 0);
        Assert.NotEqual(firstBuild, secondBuild);
        Assert.Equal(firstBuild, CapabilityContractTestData.Version(firstBuild.Value));
        Assert.True(firstBuild.Equals((object)CapabilityContractTestData.Version(firstBuild.Value)));
        Assert.False(firstBuild.Equals((object)firstBuild.Value));
        Assert.Equal(firstBuild.GetHashCode(), CapabilityContractTestData.Version(firstBuild.Value).GetHashCode());
        Assert.Equal(1, firstBuild.ComparePrecedenceTo(null));
        Assert.Equal(1, firstBuild.CompareTo(null));
        Assert.False(firstBuild.IsPreRelease);
        Assert.True(ordered[0].IsPreRelease);
        Assert.Equal(1, ordered[0].Major);
        Assert.Equal(0, ordered[0].Minor);
        Assert.Equal(0, ordered[0].Patch);
        Assert.Equal("alpha", Assert.Single(ordered[0].PreReleaseIdentifiers));
        Assert.Equal("build.1", firstBuild.BuildMetadata);
        Assert.Equal(firstBuild.Value, firstBuild.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+a+b")]
    [InlineData("1.2.3 alpha")]
    [InlineData("1.2.3-ä")]
    [InlineData("2147483648.0.0")]
    public void Exact_version_parser_rejects_malformed_or_noncanonical_input(string value)
    {
        Assert.False(CapabilityVersion.TryParse(value, out var version, out var error));
        Assert.Null(version);
        Assert.Equal("invalid_capability_version", error?.Code);
    }

    [Fact]
    public void Version_ranges_use_explicit_deterministic_interval_membership()
    {
        var any = CapabilityContractTestData.Range("*");
        var exact = CapabilityContractTestData.Range("[1.2.3]");
        var bounded = CapabilityContractTestData.Range("[1.0.0,2.0.0)");
        var lowerOpen = CapabilityContractTestData.Range("(1.0.0,)");
        var upperClosed = CapabilityContractTestData.Range("(,2.0.0]");

        Assert.True(any.IsAny);
        Assert.True(any.Contains(CapabilityContractTestData.Version("999.0.0")));
        Assert.True(exact.Contains(CapabilityContractTestData.Version("1.2.3+other-build")));
        Assert.False(exact.Contains(CapabilityContractTestData.Version("1.2.4")));
        Assert.True(bounded.IncludesMinimum);
        Assert.False(bounded.IncludesMaximum);
        Assert.True(bounded.Contains(CapabilityContractTestData.Version("1.9.9")));
        Assert.False(bounded.Contains(CapabilityContractTestData.Version("2.0.0")));
        Assert.False(lowerOpen.Contains(CapabilityContractTestData.Version("1.0.0")));
        Assert.True(upperClosed.Contains(CapabilityContractTestData.Version("2.0.0")));
        Assert.Equal(bounded, CapabilityContractTestData.Range(bounded.Value));
        Assert.True(bounded.Equals((object)CapabilityContractTestData.Range(bounded.Value)));
        Assert.False(bounded.Equals((object)bounded.Value));
        Assert.Equal(bounded.GetHashCode(), CapabilityContractTestData.Range(bounded.Value).GetHashCode());
        Assert.Equal(bounded.Value, bounded.ToString());

        foreach (var value in new string?[] { null, "", "1.2.3", "[1.2]", "(1.2.3)", "[,]", "(,)", "[1.0.0,1.0.0]", "[2.0.0,1.0.0)", "[,2.0.0]", "[1.0.0,]", "[1.0.0, 2.0.0)", "[01.0.0,2.0.0)" })
        {
            Assert.False(CapabilityVersionRange.TryParse(value, out var rejected, out var error));
            Assert.Null(rejected);
            Assert.Equal("invalid_capability_version_range", error?.Code);
        }

        Assert.Throws<ArgumentNullException>(() => bounded.Contains(null!));
    }

    [Theory]
    [InlineData("[1.2.3+build.1]")]
    [InlineData("[1.0.0+build.1,2.0.0)")]
    [InlineData("[1.0.0,2.0.0+build.1)")]
    public void Version_ranges_reject_build_metadata_aliases(string value)
    {
        Assert.False(CapabilityVersionRange.TryParse(value, out var range, out var error));
        Assert.Null(range);
        Assert.Equal("invalid_capability_version_range", error?.Code);
        Assert.Contains("build metadata", error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Randomized_version_ranges_match_their_integer_boundary_model()
    {
        var random = new Random(2_040);
        for (var index = 0; index < 1_000; index++)
        {
            var minimum = random.Next(0, 50);
            var maximum = minimum + random.Next(1, 20);
            var includesMinimum = random.Next(2) == 0;
            var includesMaximum = random.Next(2) == 0;
            var range = CapabilityContractTestData.Range($"{(includesMinimum ? '[' : '(')}{minimum}.0.0,{maximum}.0.0{(includesMaximum ? ']' : ')')}");

            foreach (var candidate in new[] { minimum - 1, minimum, random.Next(minimum, maximum + 1), maximum, maximum + 1 }.Where(value => value >= 0))
            {
                var expected = (candidate > minimum || candidate == minimum && includesMinimum) && (candidate < maximum || candidate == maximum && includesMaximum);
                Assert.Equal(expected, range.Contains(CapabilityContractTestData.Version($"{candidate}.0.0")));
            }
        }
    }

    [Fact]
    public void Bounded_supporting_identifiers_and_digests_round_trip()
    {
        var platform = CapabilityContractTestData.Platform("windows/x64");
        var samePlatform = CapabilityContractTestData.Platform("windows/x64");
        var dataClass = CapabilityContractTestData.DataClass("workspace-content");
        var secret = CapabilityContractTestData.Secret("provider-token");
        var digest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes("content"));
        var parsedDigest = CapabilityContractTestData.Digest(digest.Value[7..]);

        Assert.Equal("windows", platform.OperatingSystem);
        Assert.Equal("x64", platform.Architecture);
        Assert.Equal("windows/x64", platform.ToString());
        Assert.Equal(platform, samePlatform);
        Assert.True(platform.Equals((object)samePlatform));
        Assert.False(platform.Equals((object)"windows/x64"));
        Assert.Equal(platform.GetHashCode(), samePlatform.GetHashCode());
        Assert.True(platform.CompareTo(CapabilityContractTestData.Platform("windows/x86")) < 0);
        Assert.Equal(1, platform.CompareTo(null));
        Assert.Equal("any/any", CapabilityPlatform.Any.ToString());
        Assert.Equal("workspace-content", dataClass.ToString());
        Assert.Equal(dataClass, CapabilityContractTestData.DataClass(dataClass.Value));
        Assert.True(dataClass.Equals((object)CapabilityContractTestData.DataClass(dataClass.Value)));
        Assert.False(dataClass.Equals((object)dataClass.Value));
        Assert.Equal(1, dataClass.CompareTo(null));
        Assert.True(dataClass.CompareTo(CapabilityContractTestData.DataClass("workspace-secret")) < 0);
        Assert.Equal("provider-token", secret.ToString());
        Assert.Equal(secret, CapabilityContractTestData.Secret(secret.Name));
        Assert.True(secret.Equals((object)CapabilityContractTestData.Secret(secret.Name)));
        Assert.False(secret.Equals((object)secret.Name));
        Assert.Equal(1, secret.CompareTo(null));
        Assert.True(secret.CompareTo(CapabilityContractTestData.Secret("provider-zzz")) < 0);
        Assert.Equal(digest, parsedDigest);
        Assert.True(digest.FixedTimeEquals(parsedDigest));
        Assert.False(digest.FixedTimeEquals(null));
        Assert.True(digest.Equals((object)parsedDigest));
        Assert.False(digest.Equals((object)digest.Value));
        Assert.Equal(digest.GetHashCode(), parsedDigest.GetHashCode());
        Assert.Equal(digest.Value, digest.ToString());

        Assert.False(CapabilityPlatform.TryParse("any/x64", out _, out _));
        Assert.False(CapabilityPlatform.TryParse("Windows/x64", out _, out _));
        Assert.False(CapabilityPlatform.TryParse("windows", out _, out _));
        Assert.False(CapabilityDataClass.TryParse("Private Data", out _, out _));
        Assert.False(CapabilitySecretRequirement.TryParse("token=value", out _, out _));
        Assert.False(CapabilityIntegrityDigest.TryParse("sha256:" + new string('A', 64), out _, out _));
        Assert.False(CapabilityIntegrityDigest.TryParse("md5:" + new string('a', 32), out _, out _));
    }

    private static string RandomToken(Random random, int length)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, random, (span, source) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = Alphabet[source.Next(Alphabet.Length)];
            }
        });
    }
}
