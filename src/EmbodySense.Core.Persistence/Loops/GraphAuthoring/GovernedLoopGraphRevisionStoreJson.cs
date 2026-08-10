using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring;

internal static class GovernedLoopGraphRevisionStoreJson
{
    private const int MaximumJsonDepth = 32;
    private static readonly JsonSerializerOptions _writeOptions = CreateOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateOptions(writeIndented: false);

    public static byte[] Serialize(GovernedLoopGraphRevisionArtifactDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return AppendNewline(JsonSerializer.SerializeToUtf8Bytes(ToJson(document), _writeOptions));
    }

    public static byte[] Serialize(GovernedLoopGraphRevisionIntentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return AppendNewline(JsonSerializer.SerializeToUtf8Bytes(ToJson(document), _writeOptions));
    }

    public static string ComputeContentDigest(GovernedLoopGraphRevisionArtifactDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var json = ToJson(document) with { ContentDigest = string.Empty, AuthenticationTag = string.Empty };
        return CapabilityIntegrityDigest.Compute(JsonSerializer.SerializeToUtf8Bytes(json, _hashOptions)).Value;
    }

    public static string ComputeContentDigest(GovernedLoopGraphRevisionIntentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var json = ToJson(document) with { ContentDigest = string.Empty, AuthenticationTag = string.Empty };
        return CapabilityIntegrityDigest.Compute(JsonSerializer.SerializeToUtf8Bytes(json, _hashOptions)).Value;
    }

    public static string ComputePayloadHash(GovernedLoopGraphDefinition graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var payload = new GraphPayloadHashJson(
            graph.SchemaVersion,
            ExecutableGraph(graph),
            Layout(graph.DisplayMetadata));
        var canonical = JsonSerializer.SerializeToUtf8Bytes(payload, _hashOptions);
        var domain = Encoding.UTF8.GetBytes("embodysense-governed-loop-graph-payload-v1\n");
        var bytes = new byte[domain.Length + canonical.Length];
        domain.CopyTo(bytes, 0);
        canonical.CopyTo(bytes, domain.Length);
        return Digest(bytes);
    }

    public static GovernedLoopGraphRevisionArtifactDocument DeserializeArtifact(byte[] bytes)
    {
        RequireStrictJson(bytes);
        var json = JsonSerializer.Deserialize<GraphRevisionArtifactJson>(bytes, _writeOptions)
            ?? throw new FormatException("The governed-loop graph-revision artifact is empty.");
        var document = FromJson(json);
        if (!string.Equals(document.ContentDigest, ComputeContentDigest(document), StringComparison.Ordinal))
        {
            throw new FormatException("The governed-loop graph-revision artifact content digest is invalid.");
        }

        RequireCanonicalBytes(bytes, Serialize(document), "graph-revision artifact");
        return document;
    }

    public static GovernedLoopGraphRevisionIntentDocument DeserializeIntent(byte[] bytes)
    {
        RequireStrictJson(bytes);
        var json = JsonSerializer.Deserialize<GraphRevisionIntentJson>(bytes, _writeOptions)
            ?? throw new FormatException("The governed-loop graph-authoring intent is empty.");
        var document = FromJson(json);
        if (!string.Equals(document.ContentDigest, ComputeContentDigest(document), StringComparison.Ordinal))
        {
            throw new FormatException("The governed-loop graph-authoring intent content digest is invalid.");
        }

        RequireCanonicalBytes(bytes, Serialize(document), "graph-authoring intent");
        return document;
    }

