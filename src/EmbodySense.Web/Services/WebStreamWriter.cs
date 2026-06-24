using System.Text.Json;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebStreamWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(HttpResponse response, WebStreamEvent item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(item);

        await response.WriteAsync(JsonSerializer.Serialize(item, JsonOptions), cancellationToken);
        await response.WriteAsync(Environment.NewLine, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
