using EmbodySense.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Tests;

internal sealed class RecordingHubContext : IHubContext<WebSessionHub, IWebSessionClient>
{
    public RecordingHubClients ClientsRecorder { get; } = new();

    public IHubClients<IWebSessionClient> Clients => ClientsRecorder;

    public IGroupManager Groups { get; } = new NoopGroupManager();
}
