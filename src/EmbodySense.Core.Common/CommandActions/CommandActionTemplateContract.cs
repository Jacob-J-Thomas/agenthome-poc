using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Validates, snapshots, and hashes immutable structured command templates.</summary>
public static class CommandActionTemplateContract
{
    private const string Domain = "embodysense.command-action-template.v1";

    /// <summary>Creates one validated template and applies its canonical content hash.</summary>
    public static CommandActionTemplate Create(
        int schemaVersion,
        CapabilityDescriptorIdentity capability,
        CapabilityImplementationIdentity implementation,
        CapabilityIntegrityDigest artifactDigest,
        long activationRevision,
        string templateId,
        long templateVersion,
        IReadOnlyList<CommandActionSlotDefinition> slots,
        IReadOnlyList<CommandActionArgumentPart> arguments,
        IReadOnlyList<CommandActionEnvironmentEntry> environment,
        CommandActionSecondaryGrammarPolicy secondaryGrammar,
        CommandActionStandardInputKind standardInput,
        string? standardInputSlot,
        CommandActionOutputKind output,
        CommandActionIsolationPolicy isolation,
        bool requiresCredentialChannel)
    {
        var candidate = new CommandActionTemplate(
            schemaVersion,
            capability,
            implementation,
            artifactDigest,
            activationRevision,
            templateId,
            templateVersion,
            slots,
            arguments,
            environment,
            secondaryGrammar,
            standardInput,
            standardInputSlot,
            output,
            isolation,
            requiresCredentialChannel,
            string.Empty);
        var reasonCode = ValidateForHash(candidate);
        if (reasonCode is not null)
        {
            throw new ArgumentException(reasonCode, nameof(templateId));
        }
        return candidate with { ContentHash = Compute(candidate) };
    }

