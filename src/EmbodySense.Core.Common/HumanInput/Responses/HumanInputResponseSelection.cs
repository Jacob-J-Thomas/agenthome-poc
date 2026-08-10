namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseSelection
{
    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponseSelection {{ SchemaVersion = {SchemaVersion}, SelectionId = {SelectionId}, Request = {Request}, PolicyKind = {PolicyKind}, ResponseCount = {Responses.Length}, SelectedAtUtc = {SelectedAtUtc:O}, SelectionHash = {SelectionHash}, PrivateAttribution = [REDACTED] }}";
}
