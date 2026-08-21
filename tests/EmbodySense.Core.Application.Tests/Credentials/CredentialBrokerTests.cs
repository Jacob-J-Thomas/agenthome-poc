using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

public sealed class CredentialBrokerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_success_and_terminal_replay_invoke_provider_and_consumer_at_most_once()
    {
        var fixture = Fixture();
        var firstConsumer = new RecordingConsumer();
        var first = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), firstConsumer, CancellationToken.None);
        var replayConsumer = new RecordingConsumer();
        var replay = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), replayConsumer, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.Equal(CredentialLeasePhase.Redeemed, first.LeaseAttempt!.Current.Phase);
        Assert.Equal(first.LeaseAttempt, replay.LeaseAttempt);
        Assert.Equal(1, fixture.Provider.UseCount);
        Assert.Equal(1, firstConsumer.Count);
        Assert.Equal(0, replayConsumer.Count);
        var evidence = Assert.Single(fixture.Registry.Evidence);
        Assert.Null(evidence.UsedScope.Target);
        Assert.NotNull(evidence.Lease);
        Assert.True(CredentialContractValidator.Validate(evidence).IsValid);
        Assert.True(CredentialContractJson.TrySerialize(evidence, out var json, out var validation), string.Join(';', validation.Errors));
        Assert.True(CredentialContractJson.TryDeserializeEvidence(json, out var roundTripped, out validation), string.Join(';', validation.Errors));
        Assert.True(CredentialContractJson.TrySerialize(roundTripped, out var roundTrippedJson, out validation), string.Join(';', validation.Errors));
        Assert.Equal(json, roundTrippedJson);
        Assert.DoesNotContain("private-target-not-persisted", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ephemeral", json, StringComparison.Ordinal);
        Assert.Contains(evidence.Lease!.Intent.Target.TargetFingerprint, json, StringComparison.Ordinal);

        var tamperedVersions = evidence.Lease.Attempt.Versions
            .Select((version, index) => index == evidence.Lease.Attempt.Versions.Count - 1 ? version with { ContentHash = Hash('e') } : version)
            .ToArray();
        var tampered = evidence with
        {
            Lease = evidence.Lease with
            {
                Attempt = new CredentialLeaseAttemptHistory(
                    evidence.Lease.Attempt.SchemaVersion,
                    evidence.Lease.Intent,
                    tamperedVersions),
            },
        };
        Assert.False(CredentialContractValidator.Validate(tampered).IsValid);
        Assert.False(CredentialContractJson.TrySerialize(tampered, out _, out _));
    }

    [Fact]
    public async Task Concurrent_exact_calls_have_one_owner_and_one_callback()
    {
        var provider = new RecordingProvider { BlockUse = true };
        var fixture = Fixture(provider: provider);
        var first = fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None).AsTask();
        await provider.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var concurrent = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);
        provider.ReleaseUse.TrySetResult();
        var completed = await first;

        Assert.True(completed.Succeeded);
        Assert.False(concurrent.Succeeded);
        Assert.Equal(CredentialFailureCode.Conflict, concurrent.Failure!.Code);
        Assert.Equal(1, provider.UseCount);
        Assert.Equal(1, provider.CallbackCount);
    }

    [Fact]
    public async Task Conclusive_pre_callback_provider_failure_is_redemption_failed_and_never_retried()
    {
        var provider = new RecordingProvider { Result = CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.NotFound)) };
        var fixture = Fixture(provider: provider);

        var first = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);
        var replay = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialLeasePhase.RedemptionFailed, first.LeaseAttempt!.Current.Phase);
        Assert.Equal(CredentialFailureCode.NotFound, first.Failure!.Code);
        Assert.Equal(first.LeaseAttempt, replay.LeaseAttempt);
        Assert.Equal(1, provider.UseCount);
        Assert.Equal(0, provider.CallbackCount);
    }

    [Fact]
    public async Task Callback_then_provider_failure_is_ambiguous_and_never_replayed()
    {
        var provider = new RecordingProvider { InvokeThenFail = true };
        var fixture = Fixture(provider: provider);

        var first = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);
        var replay = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, first.LeaseAttempt!.Current.Phase);
        Assert.Equal(CredentialFailureCode.OutcomeUncertain, first.Failure!.Code);
        Assert.Equal(first.LeaseAttempt, replay.LeaseAttempt);
        Assert.Equal(1, provider.UseCount);
        Assert.Equal(1, provider.CallbackCount);
    }

    [Theory]
    [InlineData(CredentialLeasePhase.IntentPrepared, CredentialLeasePhase.NotRedeemed)]
    [InlineData(CredentialLeasePhase.Authorized, CredentialLeasePhase.NotRedeemed)]
    [InlineData(CredentialLeasePhase.RedemptionBoundaryReached, CredentialLeasePhase.RedemptionAmbiguous)]
    public async Task Restart_closes_abandoned_attempt_without_provider_replay(CredentialLeasePhase abandonedPhase, CredentialLeasePhase expected)
    {
        var fixture = Fixture();
        var history = PreparedHistory(fixture.Request.LeaseIntent!);
        if (abandonedPhase is CredentialLeasePhase.Authorized or CredentialLeasePhase.RedemptionBoundaryReached)
        {
            history = Append(history, CredentialLeasePhase.Authorized, _now, Hash('a'), Hash('b'));
        }
        if (abandonedPhase == CredentialLeasePhase.RedemptionBoundaryReached)
        {
            history = Append(history, abandonedPhase, _now);
        }
        fixture.Store.Seed(history);

        var recovered = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(expected, recovered.LeaseAttempt!.Current.Phase);
        Assert.Equal(0, fixture.Provider.UseCount);
        Assert.False(recovered.Succeeded);
    }

    [Fact]
    public async Task Every_substituted_exact_lease_dimension_fails_before_callback()
    {
        var fixture = Fixture();
        var intent = fixture.Request.LeaseIntent!;
        var substitutions = new[]
        {
            intent with { LeaseId = "lease-2" },
            intent with { CredentialUseGeneration = 2 },
            intent with { Execution = intent.Execution with { WorkspaceId = "workspace-2" } },
            intent with { Execution = intent.Execution with { ActorId = "actor-2" } },
            intent with { Execution = intent.Execution with { ActorAuthenticationEvidenceHash = Hash('e') } },
            intent with { Execution = intent.Execution with { AttributionEvidenceHash = Hash('e') } },
            intent with { Execution = intent.Execution with { AdmissionReceiptHash = Hash('e') } },
            intent with { Execution = intent.Execution with { RunId = "run-2" } },
            intent with { Execution = intent.Execution with { GraphId = "graph-2" } },
            intent with { Execution = intent.Execution with { GraphRevisionId = "revision-2" } },
            intent with { Execution = intent.Execution with { GraphExecutableHash = Hash('e') } },
            intent with { Execution = intent.Execution with { ExecutionGeneration = 2 } },
            intent with { Execution = intent.Execution with { RoleId = "role-2" } },
            intent with { Execution = intent.Execution with { RoleRevision = 2 } },
            intent with { Execution = intent.Execution with { RoleContentHash = Hash('e') } },
            intent with { Execution = intent.Execution with { LoopId = "loop-2" } },
            intent with { Execution = intent.Execution with { LoopRevisionId = "revision-2" } },
            intent with { Execution = intent.Execution with { DeclaredLoopRevision = 2 } },
            intent with { Execution = intent.Execution with { LoopPublicationHash = Hash('e') } },
            intent with { Authority = intent.Authority with { AuthorityProofHash = Hash('e') } },
            intent with { Authority = intent.Authority with { AuthorityProfileRevision = 2 } },
            intent with { Authority = intent.Authority with { AuthorityProfileHash = Hash('e') } },
            intent with { Authority = intent.Authority with { GrantRevision = 2 } },
            intent with { Authority = intent.Authority with { GrantHash = Hash('e') } },
            intent with { Authority = intent.Authority with { AuthorityBoundaryHash = Hash('e') } },
            intent with { Authority = intent.Authority with { CurrentAuthorityDecisionHash = Hash('e') } },
            intent with { Authority = intent.Authority with { DelegationEnvelopeHash = Hash('e') } },
            intent with { Effect = intent.Effect with { NodeAttempt = 2 } },
            intent with { Effect = intent.Effect with { EffectOperationId = "effect-operation-2" } },
            intent with { Effect = intent.Effect with { IdempotencyOperationId = "idempotency-2" } },
            intent with { Effect = intent.Effect with { EffectGeneration = 2 } },
            intent with { Effect = intent.Effect with { EffectAttemptHash = Hash('e') } },
            intent with { Capability = intent.Capability with { CapabilityVersion = "1.0.1" } },
            intent with { Capability = intent.Capability with { CapabilityDescriptorHash = Hash('e') } },
            intent with { Capability = intent.Capability with { CapabilityImplementationId = "http/other" } },
            intent with { Profile = new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.Applicable, "org.embodysense/profile", Hash('e')) },
            intent with { Registry = intent.Registry with { RegistryRevision = 8 } },
            intent with { Registry = intent.Registry with { ConsentReferenceId = "consent-2" } },
            intent with { Target = intent.Target with { TargetFingerprint = Hash('e') } },
            intent with { Target = intent.Target with { Purpose = "changed governed purpose" } },
        };
        var requests = substitutions.Select(candidate => fixture.Request with { LeaseIntent = Rehash(candidate) })
            .Append(fixture.Request with { RequestedScope = fixture.Request.RequestedScope with { Target = "substituted-private-target" } })
            .Append(fixture.Request with { Purpose = "changed governed purpose" })
            .Append(fixture.Request with { AuthorityProof = fixture.Request.AuthorityProof with { ProofId = Id("proof-2") } });

        foreach (var request in requests)
        {
            var isolated = Fixture(request: request, authoritativeRequest: fixture.Request);
            var result = await isolated.Broker.UseAsync(request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(0, isolated.Provider.UseCount);
            Assert.Equal(0, isolated.Provider.CallbackCount);
        }
    }

    [Fact]
    public async Task Unavailable_trusted_time_fails_closed_before_durable_or_provider_work()
    {
        var request = Request();
        var store = new InMemoryLeaseStore();
        var registry = new RecordingRegistry(RegistryRead(request));
        var provider = new RecordingProvider();
        var broker = new CredentialBroker(
            new AcceptingProofVerifier(),
            new CredentialLeaseCurrentAuthorityVerifier(new CurrentSource(request.LeaseIntent!)),
            store,
            new InMemoryBoundaryGate(store, registry),
            registry,
            registry,
            new ProviderResolver(provider, Provider()),
            new ThrowingTimeProvider());

        var result = await broker.UseAsync(request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialFailureCode.Unavailable, result.Failure!.Code);
        Assert.Null(result.LeaseAttempt);
        Assert.Equal(0, provider.UseCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Default_or_regressed_trusted_time_after_intent_publication_closes_not_redeemed_without_throwing_or_callback(bool defaultTime)
    {
        var request = Request();
        var store = new InMemoryLeaseStore();
        var registry = new RecordingRegistry(RegistryRead(request));
        var provider = new RecordingProvider();
        var broker = new CredentialBroker(
            new AcceptingProofVerifier(),
            new CredentialLeaseCurrentAuthorityVerifier(new CurrentSource(request.LeaseIntent!)),
            store,
            new InMemoryBoundaryGate(store, registry),
            registry,
            registry,
            new ProviderResolver(provider, Provider()),
            new SequenceTimeProvider(_now, defaultTime ? default : _now.AddTicks(-1)));

        var result = await broker.UseAsync(request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialFailureCode.Unavailable, result.Failure!.Code);
        Assert.Equal(CredentialLeasePhase.NotRedeemed, result.LeaseAttempt!.Current.Phase);
        Assert.Equal(0, provider.UseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Direct_or_delegated_authority_and_applicable_or_inapplicable_profile_require_exact_current_snapshot(
        bool delegated,
        bool profileApplicable)
    {
        var baseline = Request();
        var baseIntent = baseline.LeaseIntent!;
        var deadlines = baseIntent.Deadlines with
        {
            DelegationExpiresAtUtc = delegated ? _now.AddSeconds(30) : null,
            ProfileExpiresAtUtc = profileApplicable ? _now.AddSeconds(25) : null,
        };
        var intent = Rehash(baseIntent with
        {
            Authority = baseIntent.Authority with { DelegationEnvelopeHash = delegated ? Hash('f') : null },
            Profile = profileApplicable
                ? new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.Applicable, "org.embodysense/profile", Hash('e'))
                : new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.NotApplicable, null, null),
            Deadlines = deadlines,
            EffectiveExpiresAtUtc = CredentialLeaseContract.ComputeEffectiveExpiry(baseIntent.IssuedAtUtc, deadlines),
        });
        var request = baseline with { LeaseIntent = intent };
        var exact = Fixture(request: request);

        var accepted = await exact.Broker.UseAsync(request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.True(accepted.Succeeded);
        Assert.Equal(1, exact.Provider.UseCount);

        var driftedIntent = Rehash(intent with
        {
            Authority = intent.Authority with
            {
                DelegationEnvelopeHash = delegated ? Hash('d') : null,
                CurrentAuthorityDecisionHash = !delegated && !profileApplicable ? Hash('d') : intent.Authority.CurrentAuthorityDecisionHash,
            },
            Profile = profileApplicable
                ? intent.Profile with { ProfileHash = Hash('d') }
                : intent.Profile,
        });
        var driftedAuthority = Fixture(request: request, authoritativeRequest: request with { LeaseIntent = driftedIntent });

        var rejected = await driftedAuthority.Broker.UseAsync(request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.False(rejected.Succeeded);
        Assert.Equal(CredentialLeasePhase.NotRedeemed, rejected.LeaseAttempt!.Current.Phase);
        Assert.Equal(0, driftedAuthority.Provider.UseCount);
    }

    [Fact]
    public async Task Boundary_commit_followed_by_gate_failure_is_durably_ambiguous_and_never_calls_provider()
    {
        var fixture = Fixture(throwAfterBoundary: true);

        var result = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(CredentialFailureCode.OutcomeUncertain, result.Failure!.Code);
        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, result.LeaseAttempt!.Current.Phase);
        Assert.Equal(0, fixture.Provider.UseCount);
        Assert.Equal(0, fixture.Provider.CallbackCount);
    }

    [Fact]
    public async Task Boundary_commit_followed_by_closed_unavailable_result_is_read_back_as_ambiguous()
    {
        var fixture = Fixture(returnUnavailableAfterBoundary: true);

        var result = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialFailureCode.OutcomeUncertain, result.Failure!.Code);
        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, result.LeaseAttempt!.Current.Phase);
        Assert.Equal(0, fixture.Provider.UseCount);
    }

    [Theory]
    [InlineData("proof-denied")]
    [InlineData("current-unavailable")]
    [InlineData("provider-unconfigured")]
    [InlineData("provider-unhealthy")]
    public async Task Unavailable_or_denied_authority_and_provider_dependencies_fail_before_callback(string stage)
    {
        var provider = new RecordingProvider();
        if (stage == "provider-unhealthy")
        {
            provider.Health = CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Unavailable, CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
        var fixture = Fixture(
            provider,
            denyProof: stage == "proof-denied",
            currentStatus: stage == "current-unavailable" ? CredentialLeaseCurrentVerificationStatus.Unavailable : CredentialLeaseCurrentVerificationStatus.Authorized,
            unconfiguredProvider: stage == "provider-unconfigured");

        var result = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, provider.UseCount);
        Assert.Equal(0, provider.CallbackCount);
        Assert.Equal(CredentialLeasePhase.NotRedeemed, result.LeaseAttempt!.Current.Phase);
    }

    [Fact]
    public async Task Terminal_evidence_outage_retries_evidence_only_without_provider_replay()
    {
        var fixture = Fixture(failEvidence: true);

        var unavailable = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);
        fixture.Registry.FailEvidence = false;
        var replay = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialFailureCode.Unavailable, unavailable.Failure!.Code);
        Assert.Equal(CredentialLeasePhase.Redeemed, unavailable.LeaseAttempt!.Current.Phase);
        Assert.True(replay.Succeeded);
        Assert.Equal(1, fixture.Provider.UseCount);
        Assert.Single(fixture.Registry.Evidence);
    }

    [Theory]
    [InlineData("callback-throws")]
    [InlineData("provider-swallows-callback-failure")]
    [InlineData("provider-invokes-twice")]
    [InlineData("cancellation-after-callback")]
    public async Task Every_post_boundary_callback_or_cancellation_uncertainty_is_terminal_and_nonreplayable(string behavior)
    {
        var provider = new RecordingProvider
        {
            InvokeTwice = behavior == "provider-invokes-twice",
            BlockUse = behavior == "cancellation-after-callback",
        };
        var fixture = Fixture(provider);
        provider.SwallowCallbackFailure = behavior == "provider-swallows-callback-failure";
        var consumer = new RecordingConsumer { Throw = behavior is "callback-throws" or "provider-swallows-callback-failure" };
        using var cancellation = new CancellationTokenSource();
        var use = fixture.Broker.UseAsync(fixture.Request, Id("run-1"), consumer, cancellation.Token).AsTask();
        if (behavior == "cancellation-after-callback")
        {
            await provider.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
        }

        var result = await use;
        var replay = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, result.LeaseAttempt!.Current.Phase);
        Assert.Equal(CredentialFailureCode.OutcomeUncertain, result.Failure!.Code);
        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, replay.LeaseAttempt!.Current.Phase);
        Assert.Equal(1, provider.UseCount);
    }

    [Fact]
    public async Task Provider_return_while_callback_is_still_active_is_ambiguous_and_never_replayed()
    {
        var consumer = new BlockingConsumer();
        var provider = new RecordingProvider
        {
            ReturnWhileCallbackActive = true,
            WaitBeforeReturn = consumer.Entered.Task,
        };
        var fixture = Fixture(provider);

        var result = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), consumer, CancellationToken.None);
        consumer.Release.TrySetResult();
        await provider.BackgroundCallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replay = await fixture.Broker.UseAsync(fixture.Request, Id("run-1"), new RecordingConsumer(), CancellationToken.None);

        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, result.LeaseAttempt!.Current.Phase);
        Assert.Equal(CredentialFailureCode.OutcomeUncertain, result.Failure!.Code);
        Assert.Equal(CredentialLeasePhase.RedemptionAmbiguous, replay.LeaseAttempt!.Current.Phase);
        Assert.Equal(1, provider.UseCount);
        Assert.Equal(1, consumer.Count);
    }

    private static BrokerFixture Fixture(
        RecordingProvider? provider = null,
        CredentialUseRequest? request = null,
        CredentialUseRequest? authoritativeRequest = null,
        bool throwAfterBoundary = false,
        bool returnUnavailableAfterBoundary = false,
        bool denyProof = false,
        CredentialLeaseCurrentVerificationStatus currentStatus = CredentialLeaseCurrentVerificationStatus.Authorized,
        bool unconfiguredProvider = false,
        bool failEvidence = false)
    {
        request ??= Request();
        authoritativeRequest ??= request;
        provider ??= new RecordingProvider();
        var store = new InMemoryLeaseStore();
        var registry = new RecordingRegistry(RegistryRead(authoritativeRequest)) { FailEvidence = failEvidence };
        var verifier = new CredentialLeaseCurrentAuthorityVerifier(new CurrentSource(authoritativeRequest.LeaseIntent!, currentStatus));
        var resolver = new ProviderResolver(provider, Provider(), unconfiguredProvider);
        var gate = new InMemoryBoundaryGate(store, registry, throwAfterBoundary, returnUnavailableAfterBoundary);
        var broker = new CredentialBroker(denyProof ? new RejectingProofVerifier() : new AcceptingProofVerifier(), verifier, store, gate, registry, registry, resolver, new FixedTimeProvider(_now));
        return new BrokerFixture(broker, request, store, registry, provider);
    }

    private static CredentialUseRequest Request()
    {
        var identity = new CapabilityDescriptorIdentity(CapabilityId("org.embodysense/http/call"), CapabilityVersion("1.0.0"), CapabilityHash(Hash('c')));
        var implementation = new CapabilityImplementationIdentity(CapabilityProvider("org.embodysense"), "http/call");
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, implementation, "example-api", "private-target-not-persisted", "read", "actor-1", _now.AddMinutes(-1), _now.AddMinutes(1));
        var binding = new CredentialCapabilityBinding(1, Reference(), Requirement("provider-token"), identity, implementation, scope);
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        var proof = new CredentialAuthorityProof(1, Id("proof-1"), binding.ReferenceId, bindingHash!, scope, "actor-1", Id("run-1"), 1, _now.AddMinutes(-1), _now.AddMinutes(1), CredentialProvider("org.embodysense.authority"), CredentialHash(Hash('f')));
        Assert.True(CredentialContractJson.TryHash(proof, out var proofHash, out _));
        var deadlines = new CredentialLeaseDeadlines(proof.ExpiresAtUtc, null, scope.NotAfterUtc, null, null, null, null, null);
        var intent = new CredentialLeaseIntent(
            1,
            "lease-1",
            "credential-use-1",
            1,
            new CredentialLeaseExecutionScope("workspace-1", "actor-1", Hash('1'), Hash('2'), Hash('3'), "run-1", "graph-1", "revision-1", Hash('4'), 1, "role-1", 1, Hash('5'), "loop-1", "revision-1", 1, Hash('6')),
            new CredentialLeaseAuthorityScope(proof.ProofId.Value, proofHash!.Value, "authority-1", 1, Hash('7'), "grant-1", 1, Hash('8'), Hash('9'), Hash('a'), null),
            new CredentialLeaseEffectScope("node-1", 1, "effect-1", "effect-operation-1", "idempotency-1", 1, Hash('b'), 5),
            new CredentialLeaseCapabilityScope(identity.Id.Value, identity.Version.Value, identity.Hash.Value, implementation.ProviderId.Value, implementation.ImplementationId, binding.Requirement.Name),
            new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.NotApplicable, null, null),
            new CredentialLeaseRegistryScope(binding.ReferenceId.Value, bindingHash!.Value, 7, "consent-1", Provider().Value),
            new CredentialLeaseTargetScope(scope.Service!, CredentialLeaseContract.ComputeTargetFingerprint(scope.Service!, System.Text.Encoding.UTF8.GetBytes(scope.Target!)), scope.OperationClass!, "governed provider use"),
            _now.AddSeconds(-1),
            deadlines,
            CredentialLeaseContract.ComputeEffectiveExpiry(_now.AddSeconds(-1), deadlines),
            string.Empty);
        return new CredentialUseRequest(binding, bindingHash, scope, proof, CredentialLeaseContract.ApplyIntentHash(intent), "governed provider use");
    }

    private static CredentialRegistryReadResult RegistryRead(CredentialUseRequest request)
    {
        var reference = new CredentialReference(1, request.Binding.ReferenceId, "api-token", CredentialLifecycleStatus.Active, "actor-1", "provider access", Provider(), _now.AddDays(-1), _now, _now.AddDays(1), new Dictionary<string, string>());
        var entry = new CredentialRegistryEntry(reference, request.Binding, request.BindingHash, Id("consent-1"), CredentialProviderHealthStatus.Available, 5, Id("registry-operation-1"), true);
        return new CredentialRegistryReadResult(7, [entry], [], [], [], null);
    }

    private static CredentialLeaseAttemptHistory PreparedHistory(CredentialLeaseIntent intent)
        => CredentialLeaseContract.CreateHistory(intent, [CredentialLeaseContract.Prepare(intent, _now)]);

    private static CredentialLeaseAttemptHistory Append(CredentialLeaseAttemptHistory history, CredentialLeasePhase phase, DateTimeOffset at, string? authority = null, string? registry = null)
    {
        CredentialFailureCode? failure = phase == CredentialLeasePhase.NotRedeemed ? CredentialFailureCode.Unavailable : phase == CredentialLeasePhase.RedemptionAmbiguous ? CredentialFailureCode.OutcomeUncertain : null;
        var next = CredentialLeaseContract.Advance(history.Intent, history.Current, phase, at, authority, registry, failure);
        return CredentialLeaseContract.CreateHistory(history.Intent, [.. history.Versions, next]);
    }

    private static CredentialLeaseIntent Rehash(CredentialLeaseIntent intent) => CredentialLeaseContract.ApplyIntentHash(intent with { ContentHash = string.Empty });
    private static string Hash(char character) => "sha256:" + new string(character, 64);

    private sealed record BrokerFixture(CredentialBroker Broker, CredentialUseRequest Request, InMemoryLeaseStore Store, RecordingRegistry Registry, RecordingProvider Provider);

    private sealed class AcceptingProofVerifier : ICredentialAuthorityProofVerifier
    {
        public ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CredentialContractId currentRunId, CancellationToken cancellationToken)
            => ValueTask.FromResult(CredentialAuthorityVerificationResult.Accept());
    }

    private sealed class RejectingProofVerifier : ICredentialAuthorityProofVerifier
    {
        public ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CredentialContractId currentRunId, CancellationToken cancellationToken)
            => ValueTask.FromResult(CredentialAuthorityVerificationResult.Reject(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized)));
    }

    private sealed class CurrentSource(CredentialLeaseIntent expected, CredentialLeaseCurrentVerificationStatus status = CredentialLeaseCurrentVerificationStatus.Authorized) : ICredentialLeaseCurrentAuthoritySnapshotSource
    {
        public Task<CredentialLeaseCurrentAuthoritySnapshot> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(status == CredentialLeaseCurrentVerificationStatus.Authorized
                ? new CredentialLeaseCurrentAuthoritySnapshot(status, expected, Hash('d'))
                : new CredentialLeaseCurrentAuthoritySnapshot(status));
    }

    private sealed class ProviderResolver(RecordingProvider provider, CredentialProviderId providerId, bool unconfigured = false) : ICredentialValueProviderResolver
    {
        public Task<CredentialValueProviderResolution> ResolveAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId requestedProviderId, CancellationToken cancellationToken = default)
            => Task.FromResult(!unconfigured && requestedProviderId.Equals(providerId)
                ? new CredentialValueProviderResolution(CredentialValueProviderResolutionStatus.Resolved, providerId, provider)
                : new CredentialValueProviderResolution(CredentialValueProviderResolutionStatus.NotConfigured));
    }

    private sealed class InMemoryBoundaryGate(InMemoryLeaseStore store, RecordingRegistry registry, bool throwAfterBoundary = false, bool returnUnavailableAfterBoundary = false) : ICredentialLeaseRedemptionGate
    {
        public async Task<CredentialLeaseBoundaryResult> TryEnterAsync(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, DateTimeOffset trustedNowUtc, CancellationToken cancellationToken = default)
        {
            var match = CredentialLeaseRegistryMatcher.Match(authorized.Intent, await registry.ReadAsync(cancellationToken), trustedNowUtc);
            if (!match.Succeeded)
            {
                var denied = CredentialLeaseContract.Advance(authorized.Intent, authorized.Current, CredentialLeasePhase.NotRedeemed, trustedNowUtc, failureCode: match.Failure!.Code);
                var deniedHistory = CredentialLeaseContract.CreateHistory(authorized.Intent, [.. authorized.Versions, denied]);
                var deniedCommit = await store.CompareExchangeAsync(authorized.Current.ContentHash, deniedHistory, lease, cancellationToken);
                return new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.NotRedeemed, deniedCommit.History);
            }

            var boundary = CredentialLeaseContract.Advance(authorized.Intent, authorized.Current, CredentialLeasePhase.RedemptionBoundaryReached, trustedNowUtc);
            var replacement = CredentialLeaseContract.CreateHistory(authorized.Intent, [.. authorized.Versions, boundary]);
            var commit = await store.CompareExchangeAsync(authorized.Current.ContentHash, replacement, lease, cancellationToken);
            if (throwAfterBoundary)
            {
                throw new IOException("simulated response loss after durable boundary publication");
            }
            if (returnUnavailableAfterBoundary)
            {
                return new CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus.Unavailable, authorized);
            }
            return new CredentialLeaseBoundaryResult(commit.Status is CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed ? CredentialLeaseBoundaryStatus.Entered : CredentialLeaseBoundaryStatus.Conflict, commit.History);
        }
    }

    private sealed class InMemoryLeaseStore : ICredentialLeaseAttemptStore
    {
        private readonly object _sync = new();
        private CredentialLeaseAttemptHistory? _history;
        private Owner? _owner;

        public Task<CredentialLeaseAttemptStoreResult> BeginAsync(CredentialLeaseIntent intent, CredentialLeaseAttemptVersion prepared, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_history is null)
                {
                    _history = CredentialLeaseContract.CreateHistory(intent, [prepared]);
                    _owner = new Owner(this);
                    return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Created, _history, _owner));
                }
                if (!string.Equals(_history.Intent.ContentHash, intent.ContentHash, StringComparison.Ordinal))
                {
                    return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Conflict, _history));
                }
                if (_history.Current.Phase is CredentialLeasePhase.NotRedeemed or CredentialLeasePhase.Redeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous)
                {
                    return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Replayed, _history));
                }
                if (_owner is not null)
                {
                    return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.OperationInProgress, _history));
                }
                _owner = new Owner(this);
                return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Replayed, _history, _owner));
            }
        }

        public Task<CredentialLeaseAttemptStoreResult> CompareExchangeAsync(string expectedContentHash, CredentialLeaseAttemptHistory replacement, ICredentialLeaseAttemptLease lease, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_history is null || lease is not Owner owner || !ReferenceEquals(owner, _owner) || owner.Disposed || !string.Equals(_history.Current.ContentHash, expectedContentHash, StringComparison.Ordinal))
                {
                    return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Conflict, _history));
                }
                if (CredentialLeaseContract.Validate(replacement) is not null
                    || replacement.Versions.Count != _history.Versions.Count + 1
                    || !_history.Versions.SequenceEqual(replacement.Versions.Take(_history.Versions.Count))
                    || !CredentialLeaseContract.IsDirectSuccessor(_history.Intent, _history.Current, replacement.Current))
                {
                    return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Conflict, _history));
                }
                _history = replacement;
                return Task.FromResult(new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Created, _history));
            }
        }

        public Task<CredentialLeaseAttemptStoreResult> ResumeAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(_history is null ? new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.NotFound) : new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Replayed, _history));

        public Task<CredentialLeaseAttemptStoreResult> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default)
            => Task.FromResult(_history is null ? new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.NotFound) : new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Replayed, _history));

        internal void Seed(CredentialLeaseAttemptHistory history)
        {
            lock (_sync)
            {
                _history = history;
                _owner = null;
            }
        }

        private void Release(Owner owner)
        {
            lock (_sync)
            {
                if (ReferenceEquals(owner, _owner))
                {
                    _owner = null;
                }
            }
        }

        private sealed class Owner(InMemoryLeaseStore store) : ICredentialLeaseAttemptLease
        {
            internal bool Disposed { get; private set; }

            public void Dispose()
            {
                if (!Disposed)
                {
                    Disposed = true;
                    store.Release(this);
                }
            }
        }
    }

    private sealed class RecordingRegistry(CredentialRegistryReadResult read) : ICredentialRegistryStore
    {
        private readonly List<CredentialUseEvidence> _evidence = [];
        internal IReadOnlyList<CredentialUseEvidence> Evidence => _evidence;
        internal bool FailEvidence { get; set; }
        internal bool FailReservation { get; set; }

        public ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialActorAuthentication.AuthenticatedUser);
        public ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialReferenceLookupResult.Found(read.Entries[0].Reference));
        public Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(read);
        public Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken)
            => ValueTask.FromResult(FailReservation ? CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable)) : CredentialEvidenceWriteResult.Success());
        public ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken)
        {
            if (FailEvidence)
            {
                return ValueTask.FromResult(CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
            }
            var existing = _evidence.SingleOrDefault(item => item.EvidenceId.Equals(evidence.EvidenceId));
            if (existing is null)
            {
                _evidence.Add(evidence);
                return ValueTask.FromResult(CredentialEvidenceWriteResult.Success());
            }
            return ValueTask.FromResult(existing == evidence ? CredentialEvidenceWriteResult.Success() : CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict)));
        }
    }

    private sealed class RecordingProvider : ICredentialValueProvider
    {
        internal CredentialProviderResult Result { get; set; } = CredentialProviderResult.Success();
        internal bool InvokeThenFail { get; set; }
        internal bool InvokeTwice { get; set; }
        internal bool BlockUse { get; set; }
        internal bool SwallowCallbackFailure { get; set; }
        internal bool ReturnWhileCallbackActive { get; set; }
        internal Task? WaitBeforeReturn { get; set; }
        internal CredentialProviderHealthResult Health { get; set; } = CredentialProviderHealthResult.Available();
        internal int UseCount { get; private set; }
        internal int CallbackCount { get; private set; }
        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseUse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BackgroundCallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(Health);

        public async ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken)
        {
            UseCount++;
            if (ReturnWhileCallbackActive)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        trustedConsumer.Use("ephemeral"u8);
                    }
                    finally
                    {
                        BackgroundCallbackCompleted.TrySetResult();
                    }
                });
                await (WaitBeforeReturn ?? Task.CompletedTask).WaitAsync(cancellationToken);
                return Result;
            }
            if (Result.Succeeded || InvokeThenFail)
            {
                try
                {
                    trustedConsumer.Use("ephemeral"u8);
                    CallbackCount++;
                    CallbackEntered.TrySetResult();
                }
                catch when (SwallowCallbackFailure)
                {
                }
                if (InvokeTwice)
                {
                    trustedConsumer.Use("ephemeral"u8);
                }
            }
            if (BlockUse)
            {
                await ReleaseUse.Task.WaitAsync(cancellationToken);
            }
            return InvokeThenFail ? CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.CallbackFailed)) : Result;
        }
    }

    private sealed class RecordingConsumer : ICredentialTrustedUseConsumer
    {
        internal int Count { get; private set; }
        internal bool Throw { get; set; }
        public void Use(ReadOnlySpan<byte> credential)
        {
            Count++;
            if (Throw)
            {
                throw new InvalidOperationException("simulated trusted consumer failure");
            }
        }
    }

    private sealed class BlockingConsumer : ICredentialTrustedUseConsumer
    {
        internal int Count { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Use(ReadOnlySpan<byte> credential)
        {
            Count++;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("trusted-clock-unavailable");
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }

    private static CredentialContractId Id(string value) { Assert.True(CredentialContractId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CredentialReferenceId Reference() { Assert.True(CredentialReferenceId.TryParse("reference-1", out var parsed, out _)); return parsed!; }
    private static CredentialProviderId Provider() => CredentialProvider("org.embodysense.windows");
    private static CredentialProviderId CredentialProvider(string value) { Assert.True(CredentialProviderId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CredentialContractHash CredentialHash(string value) { Assert.True(CredentialContractHash.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityId CapabilityId(string value) { Assert.True(EmbodySense.Core.Common.Capabilities.CapabilityId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityVersion CapabilityVersion(string value) { Assert.True(EmbodySense.Core.Common.Capabilities.CapabilityVersion.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityDescriptorHash CapabilityHash(string value) { Assert.True(CapabilityDescriptorHash.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilityProviderId CapabilityProvider(string value) { Assert.True(CapabilityProviderId.TryParse(value, out var parsed, out _)); return parsed!; }
    private static CapabilitySecretRequirement Requirement(string value) { Assert.True(CapabilitySecretRequirement.TryParse(value, out var parsed, out _)); return parsed!; }
}
