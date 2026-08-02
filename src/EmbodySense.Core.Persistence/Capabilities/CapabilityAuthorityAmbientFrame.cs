namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class CapabilityAuthorityAmbientFrame(string identity, CapabilityAuthorityAmbientFrame? parent)
{
    private int _active = 1;

    internal bool ContainsActive(string candidate)
    {
        for (CapabilityAuthorityAmbientFrame? frame = this; frame is not null; frame = frame.Parent)
        {
            if (Volatile.Read(ref frame._active) == 1 && string.Equals(frame.Identity, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    internal string Identity { get; } = identity;

    internal CapabilityAuthorityAmbientFrame? Parent { get; } = parent;

    internal void Invalidate() => Interlocked.Exchange(ref _active, 0);
}
