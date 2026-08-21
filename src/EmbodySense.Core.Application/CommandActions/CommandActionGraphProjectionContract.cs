using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Validates whether one exact command registration can be represented without weakening the schema-1 graph contract.</summary>
public static class CommandActionGraphProjectionContract
{
    /// <summary>Gets the conservative full activation payload when the registration is graph-compatible.</summary>
    /// <param name="registration">The exact server-owned command registration.</param>
    /// <param name="payloadCharacters">The sum of every independently usable slot ceiling when compatible.</param>
    /// <returns><see langword="true"/> only when the complete template can be projected without truncating or broadening its semantics.</returns>
    public static bool TryGetPayloadCharacters(CommandActionRegistration? registration, out int payloadCharacters)
    {
        payloadCharacters = 0;
        if (CommandActionRegistrationContract.Validate(registration) is not null)
        {
            return false;
        }

        long total = 0;
        foreach (var slot in registration!.Template.Slots)
        {
            if (slot.MaxUtf8Bytes > CustomLoopLimits.MaxGraphParameterValueCharacters
                || !EnumerationValuesAreGraphCompatible(slot))
            {
                return false;
            }

            total += slot.MaxUtf8Bytes;
            if (total > CustomLoopLimits.MaxGraphNodePayloadCharacters)
            {
                return false;
            }
        }

        payloadCharacters = (int)total;
        return true;
    }

    private static bool EnumerationValuesAreGraphCompatible(CommandActionSlotDefinition slot)
        => slot.Kind != CommandActionSlotKind.Enumeration
            || slot.EnumerationValues.All(value => !value.StartsWith('@') && (slot.AllowLeadingOption || !value.StartsWith('-')));
}
