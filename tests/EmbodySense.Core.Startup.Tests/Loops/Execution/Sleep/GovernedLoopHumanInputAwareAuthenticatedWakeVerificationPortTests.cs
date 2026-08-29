using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPortTests
{
    [Fact]
    public async Task Routes_only_the_reserved_human_input_prefix_to_its_canonical_verifier()
    {
        var external = new HumanInputResponseContinuationRecordingAuthenticatedWakeVerifier();
        var humanInput = new HumanInputResponseContinuationRecordingAuthenticatedWakeVerifier();
        var router = new GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPort(external, humanInput);

        _ = await router.VerifyAsync(Request("ordinary-event-one"));
        _ = await router.VerifyAsync(Request(GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix + "checkpoint-one"));

        Assert.Equal(["ordinary-event-one"], external.References);
        Assert.Equal([GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix + "checkpoint-one"], humanInput.References);
    }

    [Fact]
    public void Constructor_requires_both_fail_closed_verification_ports()
    {
        var verifier = new HumanInputResponseContinuationRecordingAuthenticatedWakeVerifier();

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPort(null!, verifier));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPort(verifier, null!));
    }

    private static GovernedLoopAuthenticatedWakeVerificationRequest Request(string reference)
        => new(
            new string('a', 64),
            new string('b', 64),
            reference,
            new string('c', 64),
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));

}
