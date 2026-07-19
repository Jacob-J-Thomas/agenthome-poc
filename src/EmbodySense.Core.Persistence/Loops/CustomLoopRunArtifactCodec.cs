using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops;

internal static partial class CustomLoopRunArtifactCodec
{
    internal const string ArtifactKind = "custom-loop-run";
    internal const int CurrentArtifactSchemaVersion = 1;
    internal const int CurrentProjectionSchemaVersion = 1;
    private const string EncodingName = "utf-8";
    private const string ContentReferenceProperty = "$content";
    private const string BlockReferenceProperty = "$contextBlock";
    private const string AuthorityReferenceProperty = "$authority";
    private const string ToolRequestReferenceProperty = "$toolRequest";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static byte[] Encode(CustomLoopRunRecord run, byte[]? previousEnvelope = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (previousEnvelope is not null)
        {
            _ = Parse(previousEnvelope, requireCanonical: true);
        }

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

    internal static CustomLoopRunRecord Decode(byte[] utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        return Parse(utf8Json, requireCanonical: true).Run;
    }

    internal static bool IsEnvelope(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("artifactKind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && string.Equals(kind.GetString(), ArtifactKind, StringComparison.Ordinal);
    }

    private static ParsedEnvelope Parse(byte[] utf8Json, bool requireCanonical)
    {
        JsonObject root;
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = JsonOptions.MaxDepth });
            RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            root = JsonNode.Parse(utf8Json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = JsonOptions.MaxDepth }) as JsonObject
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

