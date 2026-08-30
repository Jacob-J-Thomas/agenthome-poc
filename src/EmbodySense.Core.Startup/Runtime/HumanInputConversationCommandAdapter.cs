using System.Collections.Immutable;
using System.Text.Json;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>Projects bounded Human Input inspection and response commands through the default human conversation.</summary>
/// <remarks>The adapter is deliberately handled before model dispatch and never appends a submitted value to conversation
/// context, transcript history, diagnostics, or command output. It accepts only canonical request identities, caller-held
/// operation identities, response identifiers, and untrusted response data; the shared facade derives all authority terms.</remarks>
internal sealed class HumanInputConversationCommandAdapter
{
    private const int MaximumJsonEscapedCharacterLength = 6;
    private const int MaximumPayloadCharacters = HumanInputLimits.MaxStructuredFields
        * ((HumanInputLimits.MaxIdentifierCharacters + HumanInputLimits.MaxResponseTextCharacters) * MaximumJsonEscapedCharacterLength + 32)
        + HumanInputLimits.MaxExplanationCharacters * MaximumJsonEscapedCharacterLength
        + 4_096;
    private const string CommandName = "/human-input";
    private readonly HumanInputConversationOperationCache _operations = new();
    private readonly HumanInputRuntimeFacade _humanInput;

    internal HumanInputConversationCommandAdapter(HumanInputRuntimeFacade humanInput)
    {
        _humanInput = humanInput ?? throw new ArgumentNullException(nameof(humanInput));
    }

