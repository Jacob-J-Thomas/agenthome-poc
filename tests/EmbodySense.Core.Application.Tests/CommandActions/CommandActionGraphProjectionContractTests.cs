using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Tests.CommandActions;

public sealed class CommandActionGraphProjectionContractTests
{
    [Fact]
    public void Projection_prices_the_sum_of_every_independently_usable_slot()
    {
        var registration = Registration(
        [
            new CommandActionSlotDefinition("first", CommandActionSlotKind.BoundedText, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [], false),
            new CommandActionSlotDefinition("second", CommandActionSlotKind.BoundedText, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [], false),
        ]);

        Assert.True(CommandActionGraphProjectionContract.TryGetPayloadCharacters(registration, out var payloadCharacters));
        Assert.Equal(CustomLoopLimits.MaxGraphParameterValueCharacters * 2, payloadCharacters);
    }

    [Fact]
    public void Projection_rejects_any_template_that_cannot_preserve_graph_parameter_or_payload_bounds()
    {
        var overlongSlot = Registration(
        [
            new CommandActionSlotDefinition("value", CommandActionSlotKind.BoundedText, CustomLoopLimits.MaxGraphParameterValueCharacters + 1, null, null, [], false),
        ]);
        var overBudget = Registration(Enumerable.Range(0, 32).Select(index =>
            new CommandActionSlotDefinition($"value-{index:D2}", CommandActionSlotKind.BoundedText, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [], false)).ToArray());

        Assert.False(CommandActionGraphProjectionContract.TryGetPayloadCharacters(overlongSlot, out _));
        Assert.False(CommandActionGraphProjectionContract.TryGetPayloadCharacters(overBudget, out _));
    }

    [Theory]
    [InlineData("@file", true)]
    [InlineData("--unsafe", false)]
    public void Projection_rejects_enumerations_that_would_poison_the_graph_catalog(string value, bool allowLeadingOption)
    {
        var registration = Registration(
        [
            new CommandActionSlotDefinition("mode", CommandActionSlotKind.Enumeration, 64, null, null, [value], allowLeadingOption),
        ]);

        Assert.False(CommandActionGraphProjectionContract.TryGetPayloadCharacters(registration, out _));
    }

    private static CommandActionRegistration Registration(IReadOnlyList<CommandActionSlotDefinition> slots)
    {
        var registration = CommandActionApplicationTestData.Registration();
        var template = CommandActionTemplateContract.Create(
            registration.Template.SchemaVersion,
            registration.Template.Capability,
            registration.Template.Implementation,
            registration.Template.ArtifactDigest,
            registration.Template.ActivationRevision,
            registration.Template.TemplateId,
            registration.Template.TemplateVersion,
            slots,
            slots.Select(slot => new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, slot.Name)).ToArray(),
            registration.Template.Environment,
            registration.Template.SecondaryGrammar,
            CommandActionStandardInputKind.Closed,
            null,
            registration.Template.Output,
            registration.Template.Isolation,
            registration.Template.RequiresCredentialChannel);
        return registration with { Template = template };
    }
}
