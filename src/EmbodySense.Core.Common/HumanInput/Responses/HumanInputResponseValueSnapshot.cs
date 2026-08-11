using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

internal static class HumanInputResponseValueSnapshot
{
    internal static HumanInputResponseValue Capture(HumanInputResponseValue value)
    {
        ImmutableArray<HumanInputStructuredFieldValue>? fields = value.StructuredFields is not { } source
            ? null
            : source.Select(field => field is null ? null! : field with { }).ToImmutableArray();
        return value with
        {
            StructuredFields = fields,
            Reference = value.Reference is null ? null : value.Reference with { }
        };
    }
}