    internal static bool MatchesCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        return trimmed.StartsWith(CommandName, StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == CommandName.Length || char.IsWhiteSpace(trimmed[CommandName.Length]));
    }

    internal async Task<AgentRuntimeTurnResult?> TryHandleAsync(string? input, CancellationToken cancellationToken = default)
    {
        if (!TryGetRemainder(input, out var remainder))
        {
            return null;
        }

        var command = ReadToken(ref remainder);
        return command?.ToLowerInvariant() switch
        {
            null or "" or "help" => AgentRuntimeTurnResult.CommandOutput(HelpText),
            "list" => await ListAsync(remainder, cancellationToken).ConfigureAwait(false),
            "inspect" => await InspectAsync(remainder, cancellationToken).ConfigureAwait(false),
            "submit" => await SubmitAsync(remainder, cancellationToken).ConfigureAwait(false),
            "withdraw" => await TargetResponseAsync(remainder, HumanInputResponseOperationKind.Withdraw, cancellationToken).ConfigureAwait(false),
            "select" => await TargetResponseAsync(remainder, HumanInputResponseOperationKind.Select, cancellationToken).ConfigureAwait(false),
            _ => AgentRuntimeTurnResult.CommandOutput(HelpText)
        };
    }

    private async Task<AgentRuntimeTurnResult> ListAsync(string remainder, CancellationToken cancellationToken)
    {
        var cursor = ReadToken(ref remainder);
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            return AgentRuntimeTurnResult.CommandOutput("Usage: /human-input list [opaque-cursor]");
        }

        var page = await _humanInput.ListAsync(new HumanInputRequestPosturePageRequest(50, cursor), cancellationToken).ConfigureAwait(false);
        if (page.Status != HumanInputRequestPosturePageStatus.Ready)
        {
            return AgentRuntimeTurnResult.CommandOutput($"Human Input catalog is {Format(page.Status)}.");
        }

        if (page.Requests.Count == 0)
        {
            return AgentRuntimeTurnResult.CommandOutput("No Human Input requests were found.");
        }

        var lines = page.Requests.Select(FormatSummary).ToList();
        if (page.NextCursor is not null)
        {
            lines.Add($"Next cursor: `{page.NextCursor}`");
        }

        return AgentRuntimeTurnResult.CommandOutput("Human Input requests:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }

    private async Task<AgentRuntimeTurnResult> InspectAsync(string remainder, CancellationToken cancellationToken)
    {
        var requestId = ReadToken(ref remainder);
        if (requestId is null || !HumanInputIdentifier.IsValid(requestId) || !string.IsNullOrWhiteSpace(remainder))
        {
            return AgentRuntimeTurnResult.CommandOutput("Usage: /human-input inspect <request-id>");
        }

        var read = await _humanInput.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
        return read.Status == HumanInputRequestPostureReadStatus.Ready && read.Request is not null
            ? AgentRuntimeTurnResult.CommandOutput(FormatInspection(read.Request))
            : AgentRuntimeTurnResult.CommandOutput($"Human Input request `{requestId}` is {Format(read.Status)}.");
    }

    private async Task<AgentRuntimeTurnResult> SubmitAsync(string remainder, CancellationToken cancellationToken)
    {
        if (!TryReadResponseTerms(ref remainder, out var requestId, out var operationId, out var responseId))
        {
            return AgentRuntimeTurnResult.CommandOutput("Usage: /human-input submit <request-id> <operation-id> <response-id> <response-json>");
        }

        if (!TryParsePayload(remainder, out var value, out var explanation))
        {
            return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` is invalid. Response data was not recorded.");
        }

        if (!_operations.TryGet(
                operationId,
                HumanInputResponseOperationKind.Submit,
                requestId,
                responseId,
                value,
                explanation,
                out var input))
        {
            return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` conflicts with previously retained response intent.");
        }

        if (input is null)
        {
            var read = await _humanInput.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (read.Status != HumanInputRequestPostureReadStatus.Ready || read.Request is null)
            {
                return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` could not read request `{requestId}`: {Format(read.Status)}.");
            }

            var candidate = new HumanInputResponseOperationInput(
                operationId,
                HumanInputResponseOperationKind.Submit,
                requestId,
                read.Request.LifecycleVersion,
                read.Request.Status,
                read.Request.CurrentRequest,
                responseId,
                value,
                explanation);
            if (!_operations.TryAdd(candidate, out input) || input is null)
            {
                return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` could not retain exact response intent.");
            }
        }

        var result = await _humanInput.SubmitResponseAsync(input, cancellationToken).ConfigureAwait(false);
        return AgentRuntimeTurnResult.CommandOutput(FormatOperation(result));
    }

    private async Task<AgentRuntimeTurnResult> TargetResponseAsync(
        string remainder,
        HumanInputResponseOperationKind kind,
        CancellationToken cancellationToken)
    {
        if (!TryReadResponseTerms(ref remainder, out var requestId, out var operationId, out var responseId) || !string.IsNullOrWhiteSpace(remainder))
        {
            return AgentRuntimeTurnResult.CommandOutput($"Usage: /human-input {kind.ToString().ToLowerInvariant()} <request-id> <operation-id> <response-id>");
        }

        if (!_operations.TryGet(operationId, kind, requestId, responseId, null, null, out var input))
        {
            return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` conflicts with previously retained response intent.");
        }

        if (input is null)
        {
            var read = await _humanInput.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (read.Status != HumanInputRequestPostureReadStatus.Ready || read.Request is null)
            {
                return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` could not read request `{requestId}`: {Format(read.Status)}.");
            }

            var candidate = new HumanInputResponseOperationInput(
                operationId,
                kind,
                requestId,
                read.Request.LifecycleVersion,
                read.Request.Status,
                read.Request.CurrentRequest,
                responseId,
                null,
                null);
            if (!_operations.TryAdd(candidate, out input) || input is null)
            {
                return AgentRuntimeTurnResult.CommandOutput($"Human Input operation `{operationId}` could not retain exact response intent.");
            }
        }

        var result = await _humanInput.SubmitResponseAsync(input, cancellationToken).ConfigureAwait(false);
        return AgentRuntimeTurnResult.CommandOutput(FormatOperation(result));
    }

    private static bool TryReadResponseTerms(ref string remainder, out string requestId, out string operationId, out string responseId)
    {
        requestId = ReadToken(ref remainder) ?? string.Empty;
        operationId = ReadToken(ref remainder) ?? string.Empty;
        responseId = ReadToken(ref remainder) ?? string.Empty;
        return HumanInputIdentifier.IsValid(requestId)
            && HumanInputIdentifier.IsValid(operationId)
            && HumanInputIdentifier.IsValid(responseId);
    }

    private static bool TryGetRemainder(string? input, out string remainder)
    {
        remainder = string.Empty;
        if (!MatchesCommand(input))
        {
            return false;
        }

        var trimmed = input!.Trim();
        remainder = trimmed[CommandName.Length..].Trim();
        return true;
    }

    private static string? ReadToken(ref string remainder)
    {
        remainder = remainder.TrimStart();
        if (remainder.Length == 0)
        {
            return null;
        }

        var index = remainder.IndexOfAny([' ', '\t', '\r', '\n']);
        if (index < 0)
        {
            var token = remainder;
            remainder = string.Empty;
            return token;
        }

        var result = remainder[..index];
        remainder = remainder[index..].TrimStart();
        return result;
    }

    private static bool TryParsePayload(string payload, out HumanInputResponseValue? value, out string? explanation)
    {
        value = null;
        explanation = null;
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaximumPayloadCharacters)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !ContainsOnly(root, "value", "explanation")
                || !TryGetProperty(root, "value", out var valueElement)
                || !TryParseValue(valueElement, out value))
            {
                return false;
            }

            if (TryGetProperty(root, "explanation", out var explanationElement))
            {
                if (explanationElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                explanation = explanationElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            value = null;
            explanation = null;
            return false;
        }
    }

    private static bool TryParseValue(JsonElement element, out HumanInputResponseValue? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetProperty(element, "kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return kindElement.GetString()?.ToLowerInvariant() switch
        {
            "text" => TryParseText(element, out value),
            "choice" => TryParseChoice(element, out value),
            "confirmation" => TryParseConfirmation(element, out value),
            "structured" => TryParseStructured(element, out value),
            "reference" => TryParseReference(element, out value),
            _ => false
        };
    }

    private static bool TryParseText(JsonElement element, out HumanInputResponseValue? value)
    {
        value = null;
        if (!ContainsOnly(element, "kind", "text") || !TryGetString(element, "text", out var text))
        {
            return false;
        }

        value = new HumanInputResponseValue(HumanInputResponseKind.Text, text, null, null, null, null);
        return true;
    }

    private static bool TryParseChoice(JsonElement element, out HumanInputResponseValue? value)
    {
        value = null;
        if (!ContainsOnly(element, "kind", "choiceId") || !TryGetString(element, "choiceId", out var choiceId))
        {
            return false;
        }

        value = new HumanInputResponseValue(HumanInputResponseKind.Choice, null, choiceId, null, null, null);
        return true;
    }

    private static bool TryParseConfirmation(JsonElement element, out HumanInputResponseValue? value)
    {
        value = null;
        if (!ContainsOnly(element, "kind", "confirmation")
            || !TryGetProperty(element, "confirmation", out var confirmation)
            || confirmation.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = new HumanInputResponseValue(HumanInputResponseKind.Confirmation, null, null, confirmation.GetBoolean(), null, null);
        return true;
    }

    private static bool TryParseStructured(JsonElement element, out HumanInputResponseValue? value)
    {
        value = null;
        if (!ContainsOnly(element, "kind", "fields")
            || !TryGetProperty(element, "fields", out var fieldsElement)
            || fieldsElement.ValueKind != JsonValueKind.Array
            || fieldsElement.GetArrayLength() > HumanInputLimits.MaxStructuredFields)
        {
            return false;
        }

        var fields = ImmutableArray.CreateBuilder<HumanInputStructuredFieldValue>();
        foreach (var field in fieldsElement.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object
                || !ContainsOnly(field, "fieldId", "text", "choiceId")
                || !TryGetString(field, "fieldId", out var fieldId)
                || !TryGetOptionalString(field, "text", out var text)
                || !TryGetOptionalString(field, "choiceId", out var choiceId))
            {
                return false;
            }

            fields.Add(new HumanInputStructuredFieldValue(fieldId, text, choiceId));
        }

        value = new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, fields.ToImmutable(), null);
        return true;
    }

    private static bool TryParseReference(JsonElement element, out HumanInputResponseValue? value)
    {
        value = null;
        if (!ContainsOnly(element, "kind", "reference")
            || !TryGetProperty(element, "reference", out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.Object
            || !ContainsOnly(referenceElement, "kind", "value")
            || !TryGetProperty(referenceElement, "kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || !TryGetString(referenceElement, "value", out var referenceValue))
        {
            return false;
        }

        var kind = kindElement.GetString()?.ToLowerInvariant() switch
        {
            "artifact" => HumanInputReferenceKind.Artifact,
            "reference" => HumanInputReferenceKind.Reference,
            _ => HumanInputReferenceKind.Unknown
        };
        if (kind == HumanInputReferenceKind.Unknown)
        {
            return false;
        }

        value = new HumanInputResponseValue(HumanInputResponseKind.Reference, null, null, null, null, new HumanInputReference(kind, referenceValue));
        return true;
    }

    private static bool ContainsOnly(JsonElement element, params string[] names)
    {
        var encountered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!encountered.Add(property.Name) || !names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!TryGetProperty(element, name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static string FormatOperation(HumanInputOperationResult result)
    {
        var output = $"Human Input operation `{result.OperationId}` is {Format(result.Status)}.";
        return result.Request is null ? output : output + Environment.NewLine + FormatSummary(result.Request);
    }

    private static string FormatInspection(HumanInputRequestPosture request)
    {
        var lines = new List<string>
        {
            $"Human Input request `{request.RequestId}`",
            $"Status: {request.Status} (lifecycle version {request.LifecycleVersion})",
            $"Request version: {request.Presentation.RequestVersionId}",
            $"Response schema: {request.Presentation.ResponseSchema.Kind}",
            $"Privacy: {request.Presentation.PrivacyClass}",
            $"Response policy: {request.Presentation.ResponsePolicyKind}",
            $"Response window: {request.Presentation.Timing.RequestedAtUtc:O} through {request.Presentation.Timing.ExpiresAtUtc:O}",
            $"Prompt: {request.Presentation.Prompt}",
            $"Responses: {request.ActiveResponseCount} active, {request.WithdrawnResponseCount} withdrawn, {request.AcceptedResponseCount} accepted"
        };
        if (request.SupersedesRequestId is not null)
        {
            lines.Add($"Supersedes: {request.SupersedesRequestId}");
        }

        if (request.SupersededByRequestId is not null)
        {
            lines.Add($"Superseded by: {request.SupersededByRequestId}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSummary(HumanInputRequestPosture request)
        => $"- `{request.RequestId}`: {request.Status}; version {request.LifecycleVersion}; {request.Presentation.ResponseSchema.Kind}; expires {request.Presentation.Timing.ExpiresAtUtc:O}";

    private static string Format<TStatus>(TStatus status) where TStatus : struct, Enum
        => status.ToString().ToLowerInvariant();

    private const string HelpText = """
        Human Input commands:
        /human-input list [opaque-cursor]
        /human-input inspect <request-id>
        /human-input submit <request-id> <operation-id> <response-id> <response-json>
        /human-input withdraw <request-id> <operation-id> <response-id>
        /human-input select <request-id> <operation-id> <response-id>

        Response JSON is a private, untrusted payload and is not added to the conversation transcript. The runtime retains exact submitted intent only for this interactive session, so supply the same operation id and exact payload to retry an outcome-unknown operation. Example shape: {"value":{"kind":"text","text":"..."},"explanation":"optional"}.
        """;
}
