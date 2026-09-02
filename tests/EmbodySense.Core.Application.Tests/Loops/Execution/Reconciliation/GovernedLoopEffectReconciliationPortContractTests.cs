using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationPortContractTests
{
    [Fact]
    public async Task Public_port_contracts_compile_and_route_cancellation_without_hidden_runtime_dependencies()
    {
        var ports = new RecordingGovernedLoopEffectReconciliationPorts();
        IGovernedLoopEffectReconciliationCaseStore caseStore = ports;
        IGovernedLoopEffectReconciliationAuthorizationSource authorization = ports;
        IGovernedLoopEffectReconciliationProbeRegistry registry = ports;
        IGovernedLoopEffectReconciliationProbe probe = ports;
        IGovernedLoopEffectReconciliationInputSource inputSource = ports;
        IGovernedLoopEffectReconciliationResolutionReader resolutionReader = ports;
        using var cancellation = new CancellationTokenSource();

        await caseStore.ListAsync(null!, cancellation.Token);
        await caseStore.ReadAsync(null!, cancellation.Token);
        await caseStore.CompareExchangeAsync(null!, cancellation.Token);
        await authorization.AuthorizeAsync(null!, cancellation.Token);
        await registry.ListAsync(null!, cancellation.Token);
        await registry.ReadAsync(null!, cancellation.Token);
        await probe.ProbeAsync(null!, cancellation.Token);
        await inputSource.ReadAsync(null!, cancellation.Token);
        await resolutionReader.ReadAsync(null!, cancellation.Token);

        Assert.Equal(9, ports.CancellationTokens.Count);
        Assert.All(ports.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }
}