    private static GovernedLoopGraphRevisionArtifactDocument FromJson(GraphRevisionArtifactJson json)
    {
        if (json.SchemaVersion != GovernedLoopGraphDefinition.CurrentSchemaVersion
            || json.TrustGeneration < 1
            || !IsWorkspaceIdentity(json.WorkspaceIdentity)
            || !IsHash(json.ExecutableHash)
            || !IsHash(json.LayoutHash)
            || !IsHash(json.PayloadHash)
            || !IsIntegrityDigest(json.ContentDigest)
            || string.IsNullOrEmpty(json.AuthenticationTag))
        {
            throw new FormatException("The governed-loop graph-revision artifact envelope is invalid.");
        }

        var graphJson = json.ExecutableGraph ?? throw new FormatException("The governed-loop executable graph is missing.");
        var layoutJson = json.Layout ?? throw new FormatException("The governed-loop graph layout is missing.");
        var graph = GovernedLoopGraphDefinition.Create(
            graphJson.SchemaVersion,
            Required(graphJson.GraphId, "graph id"),
            Required(graphJson.RevisionId, "revision id"),
            Required(graphJson.Purpose, "purpose"),
            Required(graphJson.OwningRoleId, "owning role"),
            Required(graphJson.EntryNodeId, "entry node"),
            Required(graphJson.TerminalNodeIds, "terminal nodes"),
            GovernedLoopAuthorityCeiling.Create(Required(graphJson.AuthorityCeiling, "graph authority ceiling")),
            Required(graphJson.ValueSchemas, "value schemas").Select(ValueSchema),
            Required(graphJson.Nodes, "nodes").Select(Node),
            Required(graphJson.ControlEdges, "control edges").Select(ControlEdge),
            Required(graphJson.Bindings, "bindings").Select(Binding),
            OutputContract(graphJson.OutputContract),
            Layout(layoutJson));
        var layoutHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graph);
        var payloadHash = ComputePayloadHash(graph);
        if (!string.Equals(graph.ExecutableHash, json.ExecutableHash, StringComparison.Ordinal)
            || !string.Equals(layoutHash, json.LayoutHash, StringComparison.Ordinal)
            || !string.Equals(payloadHash, json.PayloadHash, StringComparison.Ordinal))
        {
            throw new FormatException("The governed-loop graph-revision artifact derived hashes are invalid.");
        }

