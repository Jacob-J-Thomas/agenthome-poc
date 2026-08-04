using EmbodySense.Core.Common.Secrets;
using EmbodySense.Core.Common.Secrets.Redaction;

namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class RedactionScopeFixture : IDisposable
{
    private readonly EphemeralSecretMaterial _material;

    public RedactionScopeFixture(string value)
    {
        _material = EphemeralSecretMaterial.Create(value);
        Scope = SensitiveRedactionScope.Create([_material]);
        _material.Dispose();
    }

    public SensitiveRedactionScope Scope { get; }

    public void Dispose()
    {
        Scope.Dispose();
        _material.Dispose();
    }
}
