namespace EmbodySense.Core.Common.HumanInput.Models;

public sealed partial record HumanInputResponseValue
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponseValue {{ Kind = {Kind}, Content = [REDACTED] }}";
}