    /// <summary>Returns a bounded reason code when a template is invalid; otherwise <see langword="null"/>.</summary>
    public static string? Validate(CommandActionTemplate? template)
    {
        var reasonCode = ValidateForHash(template);
        return reasonCode is not null
            ? reasonCode
            : CommandActionFingerprint.IsCanonicalSha256(template!.ContentHash)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(template.ContentHash), Encoding.ASCII.GetBytes(Compute(template)))
                    ? null
                    : "command-template-content-hash-mismatch";
    }

    /// <summary>Computes the canonical content hash, excluding <c>ContentHash</c>.</summary>
    public static string Compute(CommandActionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var reasonCode = ValidateForHash(template);
        if (reasonCode is not null)
        {
            throw new ArgumentException(reasonCode, nameof(template));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        Append(hash, template.SchemaVersion);
        Append(hash, template.Capability.Id.Value);
        Append(hash, template.Capability.Version.Value);
        Append(hash, template.Capability.Hash.Value);
        Append(hash, template.Implementation.ProviderId.Value);
        Append(hash, template.Implementation.ImplementationId);
        Append(hash, template.ArtifactDigest.Value);
        Append(hash, template.ActivationRevision);
        Append(hash, template.TemplateId);
        Append(hash, template.TemplateVersion);
        Append(hash, template.Slots.Count);
        foreach (var slot in template.Slots)
        {
            Append(hash, slot.Name);
            Append(hash, (int)slot.Kind);
            Append(hash, slot.MaxUtf8Bytes);
            Append(hash, slot.MinimumInteger);
            Append(hash, slot.MaximumInteger);
            Append(hash, slot.AllowLeadingOption);
            Append(hash, slot.EnumerationValues.Count);
            foreach (var value in slot.EnumerationValues)
            {
                Append(hash, value);
            }
        }
        Append(hash, template.Arguments.Count);
        foreach (var argument in template.Arguments)
        {
            Append(hash, (int)argument.Kind);
            Append(hash, argument.Value);
        }
        Append(hash, template.Environment.Count);
        foreach (var entry in template.Environment)
        {
            Append(hash, entry.Name);
            Append(hash, entry.Value);
        }
        Append(hash, (int)template.SecondaryGrammar);
        Append(hash, (int)template.StandardInput);
        Append(hash, template.StandardInputSlot);
        Append(hash, (int)template.Output);
        Append(hash, (int)template.Isolation.WorkingDirectory);
        Append(hash, (int)template.Isolation.Network);
        Append(hash, template.Isolation.MaxExecutionMilliseconds);
        Append(hash, template.Isolation.MaxTerminationMilliseconds);
        Append(hash, template.Isolation.MaxMemoryBytes);
        Append(hash, template.Isolation.MaxOutputBytes);
        Append(hash, template.Isolation.MaxConcurrency);
        Append(hash, template.Isolation.RequireProcessTreeTermination);
        Append(hash, template.RequiresCredentialChannel);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Gets whether a template id is one canonical bounded capability path.</summary>
    public static bool IsTemplateId(string? value)
        => CapabilityIdentifierRules.IsPath(value, CommandActionContractLimits.MaxTemplateIdCharacters);

    /// <summary>Gets whether a graph/template slot name is one canonical bounded identifier token.</summary>
    public static bool IsSlotName(string? value)
        => CapabilityIdentifierRules.IsToken(value);

    /// <summary>Gets whether a literal is normalized, free of unsafe Unicode, and within its UTF-8 byte bound.</summary>
    public static bool IsSafeLiteralToken(string? value, int maximumUtf8Bytes, bool allowEmpty)
        => value is not null
            && (allowEmpty || value.Length > 0)
            && Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes
            && CapabilityTextRules.IsSafeNormalized(value, value.Length, allowEmpty);

    private static string? ValidateForHash(CommandActionTemplate? template)
    {
        if (template is null)
        {
            return "command-template-required";
        }
        if (template.SchemaVersion != CommandActionContractLimits.CurrentSchemaVersion)
        {
            return "command-template-schema-unsupported";
        }
        if (template.Capability?.Id is null
            || template.Capability.Version is null
            || template.Capability.Hash is null
            || !CapabilityId.TryParse(template.Capability.Id.Value, out _, out _)
            || !CapabilityVersion.TryParse(template.Capability.Version.Value, out _, out _)
            || !CapabilityDescriptorHash.TryParse(template.Capability.Hash.Value, out _, out _)
            || template.Implementation?.ProviderId is null
            || !CapabilityProviderId.TryParse(template.Implementation.ProviderId.Value, out _, out _)
            || !CapabilityIdentifierRules.IsPath(template.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            || template.ArtifactDigest is null
            || !CapabilityIntegrityDigest.TryParse(template.ArtifactDigest.Value, out _, out _))
        {
            return "command-template-artifact-pin-invalid";
        }
        if (template.ActivationRevision < 1 || !IsTemplateId(template.TemplateId) || template.TemplateVersion < 1)
        {
            return "command-template-identity-invalid";
        }
        if (!ValidateSlots(template.Slots, out var slotsFailure))
        {
            return slotsFailure;
        }
        if (!ValidateArguments(template.Arguments, template.Slots, template.StandardInputSlot, out var argumentsFailure))
        {
            return argumentsFailure;
        }
        if (!ValidateEnvironment(template.Environment, out var environmentFailure))
        {
            return environmentFailure;
        }
        if (template.SecondaryGrammar != CommandActionSecondaryGrammarPolicy.None
            || !ValidateStandardInput(template.StandardInput, template.StandardInputSlot, template.Slots)
            || template.Output != CommandActionOutputKind.Json
            || !ValidateIsolation(template.Isolation))
        {
            return "command-template-policy-invalid";
        }
        return null;
    }

    private static bool ValidateSlots(IReadOnlyList<CommandActionSlotDefinition>? slots, out string? reasonCode)
    {
        reasonCode = "command-template-slots-invalid";
        if (slots is null || slots.Count > CommandActionContractLimits.MaxSlots)
        {
            return false;
        }
        var names = slots.Select(slot => slot?.Name).ToArray();
        if (names.Any(name => !CapabilityIdentifierRules.IsToken(name))
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || !names.SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }
        foreach (var slot in slots)
        {
            if (slot is null
                || slot.Kind == CommandActionSlotKind.Unknown
                || !Enum.IsDefined(slot.Kind)
                || slot.MaxUtf8Bytes is < 1 or > CommandActionContractLimits.MaxValueUtf8Bytes
                || slot.EnumerationValues is null)
            {
                return false;
            }
            var integer = slot.Kind == CommandActionSlotKind.Integer;
            var enumeration = slot.Kind == CommandActionSlotKind.Enumeration;
            if (integer != (slot.MinimumInteger.HasValue && slot.MaximumInteger.HasValue)
                || integer && slot.MinimumInteger > slot.MaximumInteger
                || enumeration != (slot.EnumerationValues.Count > 0)
                || slot.EnumerationValues.Count > CommandActionContractLimits.MaxSlots
                || slot.EnumerationValues.Distinct(StringComparer.Ordinal).Count() != slot.EnumerationValues.Count
                || !slot.EnumerationValues.SequenceEqual(slot.EnumerationValues.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || slot.EnumerationValues.Any(value => !IsSafeLiteralToken(value, slot.MaxUtf8Bytes, allowEmpty: false)))
            {
                return false;
            }
        }
        reasonCode = null;
        return true;
    }

    private static bool ValidateArguments(
        IReadOnlyList<CommandActionArgumentPart>? arguments,
        IReadOnlyList<CommandActionSlotDefinition> slots,
        string? standardInputSlot,
        out string? reasonCode)
    {
        reasonCode = "command-template-arguments-invalid";
        if (arguments is null || arguments.Count > CommandActionContractLimits.MaxArguments)
        {
            return false;
        }
        var names = slots.Select(slot => slot.Name).ToHashSet(StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        string? precedingFixed = null;
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Kind == CommandActionArgumentPartKind.Unknown || !Enum.IsDefined(argument.Kind))
            {
                return false;
            }
            if (argument.Kind == CommandActionArgumentPartKind.Fixed)
            {
                if (!IsSafeLiteralToken(argument.Value, CommandActionContractLimits.MaxValueUtf8Bytes, allowEmpty: true)
                    || argument.Value.StartsWith('@')
                    || IsSecondaryGrammarIntroducer(argument.Value))
                {
                    return false;
                }
                precedingFixed = argument.Value;
            }
            else if (!names.Contains(argument.Value)
                || !consumed.Add(argument.Value)
                || IsSecondaryGrammarIntroducer(precedingFixed))
            {
                return false;
            }
            else
            {
                precedingFixed = null;
            }
        }
        if (standardInputSlot is not null)
        {
            consumed.Add(standardInputSlot);
        }
        if (!consumed.SetEquals(names))
        {
            reasonCode = "command-template-slot-consumption-invalid";
            return false;
        }
        reasonCode = null;
        return true;
    }

    private static bool IsSecondaryGrammarIntroducer(string? value)
        => value is "-c" or "/c" or "/C" or "-Command" or "-EncodedCommand" or "--eval" or "--execute" or "--require" or "--import" or "--config" or "--config-file";

    private static bool ValidateEnvironment(IReadOnlyList<CommandActionEnvironmentEntry>? environment, out string? reasonCode)
    {
        reasonCode = "command-template-environment-invalid";
        if (environment is null || environment.Count > CommandActionContractLimits.MaxEnvironmentEntries)
        {
            return false;
        }
        var names = environment.Select(entry => entry?.Name).ToArray();
        if (names.Any(name => !IsEnvironmentName(name))
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || !names.SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || environment.Any(entry => entry is null || !IsSafeLiteralToken(entry.Value, CommandActionContractLimits.MaxValueUtf8Bytes, allowEmpty: true)))
        {
            return false;
        }
        reasonCode = null;
        return true;
    }

    private static bool ValidateStandardInput(CommandActionStandardInputKind kind, string? slotName, IReadOnlyList<CommandActionSlotDefinition> slots)
    {
        if (kind == CommandActionStandardInputKind.Closed)
        {
            return slotName is null;
        }
        var slot = slots.SingleOrDefault(candidate => string.Equals(candidate.Name, slotName, StringComparison.Ordinal));
        return kind switch
        {
            CommandActionStandardInputKind.SlotUtf8 => slot?.Kind == CommandActionSlotKind.BoundedText,
            CommandActionStandardInputKind.SlotJson => slot?.Kind == CommandActionSlotKind.BoundedJson,
            _ => false,
        };
    }

    private static bool ValidateIsolation(CommandActionIsolationPolicy? isolation)
        => isolation is
        {
            WorkingDirectory: CommandActionWorkingDirectoryKind.ArtifactRoot,
            Network: CommandActionNetworkPolicy.Denied,
            MaxExecutionMilliseconds: >= 1 and <= CommandActionContractLimits.MaxExecutionMilliseconds,
            MaxTerminationMilliseconds: >= 1 and <= CommandActionContractLimits.MaxTerminationMilliseconds,
            MaxMemoryBytes: >= 1 and <= CommandActionContractLimits.MaxMemoryBytes,
            MaxOutputBytes: >= 1 and <= CommandActionContractLimits.MaxOutputBytes,
            MaxConcurrency: >= 1 and <= CommandActionContractLimits.MaxConcurrency,
            RequireProcessTreeTermination: true,
        };

    private static bool IsEnvironmentName(string? value)
        => value is { Length: >= 1 and <= CommandActionContractLimits.MaxEnvironmentNameCharacters }
            && value[0] is >= 'A' and <= 'Z'
            && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Span<byte> missing = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(missing, -1);
            hash.AppendData(missing);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value) => Append(hash, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static void Append(IncrementalHash hash, long value) => Append(hash, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static void Append(IncrementalHash hash, long? value) => Append(hash, value?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static void Append(IncrementalHash hash, bool value) => Append(hash, value ? "1" : "0");
}
