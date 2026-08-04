using EmbodySense.Core.Application.Secrets.Redaction.Models;
using EmbodySense.Core.Common.Secrets.Redaction;

namespace EmbodySense.Core.Application.Secrets.Redaction;

internal sealed class RedactionTraversalContext
{
    private readonly HashSet<object> _activeReferences = new(ReferenceEqualityComparer.Instance);

    public RedactionTraversalContext(SensitiveRedactionScope scope, RedactionProjectionLimits limits)
    {
        Scope = scope;
        Limits = limits;
        Accumulator = new RedactionProjectionAccumulator();
    }

    public SensitiveRedactionScope Scope { get; }

    public RedactionProjectionLimits Limits { get; }

    public RedactionProjectionAccumulator Accumulator { get; }

    public bool TryEnter(object value)
    {
        return _activeReferences.Add(value);
    }

    public void Exit(object value)
    {
        _activeReferences.Remove(value);
    }
}