        var contentEntries = ParseContentEntries(RequireArray(root, "content"));
        var contents = new ContentRegistry(contentEntries);
        var blockEntries = ParseStructuralEntries(RequireArray(root, "contextBlocks"), "b", "contextBlock", "context-block");
        var authorityEntries = ParseStructuralEntries(RequireArray(root, "authorities"), "a", "authority", "authority");
        var requestEntries = ParseStructuralEntries(RequireArray(root, "toolRequests"), "q", "toolRequest", "tool-request");
        ResolveStructuralContent(blockEntries, contents);
        ResolveStructuralContent(authorityEntries, contents);
        ResolveStructuralContent(requestEntries, contents);
        var blocks = new StructuralRegistry("b", "context-block", blockEntries);
        var authorities = new StructuralRegistry("a", "authority", authorityEntries);
        var requests = new StructuralRegistry("q", "tool-request", requestEntries);
        ValidateToolRequestTable(requestEntries, authorities);
        var hydratedProjection = RequireObject(root, "run").DeepClone().AsObject();
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
            run = hydratedProjection.Deserialize<CustomLoopRunRecord>(JsonOptions)
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
            var reprojectedContents = new ContentRegistry([]);
            var reprojectedBlocks = new StructuralRegistry("b", "context-block", []);
            var reprojectedAuthorities = new StructuralRegistry("a", "authority", []);
            var reprojectedRequests = new StructuralRegistry("q", "tool-request", []);
            var reprojectedRun = Project(run, reprojectedContents, reprojectedBlocks, reprojectedAuthorities, reprojectedRequests);
            var canonical = SerializeEnvelope(reprojectedContents, reprojectedBlocks.Entries, reprojectedAuthorities.Entries, reprojectedRequests.Entries, reprojectedRun);
            reprojectedContents.RequireEverySeedReferenced();
            reprojectedBlocks.RequireEverySeedReferenced();
            reprojectedAuthorities.RequireEverySeedReferenced();
            reprojectedRequests.RequireEverySeedReferenced();
            if (!canonical.AsSpan().SequenceEqual(utf8Json))
            {
                var firstDifference = FirstDifference(canonical, utf8Json);
                throw new FormatException($"The custom-loop live-run envelope is not the one canonical hydrate-and-reproject encoding (first differing byte {firstDifference}, canonical length {canonical.Length}, persisted length {utf8Json.Length}).");
            }
        }

        return new ParsedEnvelope(run, contentEntries, blockEntries, authorityEntries, requestEntries);
    }

    private static int FirstDifference(byte[] left, byte[] right)
    {
        var sharedLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            if (left[index] != right[index])
            {
                return index;
            }
        }

        return sharedLength;
    }

    private static JsonObject Project(CustomLoopRunRecord run, ContentRegistry contents, StructuralRegistry blocks, StructuralRegistry authorities, StructuralRegistry requests)
    {
        var projection = JsonSerializer.SerializeToNode(run, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("The custom-loop run could not be projected.");
        ProjectDefinition(RequireObject(projection, "admittedDefinition"), contents);
        ReferenceProperty(projection, "triggerPrompt", contents);
        ProjectContextSnapshot(RequireObject(projection, "contextSnapshot"), contents);
        ProjectCheckpoint(RequireObject(projection, "checkpoint"), contents);
        CompactToolEvidence(projection, contents, blocks, authorities, requests);
        ReferenceProperty(projection, "finalOutput", contents);
        ReferenceProperty(projection, "failureDetail", contents);
        return projection;
    }

    private static void ProjectDefinition(JsonObject definition, ContentRegistry contents)
    {
        ReferenceProperty(definition, "displayName", contents);
        ReferenceProperty(definition, "description", contents);
        var trigger = RequireObject(definition, "triggerPolicy");
        ReferenceProperty(trigger, "presetPrompt", contents);
        foreach (var item in RequireArray(definition, "inferenceSteps"))
        {
            var step = item?.AsObject() ?? throw new FormatException("Inference-step projection entries must be objects.");
            ReferenceProperty(step, "name", contents);
            ReferenceProperty(step, "instruction", contents);
        }

        var exit = RequireObject(definition, "exitPolicy");
        ReferenceProperty(exit, "decisionInstruction", contents);
    }

    private static void ProjectContextSnapshot(JsonObject snapshot, ContentRegistry contents)
    {
        foreach (var item in RequireArray(snapshot, "sourceManifest"))
        {
            var source = item?.AsObject() ?? throw new FormatException("Context-manifest projection entries must be objects.");
            ReferenceProperty(source, "sourceId", contents);
            ReferenceProperty(source, "sourcePath", contents);
            ReferenceProperty(source, "content", contents);
            ReferenceProperty(source, "truncationReason", contents);
            ReferenceProperty(source, "omissionReason", contents);
        }
    }

    private static void ProjectCheckpoint(JsonObject checkpoint, ContentRegistry contents)
    {
        foreach (var item in RequireArray(checkpoint, "earlierRetainedOutputs"))
        {
            ProjectRetainedOutput(item?.AsObject(), contents);
        }

        ProjectRetainedOutput(checkpoint["previousIterationResult"] as JsonObject, contents);
        ProjectRetainedOutput(checkpoint["currentIterationResult"] as JsonObject, contents);
    }

    private static void ProjectRetainedOutput(JsonObject? output, ContentRegistry contents)
    {
        if (output is not null)
        {
            ReferenceProperty(output, "content", contents);
        }
    }

    private static void ProjectEvents(JsonArray events, ContentRegistry contents, StructuralRegistry blocks)
    {
        foreach (var item in events)
        {
            var runEvent = item?.AsObject() ?? throw new FormatException("Run-event projection entries must be objects.");
            ReferenceProperty(runEvent, "detail", contents);
            ReferenceProperty(runEvent, "canonicalOutput", contents);
            var contextBlocks = RequireArray(runEvent, "contextBlocks");
            var references = new JsonArray();
            foreach (var blockItem in contextBlocks)
            {
                var block = blockItem?.AsObject() ?? throw new FormatException("Context-block projection entries must be objects.");
                references.Add(new JsonObject { [BlockReferenceProperty] = blocks.Reference(block) });
            }

            runEvent["contextBlocks"] = references;
            if (runEvent["toolEvidence"] is JsonObject evidence)
            {
                ProjectToolEvidence(evidence, contents);
            }
        }
    }

    private static void ProjectToolEvidence(JsonObject evidence, ContentRegistry contents)
    {
        var shape = RequireInt32(evidence, "shape");
        if (shape == 2)
        {
            ProjectGovernance(RequireObject(evidence, "governance"), contents);
        }
        else if (shape == 3)
        {
            ReferenceProperty(evidence, "canonicalResultReturnedToModel", contents);
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

    private static void CompactToolEvidence(JsonObject projection, ContentRegistry contents, StructuralRegistry blocks, StructuralRegistry authorities, StructuralRegistry requests)
    {
        var states = new Dictionary<string, ToolProjectionState>(StringComparer.Ordinal);
        foreach (var item in RequireArray(projection, "events"))
        {
            var runEvent = item?.AsObject() ?? throw new FormatException("Run-event projection entries must be objects.");
            ReferenceProperty(runEvent, "detail", contents);
            ReferenceProperty(runEvent, "canonicalOutput", contents);
            var contextBlockReferences = new JsonArray();
            foreach (var blockItem in RequireArray(runEvent, "contextBlocks"))
            {
                var block = blockItem?.AsObject() ?? throw new FormatException("Context-block projection entries must be objects.");
                ProjectContextBlock(block.DeepClone().AsObject(), contents);
                contextBlockReferences.Add(new JsonObject { [BlockReferenceProperty] = blocks.Reference(block) });
            }

            runEvent["contextBlocks"] = contextBlockReferences;
            var eventAuthority = runEvent["toolAuthority"] as JsonObject;
            if (runEvent["toolEvidence"] is not JsonObject evidence)
            {
                if (eventAuthority is not null)
                {
                    ProjectAuthority(eventAuthority.DeepClone().AsObject(), contents);
                    runEvent["toolAuthority"] = ReferenceObject(AuthorityReferenceProperty, authorities.Reference(eventAuthority));
                }

                continue;
            }

            var correlationId = RequireString(evidence, "requestCorrelationId");
            var phase = RequireString(evidence, "phase");
            var returned = RequireBoolean(evidence, "returnedToModel");
            var sequence = RequireInt64(runEvent, "sequence");
            if (eventAuthority is null)
            {
                throw new FormatException("Tool evidence requires an event authority snapshot before compact projection.");
            }

            var evidenceAuthority = RequireObject(evidence, "authority");
            if (!JsonNode.DeepEquals(eventAuthority, evidenceAuthority))
            {
                throw new FormatException("Tool event authority must exactly match its evidence authority before compact projection.");
            }

            ProjectAuthority(evidenceAuthority.DeepClone().AsObject(), contents);
            var authorityId = authorities.Reference(evidenceAuthority);

            JsonObject compact;
            if (string.Equals(phase, "requestReserved", StringComparison.Ordinal))
            {
                if (states.ContainsKey(correlationId) || returned || evidence["governance"] is not null || evidence["outcome"] is not null || evidence["canonicalResultReturnedToModel"] is not null)
                {
                    throw new FormatException("A tool request reservation must be the unique exact request-and-authority owner.");
                }

                var request = new JsonObject
                {
                    ["authority"] = ReferenceObject(AuthorityReferenceProperty, authorityId),
                    ["requestOrdinal"] = Clone(evidence, "requestOrdinal"),
                    ["requestCorrelationId"] = Clone(evidence, "requestCorrelationId"),
                    ["command"] = Clone(evidence, "command"),
                    ["targetPath"] = Clone(evidence, "targetPath"),
                    ["content"] = Clone(evidence, "content"),
                    ["pattern"] = Clone(evidence, "pattern"),
                    ["resolvedTarget"] = Clone(evidence, "resolvedTarget"),
                    ["reservedUtf8Bytes"] = Clone(evidence, "reservedUtf8Bytes")
                };
                ProjectToolRequest(request.DeepClone().AsObject(), contents);
                var requestId = requests.Reference(request);
                compact = new JsonObject
                {
                    ["shape"] = 1,
                    ["phase"] = Clone(evidence, "phase"),
                    ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, requestId),
                    ["brokerRequestId"] = Clone(evidence, "brokerRequestId")
                };
                states.Add(correlationId, new ToolProjectionState(evidence.DeepClone().AsObject(), evidenceAuthority.DeepClone().AsObject(), authorityId, requestId));
            }
            else
            {
                if (!states.TryGetValue(correlationId, out var state))
                {
                    throw new FormatException("Tool evidence references a request that has no earlier exact reservation.");
                }

                if (state.IntegrityFailed)
                {
                    throw new FormatException("Tool evidence cannot continue after the request recorded an integrity failure.");
                }

                RequireRepeatedRequest(evidence, state.Request, state.Authority);
                if (!string.Equals(state.AuthorityId, authorityId, StringComparison.Ordinal))
                {
                    throw new FormatException("Tool evidence references a different authority table entry than its reserved request.");
                }

                if (string.Equals(phase, "governanceDecided", StringComparison.Ordinal))
                {
                    var governance = RequireObject(evidence, "governance");
                    if (state.Governance is not null || state.Outcome is not null || returned || evidence["outcome"] is not null || evidence["canonicalResultReturnedToModel"] is not null)
                    {
                        throw new FormatException("A governance event must be the request's unique governance owner and cannot duplicate an outcome or returned result.");
                    }

                    var projectedGovernance = governance.DeepClone().AsObject();
                    ProjectGovernance(projectedGovernance, contents);
                    compact = new JsonObject
                    {
                        ["shape"] = 2,
                        ["phase"] = Clone(evidence, "phase"),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = Clone(evidence, "brokerRequestId"),
                        ["governance"] = projectedGovernance
                    };
                    state.Governance = governance.DeepClone().AsObject();
                    state.BrokerRequestId = Clone(evidence, "brokerRequestId");
                }
                else if (string.Equals(phase, "outcomeObserved", StringComparison.Ordinal) && !returned)
                {
                    if (state.Governance is null
                        || state.Outcome is not null
                        || !JsonNode.DeepEquals(state.Governance, evidence["governance"])
                        || !JsonNode.DeepEquals(state.BrokerRequestId, evidence["brokerRequestId"]))
                    {
                        throw new FormatException("A tool outcome must be the unique outcome owner and reference the exact governance decision.");
                    }

                    compact = new JsonObject
                    {
                        ["shape"] = 3,
                        ["phase"] = Clone(evidence, "phase"),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = Clone(evidence, "brokerRequestId"),
                        ["outcome"] = Clone(evidence, "outcome"),
                        ["canonicalResultReturnedToModel"] = Clone(evidence, "canonicalResultReturnedToModel"),
                        ["canonicalResultHash"] = Clone(evidence, "canonicalResultHash"),
                        ["canonicalResultCharacterCount"] = Clone(evidence, "canonicalResultCharacterCount")
                    };
                    state.Outcome = compact.DeepClone().AsObject();
                    state.OutcomeSequence = sequence;
                    ReferenceProperty(compact, "canonicalResultReturnedToModel", contents);
                }
                else if (string.Equals(phase, "outcomeObserved", StringComparison.Ordinal) && returned)
                {
                    if (state.Outcome is null
                        || state.OutcomeSequence is null
                        || state.Returned
                        || !JsonNode.DeepEquals(state.BrokerRequestId, evidence["brokerRequestId"]))
                    {
                        throw new FormatException("A returned-to-model marker must be unique and requires one earlier exact durable outcome.");
                    }

                    RequireRepeatedOutcome(evidence, state);
                    compact = new JsonObject
                    {
                        ["shape"] = 4,
                        ["phase"] = Clone(evidence, "phase"),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = Clone(evidence, "brokerRequestId"),
                        ["outcomeSequence"] = state.OutcomeSequence.Value
                    };
                    state.Returned = true;
                }
                else if (string.Equals(phase, "integrityFailed", StringComparison.Ordinal))
                {
                    compact = new JsonObject
                    {
                        ["shape"] = 5,
                        ["phase"] = Clone(evidence, "phase"),
                        ["toolRequest"] = ReferenceObject(ToolRequestReferenceProperty, state.RequestId),
                        ["brokerRequestId"] = Clone(evidence, "brokerRequestId"),
                        ["hasGovernance"] = evidence["governance"] is not null,
                        ["hasOutcome"] = evidence["outcome"] is not null,
                        ["hasCanonicalResult"] = evidence["canonicalResultReturnedToModel"] is not null
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
        var states = new Dictionary<string, ToolHydrationState>(StringComparer.Ordinal);
        var correlationIds = new HashSet<string>(StringComparer.Ordinal);
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
            var requestId = RequireReference(compact, "toolRequest", ToolRequestReferenceProperty);
            var request = requests.Resolve(requestId);
            ValidateToolRequest(request);
            var authorityId = RequireReference(request, "authority", AuthorityReferenceProperty);
            var authority = authorities.Resolve(authorityId);
            var eventAuthorityId = RequireReference(runEvent, "toolAuthority", AuthorityReferenceProperty);
            if (!string.Equals(eventAuthorityId, authorityId, StringComparison.Ordinal))
            {
                throw new FormatException("A compact tool event references an authority different from its request.");
            }

            var correlationId = RequireString(request, "requestCorrelationId");
            JsonObject evidence;
            if (shape == 1)
            {
                RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId");
                if (!string.Equals(RequireString(compact, "phase"), "requestReserved", StringComparison.Ordinal)
                    || states.ContainsKey(requestId)
                    || !correlationIds.Add(correlationId))
                {
                    throw new FormatException("The compact tool trace contains a duplicate request reservation.");
                }

                evidence = FullEvidence(request, authority, null, null, null, null, null, returned: false, compact);
                states.Add(requestId, new ToolHydrationState(request, authority, authorityId));
            }
            else
            {
                if (!states.TryGetValue(requestId, out var state)
                    || !JsonNode.DeepEquals(request, state.Request)
                    || !string.Equals(state.AuthorityId, authorityId, StringComparison.Ordinal))
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
                    evidence = FullEvidence(state.Request, state.Authority, governance, null, null, null, null, returned: false, compact);
                    state.Governance = governance;
                    state.BrokerRequestId = Clone(compact, "brokerRequestId");
                }
                else if (shape == 3)
                {
                    RequireProperties(compact, "shape", "phase", "toolRequest", "brokerRequestId", "outcome", "canonicalResultReturnedToModel", "canonicalResultHash", "canonicalResultCharacterCount");
                    if (state.Governance is null
                        || state.OutcomeEvidence is not null
                        || !JsonNode.DeepEquals(state.BrokerRequestId, compact["brokerRequestId"]))
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
                        || !JsonNode.DeepEquals(state.BrokerRequestId, compact["brokerRequestId"]))
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

                    evidence = FullEvidence(state.Request, state.Authority, hasGovernance ? state.Governance : null, outcome, canonical, canonicalHash, canonicalCount, returned: false, compact);
                    state.IntegrityFailed = true;
                }
                else
                {
                    throw new FormatException("The compact tool evidence shape is unsupported.");
                }
            }

            runEvent["toolAuthority"] = authority.DeepClone();
            runEvent["toolEvidence"] = evidence;
        }
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

    private static void RequireRepeatedRequest(JsonObject evidence, JsonObject request, JsonObject authority)
    {
        foreach (var property in new[] { "requestOrdinal", "requestCorrelationId", "command", "targetPath", "content", "pattern", "resolvedTarget", "reservedUtf8Bytes" })
        {
            if (!JsonNode.DeepEquals(evidence[property], request[property]))
            {
                throw new FormatException($"Tool evidence structurally duplicated a mismatched request field `{property}`.");
            }
        }

        if (!JsonNode.DeepEquals(evidence["authority"], authority))
        {
            throw new FormatException("Tool evidence structurally duplicated a mismatched request authority.");
        }
    }

    private static void RequireRepeatedOutcome(JsonObject evidence, ToolProjectionState state)
    {
        if (!JsonNode.DeepEquals(evidence["governance"], state.Governance)
            || !JsonNode.DeepEquals(evidence["outcome"], state.Outcome?["outcome"])
            || !JsonNode.DeepEquals(evidence["canonicalResultReturnedToModel"], state.Outcome?["canonicalResultReturnedToModel"])
            || !JsonNode.DeepEquals(evidence["canonicalResultHash"], state.Outcome?["canonicalResultHash"])
            || !JsonNode.DeepEquals(evidence["canonicalResultCharacterCount"], state.Outcome?["canonicalResultCharacterCount"]))
        {
            throw new FormatException("The returned-to-model marker structurally duplicated a mismatched governance or outcome payload.");
        }
    }

    private static void RequireIntegrityReferences(JsonObject evidence, ToolProjectionState state)
    {
        if (evidence["governance"] is not null && !JsonNode.DeepEquals(evidence["governance"], state.Governance)
            || evidence["outcome"] is not null && !JsonNode.DeepEquals(evidence["outcome"], state.Outcome?["outcome"])
            || evidence["canonicalResultReturnedToModel"] is not null && (!JsonNode.DeepEquals(evidence["canonicalResultReturnedToModel"], state.Outcome?["canonicalResultReturnedToModel"])
                || !JsonNode.DeepEquals(evidence["canonicalResultHash"], state.Outcome?["canonicalResultHash"])
                || !JsonNode.DeepEquals(evidence["canonicalResultCharacterCount"], state.Outcome?["canonicalResultCharacterCount"]))
            || evidence["canonicalResultReturnedToModel"] is null && (evidence["canonicalResultHash"] is not null || evidence["canonicalResultCharacterCount"] is not null)
            || evidence["brokerRequestId"] is not null && !JsonNode.DeepEquals(evidence["brokerRequestId"], state.BrokerRequestId))
        {
            throw new FormatException("Tool integrity evidence may reference only the exact earlier governance and outcome evidence.");
        }
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

            foreach (var property in owner.ToArray())
            {
                if (property.Value is JsonObject reference && reference.Count == 1 && reference.ContainsKey(ContentReferenceProperty))
                {
                    owner[property.Key] = contents.Resolve(RequireString(reference, ContentReferenceProperty));
                }
                else
                {
                    ResolveContentReferences(property.Value, contents);
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
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException exception)
            {
                throw new FormatException("A content-table entry is not strict base64.", exception);
            }

            if (!string.Equals(Convert.ToBase64String(bytes), base64, StringComparison.Ordinal))
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
            if (!StrictUtf8.GetBytes(text).AsSpan().SequenceEqual(bytes)
                || utf16Characters != text.Length
                || utf8Bytes != bytes.Length
                || !string.Equals(hash, actualHash, StringComparison.Ordinal)
                || !string.Equals(id, IndexedId("c", index), StringComparison.Ordinal))
            {
                throw new FormatException("A content-table entry has mismatched id, hash, UTF-16 count, or raw UTF-8 byte count.");
            }

            entries.Add(new ContentEntry(id, hash, utf16Characters, utf8Bytes, base64, text, bytes));
        }

        return entries;
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
            var value = RequireObject(entry, valueProperty).DeepClone().AsObject();
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
            ["run"] = projection.DeepClone()
        };
        var content = SerializeNode(envelope);
        var terminated = new byte[content.Length + 1];
        content.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
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

    private static byte[] SerializeNode(JsonNode node)
    {
        return JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions);
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
            return owner[propertyName]?.GetValue<int>() ?? throw new FormatException($"Required integer `{propertyName}` is missing.");
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
            return owner[propertyName]?.GetValue<long>() ?? throw new FormatException($"Required integer `{propertyName}` is missing.");
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
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actual = owner.Select(property => property.Key).ToArray();
        if (actual.Length != expected.Length || actual.Any(property => !expectedSet.Contains(property)))
        {
            throw new FormatException($"Codec object fields must be exactly: {string.Join(", ", expected)}.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, HashSet<string> scratch)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            scratch.Clear();
            foreach (var property in element.EnumerateObject())
            {
                if (!scratch.Add(property.Name))
                {
                    throw new FormatException($"JSON object `{path}` contains duplicate property `{property.Name}`.");
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                RejectDuplicateProperties(property.Value, path + "." + property.Name, new HashSet<string>(StringComparer.Ordinal));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", new HashSet<string>(StringComparer.Ordinal));
                index++;
            }
        }
    }

    private static string Hash(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private static string IndexedId(string prefix, int index)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        do
        {
            buffer[--position] = digits[index % 36];
            index /= 36;
        }
        while (index > 0);

        return prefix + new string(buffer[position..]);
    }

    private sealed record ContentEntry(string Id, string Hash, int Utf16Characters, int Utf8Bytes, string Base64, string Text, byte[] Bytes);

    private sealed record StructuralEntry(string Id, JsonObject Value);

    private sealed record ParsedEnvelope(
        CustomLoopRunRecord Run,
        IReadOnlyList<ContentEntry> ContentEntries,
        IReadOnlyList<StructuralEntry> BlockEntries,
        IReadOnlyList<StructuralEntry> AuthorityEntries,
        IReadOnlyList<StructuralEntry> RequestEntries);

    private sealed class ToolProjectionState(JsonObject request, JsonObject authority, string authorityId, string requestId)
    {
        public JsonObject Request { get; } = request;
        public JsonObject Authority { get; } = authority;
        public string AuthorityId { get; } = authorityId;
        public string RequestId { get; } = requestId;
        public JsonObject? Governance { get; set; }
        public JsonNode? BrokerRequestId { get; set; }
        public JsonObject? Outcome { get; set; }
        public long? OutcomeSequence { get; set; }
        public bool Returned { get; set; }
        public bool IntegrityFailed { get; set; }
    }

    private sealed class ToolHydrationState(JsonObject request, JsonObject authority, string authorityId)
    {
        public JsonObject Request { get; } = request;
        public JsonObject Authority { get; } = authority;
        public string AuthorityId { get; } = authorityId;
        public JsonObject? Governance { get; set; }
        public JsonNode? BrokerRequestId { get; set; }
        public JsonObject? OutcomeEvidence { get; set; }
        public long? OutcomeSequence { get; set; }
        public bool Returned { get; set; }
        public bool IntegrityFailed { get; set; }
    }
}
