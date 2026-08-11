namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>Signals that an authority boundary violated the single-use actuator continuation protocol.</summary>
public sealed class ToolActuationAuthorityProtocolException : Exception
{
    /// <summary>Creates a protocol failure with a bounded explanation.</summary>
    /// <param name="message">The protocol invariant that was violated.</param>
    public ToolActuationAuthorityProtocolException(string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
    }
}