        return new GovernedLoopGraphRevisionArtifactDocument(
            graph,
            layoutHash,
            payloadHash,
            json.WorkspaceIdentity!,
            json.TrustGeneration,
            json.ContentDigest!,
            json.AuthenticationTag!);
    }

    private static GovernedLoopGraphRevisionIntentDocument FromJson(GraphRevisionIntentJson json)
    {
        if (json.SchemaVersion != GovernedLoopGraphRevisionIntentDocument.CurrentSchemaVersion
            || json.TrustGeneration < 1
            || !IsWorkspaceIdentity(json.WorkspaceIdentity)
            || !CustomLoopArtifactIdentifier.IsValid(json.GraphId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(json.OperationId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            || !IsHash(json.LifecycleRequestHash)
            || !IsHash(json.AuthoringRequestHash)
            || json.GraphPayloadHash is not null && !IsHash(json.GraphPayloadHash)
            || json.GraphValidationEvidenceHash is not null && !IsHash(json.GraphValidationEvidenceHash)
            || !IsIntegrityDigest(json.ContentDigest)
            || string.IsNullOrEmpty(json.AuthenticationTag))
        {
            throw new FormatException("The governed-loop graph-authoring intent is invalid.");
        }

        return new GovernedLoopGraphRevisionIntentDocument(
            json.SchemaVersion,
            json.WorkspaceIdentity!,
            json.TrustGeneration,
            json.GraphId!,
            json.OperationId!,
            json.LifecycleRequestHash!,
            json.AuthoringRequestHash!,
            json.GraphPayloadHash,
            json.GraphValidationEvidenceHash,
            json.ContentDigest!,
            json.AuthenticationTag!);
    }

    private static GraphRevisionArtifactJson ToJson(GovernedLoopGraphRevisionArtifactDocument document)
    {
        var graph = document.Graph;
        return new GraphRevisionArtifactJson(
            graph.SchemaVersion,
            document.WorkspaceIdentity,
            document.TrustGeneration,
            ExecutableGraph(graph),
            Layout(graph.DisplayMetadata),
            graph.ExecutableHash,
            document.LayoutHash,
            document.PayloadHash,
            document.ContentDigest,
            document.AuthenticationTag);
    }

    private static GraphRevisionIntentJson ToJson(GovernedLoopGraphRevisionIntentDocument document)
        => new(
            document.SchemaVersion,
            document.WorkspaceIdentity,
            document.TrustGeneration,
            document.GraphId,
            document.OperationId,
            document.LifecycleRequestHash,
            document.AuthoringRequestHash,
            document.GraphPayloadHash,
            document.GraphValidationEvidenceHash,
            document.ContentDigest,
            document.AuthenticationTag);

    private static ExecutableGraphJson ExecutableGraph(GovernedLoopGraphDefinition graph)
        => new(
            graph.SchemaVersion,
            graph.GraphId,
            graph.RevisionId,
            graph.Purpose,
            graph.OwningRoleId,
            graph.EntryNodeId,
            graph.TerminalNodeIds.ToArray(),
            graph.AuthorityCeiling.CapabilityIds.ToArray(),
            graph.ValueSchemas.Select(schema => new ValueSchemaJson(schema.Id, ValueKind(schema.Kind), schema.Nullable, schema.Format, schema.ElementSchemaId)).ToArray(),
            graph.Nodes.Select(node => new NodeJson(
                node.Id,
                NodeKind(node.Descriptor.Kind),
                node.Descriptor.TypeId,
                node.Descriptor.Version,
                node.AuthorityCeiling.CapabilityIds.ToArray(),
                node.Parameters,
                node.Ports.Select(port => new PortJson(port.Id, PortDirection(port.Direction), BindingKind(port.BindingKind), port.ValueSchemaId, port.Required)).ToArray())).ToArray(),
            graph.ControlEdges.Select(edge => new ControlEdgeJson(edge.Id, edge.FromNodeId, edge.ToNodeId, ControlCondition(edge.Condition))).ToArray(),
            graph.Bindings.Select(binding => new BindingJson(binding.Id, BindingKind(binding.Kind), binding.FromNodeId, binding.FromPortId, binding.ToNodeId, binding.ToPortId)).ToArray(),
            new OutputContractJson(
                graph.OutputContract.Summary,
                graph.OutputContract.Outputs.Select(output => new OutputJson(output.Id, output.ValueSchemaId, output.SourceNodeId, output.SourcePortId, output.Required)).ToArray()));

    private static GraphLayoutJson Layout(GovernedLoopDisplayMetadata layout)
        => new(
            layout.DisplayName,
            layout.Description,
            layout.Nodes.Select(node => new NodeLayoutJson(node.NodeId, node.DisplayName, node.Description, node.CanvasX, node.CanvasY)).ToArray());

    private static GovernedLoopValueSchemaDefinition ValueSchema(ValueSchemaJson schema)
        => new(
            Required(schema.Id, "value schema id"),
            ValueKind(Required(schema.Kind, "value schema kind")),
            schema.Nullable,
            schema.Format,
            schema.ElementSchemaId);

    private static GovernedLoopNodeDefinition Node(NodeJson node)
        => new(
            Required(node.Id, "node id"),
            new GovernedLoopNodeDescriptor(
                NodeKind(Required(node.Kind, "node kind")),
                Required(node.TypeId, "node type"),
                node.DescriptorVersion),
            Required(node.Ports, "node ports").Select(port => new GovernedLoopPortDefinition(
                Required(port.Id, "port id"),
                PortDirection(Required(port.Direction, "port direction")),
                BindingKind(Required(port.BindingKind, "port binding kind")),
                Required(port.ValueSchemaId, "port value schema"),
                port.Required)).ToArray(),
            GovernedLoopAuthorityCeiling.Create(Required(node.AuthorityCeiling, "node authority ceiling")),
            Required(node.Parameters, "node parameters"));

    private static GovernedLoopControlEdgeDefinition ControlEdge(ControlEdgeJson edge)
        => new(
            Required(edge.Id, "control edge id"),
            Required(edge.FromNodeId, "control edge source"),
            Required(edge.ToNodeId, "control edge target"),
            ControlCondition(Required(edge.Condition, "control edge condition")));

    private static GovernedLoopBindingDefinition Binding(BindingJson binding)
        => new(
            Required(binding.Id, "binding id"),
            BindingKind(Required(binding.Kind, "binding kind")),
            Required(binding.FromNodeId, "binding source node"),
            Required(binding.FromPortId, "binding source port"),
            Required(binding.ToNodeId, "binding target node"),
            Required(binding.ToPortId, "binding target port"));

    private static GovernedLoopOutputContract OutputContract(OutputContractJson? contract)
    {
        if (contract is null)
        {
            throw new FormatException("The governed-loop output contract is missing.");
        }
        return new GovernedLoopOutputContract(
            Required(contract.Summary, "output summary"),
            Required(contract.Outputs, "outputs").Select(output => new GovernedLoopOutputDefinition(
                Required(output.Id, "output id"),
                Required(output.ValueSchemaId, "output value schema"),
                Required(output.SourceNodeId, "output source node"),
                Required(output.SourcePortId, "output source port"),
                output.Required)).ToArray());
    }

    private static GovernedLoopDisplayMetadata Layout(GraphLayoutJson layout)
        => new(
            Required(layout.DisplayName, "display name"),
            Required(layout.Description, "display description"),
            Required(layout.Nodes, "node layout").Select(node => new GovernedLoopNodeDisplayMetadata(
                Required(node.NodeId, "layout node id"),
                Required(node.DisplayName, "layout display name"),
                Required(node.Description, "layout description"),
                node.CanvasX,
                node.CanvasY)).ToArray());

    private static void RequireStrictJson(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new FormatException("Governed-loop graph-authoring JSON must be non-empty UTF-8 without a byte-order mark.");
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement))
        {
            throw new FormatException("Governed-loop graph-authoring JSON must be one object without duplicate properties.");
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RequireCanonicalBytes(byte[] actual, byte[] canonical, string kind)
    {
        if (!CryptographicOperations.FixedTimeEquals(actual, canonical))
        {
            throw new FormatException($"The governed-loop {kind} is not canonical schema-1 JSON.");
        }
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
        => new(JsonSerializerDefaults.Web)
        {
            MaxDepth = MaximumJsonDepth,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented,
        };

    private static byte[] AppendNewline(byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    private static string Digest(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopRevisionContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsIntegrityDigest(string? value)
        => CapabilityIntegrityDigest.TryParse(value, out _, out _);

    private static bool IsWorkspaceIdentity(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string Required(string? value, string name)
        => value ?? throw new FormatException($"The governed-loop {name} is missing.");

    private static T[] Required<T>(T[]? value, string name)
        => value ?? throw new FormatException($"The governed-loop {name} collection is missing.");

    private static IReadOnlyDictionary<string, string> Required(IReadOnlyDictionary<string, string>? value, string name)
        => value ?? throw new FormatException($"The governed-loop {name} map is missing.");

    private static GovernedLoopNodeKind NodeKind(string value) => value switch
    {
        "trigger" => GovernedLoopNodeKind.Trigger,
        "inference" => GovernedLoopNodeKind.Inference,
        "transform" => GovernedLoopNodeKind.Transform,
        "validate" => GovernedLoopNodeKind.Validate,
        "state" => GovernedLoopNodeKind.State,
        "condition" => GovernedLoopNodeKind.Condition,
        "join" => GovernedLoopNodeKind.Join,
        "wait" => GovernedLoopNodeKind.Wait,
        "action" => GovernedLoopNodeKind.Action,
        "human-review" => GovernedLoopNodeKind.HumanReview,
        "human-input" => GovernedLoopNodeKind.HumanInput,
        "child-loop" => GovernedLoopNodeKind.ChildLoop,
        "exit" => GovernedLoopNodeKind.Exit,
        "fail" => GovernedLoopNodeKind.Fail,
        _ => throw new FormatException("The governed-loop node kind is not canonical."),
    };

    private static string NodeKind(GovernedLoopNodeKind value) => value switch
    {
        GovernedLoopNodeKind.Trigger => "trigger",
        GovernedLoopNodeKind.Inference => "inference",
        GovernedLoopNodeKind.Transform => "transform",
        GovernedLoopNodeKind.Validate => "validate",
        GovernedLoopNodeKind.State => "state",
        GovernedLoopNodeKind.Condition => "condition",
        GovernedLoopNodeKind.Join => "join",
        GovernedLoopNodeKind.Wait => "wait",
        GovernedLoopNodeKind.Action => "action",
        GovernedLoopNodeKind.HumanReview => "human-review",
        GovernedLoopNodeKind.HumanInput => "human-input",
        GovernedLoopNodeKind.ChildLoop => "child-loop",
        GovernedLoopNodeKind.Exit => "exit",
        GovernedLoopNodeKind.Fail => "fail",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static GovernedLoopValueKind ValueKind(string value) => value switch
    {
        "text" => GovernedLoopValueKind.Text,
        "boolean" => GovernedLoopValueKind.Boolean,
        "integer" => GovernedLoopValueKind.Integer,
        "number" => GovernedLoopValueKind.Number,
        "object" => GovernedLoopValueKind.Object,
        "array" => GovernedLoopValueKind.Array,
        "binary" => GovernedLoopValueKind.Binary,
        _ => throw new FormatException("The governed-loop value kind is not canonical."),
    };

    private static string ValueKind(GovernedLoopValueKind value) => value switch
    {
        GovernedLoopValueKind.Text => "text",
        GovernedLoopValueKind.Boolean => "boolean",
        GovernedLoopValueKind.Integer => "integer",
        GovernedLoopValueKind.Number => "number",
        GovernedLoopValueKind.Object => "object",
        GovernedLoopValueKind.Array => "array",
        GovernedLoopValueKind.Binary => "binary",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static GovernedLoopBindingKind BindingKind(string value) => value switch
    {
        "data" => GovernedLoopBindingKind.Data,
        "context" => GovernedLoopBindingKind.Context,
        _ => throw new FormatException("The governed-loop binding kind is not canonical."),
    };

    private static string BindingKind(GovernedLoopBindingKind value) => value switch
    {
        GovernedLoopBindingKind.Data => "data",
        GovernedLoopBindingKind.Context => "context",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static GovernedLoopPortDirection PortDirection(string value) => value switch
    {
        "input" => GovernedLoopPortDirection.Input,
        "output" => GovernedLoopPortDirection.Output,
        _ => throw new FormatException("The governed-loop port direction is not canonical."),
    };

    private static string PortDirection(GovernedLoopPortDirection value) => value switch
    {
        GovernedLoopPortDirection.Input => "input",
        GovernedLoopPortDirection.Output => "output",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static GovernedLoopControlCondition ControlCondition(string value) => value switch
    {
        "always" => GovernedLoopControlCondition.Always,
        "success" => GovernedLoopControlCondition.Success,
        "failure" => GovernedLoopControlCondition.Failure,
        "true" => GovernedLoopControlCondition.True,
        "false" => GovernedLoopControlCondition.False,
        "timeout" => GovernedLoopControlCondition.Timeout,
        "approved" => GovernedLoopControlCondition.Approved,
        "rejected" => GovernedLoopControlCondition.Rejected,
        _ => throw new FormatException("The governed-loop control condition is not canonical."),
    };

    private static string ControlCondition(GovernedLoopControlCondition value) => value switch
    {
        GovernedLoopControlCondition.Always => "always",
        GovernedLoopControlCondition.Success => "success",
        GovernedLoopControlCondition.Failure => "failure",
        GovernedLoopControlCondition.True => "true",
        GovernedLoopControlCondition.False => "false",
        GovernedLoopControlCondition.Timeout => "timeout",
        GovernedLoopControlCondition.Approved => "approved",
        GovernedLoopControlCondition.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
