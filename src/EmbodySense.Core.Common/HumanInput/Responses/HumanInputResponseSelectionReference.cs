namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseSelectionReference
{
    /// <summary>Creates an exact privacy-safe reference from one already validated selection.</summary>
    /// <param name="selection">The valid immutable selection.</param>
    /// <returns>The exact selection reference.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="selection"/> is null.</exception>
    public static HumanInputResponseSelectionReference Create(HumanInputResponseSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new HumanInputResponseSelectionReference(CurrentSchemaVersion, selection.SelectionId, selection.Request, selection.SelectionHash);
    }

    /// <summary>Determines whether this reference exactly identifies the supplied selection.</summary>
    /// <param name="selection">The selection to compare.</param>
    /// <returns><see langword="true"/> only when every exact identity and canonical hash matches.</returns>
    public bool Matches(HumanInputResponseSelection? selection)
        => selection is not null
            && Equals(this, new HumanInputResponseSelectionReference(CurrentSchemaVersion, selection.SelectionId, selection.Request, selection.SelectionHash));

    /// <inheritdoc />
    public override string ToString() => $"HumanInputResponseSelectionReference {{ SchemaVersion = {SchemaVersion}, SelectionId = {SelectionId}, Request = {Request}, SelectionHash = {SelectionHash} }}";
}
