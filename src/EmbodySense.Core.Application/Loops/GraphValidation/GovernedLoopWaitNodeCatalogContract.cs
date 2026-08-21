using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the closed executable schema-1 catalog contract for durable timestamp and authenticated-event Wait nodes.</summary>
public static class GovernedLoopWaitNodeCatalogContract
{
    private const int CanonicalUtcTimestampCharacters = 28;
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success });
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _successFailure =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure });
    private static readonly IReadOnlyList<GovernedLoopNodeCatalogDescriptor> _descriptors = CreateDescriptors();

    /// <summary>Gets the two exact Wait descriptor declarations in canonical key order.</summary>
    public static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> Descriptors => _descriptors;

    /// <summary>Resolves one exact Wait descriptor declaration without aliases or fallback.</summary>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out GovernedLoopNodeCatalogDescriptor? contract)
    {
        contract = descriptor is null
            ? null
            : _descriptors.SingleOrDefault(candidate => Equals(candidate.Descriptor, descriptor));
        return contract is not null;
    }

    internal static bool HasExactCatalogSemantics(GovernedLoopNodeCatalogDescriptor candidate)
    {
        if (!TryResolve(candidate.Descriptor, out var canonical) || canonical is null)
        {
            return false;
        }

        return candidate.IsAdvertised == canonical.IsAdvertised
            && candidate.IsExecutable == canonical.IsExecutable
            && candidate.IsLegalEntry == canonical.IsLegalEntry
            && candidate.IsLegalTerminal == canonical.IsLegalTerminal
            && candidate.AllowedControlOutcomes.SequenceEqual(canonical.AllowedControlOutcomes)
            && candidate.RequiredControlOutcomes.SequenceEqual(canonical.RequiredControlOutcomes)
            && candidate.JoinPolicy == canonical.JoinPolicy
            && candidate.MinimumIncomingControlEdges == canonical.MinimumIncomingControlEdges
            && candidate.AllowsCycle == canonical.AllowsCycle
            && string.Equals(candidate.CycleIterationBudgetParameterId, canonical.CycleIterationBudgetParameterId, StringComparison.Ordinal)
            && string.Equals(candidate.CycleTimeBudgetMillisecondsParameterId, canonical.CycleTimeBudgetMillisecondsParameterId, StringComparison.Ordinal)
            && candidate.Ports.Count == 0
            && candidate.Parameters.Count == canonical.Parameters.Count
            && candidate.Parameters.Zip(canonical.Parameters).All(pair => HasExactParameterSemantics(pair.First, pair.Second))
            && candidate.RequiredCapabilityIds.Count == 0
            && Equals(candidate.ResourceBudget, canonical.ResourceBudget);
    }

    private static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> CreateDescriptors()
    {
        var descriptors = new[]
        {
            Wait(
                GovernedLoopWaitVocabulary.Timestamp,
                new GovernedLoopCatalogParameterContract(
                    GovernedLoopWaitVocabulary.DeadlineUtcParameter,
                    GovernedLoopParameterValueKind.Text,
                    Required: true,
                    CanonicalUtcTimestampCharacters,
                    CanonicalUtcTimestampCharacters,
                    null,
                    null,
                    Array.Empty<string>())),
            Wait(
                GovernedLoopWaitVocabulary.AuthenticatedEvent,
                new GovernedLoopCatalogParameterContract(
                    GovernedLoopWaitVocabulary.EventReferenceParameter,
                    GovernedLoopParameterValueKind.Text,
                    Required: true,
                    1,
                    GovernedLoopWaitContractLimits.MaxEventReferenceCharacters,
                    null,
                    null,
                    Array.Empty<string>())),
        };
        return Array.AsReadOnly(descriptors.OrderBy(DescriptorKey, StringComparer.Ordinal).ToArray());
    }

    private static GovernedLoopNodeCatalogDescriptor Wait(
        string typeId,
        GovernedLoopCatalogParameterContract conditionParameter)
        => new(
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, typeId, GovernedLoopWaitVocabulary.DescriptorVersion),
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _successFailure,
            _success,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            null,
            null,
            Array.Empty<GovernedLoopCatalogPortContract>(),
            Array.AsReadOnly(new[] { conditionParameter }),
            Array.Empty<string>(),
            new GovernedLoopNodeResourceBudget(1, 0, CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation, 0));

    private static bool HasExactParameterSemantics(
        GovernedLoopCatalogParameterContract? candidate,
        GovernedLoopCatalogParameterContract canonical)
        => candidate is not null
            && string.Equals(candidate.Id, canonical.Id, StringComparison.Ordinal)
            && candidate.ValueKind == canonical.ValueKind
            && candidate.Required == canonical.Required
            && candidate.MinimumCharacters == canonical.MinimumCharacters
            && candidate.MaximumCharacters == canonical.MaximumCharacters
            && candidate.MaximumUtf8Bytes == canonical.MaximumUtf8Bytes
            && candidate.AllowLeadingOption == canonical.AllowLeadingOption
            && candidate.AllowResponseFileReference == canonical.AllowResponseFileReference
            && candidate.MinimumInteger == canonical.MinimumInteger
            && candidate.MaximumInteger == canonical.MaximumInteger
            && candidate.AllowedValues is not null
            && candidate.AllowedValues.SequenceEqual(canonical.AllowedValues, StringComparer.Ordinal);

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor)
        => $"{(int)descriptor.Descriptor.Kind:D3}:{descriptor.Descriptor.TypeId}:{descriptor.Descriptor.Version:D10}";
}
