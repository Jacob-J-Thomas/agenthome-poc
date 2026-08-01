namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Maps server-observed physical directory material to the workspace identity used by capability-catalog trust.</summary>
/// <remarks>This is trusted server infrastructure. Implementations must preserve every physical lifetime discriminator supplied by the path session.</remarks>
public interface ICapabilityCatalogWorkspaceIdentityProvider
{
    /// <summary>Creates the canonical workspace identity for one retained physical directory handle.</summary>
    /// <param name="physicalIdentityMaterial">Opaque server-observed physical directory material.</param>
    /// <returns>The canonical workspace identity.</returns>
    string Create(string physicalIdentityMaterial);
}
