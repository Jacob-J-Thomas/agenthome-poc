using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

namespace EmbodySense.Core.Application.Tests.Loops;

public sealed class GovernedLoopAdmissionRequestHashTests
{
    [Fact]
    public void Apply_and_matches_bind_every_stable_request_coordinate()
    {
        var request = Request();

        Assert.True(GovernedLoopAdmissionRequestHash.Matches(request));
        Assert.Equal(GovernedLoopAdmissionRequestHash.Compute(request), request.RequestHash);

        GovernedLoopAdmissionRequest[] changed =
        [
            request with { SchemaVersion = 2 },
            request with { OperationId = "admission-operation-2" },
            request with { InvocationPayloadHash = AuthorityGrantApplicationTestFixture.Hash64('8') },
            request with { Publication = request.Publication with { PublicationOperationId = "publish-loop-2" } },
            request with { AuthorityGrant = request.AuthorityGrant with { ContentHash = AuthorityGrantApplicationTestFixture.Hash64('7') } },
            request with { ActorId = AuthorityGrantApplicationTestFixture.Actor("different-actor") },
            request with { Surface = "http" }
        ];

        Assert.All(changed, candidate => Assert.False(GovernedLoopAdmissionRequestHash.Matches(candidate)));
    }

    [Fact]
    public void Matches_fails_closed_for_absent_or_malformed_hashes()
    {
        var request = Request();

        Assert.False(GovernedLoopAdmissionRequestHash.Matches(null));
        Assert.False(GovernedLoopAdmissionRequestHash.Matches(request with { RequestHash = string.Empty }));
        Assert.False(GovernedLoopAdmissionRequestHash.Matches(request with { RequestHash = request.RequestHash.ToUpperInvariant() }));
        Assert.False(GovernedLoopAdmissionRequestHash.Matches(request with { Surface = "bad\ud800" }));
        Assert.False(GovernedLoopAdmissionRequestHash.Matches(request with { Surface = "bad\udc00" }));
    }

    [Fact]
    public void Compute_is_deterministic_for_explicitly_absent_coordinates_and_well_formed_surrogates()
    {
        var request = Request() with
        {
            Publication = null!,
            AuthorityGrant = null!,
            ActorId = null!,
            Surface = "chat-\U0001f680",
        };

        var first = GovernedLoopAdmissionRequestHash.Compute(request);
        var second = GovernedLoopAdmissionRequestHash.Compute(request);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.All(first, character => Assert.True(char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character)));
    }

    [Fact]
    public void Compute_distinguishes_an_absent_revision_from_an_absent_publication()
    {
        var request = Request();
        var withoutRevision = request with { Publication = request.Publication with { Revision = null! } };
        var withoutPublication = request with { Publication = null! };

        Assert.NotEqual(
            GovernedLoopAdmissionRequestHash.Compute(withoutRevision),
            GovernedLoopAdmissionRequestHash.Compute(withoutPublication));
    }

    private static GovernedLoopAdmissionRequest Request()
    {
        var grant = AuthorityGrantApplicationTestFixture.Grant();
        return GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            "admission-operation-1",
            AuthorityGrantApplicationTestFixture.Hash64('6'),
            string.Empty,
            grant.Binding.Loop,
            new(grant.GrantId, grant.Revision, grant.ContentHash),
            AuthorityGrantApplicationTestFixture.Actor(),
            "cli"));
    }
}
