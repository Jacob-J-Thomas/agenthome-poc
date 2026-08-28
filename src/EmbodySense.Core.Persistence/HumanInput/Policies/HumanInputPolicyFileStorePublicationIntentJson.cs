using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Persistence.HumanInput.Policies.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

/// <summary>Encodes a canonical, strict schema-1 recoverable policy-publication intent.</summary>
internal static class HumanInputPolicyFileStorePublicationIntentJson
{
    private const int SchemaVersion = 1;

    public static byte[] Serialize(HumanInputPolicyFileStorePublicationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.ExpectedStoreGeneration is < 0 or long.MaxValue) throw new ArgumentOutOfRangeException(nameof(intent));

        var artifactBytes = HumanInputPolicyArtifactJson.Serialize(intent.Artifact);
        return Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"expectedStoreGeneration\":"
            + intent.ExpectedStoreGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"artifact\":\""
            + Convert.ToBase64String(artifactBytes)
            + "\"}");
    }

    public static HumanInputPolicyFileStorePublicationIntent Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1) throw new FormatException("The Human Input policy publication intent is empty.");

        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The Human Input policy publication intent is invalid.");

        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 3
            || !string.Equals(properties[0].Name, "schemaVersion", StringComparison.Ordinal)
            || !string.Equals(properties[1].Name, "expectedStoreGeneration", StringComparison.Ordinal)
            || !string.Equals(properties[2].Name, "artifact", StringComparison.Ordinal)
            || properties[0].Value.ValueKind != JsonValueKind.Number
            || properties[1].Value.ValueKind != JsonValueKind.Number
            || properties[2].Value.ValueKind != JsonValueKind.String
            || !properties[0].Value.TryGetInt32(out var schemaVersion)
            || schemaVersion != SchemaVersion
            || !properties[1].Value.TryGetInt64(out var expectedStoreGeneration)
            || expectedStoreGeneration is < 0 or long.MaxValue)
        {
            throw new FormatException("The Human Input policy publication intent is invalid.");
        }

        var encodedArtifact = properties[2].Value.GetString();
        if (string.IsNullOrEmpty(encodedArtifact)) throw new FormatException("The Human Input policy publication intent is invalid.");
        byte[] artifactBytes;
        try
        {
            artifactBytes = Convert.FromBase64String(encodedArtifact);
        }
        catch (FormatException exception)
        {
            throw new FormatException("The Human Input policy publication intent is invalid.", exception);
        }

        HumanInputPolicyArtifact artifact;
        try
        {
            artifact = HumanInputPolicyArtifactJson.Deserialize(artifactBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new FormatException("The Human Input policy publication intent is invalid.", exception);
        }

        var intent = new HumanInputPolicyFileStorePublicationIntent(expectedStoreGeneration, artifact);
        if (!Serialize(intent).AsSpan().SequenceEqual(bytes)) throw new FormatException("The Human Input policy publication intent is not canonical.");
        return intent;
    }
}
