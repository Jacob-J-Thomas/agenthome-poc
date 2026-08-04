using System.Text.Json;
using EmbodySense.Core.Common.Secrets;

namespace EmbodySense.Core.Common.Tests.Secrets;

public sealed class EphemeralSecretMaterialTests
{
    [Fact]
    public void Dispose_zeros_owned_memory_and_is_idempotent()
    {
        const string Canary = "canary-secret-value";
        var owned = Canary.ToCharArray();
        var material = EphemeralSecretMaterial.TakeOwnership(owned);

        Assert.Equal(Canary, new string(owned));
        material.Dispose();
        material.Dispose();

        Assert.True(material.IsDisposed);
        Assert.Equal(0, material.Length);
        Assert.All(owned, character => Assert.Equal('\0', character));
    }

    [Fact]
    public void Public_string_debugger_and_serialization_projections_are_value_free()
    {
        const string Canary = "ephemeral-secret-material";
        using var material = EphemeralSecretMaterial.Create(Canary);

        var serialized = JsonSerializer.Serialize(material);

        Assert.DoesNotContain(Canary, material.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_copies_the_source_and_rejects_unbounded_material_without_echoing_it()
    {
        var source = "copy-me".ToCharArray();
        using var material = EphemeralSecretMaterial.Create(source);
        Array.Fill(source, 'x');

        Assert.Equal("[ephemeral-secret-material]", material.ToString());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => EphemeralSecretMaterial.Create(new string('z', EphemeralSecretMaterial.MaxCharacters + 1)));
        Assert.DoesNotContain(new string('z', 32), exception.Message, StringComparison.Ordinal);

        material.Dispose();
        Assert.Equal("[ephemeral-secret-material]", material.ToString());
    }

    [Fact]
    public void Empty_material_uses_an_empty_projection_instead_of_a_marker_that_contains_its_value()
    {
        using var material = EphemeralSecretMaterial.Create("");

        Assert.Empty(material.ToString());
    }

    [Fact]
    public void Marker_collision_remains_value_free_after_disposal()
    {
        var material = EphemeralSecretMaterial.Create("ephemeral-secret-material");

        Assert.Empty(material.ToString());
        material.Dispose();

        Assert.Empty(material.ToString());
    }
}
