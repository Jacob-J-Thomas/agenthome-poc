using EmbodySense.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Tests;

internal sealed class RecordingHubClients : IHubClients<IWebSessionClient>
{
    private readonly RecordingWebSessionClient _noop = new();

    public RecordingWebSessionClient AllClient { get; } = new();

    public RecordingWebSessionClient TargetedClient { get; } = new();

    public List<string> TargetedConnectionIds { get; } = [];

    public IWebSessionClient All => AllClient;

    public IWebSessionClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => _noop;

    public IWebSessionClient Client(string connectionId)
    {
        TargetedConnectionIds.Add(connectionId);
        return TargetedClient;
    }

    public IWebSessionClient Clients(IReadOnlyList<string> connectionIds) => _noop;

    public IWebSessionClient Group(string groupName) => _noop;

    public IWebSessionClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _noop;

    public IWebSessionClient Groups(IReadOnlyList<string> groupNames) => _noop;

    public IWebSessionClient User(string userId) => _noop;

    public IWebSessionClient Users(IReadOnlyList<string> userIds) => _noop;
}
