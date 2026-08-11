using EmbodySense.Core.Common.Loops.Custom.Execution;
using System.Security.Cryptography;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Models;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Loops.Execution;
using EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Maps custom-loop run records to and from the canonical compact version-1 JSON envelope.
/// </summary>
/// <remarks>
/// The envelope de-duplicates content, context blocks, authorities, and tool requests into hash-verified registries. Decoding
/// rejects duplicate or unknown properties, invalid UTF-8, unsupported schema versions, noncanonical reference ordering,
/// unreferenced registry entries, hash mismatches, and semantically invalid reconstructed runs.
/// </remarks>
internal static class CustomLoopRunArtifactCodec
{
    /// <summary>
    /// Identifies the canonical custom-loop run envelope kind.
    /// </summary>
    internal const string ArtifactKind = "custom-loop-run";
    /// <summary>
    /// Identifies the only supported envelope schema version.
    /// </summary>
    internal const int CurrentArtifactSchemaVersion = 1;
    /// <summary>
    /// Identifies the only supported projected-run schema version.
    /// </summary>
    internal const int CurrentProjectionSchemaVersion = 1;
    private const string EncodingName = "utf-8";
    private const string ContentReferenceProperty = "$content";
    private const string BlockReferenceProperty = "$contextBlock";
    private const string AuthorityReferenceProperty = "$authority";
    private const string ToolRequestReferenceProperty = "$toolRequest";
    /// <summary>
    /// Provides the no-BOM UTF-8 encoding that rejects invalid byte sequences.
    /// </summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = CustomLoopJsonDepthPolicy.CanonicalRunArtifactMaximumDepth,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new GovernedLoopRevisionReferenceJsonConverter(),
            new GovernedLoopExecutionBindingJsonConverter(),
            new GovernedLoopFrontierPostureJsonConverter(),
            new AuthorityActorIdJsonConverter(),
            new AuthorityGrantIdJsonConverter(),
            new AuthorityGrantRevisionJsonConverter(),
            new AuthorityProfileIdJsonConverter(),
            new AuthorityProfileRevisionJsonConverter(),
            new AuthorityProfileHashJsonConverter(),
            new CapabilityDataClassJsonConverter(),
        }
    };

    /// <summary>
    /// Encodes one validated run into a canonical compact envelope.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The canonical UTF-8 JSON bytes.</returns>
    internal static byte[] Encode(CustomLoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var contents = new ContentRegistry([]);
        var blocks = new StructuralRegistry("b", "context-block", []);
        var authorities = new StructuralRegistry("a", "authority", []);
        var requests = new StructuralRegistry("q", "tool-request", []);
        var projection = Project(run, contents, blocks, authorities, requests);
        var encoded = SerializeEnvelope(contents, blocks.Entries, authorities.Entries, requests.Entries, projection);
        contents.RequireEverySeedReferenced();
        blocks.RequireEverySeedReferenced();
        authorities.RequireEverySeedReferenced();
        requests.RequireEverySeedReferenced();
        return encoded;
    }

    /// <summary>
    /// Decodes a canonical envelope and validates both JSON depth and reconstructed run semantics.
    /// </summary>
    /// <param name="utf8Json">The utf8 JSON.</param>
    /// <param name="path">The path.</param>
    /// <returns>The reconstructed custom-loop run.</returns>
    internal static CustomLoopRunRecord Decode(byte[] utf8Json, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        return Parse(utf8Json, requireCanonical: true, validateDepth: true, path: path).Run;
    }

    /// <summary>
    /// Decodes a canonical envelope whose JSON depth has already been validated by the caller.
    /// </summary>
    /// <param name="utf8Json">The utf8 JSON.</param>
    /// <param name="path">The path.</param>
    /// <returns>The reconstructed custom-loop run.</returns>
    internal static CustomLoopRunRecord DecodeDepthValidated(ReadOnlyMemory<byte> utf8Json, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(utf8Json, requireCanonical: true, validateDepth: false, path: path).Run;
    }

    /// <summary>
    /// Determines whether a JSON root advertises the canonical custom-loop run artifact kind.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <returns><see langword="true"/> when is envelope; otherwise, <see langword="false"/>.</returns>
    internal static bool IsEnvelope(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("artifactKind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && string.Equals(kind.GetString(), ArtifactKind, StringComparison.Ordinal);
    }

    private static ParsedEnvelope Parse(ReadOnlyMemory<byte> utf8Json, bool requireCanonical, bool validateDepth = true, string? path = null)
    {
        if (validateDepth)
        {
            CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(utf8Json.Span, _jsonOptions.MaxDepth, "Custom-loop run artifact", path);
        }

        JsonObject root;
        try
        {
            // JsonNode can collapse duplicate properties. Reject them in one streaming pass first so no
            // alternate persisted spelling can hydrate to an apparently unambiguous run.
            RejectDuplicateProperties(utf8Json.Span);
            root = JsonNode.Parse(utf8Json.Span, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth }) as JsonObject
                ?? throw new FormatException("The custom-loop live-run envelope was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("The custom-loop live-run envelope contains invalid JSON or UTF-8.", exception);
        }

        RequireProperties(root, "artifactKind", "artifactSchemaVersion", "projectionSchemaVersion", "encoding", "content", "contextBlocks", "authorities", "toolRequests", "run");
        if (!string.Equals(RequireString(root, "artifactKind"), ArtifactKind, StringComparison.Ordinal)
            || RequireInt32(root, "artifactSchemaVersion") != CurrentArtifactSchemaVersion
            || RequireInt32(root, "projectionSchemaVersion") != CurrentProjectionSchemaVersion
            || !string.Equals(RequireString(root, "encoding"), EncodingName, StringComparison.Ordinal))
        {
            throw new FormatException("The custom-loop live-run envelope kind, schema version, projection version, or encoding is unsupported.");
        }

        var compactProjection = RequireObject(root, "run");
        if (RequireInt32(compactProjection, "schemaVersion") != CustomLoopRunRecord.CurrentSchemaVersion)
        {
            throw new FormatException($"The hydrated custom-loop run violates its semantic limits. schemaVersion: Run schema version must be {CustomLoopRunRecord.CurrentSchemaVersion}. Pre-1.0 artifacts from another schema are unsupported; remove and recreate the affected development artifact.");
        }

        if (requireCanonical)
        {
            ValidateProjectionPropertyOrder(compactProjection, typeof(CustomLoopRunRecord));
            using var canonical = new CanonicalJsonByteComparer(utf8Json[..^1]);
            using (var writer = new Utf8JsonWriter(canonical))
            {
                root.WriteTo(writer, _jsonOptions);
            }

            if (utf8Json.Span[^1] != (byte)'\n' || !canonical.IsEqual)
            {
                throw new FormatException($"The custom-loop live-run envelope is not the one canonical encoding (first differing byte {canonical.FirstDifference}, canonical length {canonical.Length + 1}, persisted length {utf8Json.Length}).");
            }

        }

        var contentEntries = ParseContentEntries(RequireArray(root, "content"));
        var contents = new ContentRegistry(contentEntries);
        var blockEntries = ParseStructuralEntries(RequireArray(root, "contextBlocks"), "b", "contextBlock", "context-block");
        var authorityEntries = ParseStructuralEntries(RequireArray(root, "authorities"), "a", "authority", "authority");
        var requestEntries = ParseStructuralEntries(RequireArray(root, "toolRequests"), "q", "toolRequest", "tool-request");
        ValidateStructuralPayloadPropertyOrder(blockEntries, typeof(CustomLoopContextBlock));
        ValidateStructuralPayloadPropertyOrder(authorityEntries, typeof(CustomLoopToolAuthoritySnapshot));
        ResolveStructuralContent(blockEntries, contents);
        ResolveStructuralContent(authorityEntries, contents);
        ResolveStructuralContent(requestEntries, contents);
        var blocks = new StructuralRegistry("b", "context-block", blockEntries);
        var authorities = new StructuralRegistry("a", "authority", authorityEntries);
        var requests = new StructuralRegistry("q", "tool-request", requestEntries);
        ValidateToolRequestTable(requestEntries, authorities);
        if (requireCanonical)
        {
            ValidateCanonicalStructuralReferenceOrder(compactProjection, blockEntries.Count, authorityEntries.Count, requestEntries.Count);
        }

        var hydratedProjection = compactProjection;
        ExpandContextBlocks(hydratedProjection, blocks);
        ResolveContentReferences(hydratedProjection, contents);
        ExpandToolEvidence(hydratedProjection, authorities, requests);
        contents.RequireEverySeedReferenced();
        blocks.RequireEverySeedReferenced();
        authorities.RequireEverySeedReferenced();
        requests.RequireEverySeedReferenced();

        CustomLoopRunRecord run;
        try
        {
            run = hydratedProjection.Deserialize<CustomLoopRunRecord>(_jsonOptions)
                ?? throw new FormatException("The hydrated custom-loop run was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException("The hydrated custom-loop run contains unknown, missing, or malformed fields.", exception);
        }

        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            var detail = string.Join(" ", validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new FormatException($"The hydrated custom-loop run violates its semantic limits. {detail}");
        }

        if (requireCanonical)
        {
            ValidateCanonicalContentReferenceOrder(run, contentEntries);
            ValidateCanonicalStringSpellings(utf8Json.Span);
        }

        return new ParsedEnvelope(run, contentEntries, blockEntries, authorityEntries, requestEntries);
    }

    internal static bool IsEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("artifactKind"))
            {
                return reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ArtifactKind);
            }
        }

        return false;
    }

    private static void ValidateCanonicalContentReferenceOrder(CustomLoopRunRecord run, IReadOnlyList<ContentEntry> contentEntries)
    {
        var contentIds = contentEntries.ToDictionary(entry => entry.Text, entry => entry.Id, StringComparer.Ordinal);
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var nextContent = 0;
        void Content(string? text)
        {
            if (text is null || !contentIds.TryGetValue(text, out var id))
            {
                return;
            }

            if (seenContent.Add(id) && !string.Equals(id, IndexedId("c", nextContent++), StringComparison.Ordinal))
            {
                throw new FormatException("The content table is not in canonical first-use order.");
            }
        }

        Content(run.AdmittedDefinition.DisplayName);
        Content(run.AdmittedDefinition.Description);
        Content(run.AdmittedDefinition.TriggerPolicy.PresetPrompt);
        foreach (var step in run.AdmittedDefinition.InferenceSteps)
        {
            Content(step.Name);
            Content(step.Instruction);
        }

        Content(run.AdmittedDefinition.ExitPolicy.DecisionInstruction);
        Content(run.TriggerPrompt);
        foreach (var source in run.ContextSnapshot.SourceManifest)
        {
            Content(source.SourceId);
            Content(source.SourcePath);
            Content(source.Content);
            Content(source.TruncationReason);
            Content(source.OmissionReason);
        }

        foreach (var output in run.Checkpoint.EarlierRetainedOutputs)
        {
            Content(output.Content);
        }

        Content(run.Checkpoint.PreviousIterationResult?.Content);
        Content(run.Checkpoint.CurrentIterationResult?.Content);
        var knownRequests = new HashSet<(int RequestOrdinal, string RequestCorrelationId)>();
        foreach (var runEvent in run.Events)
        {
            Content(runEvent.Detail);
            Content(runEvent.CanonicalOutput);
            foreach (var block in runEvent.ContextBlocks)
            {
                Content(block.SourceId);
                Content(block.OmissionReason);
                Content(block.Content);
                Content(block.SourceVersion);
            }

            Content(runEvent.ToolAuthority?.Detail);
            if (runEvent.ToolEvidence is not { } evidence)
            {
                continue;
            }

            Content(evidence.Authority.Detail);
            var requestKey = ToolRequestKey(evidence);
            var ownsRequest = evidence.Phase == CustomLoopToolEvidencePhase.RequestReserved || evidence.Phase == CustomLoopToolEvidencePhase.IntegrityFailed && !knownRequests.Contains(requestKey);
            if (ownsRequest)
            {
                Content(evidence.TargetPath);
                Content(evidence.Content);
                Content(evidence.Pattern);
                Content(evidence.ResolvedTarget);
                knownRequests.Add(requestKey);
            }

            if (evidence.Phase == CustomLoopToolEvidencePhase.GovernanceDecided && evidence.Governance is { } governance)
            {
                Content(governance.AuthorityDetail);
                Content(governance.PermissionMatchedPath);
                Content(governance.PermissionDetail);
                Content(governance.ApprovalDecisionBy);
                Content(governance.ApprovalDetail);
            }

            if (evidence.Phase == CustomLoopToolEvidencePhase.OutcomeObserved && !evidence.ReturnedToModel)
            {
                Content(evidence.CanonicalResultReturnedToModel);
            }
        }

        Content(run.FinalOutput);
        Content(run.FailureDetail);
        if (run.SequentialInvocationSnapshot is { } sequentialInvocation)
        {
            Content(sequentialInvocation.TriggerPrompt);
            foreach (var source in sequentialInvocation.ContextManifest)
            {
                Content(source.SourceId);
                Content(source.SourcePath);
                Content(source.Content);
                Content(source.TruncationReason);
                Content(source.OmissionReason);
            }
        }

        if (nextContent != contentEntries.Count)
        {
            throw new FormatException("The content table contains an unreferenced or noncanonical entry.");
        }
    }

    private static void ValidateCanonicalStructuralReferenceOrder(JsonObject compactProjection, int blockEntryCount, int authorityEntryCount, int requestEntryCount)
    {
        var compactEvents = RequireArray(compactProjection, "events");
        ValidateFirstUseReferences(compactEvents.SelectMany(item => RequireArray(item!.AsObject(), "contextBlocks").Select(reference => RequireString(reference!.AsObject(), BlockReferenceProperty))), "b", blockEntryCount, "context-block");
        ValidateFirstUseReferences(compactEvents.Select(item => item!.AsObject()["toolAuthority"]).OfType<JsonObject>().Select(reference => RequireString(reference, AuthorityReferenceProperty)), "a", authorityEntryCount, "authority");
        ValidateFirstUseReferences(compactEvents.Select(item => item!.AsObject()["toolEvidence"]).OfType<JsonObject>().Select(evidence => RequireReference(evidence, "toolRequest", ToolRequestReferenceProperty)), "q", requestEntryCount, "tool-request");
        foreach (var evidence in compactEvents.Select(item => item!.AsObject()["toolEvidence"]).OfType<JsonObject>().Where(evidence => RequireInt32(evidence, "shape") == 2))
        {
            ValidateProjectionPropertyOrder(RequireObject(evidence, "governance"), typeof(ToolGovernanceEvidence));
        }
    }

    private static void ValidateStructuralPayloadPropertyOrder(IEnumerable<StructuralEntry> entries, Type projectedType)
    {
        foreach (var entry in entries)
        {
            ValidateProjectionPropertyOrder(entry.Value, projectedType);
        }
    }

    private static void ValidateFirstUseReferences(IEnumerable<string> references, string prefix, int entryCount, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var reference in references)
        {
            if (seen.Add(reference) && !string.Equals(reference, IndexedId(prefix, next++), StringComparison.Ordinal))
            {
                throw new FormatException($"The {description} table is not in canonical first-use order.");
            }
        }

        if (next != entryCount)
        {
            throw new FormatException($"The canonical {description} table contains an unreferenced or noncanonical entry.");
        }
    }

    private static void ValidateProjectionPropertyOrder(JsonNode? node, Type projectedType)
    {
        projectedType = Nullable.GetUnderlyingType(projectedType) ?? projectedType;
        if (node is JsonValue value)
        {
            ValidateCanonicalPrimitiveValue(value, projectedType);
            return;
        }

        if (node is JsonArray array)
        {
            var elementType = projectedType.IsArray ? projectedType.GetElementType() : projectedType.IsGenericType ? projectedType.GetGenericArguments().FirstOrDefault() : null;
            if (elementType is not null)
            {
                foreach (var item in array)
                {
                    ValidateProjectionPropertyOrder(item, elementType);
                }
            }

            return;
        }

        if (node is not JsonObject owner
            || owner.ContainsKey(ContentReferenceProperty)
            || owner.ContainsKey(BlockReferenceProperty)
            || owner.ContainsKey(AuthorityReferenceProperty)
            || owner.ContainsKey(ToolRequestReferenceProperty)
            || projectedType == typeof(CustomLoopToolTraceEvidence) && owner.ContainsKey("shape"))
        {
            return;
        }

        if (projectedType == typeof(GovernedLoopExecutionBinding))
        {
            ValidateExactPropertyOrder(owner, "schemaVersion", "runId", "revision", "executionGeneration");
            ValidateProjectionPropertyOrder(owner["revision"], typeof(GovernedLoopRevisionReference));
            return;
        }

        if (projectedType == typeof(GovernedLoopRevisionReference))
        {
            ValidateExactPropertyOrder(owner, "schemaVersion", "graphId", "revisionId", "executableHash");
            return;
        }

        var typeInfo = _jsonOptions.GetTypeInfo(projectedType);
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        using var actual = owner.GetEnumerator();
        var hasActual = actual.MoveNext();
        foreach (var property in typeInfo.Properties)
        {
            if (hasActual && string.Equals(actual.Current.Key, property.Name, StringComparison.Ordinal))
            {
                if (IsOmittedByCanonicalSerializer(property, actual.Current.Value))
                {
                    throw new FormatException($"The projected `{projectedType.Name}` field `{property.Name}` is omitted by the canonical serializer.");
                }

                ValidateProjectionPropertyOrder(actual.Current.Value, property.PropertyType);
                hasActual = actual.MoveNext();
                continue;
            }

            if (property.ShouldSerialize is null)
            {
                throw new FormatException($"The projected `{projectedType.Name}` fields are not in canonical serializer order.");
            }
        }

        if (hasActual)
        {
            throw new FormatException($"The projected `{projectedType.Name}` fields are not in canonical serializer order.");
        }
    }

    private static void ValidateExactPropertyOrder(JsonObject owner, params string[] expectedNames)
    {
        var actualNames = owner.Select(property => property.Key).ToArray();
        if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            throw new FormatException($"The projected fields are not in canonical serializer order for `{string.Join(".`, `", expectedNames)}`.");
        }
    }

    private static bool IsOmittedByCanonicalSerializer(JsonPropertyInfo property, JsonNode? value)
    {
        var ignore = property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).OfType<JsonIgnoreAttribute>().SingleOrDefault();
        return ignore?.Condition switch
        {
            JsonIgnoreCondition.Always => true,
            JsonIgnoreCondition.WhenWritingNull => value is null,
            JsonIgnoreCondition.WhenWritingDefault => IsDefaultValue(value, property.PropertyType),
            _ => false
        };
    }

    private static bool IsDefaultValue(JsonNode? node, Type valueType)
    {
        if (node is null)
        {
            return true;
        }

        if (!valueType.IsValueType || Nullable.GetUnderlyingType(valueType) is not null)
        {
            return false;
        }

        try
        {
            return Equals(node.Deserialize(valueType, _jsonOptions), Activator.CreateInstance(valueType));
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The projected `{valueType.Name}` value is malformed.", exception);
        }
    }

    private static void ValidateCanonicalStringSpellings(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth });
        var encoder = _jsonOptions.Encoder ?? JavaScriptEncoder.Default;
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.String))
            {
                continue;
            }

            var rawValue = reader.ValueSpan;
            if (IsTriviallyCanonicalStringValue(rawValue, encoder))
            {
                continue;
            }

            var decoded = reader.GetString() ?? throw new FormatException("A JSON property name or string token was unexpectedly null.");
            var canonical = JsonEncodedText.Encode(decoded, encoder).EncodedUtf8Bytes;
            if (!rawValue.SequenceEqual(canonical))
            {
                throw new FormatException("The custom-loop live-run envelope contains a string that does not use its canonical serializer spelling.");
            }
        }
    }

    private static bool IsTriviallyCanonicalStringValue(ReadOnlySpan<byte> rawValue, JavaScriptEncoder encoder)
    {
        foreach (var value in rawValue)
        {
            if (value >= 0x80 || value == (byte)'\\' || encoder.WillEncode((char)value))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateCanonicalPrimitiveValue(JsonValue value, Type projectedType)
    {
        if (projectedType == typeof(string) || projectedType == typeof(JsonNode) || projectedType == typeof(JsonElement))
        {
            return;
        }

        try
        {
            var typed = value.Deserialize(projectedType, _jsonOptions);
            var canonical = JsonSerializer.SerializeToNode(typed, projectedType, _jsonOptions) ?? throw new FormatException($"The projected `{projectedType.Name}` value was empty.");
            if (!SerializeNode(value).AsSpan().SequenceEqual(SerializeNode(canonical)))
            {
                throw new FormatException($"The projected `{projectedType.Name}` value does not use its canonical serializer spelling.");
            }
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The projected `{projectedType.Name}` value is malformed.", exception);
        }
    }

    private static byte[] Terminate(byte[] content)
    {
        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    private static JsonObject Project(CustomLoopRunRecord run, ContentRegistry contents, StructuralRegistry blocks, StructuralRegistry authorities, StructuralRegistry requests)
    {
        JsonObject projection;
        try
        {
            projection = JsonSerializer.SerializeToNode(PrepareRunForProjection(run, contents), _jsonOptions)?.AsObject()
                ?? throw new InvalidOperationException("The custom-loop run could not be projected.");
        }
        catch (JsonException exception)
        {
            throw CustomLoopJsonDepthPolicy.SerializationDepthException("Custom-loop run artifact", _jsonOptions.MaxDepth, exception);
        }

        ProjectPreparedDefinition(RequireObject(projection, "admittedDefinition"));
        ReferenceIdentifierProperty(projection, "triggerPrompt");
        ProjectPreparedContextSnapshot(RequireObject(projection, "contextSnapshot"));
        ProjectPreparedCheckpoint(RequireObject(projection, "checkpoint"));
        CompactToolEvidence(projection, run.Events, contents, blocks, authorities, requests);
        ReferenceIdentifierProperty(projection, "finalOutput");
        ReferenceIdentifierProperty(projection, "failureDetail");
        if (projection["sequentialInvocationSnapshot"] is JsonObject sequentialInvocation)
        {
            ProjectPreparedSequentialInvocation(sequentialInvocation);
        }

        return projection;
    }

    private static CustomLoopRunRecord PrepareRunForProjection(CustomLoopRunRecord run, ContentRegistry contents)
    {
        var definition = run.AdmittedDefinition with
        {
            DisplayName = contents.Reference(run.AdmittedDefinition.DisplayName),
            Description = contents.Reference(run.AdmittedDefinition.Description),
            TriggerPolicy = run.AdmittedDefinition.TriggerPolicy with { PresetPrompt = contents.Reference(run.AdmittedDefinition.TriggerPolicy.PresetPrompt) },
            InferenceSteps = run.AdmittedDefinition.InferenceSteps.Select(step => step with { Name = contents.Reference(step.Name), Instruction = contents.Reference(step.Instruction) }).ToArray(),
            ExitPolicy = run.AdmittedDefinition.ExitPolicy with { DecisionInstruction = contents.Reference(run.AdmittedDefinition.ExitPolicy.DecisionInstruction) }
        };
        var triggerPromptId = contents.Reference(run.TriggerPrompt);
        var contextSnapshot = run.ContextSnapshot with
        {
            SourceManifest = run.ContextSnapshot.SourceManifest.Select(source => source with
            {
                SourceId = contents.Reference(source.SourceId),
                SourcePath = contents.Reference(source.SourcePath),
                Content = contents.Reference(source.Content),
                TruncationReason = ReferenceIdentifier(source.TruncationReason, contents),
                OmissionReason = ReferenceIdentifier(source.OmissionReason, contents)
            }).ToArray()
        };
        var checkpoint = run.Checkpoint with
        {
            EarlierRetainedOutputs = run.Checkpoint.EarlierRetainedOutputs.Select(output => output with { Content = contents.Reference(output.Content) }).ToArray(),
            PreviousIterationResult = PrepareRetainedOutput(run.Checkpoint.PreviousIterationResult, contents),
            CurrentIterationResult = PrepareRetainedOutput(run.Checkpoint.CurrentIterationResult, contents)
        };
        var knownRequests = new HashSet<(int RequestOrdinal, string RequestCorrelationId)>();
        var events = run.Events.Select(item => PrepareEventForProjection(item, contents, knownRequests)).ToArray();
        var finalOutput = ReferenceIdentifier(run.FinalOutput, contents);
        var failureDetail = ReferenceIdentifier(run.FailureDetail, contents);
        var sequentialInvocation = PrepareSequentialInvocationForProjection(run.SequentialInvocationSnapshot, contents);
        return run with
        {
            AdmittedDefinition = definition,
            TriggerPrompt = triggerPromptId,
            ContextSnapshot = contextSnapshot,
            Checkpoint = checkpoint,
            Events = events,
            FinalOutput = finalOutput,
            FailureDetail = failureDetail,
            SequentialInvocationSnapshot = sequentialInvocation,
        };
    }

    private static GovernedLoopSequentialInvocationSnapshot? PrepareSequentialInvocationForProjection(
        GovernedLoopSequentialInvocationSnapshot? snapshot,
        ContentRegistry contents)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new GovernedLoopSequentialInvocationSnapshot(
            snapshot.SchemaVersion,
            contents.Reference(snapshot.TriggerPrompt),
            snapshot.ModelSnapshot,
            snapshot.InvokingConversation,
            snapshot.ContextCapturedAtUtc,
            snapshot.ContextManifest.Select(source => source with
            {
                SourceId = contents.Reference(source.SourceId),
                SourcePath = contents.Reference(source.SourcePath),
                Content = contents.Reference(source.Content),
                TruncationReason = ReferenceIdentifier(source.TruncationReason, contents),
                OmissionReason = ReferenceIdentifier(source.OmissionReason, contents),
            }).ToArray(),
            snapshot.ContentHash);
    }

    private static CustomLoopRetainedOutput? PrepareRetainedOutput(CustomLoopRetainedOutput? output, ContentRegistry contents)
    {
        return output is null ? null : output with { Content = contents.Reference(output.Content) };
    }

    private static string? ReferenceIdentifier(string? content, ContentRegistry contents)
    {
        return content is null ? null : contents.Reference(content);
    }

    private static CustomLoopRunEvent PrepareEventForProjection(CustomLoopRunEvent runEvent, ContentRegistry contents, HashSet<(int RequestOrdinal, string RequestCorrelationId)> knownRequests)
    {
        var detailId = contents.Reference(runEvent.Detail);
        var canonicalOutputId = ReferenceIdentifier(runEvent.CanonicalOutput, contents);
        foreach (var block in runEvent.ContextBlocks)
        {
            _ = contents.Reference(block.SourceId);
            _ = ReferenceIdentifier(block.OmissionReason, contents);
            _ = contents.Reference(block.Content);
            _ = ReferenceIdentifier(block.SourceVersion, contents);
        }

        _ = ReferenceIdentifier(runEvent.ToolAuthority?.Detail, contents);
        if (runEvent.ToolEvidence is { } evidence)
        {
            _ = contents.Reference(evidence.Authority.Detail);
            var requestKey = ToolRequestKey(evidence);
            var ownsRequest = evidence.Phase == CustomLoopToolEvidencePhase.RequestReserved || evidence.Phase == CustomLoopToolEvidencePhase.IntegrityFailed && !knownRequests.Contains(requestKey);
            if (ownsRequest)
            {
                _ = contents.Reference(evidence.TargetPath);
                _ = ReferenceIdentifier(evidence.Content, contents);
                _ = ReferenceIdentifier(evidence.Pattern, contents);
                _ = ReferenceIdentifier(evidence.ResolvedTarget, contents);
                knownRequests.Add(requestKey);
            }

            if (evidence.Phase == CustomLoopToolEvidencePhase.GovernanceDecided && evidence.Governance is { } governance)
            {
                _ = contents.Reference(governance.AuthorityDetail);
                _ = ReferenceIdentifier(governance.PermissionMatchedPath, contents);
                _ = ReferenceIdentifier(governance.PermissionDetail, contents);
                _ = ReferenceIdentifier(governance.ApprovalDecisionBy, contents);
                _ = ReferenceIdentifier(governance.ApprovalDetail, contents);
            }

            if (evidence.Phase == CustomLoopToolEvidencePhase.OutcomeObserved && !evidence.ReturnedToModel)
            {
                _ = ReferenceIdentifier(evidence.CanonicalResultReturnedToModel, contents);
            }
        }

        return runEvent with { Detail = detailId, ContextBlocks = [], CanonicalOutput = canonicalOutputId, ToolAuthority = null, ToolEvidence = null };
    }

    private static void ProjectPreparedDefinition(JsonObject definition)
    {
        ReferenceIdentifierProperty(definition, "displayName");
        ReferenceIdentifierProperty(definition, "description");
        ReferenceIdentifierProperty(RequireObject(definition, "triggerPolicy"), "presetPrompt");
        foreach (var item in RequireArray(definition, "inferenceSteps"))
        {
            var step = item?.AsObject() ?? throw new FormatException("Inference-step projection entries must be objects.");
            ReferenceIdentifierProperty(step, "name");
            ReferenceIdentifierProperty(step, "instruction");
        }

        ReferenceIdentifierProperty(RequireObject(definition, "exitPolicy"), "decisionInstruction");
    }

    private static void ProjectPreparedContextSnapshot(JsonObject snapshot)
    {
        foreach (var item in RequireArray(snapshot, "sourceManifest"))
        {
            var source = item?.AsObject() ?? throw new FormatException("Context-manifest projection entries must be objects.");
            ReferenceIdentifierProperty(source, "sourceId");
            ReferenceIdentifierProperty(source, "sourcePath");
            ReferenceIdentifierProperty(source, "content");
            ReferenceIdentifierProperty(source, "truncationReason");
            ReferenceIdentifierProperty(source, "omissionReason");
        }
    }

    private static void ProjectPreparedSequentialInvocation(JsonObject snapshot)
    {
        ReferenceIdentifierProperty(snapshot, "triggerPrompt");
        foreach (var item in RequireArray(snapshot, "contextManifest"))
        {
            var source = item?.AsObject() ?? throw new FormatException("Sequential context-manifest projection entries must be objects.");
            ReferenceIdentifierProperty(source, "sourceId");
            ReferenceIdentifierProperty(source, "sourcePath");
            ReferenceIdentifierProperty(source, "content");
            ReferenceIdentifierProperty(source, "truncationReason");
            ReferenceIdentifierProperty(source, "omissionReason");
        }
    }

    private static void ProjectPreparedCheckpoint(JsonObject checkpoint)
    {
        foreach (var item in RequireArray(checkpoint, "earlierRetainedOutputs"))
        {
            ProjectPreparedRetainedOutput(item?.AsObject());
        }

        ProjectPreparedRetainedOutput(checkpoint["previousIterationResult"] as JsonObject);
        ProjectPreparedRetainedOutput(checkpoint["currentIterationResult"] as JsonObject);
    }

    private static void ProjectPreparedRetainedOutput(JsonObject? output)
    {
        if (output is not null)
        {
            ReferenceIdentifierProperty(output, "content");
        }
    }

    private static void ProjectAuthority(JsonObject authority, ContentRegistry contents)
    {
        ReferenceProperty(authority, "detail", contents);
    }

    private static void ProjectContextBlock(JsonObject block, ContentRegistry contents)
    {
        ReferenceProperty(block, "sourceId", contents);
        ReferenceProperty(block, "omissionReason", contents);
        ReferenceProperty(block, "content", contents);
        ReferenceProperty(block, "sourceVersion", contents);
    }

    private static void ProjectToolRequest(JsonObject request, ContentRegistry contents)
    {
        ReferenceProperty(request, "targetPath", contents);
        ReferenceProperty(request, "content", contents);
        ReferenceProperty(request, "pattern", contents);
        ReferenceProperty(request, "resolvedTarget", contents);
    }

    private static void ProjectGovernance(JsonObject governance, ContentRegistry contents)
    {
        ReferenceProperty(governance, "authorityDetail", contents);
        ReferenceProperty(governance, "permissionMatchedPath", contents);
        ReferenceProperty(governance, "permissionDetail", contents);
        ReferenceProperty(governance, "approvalDecisionBy", contents);
        ReferenceProperty(governance, "approvalDetail", contents);
    }

    private static void ReferenceProperty(JsonObject owner, string propertyName, ContentRegistry contents)
    {
        if (owner[propertyName] is not JsonValue value)
        {
            return;
        }

        string? text;
        try
        {
            text = value.GetValue<string?>();
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException($"Content-bearing field `{propertyName}` must be a string or null.", exception);
        }

        if (text is not null)
        {
            owner[propertyName] = new JsonObject { [ContentReferenceProperty] = contents.Reference(text) };
        }
    }

    private static void ReferenceIdentifierProperty(JsonObject owner, string propertyName)
    {
        if (owner[propertyName] is null)
        {
            return;
        }

        owner[propertyName] = new JsonObject { [ContentReferenceProperty] = RequireString(owner, propertyName) };
    }

    private static void CompactToolEvidence(JsonObject projection, IReadOnlyList<CustomLoopRunEvent> sourceEvents, ContentRegistry contents, StructuralRegistry blocks, StructuralRegistry authorities, StructuralRegistry requests)
    {
        // This state machine preserves the append-only reservation -> governance -> outcome ->
        // returned/integrity protocol while deduplicating repeated request and authority material.
        var states = new Dictionary<(int RequestOrdinal, string RequestCorrelationId), ToolProjectionState>();
        var reservationCorrelationIds = new HashSet<string>(StringComparer.Ordinal);
        var standaloneIntegrityProjected = false;
        var projectedEvents = RequireArray(projection, "events");
        if (projectedEvents.Count != sourceEvents.Count)
        {
            throw new FormatException("The projected event count did not match the source run.");
        }

        for (var eventIndex = 0; eventIndex < projectedEvents.Count; eventIndex++)
        {
            var runEvent = projectedEvents[eventIndex]?.AsObject() ?? throw new FormatException("Run-event projection entries must be objects.");
            var sourceEvent = sourceEvents[eventIndex];
            ReferenceIdentifierProperty(runEvent, "detail");
            ReferenceIdentifierProperty(runEvent, "canonicalOutput");
            var contextBlockReferences = new JsonArray();
            foreach (var sourceBlock in sourceEvent.ContextBlocks)
            {
                var block = SerializeObject(sourceBlock, "context block");
                ProjectContextBlock(block.DeepClone().AsObject(), contents);
                contextBlockReferences.Add(new JsonObject { [BlockReferenceProperty] = blocks.Reference(block) });
            }

            runEvent["contextBlocks"] = contextBlockReferences;
            var eventAuthority = sourceEvent.ToolAuthority;
            var evidence = sourceEvent.ToolEvidence;
            if (evidence is null)
            {
                if (eventAuthority is not null)
                {
                    var authorityNode = SerializeObject(eventAuthority, "tool authority");
                    ProjectAuthority(authorityNode.DeepClone().AsObject(), contents);
                    runEvent["toolAuthority"] = ReferenceObject(AuthorityReferenceProperty, authorities.Reference(authorityNode));
                }

                continue;
            }

            var requestKey = ToolRequestKey(evidence);
            var phase = evidence.Phase;
            var returned = evidence.ReturnedToModel;
            var sequence = sourceEvent.Sequence;
            if (eventAuthority is null)
            {
                throw new FormatException("Tool evidence requires an event authority snapshot before compact projection.");
            }

            var evidenceAuthority = evidence.Authority;
            if (!eventAuthority.Matches(evidenceAuthority))
            {
                throw new FormatException("Tool event authority must exactly match its evidence authority before compact projection.");
            }

            var evidenceAuthorityNode = SerializeObject(evidenceAuthority, "tool authority");
            ProjectAuthority(evidenceAuthorityNode.DeepClone().AsObject(), contents);
            var authorityId = authorities.Reference(evidenceAuthorityNode);

            JsonObject compact;
            if (phase == CustomLoopToolEvidencePhase.RequestReserved)
            {
                if (states.ContainsKey(requestKey)
                    || !reservationCorrelationIds.Add(evidence.RequestCorrelationId)
                    || returned
                    || evidence.Governance is not null
                    || evidence.Outcome is not null
                    || evidence.CanonicalResultReturnedToModel is not null)
                {
                    throw new FormatException("A tool request reservation must be the unique exact request-and-authority owner.");
                }

                var request = ToolRequest(evidence, authorityId);
                ProjectToolRequest(request.DeepClone().AsObject(), contents);
                var requestId = requests.Reference(request);
                compact = new JsonObject
                {
                    ["shape"] = 1,
                    ["phase"] = EnumNode(evidence.Phase),
                    ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, requestId),
                    ["brokerRequestId"] = evidence.BrokerRequestId
                };
                states.Add(requestKey, new ToolProjectionState(evidence, evidenceAuthority, authorityId, requestId));
            }
            else if (phase == CustomLoopToolEvidencePhase.IntegrityFailed && !states.ContainsKey(requestKey))
            {
                if (standaloneIntegrityProjected
                    || returned
                    || evidence.BrokerRequestId is not null
                    || evidence.Governance is not null
                    || evidence.Outcome is not null
                    || evidence.CanonicalResultReturnedToModel is not null
                    || evidence.CanonicalResultHash is not null
                    || evidence.CanonicalResultCharacterCount is not null)
                {
                    throw new FormatException("A standalone tool integrity record may contain only the exact non-actuating request-and-authority evidence.");
                }

                standaloneIntegrityProjected = true;
                var request = ToolRequest(evidence, authorityId);
                ProjectToolRequest(request.DeepClone().AsObject(), contents);
                var requestId = requests.Reference(request);
                compact = new JsonObject
                {
                    ["shape"] = 6,
                    ["phase"] = EnumNode(evidence.Phase),
                    ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, requestId),
                    ["brokerRequestId"] = evidence.BrokerRequestId
                };
                states.Add(requestKey, new ToolProjectionState(evidence, evidenceAuthority, authorityId, requestId)
                {
                    IntegrityFailed = true
                });
            }
            else
            {
                if (!states.TryGetValue(requestKey, out var state))
                {
                    throw new FormatException("Tool evidence references a request that has no earlier exact reservation.");
                }

                if (state.IntegrityFailed)
                {
                    throw new FormatException("Tool evidence cannot continue after the request recorded an integrity failure.");
                }

                RequireRepeatedRequest(evidence, state.Request);
                if (phase != CustomLoopToolEvidencePhase.GovernanceDecided
                    && !string.Equals(state.AuthorityId, authorityId, StringComparison.Ordinal))
                {
                    throw new FormatException("Tool evidence after governance references a different authority table entry than the refreshed request.");
                }

                if (phase == CustomLoopToolEvidencePhase.GovernanceDecided)
                {
                    var governance = evidence.Governance ?? throw new FormatException("A governance event must contain governance evidence.");
                    if (state.Governance is not null || state.Outcome is not null || returned || evidence.Outcome is not null || evidence.CanonicalResultReturnedToModel is not null)
                    {
                        throw new FormatException("A governance event must be the request's unique governance owner and cannot duplicate an outcome or returned result.");
                    }

                    var projectedGovernance = SerializeObject(governance, "tool governance");
                    ProjectGovernance(projectedGovernance, contents);
                    compact = new JsonObject
                    {
                        ["shape"] = 2,
                        ["phase"] = EnumNode(evidence.Phase),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = evidence.BrokerRequestId,
                        ["governance"] = projectedGovernance
                    };
                    state.Authority = evidenceAuthority;
                    state.AuthorityId = authorityId;
                    state.Governance = governance;
                    state.BrokerRequestId = evidence.BrokerRequestId;
                }
                else if (phase == CustomLoopToolEvidencePhase.OutcomeObserved && !returned)
                {
                    if (state.Governance is null
                        || state.Outcome is not null
                        || state.Governance != evidence.Governance
                        || !string.Equals(state.BrokerRequestId, evidence.BrokerRequestId, StringComparison.Ordinal))
                    {
                        throw new FormatException("A tool outcome must be the unique outcome owner and reference the exact governance decision.");
                    }

                    compact = new JsonObject
                    {
                        ["shape"] = 3,
                        ["phase"] = EnumNode(evidence.Phase),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = evidence.BrokerRequestId,
                        ["outcome"] = evidence.Outcome is null ? null : EnumNode(evidence.Outcome.Value),
                        ["canonicalResultReturnedToModel"] = evidence.CanonicalResultReturnedToModel,
                        ["canonicalResultHash"] = evidence.CanonicalResultHash,
                        ["canonicalResultCharacterCount"] = evidence.CanonicalResultCharacterCount
                    };
                    state.Outcome = evidence;
                    state.OutcomeSequence = sequence;
                    ReferenceProperty(compact, "canonicalResultReturnedToModel", contents);
                }
                else if (phase == CustomLoopToolEvidencePhase.OutcomeObserved && returned)
                {
                    if (state.Outcome is null
                        || state.OutcomeSequence is null
                        || state.Returned
                        || !string.Equals(state.BrokerRequestId, evidence.BrokerRequestId, StringComparison.Ordinal))
                    {
                        throw new FormatException("A returned-to-model marker must be unique and requires one earlier exact durable outcome.");
                    }

                    RequireRepeatedOutcome(evidence, state);
                    compact = new JsonObject
                    {
                        ["shape"] = 4,
                        ["phase"] = EnumNode(evidence.Phase),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = evidence.BrokerRequestId,
                        ["outcomeSequence"] = state.OutcomeSequence.Value
                    };
                    state.Returned = true;
                }
                else if (phase == CustomLoopToolEvidencePhase.IntegrityFailed)
                {
                    compact = new JsonObject
                    {
                        ["shape"] = 5,
                        ["phase"] = EnumNode(evidence.Phase),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = evidence.BrokerRequestId,
                        ["hasGovernance"] = evidence.Governance is not null,
                        ["hasOutcome"] = evidence.Outcome is not null,
                        ["hasCanonicalResult"] = evidence.CanonicalResultReturnedToModel is not null
                    };
                    RequireIntegrityReferences(evidence, state);
                    state.IntegrityFailed = true;
                }
                else
                {
                    throw new FormatException("The tool evidence phase cannot be compacted into the supported ordered protocol.");
                }
            }

            runEvent["toolAuthority"] = ReferenceObject(AuthorityReferenceProperty, authorityId);
            runEvent["toolEvidence"] = compact;
        }
    }

    private static void ExpandToolEvidence(JsonObject projection, StructuralRegistry authorities, StructuralRegistry requests)
    {
        // Expansion is the inverse protocol validator, not just decompression: every compact shape must
        // reconstruct the exact earlier request, authority, governance, and outcome evidence.
        var states = new Dictionary<string, ToolHydrationState>(StringComparer.Ordinal);
        var correlationIds = new HashSet<string>(StringComparer.Ordinal);
        var requestKeys = new HashSet<(int RequestOrdinal, string RequestCorrelationId)>();
        var standaloneIntegrityHydrated = false;
        foreach (var item in RequireArray(projection, "events"))
        {
            if (item is not JsonObject runEvent)
            {
                throw new FormatException("Run-event projection entries must be objects.");
            }

            if (runEvent["toolEvidence"] is not JsonObject compact)
            {
                if (runEvent["toolAuthority"] is JsonObject)
                {
                    runEvent["toolAuthority"] = authorities.Resolve(RequireReference(runEvent, "toolAuthority", AuthorityReferenceProperty));
                }

                continue;
            }

            var shape = RequireInt32(compact, "shape");
            ValidateCanonicalEnumProperty<CustomLoopToolEvidencePhase>(compact, "phase");
            var requestId = RequireReference(compact, "toolRequest", ToolRequestReferenceProperty);
            var request = requests.Resolve(requestId);
            ValidateToolRequest(request);
            var requestAuthorityId = RequireReference(request, "authority", AuthorityReferenceProperty);
            var requestAuthority = authorities.Resolve(requestAuthorityId);
            var eventAuthorityId = RequireReference(runEvent, "toolAuthority", AuthorityReferenceProperty);
            var eventAuthority = authorities.Resolve(eventAuthorityId);

            var correlationId = RequireString(request, "requestCorrelationId");
            var requestKey = ToolRequestKey(request);
            JsonObject evidence;
            if (shape == 1)
            {
                RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId");
                if (!string.Equals(RequireString(compact, "phase"), "requestReserved", StringComparison.Ordinal)
                    || states.ContainsKey(requestId)
                    || !requestKeys.Add(requestKey)
                    || !correlationIds.Add(correlationId)
                    || !string.Equals(eventAuthorityId, requestAuthorityId, StringComparison.Ordinal))
                {
                    throw new FormatException("The compact tool trace contains a duplicate request reservation.");
                }

                evidence = FullEvidence(request, requestAuthority, null, null, null, null, null, returned: false, compact);
                states.Add(requestId, new ToolHydrationState(request, requestAuthority, requestAuthorityId));
            }
            else if (shape == 6)
            {
                RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId");
                if (!string.Equals(RequireString(compact, "phase"), "integrityFailed", StringComparison.Ordinal)
                    || compact["brokerRequestId"] is not null
                    || standaloneIntegrityHydrated
                    || states.ContainsKey(requestId)
                    || !requestKeys.Add(requestKey)
                    || !string.Equals(eventAuthorityId, requestAuthorityId, StringComparison.Ordinal))
                {
                    throw new FormatException("A compact standalone tool integrity record must uniquely own its exact non-actuating request.");
                }

                standaloneIntegrityHydrated = true;
                evidence = FullEvidence(request, requestAuthority, null, null, null, null, null, returned: false, compact);
                states.Add(requestId, new ToolHydrationState(request, requestAuthority, requestAuthorityId)
                {
                    IntegrityFailed = true
                });
            }
            else
            {
                if (!states.TryGetValue(requestId, out var state)
                    || !JsonNode.DeepEquals(request, state.Request))
                {
                    throw new FormatException("Compact tool evidence has a dangling request reference.");
                }

                if (state.IntegrityFailed)
                {
                    throw new FormatException("Compact tool evidence cannot continue after the request recorded an integrity failure.");
                }

                if (shape == 2)
                {
                    RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId", "governance");
                    if (state.Governance is not null || state.OutcomeEvidence is not null)
                    {
                        throw new FormatException("A compact request may own exactly one ordered governance decision.");
                    }

                    var governance = RequireObject(compact, "governance").DeepClone().AsObject();
                    evidence = FullEvidence(state.Request, eventAuthority, governance, null, null, null, null, returned: false, compact);
                    state.Authority = eventAuthority;
                    state.AuthorityId = eventAuthorityId;
                    state.Governance = governance;
                    state.BrokerRequestId = Clone(compact, "brokerRequestId");
                }
                else if (shape == 3)
                {
                    RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId", "outcome", "canonicalResultReturnedToModel", "canonicalResultHash", "canonicalResultCharacterCount");
                    ValidateCanonicalEnumProperty<ToolExecutionOutcome>(compact, "outcome", allowNull: true);
                    if (state.Governance is null
                        || state.OutcomeEvidence is not null
                        || !JsonNode.DeepEquals(state.BrokerRequestId, compact["brokerRequestId"])
                        || !string.Equals(state.AuthorityId, eventAuthorityId, StringComparison.Ordinal))
                    {
                        throw new FormatException("A compact tool outcome has no earlier governance decision.");
                    }

                    evidence = FullEvidence(
                        state.Request,
                        state.Authority,
                        state.Governance,
                        compact["outcome"],
                        compact["canonicalResultReturnedToModel"],
                        compact["canonicalResultHash"],
                        compact["canonicalResultCharacterCount"],
                        returned: false,
                        compact);
                    state.OutcomeEvidence = evidence.DeepClone().AsObject();
                    state.OutcomeSequence = RequireInt64(runEvent, "sequence");
                }
                else if (shape == 4)
                {
                    RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId", "outcomeSequence");
                    if (state.OutcomeEvidence is null
                        || state.OutcomeSequence != RequireInt64(compact, "outcomeSequence")
                        || state.Returned
                        || !JsonNode.DeepEquals(state.BrokerRequestId, compact["brokerRequestId"])
                        || !string.Equals(state.AuthorityId, eventAuthorityId, StringComparison.Ordinal))
                    {
                        throw new FormatException("A compact returned marker has a dangling or mismatched outcome reference.");
                    }

                    evidence = state.OutcomeEvidence.DeepClone().AsObject();
                    evidence["phase"] = Clone(compact, "phase");
                    evidence["brokerRequestId"] = Clone(compact, "brokerRequestId");
                    evidence["returnedToModel"] = true;
                    state.Returned = true;
                }
                else if (shape == 5)
                {
                    RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId", "hasGovernance", "hasOutcome", "hasCanonicalResult");
                    var hasGovernance = RequireBoolean(compact, "hasGovernance");
                    var hasOutcome = RequireBoolean(compact, "hasOutcome");
                    var hasCanonical = RequireBoolean(compact, "hasCanonicalResult");
                    var outcome = hasOutcome ? state.OutcomeEvidence?["outcome"] : null;
                    var canonical = hasCanonical ? state.OutcomeEvidence?["canonicalResultReturnedToModel"] : null;
                    var canonicalHash = hasCanonical ? state.OutcomeEvidence?["canonicalResultHash"] : null;
                    var canonicalCount = hasCanonical ? state.OutcomeEvidence?["canonicalResultCharacterCount"] : null;
                    if (hasGovernance && state.Governance is null || (hasOutcome || hasCanonical) && state.OutcomeEvidence is null)
                    {
                        throw new FormatException("A compact integrity marker has a dangling governance or outcome reference.");
                    }

                    if (!string.Equals(state.AuthorityId, eventAuthorityId, StringComparison.Ordinal))
                    {
                        throw new FormatException("A compact integrity marker references a different authority than the latest durable request phase.");
                    }

                    evidence = FullEvidence(state.Request, eventAuthority, hasGovernance ? state.Governance : null, outcome, canonical, canonicalHash, canonicalCount, returned: false, compact);
                    state.IntegrityFailed = true;
                }
                else
                {
                    throw new FormatException("The compact tool evidence shape is unsupported.");
                }
            }

            runEvent["toolAuthority"] = eventAuthority.DeepClone();
            runEvent["toolEvidence"] = evidence;
        }
    }

    private static JsonObject ToolRequest(CustomLoopToolTraceEvidence evidence, string authorityId)
    {
        return new JsonObject
        {
            ["authority"] = ReferenceObject(AuthorityReferenceProperty, authorityId),
            ["requestOrdinal"] = evidence.RequestOrdinal,
            ["requestCorrelationId"] = evidence.RequestCorrelationId,
            ["command"] = EnumNode(evidence.Command),
            ["targetPath"] = evidence.TargetPath,
            ["content"] = evidence.Content,
            ["pattern"] = evidence.Pattern,
            ["resolvedTarget"] = evidence.ResolvedTarget,
            ["reservedUtf8Bytes"] = evidence.ReservedUtf8Bytes
        };
    }

    private static (int RequestOrdinal, string RequestCorrelationId) ToolRequestKey(CustomLoopToolTraceEvidence source)
    {
        return (source.RequestOrdinal, source.RequestCorrelationId);
    }

    private static (int RequestOrdinal, string RequestCorrelationId) ToolRequestKey(JsonObject source)
    {
        return (RequireInt32(source, "requestOrdinal"), RequireString(source, "requestCorrelationId"));
    }

    private static JsonObject FullEvidence(
        JsonObject source,
        JsonObject authority,
        JsonObject? governance,
        JsonNode? outcome,
        JsonNode? canonical,
        JsonNode? canonicalHash,
        JsonNode? canonicalCount,
        bool returned,
        JsonObject? phaseSource = null)
    {
        phaseSource ??= source;
        return new JsonObject
        {
            ["phase"] = Clone(phaseSource, "phase"),
            ["requestOrdinal"] = Clone(source, "requestOrdinal"),
            ["requestCorrelationId"] = Clone(source, "requestCorrelationId"),
            ["brokerRequestId"] = Clone(phaseSource, "brokerRequestId"),
            ["command"] = Clone(source, "command"),
            ["targetPath"] = Clone(source, "targetPath"),
            ["content"] = Clone(source, "content"),
            ["pattern"] = Clone(source, "pattern"),
            ["resolvedTarget"] = Clone(source, "resolvedTarget"),
            ["authority"] = authority.DeepClone(),
            ["governance"] = governance?.DeepClone(),
            ["outcome"] = outcome?.DeepClone(),
            ["canonicalResultReturnedToModel"] = canonical?.DeepClone(),
            ["canonicalResultHash"] = canonicalHash?.DeepClone(),
            ["canonicalResultCharacterCount"] = canonicalCount?.DeepClone(),
            ["returnedToModel"] = returned,
            ["reservedUtf8Bytes"] = Clone(source, "reservedUtf8Bytes")
        };
    }

    private static void RequireRepeatedRequest(CustomLoopToolTraceEvidence evidence, CustomLoopToolTraceEvidence request)
    {
        if (evidence.RequestOrdinal != request.RequestOrdinal
            || !string.Equals(evidence.RequestCorrelationId, request.RequestCorrelationId, StringComparison.Ordinal)
            || evidence.Command != request.Command
            || !string.Equals(evidence.TargetPath, request.TargetPath, StringComparison.Ordinal)
            || !string.Equals(evidence.Content, request.Content, StringComparison.Ordinal)
            || !string.Equals(evidence.Pattern, request.Pattern, StringComparison.Ordinal)
            || !string.Equals(evidence.ResolvedTarget, request.ResolvedTarget, StringComparison.Ordinal)
            || evidence.ReservedUtf8Bytes != request.ReservedUtf8Bytes)
        {
            throw new FormatException("Tool evidence structurally duplicated mismatched request fields.");
        }
    }

    private static void RequireRepeatedOutcome(CustomLoopToolTraceEvidence evidence, ToolProjectionState state)
    {
        if (state.Outcome is null
            || evidence.Governance != state.Governance
            || evidence.Outcome != state.Outcome.Outcome
            || !string.Equals(evidence.CanonicalResultReturnedToModel, state.Outcome.CanonicalResultReturnedToModel, StringComparison.Ordinal)
            || !string.Equals(evidence.CanonicalResultHash, state.Outcome.CanonicalResultHash, StringComparison.Ordinal)
            || evidence.CanonicalResultCharacterCount != state.Outcome.CanonicalResultCharacterCount)
        {
            throw new FormatException("The returned-to-model marker structurally duplicated a mismatched governance or outcome payload.");
        }
    }

    private static void RequireIntegrityReferences(CustomLoopToolTraceEvidence evidence, ToolProjectionState state)
    {
        if (evidence.Governance is not null && evidence.Governance != state.Governance
            || evidence.Outcome is not null && evidence.Outcome != state.Outcome?.Outcome
            || evidence.CanonicalResultReturnedToModel is not null && (!string.Equals(evidence.CanonicalResultReturnedToModel, state.Outcome?.CanonicalResultReturnedToModel, StringComparison.Ordinal)
                || !string.Equals(evidence.CanonicalResultHash, state.Outcome?.CanonicalResultHash, StringComparison.Ordinal)
                || evidence.CanonicalResultCharacterCount != state.Outcome?.CanonicalResultCharacterCount)
            || evidence.CanonicalResultReturnedToModel is null && (evidence.CanonicalResultHash is not null || evidence.CanonicalResultCharacterCount is not null)
            || evidence.BrokerRequestId is not null && !string.Equals(evidence.BrokerRequestId, state.BrokerRequestId, StringComparison.Ordinal))
        {
            throw new FormatException("Tool integrity evidence may reference only the exact earlier governance and outcome evidence.");
        }
    }

    private static JsonObject SerializeObject<T>(T value, string description)
    {
        try
        {
            return JsonSerializer.SerializeToNode(value, _jsonOptions)?.AsObject() ?? throw new FormatException($"The {description} projection was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The {description} projection could not be serialized.", exception);
        }
    }

    private static JsonNode EnumNode<T>(T value) where T : struct, Enum
    {
        return JsonSerializer.SerializeToNode(value, _jsonOptions) ?? throw new FormatException("An enum projection was empty.");
    }

    private static JsonObject ReferenceObject(string propertyName, string id)
    {
        return new JsonObject { [propertyName] = id };
    }

    private static string RequireReference(JsonObject owner, string propertyName, string referencePropertyName)
    {
        var reference = RequireObject(owner, propertyName);
        RequireProperties(reference, referencePropertyName);
        return RequireString(reference, referencePropertyName);
    }

    private static void ValidateToolRequest(JsonObject request)
    {
        RequireProperties(request, "authority", "requestOrdinal", "requestCorrelationId", "command", "targetPath", "content", "pattern", "resolvedTarget", "reservedUtf8Bytes");
        _ = RequireReference(request, "authority", AuthorityReferenceProperty);
        ValidateCanonicalEnumProperty<ToolCommand>(request, "command");
    }

    private static void ValidateCanonicalEnumProperty<TEnum>(JsonObject owner, string propertyName, bool allowNull = false) where TEnum : struct, Enum
    {
        if (owner[propertyName] is JsonValue value)
        {
            ValidateCanonicalPrimitiveValue(value, typeof(TEnum));
            return;
        }

        if (!allowNull || owner[propertyName] is not null)
        {
            throw new FormatException($"Enum field `{propertyName}` is missing or malformed.");
        }
    }

    private static void ValidateToolRequestTable(IReadOnlyList<StructuralEntry> entries, StructuralRegistry authorities)
    {
        foreach (var entry in entries)
        {
            ValidateToolRequest(entry.Value);
            _ = authorities.Resolve(RequireReference(entry.Value, "authority", AuthorityReferenceProperty));
        }
    }

    private static void ResolveStructuralContent(IReadOnlyList<StructuralEntry> entries, ContentRegistry contents)
    {
        foreach (var entry in entries)
        {
            ResolveContentReferences(entry.Value, contents);
        }
    }

    private static void ExpandContextBlocks(JsonObject projection, StructuralRegistry blocks)
    {
        foreach (var item in RequireArray(projection, "events"))
        {
            if (item is not JsonObject runEvent)
            {
                throw new FormatException("Run-event projection entries must be objects.");
            }

            var references = RequireArray(runEvent, "contextBlocks");
            if (references.Count == 0)
            {
                continue;
            }

            var expanded = new JsonArray();
            foreach (var referenceItem in references)
            {
                if (referenceItem is not JsonObject reference)
                {
                    throw new FormatException("Context-block references must be objects.");
                }

                RequireProperties(reference, BlockReferenceProperty);
                expanded.Add(blocks.Resolve(RequireString(reference, BlockReferenceProperty)));
            }

            runEvent["contextBlocks"] = expanded;
        }
    }

    private static void ResolveContentReferences(JsonNode? node, ContentRegistry contents)
    {
        if (node is JsonObject owner)
        {
            if (owner.Count == 1 && owner.ContainsKey(ContentReferenceProperty))
            {
                throw new FormatException("A content reference cannot be resolved without its containing property.");
            }

            for (var index = 0; index < owner.Count; index++)
            {
                var value = owner.GetAt(index).Value;
                if (value is JsonObject reference && reference.Count == 1 && reference.ContainsKey(ContentReferenceProperty))
                {
                    owner.SetAt(index, contents.Resolve(RequireString(reference, ContentReferenceProperty)));
                }
                else
                {
                    ResolveContentReferences(value, contents);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonObject reference && reference.Count == 1 && reference.ContainsKey(ContentReferenceProperty))
                {
                    array[index] = contents.Resolve(RequireString(reference, ContentReferenceProperty));
                }
                else
                {
                    ResolveContentReferences(array[index], contents);
                }
            }
        }
    }

    private static IReadOnlyList<ContentEntry> ParseContentEntries(JsonArray items)
    {
        var entries = new List<ContentEntry>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item is not JsonObject entry)
            {
                throw new FormatException("Content-table entries must be objects.");
            }

            RequireProperties(entry, "id", "sha256", "utf16Characters", "utf8Bytes", "base64");
            var id = RequireString(entry, "id");
            var hash = RequireString(entry, "sha256");
            var utf16Characters = RequireInt32(entry, "utf16Characters");
            var utf8Bytes = RequireInt32(entry, "utf8Bytes");
            var base64 = RequireString(entry, "base64");
            // Cross-check canonical base64, strict UTF-8 round-trip, ordered id, both character/byte
            // counts, and the raw-byte hash so table references cannot alias different content.
            var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(1, base64.Length / 4 * 3));
            try
            {
                if (!Convert.TryFromBase64String(base64, decoded, out var decodedLength))
                {
                    throw new FormatException("A content-table entry is not strict base64.");
                }

                var bytes = decoded.AsSpan(0, decodedLength);
                if (!Base64RoundTrips(bytes, base64.AsSpan()))
                {
                    throw new FormatException("A content-table entry does not use canonical base64.");
                }

                string text;
                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new FormatException("A content-table entry is not strict UTF-8.", exception);
                }

                var actualHash = Hash(bytes);
                if (!StrictUtf8RoundTrips(text, bytes) || utf16Characters != text.Length
                    || utf8Bytes != bytes.Length
                    || !string.Equals(hash, actualHash, StringComparison.Ordinal)
                    || !string.Equals(id, IndexedId("c", index), StringComparison.Ordinal))
                {
                    throw new FormatException("A content-table entry has mismatched id, hash, UTF-16 count, or raw UTF-8 byte count.");
                }

                entries.Add(new ContentEntry(id, hash, utf16Characters, utf8Bytes, base64, text));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(decoded, clearArray: true);
            }
        }

        return entries;
    }

    private static bool Base64RoundTrips(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> expected)
    {
        const int BytesPerChunk = 3072;
        Span<char> scratch = stackalloc char[BytesPerChunk / 3 * 4];
        var byteOffset = 0;
        var characterOffset = 0;
        while (byteOffset < bytes.Length)
        {
            var byteCount = Math.Min(BytesPerChunk, bytes.Length - byteOffset);
            if (!Convert.TryToBase64Chars(bytes.Slice(byteOffset, byteCount), scratch, out var charactersWritten)
                || charactersWritten > expected.Length - characterOffset
                || !scratch[..charactersWritten].SequenceEqual(expected.Slice(characterOffset, charactersWritten)))
            {
                return false;
            }

            byteOffset += byteCount;
            characterOffset += charactersWritten;
        }

        return characterOffset == expected.Length;
    }

    private static bool StrictUtf8RoundTrips(string text, ReadOnlySpan<byte> expected)
    {
        var encoder = StrictUtf8.GetEncoder();
        var remaining = text.AsSpan();
        var expectedOffset = 0;
        Span<byte> scratch = stackalloc byte[4096];
        var completed = false;
        while (!completed)
        {
            encoder.Convert(remaining, scratch, flush: true, out var charactersUsed, out var bytesUsed, out completed);
            if (bytesUsed > expected.Length - expectedOffset || !scratch[..bytesUsed].SequenceEqual(expected.Slice(expectedOffset, bytesUsed)))
            {
                return false;
            }

            remaining = remaining[charactersUsed..];
            expectedOffset += bytesUsed;
        }

        return expectedOffset == expected.Length;
    }

    private static IReadOnlyList<StructuralEntry> ParseStructuralEntries(JsonArray items, string prefix, string valueProperty, string description)
    {
        var entries = new List<StructuralEntry>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject entry)
            {
                throw new FormatException($"{description} table entries must be objects.");
            }

            RequireProperties(entry, "id", "sha256", valueProperty);
            var id = RequireString(entry, "id");
            var hash = RequireString(entry, "sha256");
            var value = RequireObject(entry, valueProperty);
            var bytes = SerializeNode(value);
            var actualHash = Hash(bytes);
            if (!string.Equals(hash, actualHash, StringComparison.Ordinal) || !string.Equals(id, IndexedId(prefix, index), StringComparison.Ordinal))
            {
                throw new FormatException($"A {description} table entry has a mismatched ordered id or canonical structural hash.");
            }

            entries.Add(new StructuralEntry(id, value));
        }

        return entries;
    }

    private static byte[] SerializeEnvelope(
        ContentRegistry contents,
        IReadOnlyList<StructuralEntry> blocks,
        IReadOnlyList<StructuralEntry> authorities,
        IReadOnlyList<StructuralEntry> requests,
        JsonObject projection)
    {
        var blockArray = ProjectStructuralTable(blocks, "contextBlock", value => ProjectContextBlock(value, contents));
        var authorityArray = ProjectStructuralTable(authorities, "authority", value => ProjectAuthority(value, contents));
        var requestArray = ProjectStructuralTable(requests, "toolRequest", value => ProjectToolRequest(value, contents));
        var contentArray = new JsonArray();
        foreach (var entry in contents.Entries)
        {
            contentArray.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["sha256"] = entry.Hash,
                ["utf16Characters"] = entry.Utf16Characters,
                ["utf8Bytes"] = entry.Utf8Bytes,
                ["base64"] = entry.Base64
            });
        }

        var envelope = new JsonObject
        {
            ["artifactKind"] = ArtifactKind,
            ["artifactSchemaVersion"] = CurrentArtifactSchemaVersion,
            ["projectionSchemaVersion"] = CurrentProjectionSchemaVersion,
            ["encoding"] = EncodingName,
            ["content"] = contentArray,
            ["contextBlocks"] = blockArray,
            ["authorities"] = authorityArray,
            ["toolRequests"] = requestArray,
            ["run"] = projection
        };
        var content = SerializeNode(envelope);
        // Persist exactly one trailing LF. Canonical readback compares this byte too, rejecting both
        // unterminated files and alternate whitespace after the envelope.
        return Terminate(content);
    }

    private static JsonArray ProjectStructuralTable(IReadOnlyList<StructuralEntry> entries, string valueProperty, Action<JsonObject> projectContent)
    {
        var items = new JsonArray();
        foreach (var entry in entries)
        {
            var projected = entry.Value.DeepClone().AsObject();
            projectContent(projected);
            items.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["sha256"] = Hash(SerializeNode(projected)),
                [valueProperty] = projected
            });
        }

        return items;
    }

    /// <summary>
    /// Serializes one codec node with the bounded canonical JSON options.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    internal static byte[] SerializeNode(JsonNode node)
    {
        return CustomLoopJsonDepthPolicy.SerializeToUtf8Bytes(node, _jsonOptions, "Custom-loop run artifact");
    }

    private static JsonNode? Clone(JsonObject owner, string propertyName)
    {
        return owner[propertyName]?.DeepClone();
    }

    private static JsonObject RequireObject(JsonObject owner, string propertyName)
    {
        return owner[propertyName] as JsonObject ?? throw new FormatException($"Required object `{propertyName}` is missing or malformed.");
    }

    private static JsonArray RequireArray(JsonObject owner, string propertyName)
    {
        return owner[propertyName] as JsonArray ?? throw new FormatException($"Required array `{propertyName}` is missing or malformed.");
    }

    private static string RequireString(JsonObject owner, string propertyName)
    {
        try
        {
            return owner[propertyName]?.GetValue<string>() ?? throw new FormatException($"Required string `{propertyName}` is missing.");
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException($"Required string `{propertyName}` is malformed.", exception);
        }
    }

    private static int RequireInt32(JsonObject owner, string propertyName)
    {
        try
        {
            var node = owner[propertyName] ?? throw new FormatException($"Required integer `{propertyName}` is missing.");
            if (node is not JsonValue value)
            {
                throw new FormatException($"Required integer `{propertyName}` is malformed.");
            }

            ValidateCanonicalPrimitiveValue(value, typeof(int));
            return value.GetValue<int>();
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException($"Required integer `{propertyName}` is malformed.", exception);
        }
    }

    private static long RequireInt64(JsonObject owner, string propertyName)
    {
        try
        {
            var node = owner[propertyName] ?? throw new FormatException($"Required integer `{propertyName}` is missing.");
            if (node is not JsonValue value)
            {
                throw new FormatException($"Required integer `{propertyName}` is malformed.");
            }

            ValidateCanonicalPrimitiveValue(value, typeof(long));
            return value.GetValue<long>();
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException($"Required integer `{propertyName}` is malformed.", exception);
        }
    }

    private static bool RequireBoolean(JsonObject owner, string propertyName)
    {
        try
        {
            return owner[propertyName]?.GetValue<bool>() ?? throw new FormatException($"Required boolean `{propertyName}` is missing.");
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException($"Required boolean `{propertyName}` is malformed.", exception);
        }
    }

    private static void RequireProperties(JsonObject owner, params string[] expected)
    {
        var actual = owner.Select(property => property.Key).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new FormatException($"Codec object fields must be exactly and canonically ordered as: {string.Join(", ", expected)}.");
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8Json)
    {
        var objects = new Stack<HashSet<string>>();
        var available = new Stack<HashSet<string>>();
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = _jsonOptions.MaxDepth });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objects.Push(available.TryPop(out var names) ? names : new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var propertyName = reader.GetString() ?? throw new FormatException("A JSON property name was null.");
                if (!objects.Peek().Add(propertyName))
                {
                    throw new FormatException($"A JSON object contains duplicate property `{propertyName}`.");
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                var names = objects.Pop();
                names.Clear();
                available.Push(names);
            }
        }
    }

    /// <summary>
    /// Computes the lowercase SHA-256 binding for persisted content.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The lowercase hexadecimal SHA-256 digest.</returns>
    internal static string Hash(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    /// <summary>
    /// Formats a deterministic compact base-36 identifier for a registry entry.
    /// </summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="index">The index.</param>
    /// <returns>The prefix followed by the base-36 index.</returns>
    internal static string IndexedId(string prefix, int index)
    {
        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        do
        {
            buffer[--position] = Digits[index % 36];
            index /= 36;
        }
        while (index > 0);

        return prefix + new string(buffer[position..]);
    }

    private sealed record ParsedEnvelope(
        CustomLoopRunRecord Run,
        IReadOnlyList<ContentEntry> ContentEntries,
        IReadOnlyList<StructuralEntry> BlockEntries,
        IReadOnlyList<StructuralEntry> AuthorityEntries,
        IReadOnlyList<StructuralEntry> RequestEntries);

    private sealed class ToolProjectionState(CustomLoopToolTraceEvidence request, CustomLoopToolAuthoritySnapshot authority, string authorityId, string requestId)
    {
        /// <summary>
        /// Gets the request JSON object.
        /// </summary>
        /// <value>The request JSON object.</value>
        public CustomLoopToolTraceEvidence Request { get; } = request;
        /// <summary>
        /// Gets the authority JSON object.
        /// </summary>
        /// <value>The authority JSON object.</value>
        public CustomLoopToolAuthoritySnapshot Authority { get; set; } = authority;
        /// <summary>
        /// Gets the authority ID.
        /// </summary>
        /// <value>The authority ID.</value>
        public string AuthorityId { get; set; } = authorityId;
        /// <summary>
        /// Gets the request ID.
        /// </summary>
        /// <value>The request ID.</value>
        public string RequestId { get; } = requestId;
        /// <summary>
        /// Gets the governance JSON object.
        /// </summary>
        /// <value>The governance JSON object.</value>
        public ToolGovernanceEvidence? Governance { get; set; }
        /// <summary>
        /// Gets the broker request ID JSON node.
        /// </summary>
        /// <value>The broker request ID JSON node.</value>
        public string? BrokerRequestId { get; set; }
        /// <summary>
        /// Gets the outcome JSON object.
        /// </summary>
        /// <value>The outcome JSON object.</value>
        public CustomLoopToolTraceEvidence? Outcome { get; set; }
        /// <summary>
        /// Gets the outcome sequence.
        /// </summary>
        /// <value>The outcome sequence.</value>
        public long? OutcomeSequence { get; set; }
        /// <summary>
        /// Gets a value indicating whether the returned condition holds.
        /// </summary>
        /// <value><see langword="true"/> when the returned condition holds; otherwise, <see langword="false"/>.</value>
        public bool Returned { get; set; }
        /// <summary>
        /// Gets a value indicating whether the integrity failed condition holds.
        /// </summary>
        /// <value><see langword="true"/> when the integrity failed condition holds; otherwise, <see langword="false"/>.</value>
        public bool IntegrityFailed { get; set; }
    }

    private sealed class ToolHydrationState(JsonObject request, JsonObject authority, string authorityId)
    {
        /// <summary>
        /// Gets the request JSON object.
        /// </summary>
        /// <value>The request JSON object.</value>
        public JsonObject Request { get; } = request;
        /// <summary>
        /// Gets the authority JSON object.
        /// </summary>
        /// <value>The authority JSON object.</value>
        public JsonObject Authority { get; set; } = authority;
        /// <summary>
        /// Gets the authority ID.
        /// </summary>
        /// <value>The authority ID.</value>
        public string AuthorityId { get; set; } = authorityId;
        /// <summary>
        /// Gets the governance JSON object.
        /// </summary>
        /// <value>The governance JSON object.</value>
        public JsonObject? Governance { get; set; }
        /// <summary>
        /// Gets the broker request ID JSON node.
        /// </summary>
        /// <value>The broker request ID JSON node.</value>
        public JsonNode? BrokerRequestId { get; set; }
        /// <summary>
        /// Gets the outcome evidence JSON object.
        /// </summary>
        /// <value>The outcome evidence JSON object.</value>
        public JsonObject? OutcomeEvidence { get; set; }
        /// <summary>
        /// Gets the outcome sequence.
        /// </summary>
        /// <value>The outcome sequence.</value>
        public long? OutcomeSequence { get; set; }
        /// <summary>
        /// Gets a value indicating whether the returned condition holds.
        /// </summary>
        /// <value><see langword="true"/> when the returned condition holds; otherwise, <see langword="false"/>.</value>
        public bool Returned { get; set; }
        /// <summary>
        /// Gets a value indicating whether the integrity failed condition holds.
        /// </summary>
        /// <value><see langword="true"/> when the integrity failed condition holds; otherwise, <see langword="false"/>.</value>
        public bool IntegrityFailed { get; set; }
    }
}
