using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Common.Tests;

public sealed class CustomLoopArtifactIdentifierTests
{
    [Theory]
    [InlineData("loop-one")]
    [InlineData("loop_one.2")]
    [InlineData("0-loop")]
    public void IsValid_accepts_canonical_filename_safe_identifiers(string value)
    {
        Assert.True(CustomLoopArtifactIdentifier.IsValid(value));
        Assert.Equal(value, CustomLoopArtifactIdentifier.Require(value, "value"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Loop")]
    [InlineData("loop/")]
    [InlineData("-loop")]
    [InlineData("loop-")]
    [InlineData("loop.")]
    [InlineData("con")]
    [InlineData("con.txt")]
    [InlineData("com9")]
    [InlineData("lpt1.json")]
    [InlineData("nul")]
    public void IsValid_rejects_noncanonical_reserved_or_trailing_separator_identifiers(string value)
    {
        Assert.False(CustomLoopArtifactIdentifier.IsValid(value));
        Assert.Throws<ArgumentException>(() => CustomLoopArtifactIdentifier.Require(value, "value"));
    }

    [Fact]
    public void IsValid_enforces_the_requested_maximum_length()
    {
        Assert.False(CustomLoopArtifactIdentifier.IsValid("abcd", maxLength: 3));
        Assert.Throws<ArgumentException>(() => CustomLoopArtifactIdentifier.Require("abcd", "value", maxLength: 3));
    }
}
