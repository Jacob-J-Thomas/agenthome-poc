using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Tests.Inference.Profiles;

public sealed class GovernedModelAttemptAdmissionServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 12, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("run-substituted", 1)]
    [InlineData("run-default", 2)]
    public async Task Exact_run_and_generation_binding_rejects_substitution_before_ledger(string runId, long generation)
    {
        var fixture = Fixture();
        var request = fixture.Request with { RunId = runId, ExecutionGeneration = generation };

        var result = await fixture.Service.ReserveAsync(request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Invalid, result.Status);
        Assert.Equal(0, fixture.Ledger.ReserveCalls);
        Assert.Equal(0, fixture.Authority.Calls);
    }

    [Theory]
    [InlineData(GovernedModelInferencePayloadLimits.MaxMessages, GovernedModelAttemptAdmissionStatus.Reserved, 1)]
    [InlineData(GovernedModelInferencePayloadLimits.MaxMessages + 1, GovernedModelAttemptAdmissionStatus.Invalid, 0)]
    public async Task Message_count_has_an_exact_public_admission_bound(
        int messageCount,
        GovernedModelAttemptAdmissionStatus expected,
        int expectedReserveCalls)
    {
        var fixture = Fixture();
        var inference = new LlmInferenceRequest(
            Enumerable.Range(0, messageCount).Select(index => LlmMessage.User($"message-{index}")).ToArray());

        var result = await fixture.Service.ReserveAsync(fixture.Request, inference);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedReserveCalls, fixture.Ledger.ReserveCalls);
        Assert.Equal(expectedReserveCalls, fixture.Authority.Calls);
    }

    [Theory]
    [InlineData(GovernedModelInferencePayloadLimits.MaxAggregateCharacters, GovernedModelAttemptAdmissionStatus.Reserved, 1)]
    [InlineData(GovernedModelInferencePayloadLimits.MaxAggregateCharacters + 1, GovernedModelAttemptAdmissionStatus.Invalid, 0)]
    public async Task Aggregate_payload_characters_have_an_exact_public_admission_bound(
        int characterCount,
        GovernedModelAttemptAdmissionStatus expected,
        int expectedReserveCalls)
    {
        var fixture = Fixture();

        var result = await fixture.Service.ReserveAsync(
            fixture.Request,
            LlmInferenceRequest.FromUserText(new string('x', characterCount)));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedReserveCalls, fixture.Ledger.ReserveCalls);
        Assert.Equal(expectedReserveCalls, fixture.Authority.Calls);
    }

    [Theory]
    [InlineData(0, GovernedModelAttemptAdmissionStatus.Reserved, 1)]
    [InlineData(1, GovernedModelAttemptAdmissionStatus.Invalid, 0)]
    public async Task Aggregate_payload_utf8_bytes_have_an_exact_public_admission_bound(
        int excessUtf8Bytes,
        GovernedModelAttemptAdmissionStatus expected,
        int expectedReserveCalls)
    {
        var fixture = Fixture();
        var threeByteCharacters = GovernedModelInferencePayloadLimits.MaxAggregateUtf8Bytes / 3;
        var remainingAscii = GovernedModelInferencePayloadLimits.MaxAggregateUtf8Bytes - (threeByteCharacters * 3) + excessUtf8Bytes;
        var content = new string('\u6f22', threeByteCharacters) + new string('x', remainingAscii);

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText(content));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedReserveCalls, fixture.Ledger.ReserveCalls);
        Assert.Equal(expectedReserveCalls, fixture.Authority.Calls);
    }

    [Theory]
    [InlineData(GovernedModelInferencePayloadLimits.MaxTrustedInstructions, GovernedModelAttemptAdmissionStatus.Reserved, 1)]
    [InlineData(GovernedModelInferencePayloadLimits.MaxTrustedInstructions + 1, GovernedModelAttemptAdmissionStatus.Invalid, 0)]
    public async Task Trusted_instruction_count_has_an_exact_public_admission_bound(
        int instructionCount,
        GovernedModelAttemptAdmissionStatus expected,
        int expectedReserveCalls)
    {
        var fixture = Fixture();
        var governance = new EmbodySenseDeveloperInstructionSet("v1", "governance", Hash('a'));
        var trusted = Enumerable.Range(0, instructionCount)
            .Select(index => new EmbodySenseTrustedInstruction($"source-{index}", "instruction"))
            .ToArray();
        var inference = new LlmInferenceRequest(
            [LlmMessage.User("test")],
            instructionContext: new LlmInferenceInstructionContext(governance, trusted));

        var result = await fixture.Service.ReserveAsync(fixture.Request, inference);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedReserveCalls, fixture.Ledger.ReserveCalls);
        Assert.Equal(expectedReserveCalls, fixture.Authority.Calls);
    }

    [Fact]
    public async Task Current_profile_after_first_catalog_page_is_exactly_revalidated_and_reserved()
    {
        var fixture = Fixture(multiPage: true);

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Reserved, result.Status);
        Assert.Equal(fixture.Pin.ContentHash, result.Primary?.ContentHash);
        Assert.Equal(2, fixture.Catalog.ReadCalls);
        Assert.Equal(1, fixture.Ledger.ReserveCalls);
        Assert.Equal(1, fixture.Authority.Calls);
    }

    [Fact]
    public async Task Recovered_catalog_is_not_current_execution_authority()
    {
        var fixture = Fixture(catalogStatus: CapabilityCatalogReadStatus.RecoveredLastProved);

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Unavailable, result.Status);
        Assert.Equal(0, fixture.Ledger.ReserveCalls);
    }

    [Fact]
    public async Task Atomic_aggregate_budget_denial_performs_no_reservation()
    {
        var fixture = Fixture(reserveStatus: GovernedModelUsageLedgerAppendStatus.BudgetExhausted);

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.BudgetExhausted, result.Status);
        Assert.Equal(1, fixture.Ledger.ReserveCalls);
        Assert.Empty(fixture.Ledger.History);
    }

    [Fact]
    public async Task Append_outcome_is_not_trusted_without_exact_durable_reread()
    {
        var fixture = Fixture(reserveStatus: GovernedModelUsageLedgerAppendStatus.AlreadyPresent, retainOnReserve: false);

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Conflict, result.Status);
        Assert.Equal(2, fixture.Ledger.ReadCalls);
        Assert.Equal(1, fixture.Ledger.ReserveCalls);
    }

    [Fact]
    public async Task Advanced_attempt_history_revalidates_current_authority_but_never_rereserves_or_becomes_dispatchable()
    {
        var fixture = Fixture();
        var initial = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));
        var reservation = Assert.IsType<GovernedModelUsageLedgerEntry>(initial.ReservationEntry);
        var dispatch = GovernedModelUsageLedgerEntry.Create(1, reservation.Identity, 2, GovernedModelUsageLedgerPhase.DispatchBoundaryReached, reservation.Reservation, null, null, null, true, Hash('f'), reservation.ContentHash, _now.AddSeconds(1));
        fixture.Ledger.History.Add(dispatch);
        var reserveCalls = fixture.Ledger.ReserveCalls;
        var authorityCalls = fixture.Authority.Calls;

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, result.Status);
        Assert.Equal(reserveCalls, fixture.Ledger.ReserveCalls);
        Assert.Equal(authorityCalls + 1, fixture.Authority.Calls);
    }

    [Fact]
    public async Task Advanced_attempt_history_survives_current_authority_revocation_as_post_dispatch_evidence()
    {
        var fixture = Fixture();
        var initial = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));
        var reservation = Assert.IsType<GovernedModelUsageLedgerEntry>(initial.ReservationEntry);
        fixture.Ledger.History.Add(GovernedModelUsageLedgerEntry.Create(
            1,
            reservation.Identity,
            2,
            GovernedModelUsageLedgerPhase.DispatchBoundaryReached,
            reservation.Reservation,
            null,
            null,
            null,
            true,
            Hash('f'),
            reservation.ContentHash,
            _now.AddSeconds(1)));
        fixture.Authority.Status = ModelAttemptAuthorityStatus.Denied;
        var reserveCalls = fixture.Ledger.ReserveCalls;

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, result.Status);
        Assert.True(result.ProviderDispatchMayHaveOccurred);
        Assert.Equal(GovernedModelUsageLedgerPhase.DispatchBoundaryReached, result.CurrentEntry?.Phase);
        Assert.Equal(reserveCalls, fixture.Ledger.ReserveCalls);
    }

    [Fact]
    public async Task Retained_reservation_is_proved_not_started_when_current_authority_is_revoked()
    {
        var fixture = Fixture();
        var initial = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));
        Assert.Equal(GovernedModelAttemptAdmissionStatus.Reserved, initial.Status);
        fixture.Authority.Status = ModelAttemptAuthorityStatus.Denied;
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);

        var result = await Execute(
            execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Ineligible, result.AdmissionStatus);
        Assert.Null(result.Response);
        Assert.False(result.ProviderDispatchMayHaveOccurred);
        Assert.Equal(0, transport.ResolverCalls);
        Assert.Equal(0, transport.Writes);
        Assert.Equal(GovernedModelUsageLedgerPhase.DispatchProvedNotStarted, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Malformed_retained_run_history_fails_closed_before_current_policy_or_reservation()
    {
        var fixture = Fixture();
        var initial = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));
        var reservation = Assert.IsType<GovernedModelUsageLedgerEntry>(initial.ReservationEntry);
        fixture.Ledger.RunReadOverride = new GovernedModelUsageLedgerRunReadResult(
            GovernedModelUsageLedgerReadStatus.Found,
            [reservation],
            0);
        var catalogCalls = fixture.Catalog.ReadCalls;
        var authorityCalls = fixture.Authority.Calls;
        var reserveCalls = fixture.Ledger.ReserveCalls;

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Unavailable, result.Status);
        Assert.Equal(catalogCalls, fixture.Catalog.ReadCalls);
        Assert.Equal(authorityCalls, fixture.Authority.Calls);
        Assert.Equal(reserveCalls, fixture.Ledger.ReserveCalls);
    }

    [Fact]
    public async Task Conflicting_payload_cannot_release_another_pre_dispatch_reservation()
    {
        var fixture = Fixture();
        var initial = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("original"));
        Assert.Equal(GovernedModelAttemptAdmissionStatus.Reserved, initial.Status);
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);

        var result = await Execute(
            execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("substituted")));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Conflict, result.AdmissionStatus);
        Assert.Null(result.Response);
        Assert.False(result.ProviderDispatchMayHaveOccurred);
        Assert.Equal(0, transport.ResolverCalls);
        Assert.Equal(0, transport.Writes);
        Assert.Single(fixture.Ledger.History);
        Assert.Equal(GovernedModelUsageLedgerPhase.ReservationCommitted, fixture.Ledger.History[0].Phase);
    }

    [Theory]
    [InlineData(ModelInferenceDataPostureStatus.Unavailable, GovernedModelAttemptAdmissionStatus.Unavailable)]
    public async Task Unavailable_input_evidence_is_not_mislabeled_as_policy_ineligibility(ModelInferenceDataPostureStatus posture, GovernedModelAttemptAdmissionStatus expected)
    {
        var fixture = Fixture(dataStatus: posture);

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, fixture.Ledger.ReserveCalls);
    }

    [Fact]
    public async Task Graph_authored_public_label_cannot_replace_server_classification_of_the_actual_payload()
    {
        var fixture = Fixture(dataPostureSource: new UnavailableDataPostureSource());

        var result = await fixture.Service.ReserveAsync(fixture.Request, LlmInferenceRequest.FromUserText("browser labeled this public"));

        Assert.True(fixture.Request.RoutingAdmission.Entries.Single().HasAuthoredInputClassification);
        Assert.Equal("public", fixture.Request.RoutingAdmission.Entries.Single().AuthoredInputDataClasses.Single().Value);
        Assert.Equal(GovernedModelAttemptAdmissionStatus.Unavailable, result.Status);
        Assert.Equal(0, fixture.Ledger.ReserveCalls);
    }

    [Fact]
    public async Task Concurrent_primary_callers_commit_exactly_one_provider_write()
    {
        var fixture = Fixture();
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);
        var request = new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test"));

        var results = await Task.WhenAll(Execute(execution, request), Execute(execution, request));

        Assert.Equal(1, transport.Writes);
        Assert.Single(results, result => result.Response is not null);
        Assert.Single(results, result => result.AdmissionStatus == GovernedModelAttemptAdmissionStatus.AlreadyAdvanced);
    }

    [Fact]
    public async Task Hostile_client_double_callback_commits_exactly_one_provider_write()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(doubleCallback: true);
        var execution = Execution(fixture, transport);

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, result.AdmissionStatus);
        Assert.Equal(1, transport.Writes);
        Assert.Equal(3, fixture.Ledger.History.Count);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Ambiguous_dispatch_append_authenticates_unique_owner_before_one_provider_write()
    {
        var fixture = Fixture();
        fixture.Ledger.ThrowAfterDispatchAppend = true;
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.NotNull(result.Response);
        Assert.Equal(1, transport.Writes);
        Assert.Contains(fixture.Ledger.History, entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchBoundaryReached);
    }

    [Fact]
    public async Task Post_boundary_provider_exception_durably_retains_unknown_attention()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(throwAfterWrite: true);
        var execution = Execution(fixture, transport);

        await Assert.ThrowsAsync<IOException>(() => Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test"))));

        Assert.Equal(1, transport.Writes);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
        Assert.True(fixture.Ledger.History[^1].UsageUnknown);
    }

    [Fact]
    public async Task Malformed_usage_after_boundary_durably_retains_unknown_attention()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(returnNullResponse: true);
        var execution = Execution(fixture, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test"))));

        Assert.Equal(1, transport.Writes);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Lease_disposal_failure_cannot_replace_success_or_weaken_conclusive_usage()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(throwOnDispose: true);
        var execution = Execution(fixture, transport);

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.NotNull(result.Response);
        Assert.Equal(1, transport.Writes);
        Assert.Equal(GovernedModelUsageLedgerPhase.Reconciled, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Resolver_receives_every_exact_hard_dimension_and_server_binds_correlation()
    {
        var fixture = Fixture(fullBudget: true);
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);
        var broker = new StubToolBroker();

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test"), broker));

        Assert.NotNull(result.Response);
        var envelope = Assert.IsType<ExactModelProfileInferenceClientRequest>(transport.ResolverRequest);
        Assert.Equal(10, envelope.Reservation.InputTokens.Maximum);
        Assert.Equal(10, envelope.Reservation.OutputTokens.Maximum);
        Assert.Equal(10, envelope.Reservation.CachedTokens.Maximum);
        Assert.Equal(10, envelope.Reservation.TotalTokens.Maximum);
        Assert.Equal("USD", envelope.Reservation.MonetaryCost.Currency);
        Assert.Equal(100, envelope.Reservation.MonetaryCost.MaximumMicros);
        Assert.Equal(fixture.Request.RoutingAdmission.ContentHash, envelope.RoutingAdmissionHash);
        Assert.Equal(fixture.Request.AttemptOperationId, envelope.ProviderAttemptId);
        Assert.Equal(envelope.AttemptIdentity.ContentHash, envelope.ProviderCorrelationId);
        Assert.Same(broker, envelope.ToolBroker);
        Assert.Equal(10, transport.InferenceRequest?.Options.MaxOutputTokenCount);
        Assert.Equal(envelope.ProviderAttemptId, transport.InferenceRequest?.Correlation?.ProviderAttemptId);
        Assert.Equal(envelope.ProviderCorrelationId, transport.InferenceRequest?.Correlation?.ProviderCorrelationId);
    }

    [Fact]
    public async Task Retry_usage_ceiling_narrows_the_durable_model_reservation_before_provider_transport()
    {
        var fixture = Fixture(fullBudget: true);
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);
        var retryCeiling = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Bounded(4),
            GovernedModelMonetaryLimit.Bounded("USD", 40));
        var request = fixture.Request with { RetryUsageCeiling = retryCeiling };

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(request, LlmInferenceRequest.FromUserText("test")));

        Assert.NotNull(result.Response);
        var envelope = Assert.IsType<ExactModelProfileInferenceClientRequest>(transport.ResolverRequest);
        Assert.Equal(4, envelope.Reservation.TotalTokens.Maximum);
        Assert.Equal("USD", envelope.Reservation.MonetaryCost.Currency);
        Assert.Equal(40, envelope.Reservation.MonetaryCost.MaximumMicros);
        Assert.NotEqual(fixture.Request.RoutingAdmission.Entries.Single().Requirements.Budget.ContentHash, envelope.AttemptIdentity.BudgetPolicyHash);
        Assert.Equal(envelope.AttemptIdentity.BudgetPolicyHash, envelope.BudgetPolicy.ContentHash);
        Assert.Equal(envelope.BudgetPolicy.ContentHash, fixture.Ledger.LastReservationRequest?.BudgetPolicy.ContentHash);
        Assert.Equal(envelope.Reservation.ContentHash, fixture.Ledger.History[0].Reservation?.ContentHash);
    }

    [Fact]
    public async Task Retry_usage_ceiling_rejects_an_incompatible_monetary_currency_before_any_durable_reservation()
    {
        var fixture = Fixture(fullBudget: true);
        var incompatible = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Bounded("EUR", 40));
        var request = fixture.Request with { RetryUsageCeiling = incompatible };

        var result = await fixture.Service.ReserveAsync(request, LlmInferenceRequest.FromUserText("test"));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Invalid, result.Status);
        Assert.Equal(0, fixture.Ledger.ReserveCalls);
        Assert.Equal(0, fixture.Authority.Calls);
    }

    [Fact]
    public async Task Changed_provider_payload_cannot_replay_prior_classification_or_reservation()
    {
        var fixture = Fixture();
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);

        var first = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("first payload")));
        var changed = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("changed payload")));

        Assert.NotNull(first.Response);
        Assert.Null(changed.Response);
        Assert.Equal(GovernedModelAttemptAdmissionStatus.Conflict, changed.AdmissionStatus);
        Assert.True(changed.ProviderDispatchMayHaveOccurred);
        Assert.Equal(1, transport.Writes);
    }

    [Fact]
    public async Task Provider_started_callback_runs_after_durable_dispatch_and_immediately_before_transport()
    {
        var fixture = Fixture();
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);
        var callbackCalls = 0;

        var result = await Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            providerRequestStarted: () =>
            {
                callbackCalls++;
                Assert.Equal(GovernedModelUsageLedgerPhase.DispatchBoundaryReached, fixture.Ledger.History[^1].Phase);
                Assert.Equal(0, transport.Writes);
            });

        Assert.NotNull(result.Response);
        Assert.Equal(1, callbackCalls);
        Assert.Equal(1, transport.Writes);
    }

    [Fact]
    public async Task Provider_started_callback_failure_parks_attention_without_a_false_dispatchable_reservation()
    {
        var fixture = Fixture();
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);

        await Assert.ThrowsAsync<IOException>(() => Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            providerRequestStarted: () => throw new IOException("owner callback failed")));

        Assert.Equal(0, transport.Writes);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
        Assert.True(fixture.Ledger.History[^1].UsageUnknown);
    }

    [Fact]
    public async Task Usage_attention_after_transport_never_returns_a_publishable_provider_response()
    {
        var fixture = Fixture();
        var overReservation = LlmInferenceUsageEvidence.Create(
            1,
            "provider/local",
            "v1",
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Authoritative(11),
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelUsageMeasurement.Unavailable,
            GovernedModelMonetaryUsageMeasurement.Unavailable);
        var transport = new CountingTransport(usage: overReservation);
        var execution = Execution(fixture, transport);

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.Equal(1, transport.Writes);
        Assert.Equal(GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, result.AdmissionStatus);
        Assert.Null(result.Response);
        Assert.Equal(GovernedModelUsageTransitionStatus.AttentionRequired, result.ReconciliationStatus);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(99, 10)]
    [InlineData(4, 4)]
    public async Task Caller_output_option_can_only_narrow_exact_hard_bound(int? callerMaximum, int expected)
    {
        var fixture = Fixture(fullBudget: true);
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);
        var inference = LlmInferenceRequest.FromUserText("test", new LlmInferenceOptions { MaxOutputTokenCount = callerMaximum });

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, inference));

        Assert.NotNull(result.Response);
        Assert.Equal(expected, transport.InferenceRequest?.Options.MaxOutputTokenCount);
    }

    [Fact]
    public async Task Successful_response_publishes_only_exact_ordered_buffered_segments_after_reconciliation()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(outputText: "alpha-beta", responseChunks: ["alpha", "-", "beta"]);
        var execution = Execution(fixture, transport);
        var published = new List<string>();

        var result = await Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            (chunk, _) =>
            {
                published.Add(chunk);
                return Task.CompletedTask;
            });

        Assert.Equal(["alpha", "-", "beta"], published);
        Assert.Equal("alpha-beta", result.Response?.OutputText);
        Assert.Equal(GovernedModelUsageLedgerPhase.Reconciled, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Divergent_stream_and_terminal_response_publish_nothing_and_require_attention()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(outputText: "terminal", responseChunks: ["different"]);
        var execution = Execution(fixture, transport);
        var published = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            (chunk, _) =>
            {
                published.Add(chunk);
                return Task.CompletedTask;
            }));

        Assert.Empty(published);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Missing_provider_boundary_publishes_nothing_and_durably_proves_not_started()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(skipBoundary: true);
        var execution = Execution(fixture, transport);
        var published = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            (chunk, _) =>
            {
                published.Add(chunk);
                return Task.CompletedTask;
            }));

        Assert.Equal(0, transport.Writes);
        Assert.Empty(published);
        Assert.Equal(GovernedModelUsageLedgerPhase.DispatchProvedNotStarted, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Current_authority_proof_change_after_resolution_fails_closed_before_transport()
    {
        var fixture = Fixture();
        var transport = new CountingTransport(onResolve: () => fixture.Authority.EvidenceHash = Hash('0'));
        var execution = Execution(fixture, transport);
        var published = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            (chunk, _) =>
            {
                published.Add(chunk);
                return Task.CompletedTask;
            }));

        Assert.Equal(0, transport.Writes);
        Assert.Empty(published);
        Assert.Equal(GovernedModelUsageLedgerPhase.DispatchProvedNotStarted, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Publication_callback_failure_after_reconciliation_does_not_create_false_usage_ambiguity()
    {
        var fixture = Fixture();
        var execution = Execution(fixture, new CountingTransport());

        await Assert.ThrowsAsync<GovernedModelResponsePublicationException>(() => Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            (_, _) => throw new IOException("surface disconnected")));

        Assert.Equal(4, fixture.Ledger.History.Count);
        Assert.Equal(GovernedModelUsageLedgerPhase.Reconciled, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Inexact_enforcement_acknowledgement_performs_zero_provider_transport()
    {
        var fixture = Fixture(fullBudget: true);
        var transport = new CountingTransport(forgeEnforcement: true);
        var execution = Execution(fixture, transport);

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Unavailable, result.AdmissionStatus);
        Assert.Equal(1, transport.ResolverCalls);
        Assert.Equal(0, transport.Writes);
        Assert.Equal(1, transport.DisposalCalls);
    }

    [Fact]
    public async Task Invalid_resolved_lease_cleanup_failure_is_disposed_and_durably_visible()
    {
        var fixture = Fixture(fullBudget: true);
        var transport = new CountingTransport(throwOnDispose: true, forgeEnforcement: true);
        var execution = Execution(fixture, transport);

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Unavailable, result.AdmissionStatus);
        Assert.Equal(0, transport.Writes);
        Assert.Equal(1, transport.DisposalCalls);
        Assert.Equal(GovernedModelUsageLedgerPhase.DispatchProvedNotStarted, fixture.Ledger.History[^2].Phase);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
        Assert.False(fixture.Ledger.History[^1].UsageUnknown);
        Assert.False(result.ProviderDispatchMayHaveOccurred);
        Assert.False(new GovernedModelPrimaryExecutionStoppedException(result).OutcomeMayExist);
    }

    [Theory]
    [InlineData("alternate-model")]
    [InlineData("invalid-surface")]
    [InlineData("oversized-response-id")]
    [InlineData("malformed-response-id")]
    [InlineData("wrong-provider")]
    public async Task Hostile_provider_response_shape_never_becomes_durable_usage_evidence(string mutation)
    {
        var fixture = Fixture();
        var transport = mutation switch
        {
            "alternate-model" => new CountingTransport(responseModel: "model/other"),
            "invalid-surface" => new CountingTransport(responseSurface: (LlmInferenceSurface)99),
            "oversized-response-id" => new CountingTransport(providerResponseId: new string('a', GovernedModelContractLimits.MaxIdentifierCharacters + 1)),
            "malformed-response-id" => new CountingTransport(providerResponseId: "response/1\nsecret"),
            "wrong-provider" => new CountingTransport(responseProviderId: "provider/other"),
            _ => throw new InvalidOperationException()
        };
        var execution = Execution(fixture, transport);
        var published = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(execution,
            new GovernedModelPrimaryExecutionRequest(fixture.Request, LlmInferenceRequest.FromUserText("test")),
            (chunk, _) =>
            {
                published.Add(chunk);
                return Task.CompletedTask;
            }));

        Assert.Equal(1, transport.Writes);
        Assert.Empty(published);
        Assert.DoesNotContain(fixture.Ledger.History, entry => entry.Phase == GovernedModelUsageLedgerPhase.UsageObserved);
        Assert.Equal(GovernedModelUsageLedgerPhase.AttentionRequired, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public async Task Caller_supplied_cross_attempt_correlation_performs_zero_resolver_or_transport()
    {
        var fixture = Fixture();
        var transport = new CountingTransport();
        var execution = Execution(fixture, transport);
        var inference = new LlmInferenceRequest(
            [LlmMessage.User("test")],
            correlation: new LlmInferenceCorrelation("attempt-other", Hash('0')));

        var result = await Execute(execution, new GovernedModelPrimaryExecutionRequest(fixture.Request, inference));

        Assert.Equal(GovernedModelAttemptAdmissionStatus.Invalid, result.AdmissionStatus);
        Assert.Equal(0, transport.ResolverCalls);
        Assert.Equal(0, transport.Writes);
        Assert.Equal(GovernedModelUsageLedgerPhase.DispatchProvedNotStarted, fixture.Ledger.History[^1].Phase);
    }

    [Fact]
    public void Ledger_identity_separates_graph_revision_generation_and_admission_authority()
    {
        var fixture = Fixture();
        var exact = fixture.ReservationEntry().Identity;
        var changedRevision = GovernedModelUsageLedgerIdentity.Create(1, exact.WorkspaceId, exact.RunId, exact.GraphId, "revision-2", exact.GraphExecutableHash, exact.ExecutionGeneration, exact.AdmissionReceiptHash, exact.RoutingAdmissionHash, exact.AuthorityEvidenceHash, exact.DataPostureEvidenceHash, exact.NodeId, exact.PlanOrdinal, exact.ActivationOrdinal, exact.VisitOrdinal, exact.AttemptOperationId, exact.AttemptNumber, exact.ProfilePinHash, exact.BudgetPolicyHash);
        var changedGeneration = GovernedModelUsageLedgerIdentity.Create(1, exact.WorkspaceId, exact.RunId, exact.GraphId, exact.GraphRevisionId, exact.GraphExecutableHash, 2, exact.AdmissionReceiptHash, exact.RoutingAdmissionHash, exact.AuthorityEvidenceHash, exact.DataPostureEvidenceHash, exact.NodeId, exact.PlanOrdinal, exact.ActivationOrdinal, exact.VisitOrdinal, exact.AttemptOperationId, exact.AttemptNumber, exact.ProfilePinHash, exact.BudgetPolicyHash);
        var changedAuthority = GovernedModelUsageLedgerIdentity.Create(1, exact.WorkspaceId, exact.RunId, exact.GraphId, exact.GraphRevisionId, exact.GraphExecutableHash, exact.ExecutionGeneration, exact.AdmissionReceiptHash, exact.RoutingAdmissionHash, Hash('0'), exact.DataPostureEvidenceHash, exact.NodeId, exact.PlanOrdinal, exact.ActivationOrdinal, exact.VisitOrdinal, exact.AttemptOperationId, exact.AttemptNumber, exact.ProfilePinHash, exact.BudgetPolicyHash);

        Assert.NotEqual(exact.ContentHash, changedRevision.ContentHash);
        Assert.NotEqual(exact.ContentHash, changedGeneration.ContentHash);
        Assert.NotEqual(exact.ContentHash, changedAuthority.ContentHash);
    }

    private static GovernedModelPrimaryExecutionService Execution(AttemptFixture fixture, CountingTransport transport)
    {
        var usage = new GovernedModelUsageReconciliationService(fixture.Ledger, new FixedTimeProvider(_now.AddSeconds(1)));
        return new GovernedModelPrimaryExecutionService(fixture.Service, usage, new Resolver(fixture.Pin, transport));
    }

    private static Task<GovernedModelPrimaryExecutionResult> Execute(
        GovernedModelPrimaryExecutionService service,
        GovernedModelPrimaryExecutionRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Action? providerRequestStarted = null,
        CancellationToken cancellationToken = default)
        => service.ExecuteAsync(
            request,
            (commitTransport, token) => commitTransport(token),
            responseChunkHandler,
            providerRequestStarted,
            cancellationToken);

    private static AttemptFixture Fixture(
        bool multiPage = false,
        CapabilityCatalogReadStatus catalogStatus = CapabilityCatalogReadStatus.Available,
        GovernedModelUsageLedgerAppendStatus reserveStatus = GovernedModelUsageLedgerAppendStatus.Appended,
        bool retainOnReserve = true,
        ModelInferenceDataPostureStatus dataStatus = ModelInferenceDataPostureStatus.Available,
        bool fullBudget = false,
        IModelInferenceDataPostureSource? dataPostureSource = null)
    {
        var profileEntry = Entry("org.example/model-a", CapabilityKind.ModelProfile);
        var pin = ProfilePin(profileEntry);
        var requirements = Requirements(fullBudget);
        var routingEntry = GovernedModelRoutingAdmissionEntry.Create(1, "node-inference", "inference", Hash('9'), requirements, true, [DataClass("public")], pin, []);
        var workspaceId = "workspace-sha256:" + Hash('1');
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact();
        var executionBinding = GovernedLoopExecutionBinding.Create(1, "run-default", artifact.RevisionArtifact.Revision, 1);
        var seedReceipt = GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(
            artifact,
            executionBinding,
            workspaceId,
            "operation-admit",
            Hash('2'),
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var seed = seedReceipt.Evidence;
        var admission = GovernedModelRoutingAdmissionSnapshot.Create(
            1,
            workspaceId,
            seedReceipt.Intent.OperationId,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(seedReceipt.Intent),
            GovernedLoopAdmissionContractHash.ComputeExecutionBindingReferenceHash(executionBinding),
            executionBinding.RunId,
            executionBinding.Revision.GraphId,
            executionBinding.Revision.RevisionId,
            executionBinding.Revision.ExecutableHash,
            executionBinding.ExecutionGeneration,
            seedReceipt.Intent.Role.Identity.RoleId,
            seedReceipt.Intent.Role.Identity.Revision,
            seedReceipt.Intent.Role.ContentHash,
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(seed.CapabilityAdmission),
            GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(seed.GrantProfile, seed.GrantBoundary, seed.GrantDependencyEvidenceHash, seed.EffectiveAuthority),
            7,
            null,
            null,
            pin.AdapterRegistryRevisionHash,
            seed.EvaluatedAtUtc,
            [routingEntry]);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            seed.SchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(seedReceipt.Intent),
            executionBinding,
            seed.GrantProfile,
            seed.GrantBoundary,
            seed.GrantDependencyEvidenceHash,
            seed.EffectiveAuthority,
            seed.CapabilityAdmission,
            admission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(seedReceipt.Intent, seed.EffectiveAuthority, seed.CapabilityAdmission, admission),
            seed.EvaluatedAtUtc,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(seedReceipt with { Evidence = evidence, ContentHash = string.Empty });
        var reservation = requirements.Budget.PerAttempt;
        var request = new GovernedModelAttemptAdmissionRequest(admission, receipt, admission.RunId, admission.ExecutionGeneration, routingEntry.NodeId, routingEntry.NodeTypeId, 0, 0, 1, "attempt-1", 1, pin.ContentHash);
        var pages = multiPage
            ? new[] { new[] { Entry("org.example/a", CapabilityKind.Skill) }, new[] { profileEntry } }
            : new[] { new[] { profileEntry } };
        var catalog = new PagedCatalog(pages, catalogStatus);
        var metadata = new MetadataSource(pin.Metadata, pin.ProfileSourceRevisionHash);
        var adapter = new AdapterSource(pin.Metadata.ContentHash, pin.AdapterRegistryRevisionHash);
        var data = dataPostureSource ?? new DataSource(dataStatus, request);
        var authority = new AuthoritySource(request, routingEntry, pin);
        var ledger = new Ledger(reserveStatus, retainOnReserve);
        var service = new GovernedModelAttemptAdmissionService(catalog, metadata, adapter, data, authority, ledger, new FixedTimeProvider(_now));
        return new AttemptFixture(service, request, pin, reservation, ledger, catalog, authority);
    }

    private sealed record AttemptFixture(GovernedModelAttemptAdmissionService Service, GovernedModelAttemptAdmissionRequest Request, GovernedModelProfilePin Pin, GovernedModelUsageCeiling Reservation, Ledger Ledger, PagedCatalog Catalog, AuthoritySource Authority)
    {
        internal GovernedModelUsageLedgerEntry ReservationEntry()
        {
            var identity = GovernedModelUsageLedgerIdentity.Create(1, Request.RoutingAdmission.WorkspaceId, Request.RunId, Request.RoutingAdmission.GraphId, Request.RoutingAdmission.GraphRevisionId, Request.RoutingAdmission.GraphExecutableHash, Request.ExecutionGeneration, Request.AdmissionReceipt.ContentHash, Request.RoutingAdmission.ContentHash, Hash('8'), Hash('7'), Request.NodeId, Request.PlanOrdinal, Request.ActivationOrdinal, Request.VisitOrdinal, Request.AttemptOperationId, Request.AttemptNumber, Pin.ContentHash, Request.RoutingAdmission.Entries[0].Requirements.Budget.ContentHash);
            return GovernedModelUsageLedgerEntry.Create(1, identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, Reservation, null, null, null, false, Pin.ContentHash, null, _now);
        }
    }

    private sealed class PagedCatalog(IReadOnlyList<CapabilityCatalogEntry[]> pages, CapabilityCatalogReadStatus status) : ICapabilityCatalogStore
    {
        internal int ReadCalls { get; private set; }
        public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            var index = startAfterId is null ? 0 : pages.ToList().FindIndex(page => page[^1].Descriptor.Id.Value == startAfterId) + 1;
            if (index < 0 || index >= pages.Count)
            {
                return Task.FromResult(new CapabilityCatalogReadResult(status, new CapabilityCatalogPage(7, [], null), "test"));
            }
            var next = index + 1 < pages.Count ? pages[index][^1].Descriptor.Id.Value : null;
            return Task.FromResult(new CapabilityCatalogReadResult(status, new CapabilityCatalogPage(7, pages[index], next), "test"));
        }
        public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MetadataSource(GovernedModelProfileMetadata metadata, string revision) : IModelProfileMetadataSource
    {
        public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.Found, metadata, revision));
    }

    private sealed class AdapterSource(string metadataHash, string revision) : IModelProfileAdapterRegistry
    {
        public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProfileAdapterPosture(ModelProfileAdapterPostureStatus.Ready, metadataHash, revision));
    }

    private sealed class DataSource(ModelInferenceDataPostureStatus status, GovernedModelAttemptAdmissionRequest request) : IModelInferenceDataPostureSource
    {
        public Task<ModelInferenceDataPosture> ReadAsync(ModelInferenceDataPostureRequest current, CancellationToken cancellationToken = default)
            => Task.FromResult(status == ModelInferenceDataPostureStatus.Available
                ? new ModelInferenceDataPosture(status, request.RunId, request.NodeId, request.PlanOrdinal, request.ActivationOrdinal, request.VisitOrdinal, request.AttemptNumber, request.AttemptOperationId, current.InputPayloadHash, [DataClass("public")], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current.InputPayloadHash))).ToLowerInvariant())
                : new ModelInferenceDataPosture(status, request.RunId, request.NodeId, request.PlanOrdinal, request.ActivationOrdinal, request.VisitOrdinal, request.AttemptNumber, request.AttemptOperationId, current.InputPayloadHash, [], null));
    }

    private sealed class AuthoritySource(GovernedModelAttemptAdmissionRequest request, GovernedModelRoutingAdmissionEntry node, GovernedModelProfilePin primary) : IModelAttemptAuthorityRevalidator
    {
        internal int Calls { get; private set; }
        internal string EvidenceHash { get; set; } = Hash('8');
        internal ModelAttemptAuthorityStatus Status { get; set; } = ModelAttemptAuthorityStatus.Allowed;
        public Task<ModelAttemptAuthorityEvidence> RevalidateAsync(GovernedModelAttemptAdmissionRequest current, GovernedModelRoutingAdmissionEntry currentNode, GovernedModelProfilePin currentPrimary, ModelInferenceDataPosture dataPosture, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ModelAttemptAuthorityEvidence(Status, request.RoutingAdmission.ContentHash, request.RunId, request.ExecutionGeneration, request.RoutingAdmission.OwningRoleId, node.NodeId, primary.ContentHash, request.PlanOrdinal, request.ActivationOrdinal, request.VisitOrdinal, request.AttemptNumber, request.AttemptOperationId, EvidenceHash));
        }
    }

    private sealed class UnavailableDataPostureSource : IModelInferenceDataPostureSource
    {
        public Task<ModelInferenceDataPosture> ReadAsync(ModelInferenceDataPostureRequest current, CancellationToken cancellationToken = default)
        {
            var request = current.Attempt;
            return Task.FromResult(new ModelInferenceDataPosture(
                ModelInferenceDataPostureStatus.Unavailable,
                request.RunId,
                request.NodeId,
                request.PlanOrdinal,
                request.ActivationOrdinal,
                request.VisitOrdinal,
                request.AttemptNumber,
                request.AttemptOperationId,
                current.InputPayloadHash,
                [],
                null));
        }
    }

    private sealed class Ledger(GovernedModelUsageLedgerAppendStatus reserveStatus, bool retainOnReserve) : IGovernedModelUsageLedger
    {
        private readonly object _sync = new();
        internal List<GovernedModelUsageLedgerEntry> History { get; } = [];
        internal GovernedModelUsageLedgerRunReadResult? RunReadOverride { get; set; }
        internal int ReadCalls { get; private set; }
        internal int ReserveCalls { get; private set; }
        internal GovernedModelUsageReservationRequest? LastReservationRequest { get; private set; }
        internal bool ThrowAfterDispatchAppend { get; set; }
        public Task<GovernedModelUsageLedgerReadResult> ReadAsync(GovernedModelUsageLedgerIdentity identity, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ReadCalls++;
                return Task.FromResult(History.Count == 0
                    ? new GovernedModelUsageLedgerReadResult(GovernedModelUsageLedgerReadStatus.NotFound, [], 0)
                    : new GovernedModelUsageLedgerReadResult(GovernedModelUsageLedgerReadStatus.Found, History.ToArray(), History.Count));
            }
        }

        public Task<GovernedModelUsageLedgerRunReadResult> ReadRunAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (RunReadOverride is not null)
                {
                    return Task.FromResult(RunReadOverride);
                }
                return Task.FromResult(History.Count == 0
                    ? new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.NotFound, [], 0)
                    : new GovernedModelUsageLedgerRunReadResult(GovernedModelUsageLedgerReadStatus.Found, History.ToArray(), History.Count));
            }
        }
        public Task<GovernedModelUsageReservationResult> ReserveAsync(GovernedModelUsageReservationRequest request, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ReserveCalls++;
                LastReservationRequest = request;
                var entry = GovernedModelUsageLedgerEntry.Create(1, request.Identity, 1, GovernedModelUsageLedgerPhase.ReservationCommitted, request.BudgetPolicy.PerAttempt, null, null, null, false, request.EvidenceHash, null, request.RecordedAtUtc);
                if (History.Count > 0)
                {
                    var exact = string.Equals(History[0].ContentHash, entry.ContentHash, StringComparison.Ordinal);
                    return Task.FromResult(new GovernedModelUsageReservationResult(exact ? GovernedModelUsageLedgerAppendStatus.AlreadyPresent : GovernedModelUsageLedgerAppendStatus.Conflict, History.Count, exact ? History[0] : null));
                }
                if (retainOnReserve && reserveStatus is GovernedModelUsageLedgerAppendStatus.Appended or GovernedModelUsageLedgerAppendStatus.AlreadyPresent)
                {
                    History.Add(entry);
                }
                var generation = reserveStatus is GovernedModelUsageLedgerAppendStatus.Appended or GovernedModelUsageLedgerAppendStatus.AlreadyPresent ? Math.Max(1, History.Count) : History.Count;
                return Task.FromResult(new GovernedModelUsageReservationResult(reserveStatus, generation, reserveStatus is GovernedModelUsageLedgerAppendStatus.Appended or GovernedModelUsageLedgerAppendStatus.AlreadyPresent ? entry : null));
            }
        }
        public Task<GovernedModelUsageLedgerAppendResult> AppendAsync(GovernedModelUsageLedgerEntry entry, long expectedGeneration, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (entry.Generation <= History.Count)
                {
                    var existing = History[(int)entry.Generation - 1];
                    return Task.FromResult(new GovernedModelUsageLedgerAppendResult(string.Equals(existing.ContentHash, entry.ContentHash, StringComparison.Ordinal) ? GovernedModelUsageLedgerAppendStatus.AlreadyPresent : GovernedModelUsageLedgerAppendStatus.Conflict, History.Count));
                }
                if (expectedGeneration != History.Count || entry.Generation != History.Count + 1)
                {
                    return Task.FromResult(new GovernedModelUsageLedgerAppendResult(GovernedModelUsageLedgerAppendStatus.Conflict, History.Count));
                }
                History.Add(entry);
                if (ThrowAfterDispatchAppend && entry.Phase == GovernedModelUsageLedgerPhase.DispatchBoundaryReached)
                {
                    ThrowAfterDispatchAppend = false;
                    throw new IOException("Ambiguous durable append.");
                }
                return Task.FromResult(new GovernedModelUsageLedgerAppendResult(GovernedModelUsageLedgerAppendStatus.Appended, History.Count));
            }
        }
    }

    private sealed class Resolver(GovernedModelProfilePin pin, CountingTransport transport) : IExactModelProfileInferenceClientResolver
    {
        public Task<ExactModelProfileInferenceClientResolution> ResolveAsync(ExactModelProfileInferenceClientRequest request, CancellationToken cancellationToken = default)
        {
            transport.ResolverCalls++;
            transport.ResolverRequest = request;
            transport.OnResolve?.Invoke();
            var acknowledgement = new ExactModelProfileEnforcementAcknowledgement(
                request.Primary.ContentHash,
                request.AttemptIdentity.ContentHash,
                request.Reservation.ContentHash,
                request.BudgetPolicy.ContentHash,
                request.RoutingAdmissionHash,
                request.AdmissionReceiptHash,
                request.AuthorityEvidenceHash,
                request.DataPostureEvidenceHash,
                pin.Metadata.ProviderId,
                LlmInferenceSurface.OpenAiCodex,
                request.ProviderAttemptId,
                request.ProviderCorrelationId,
                Hash('6'));
            if (transport.ForgeEnforcement)
            {
                acknowledgement = acknowledgement with { BudgetPolicyHash = Hash('0') };
            }
            return Task.FromResult(new ExactModelProfileInferenceClientResolution(ExactModelProfileInferenceClientResolutionStatus.Resolved, new Lease(pin, transport, acknowledgement)));
        }
    }

    private sealed class Lease(GovernedModelProfilePin pin, CountingTransport transport, ExactModelProfileEnforcementAcknowledgement acknowledgement) : IExactModelProfileInferenceClientLease
    {
        public string ProfilePinHash => pin.ContentHash;
        public string ConfigurationHash => pin.Metadata.ConfigurationHash;
        public ExactModelProfileEnforcementAcknowledgement Enforcement => acknowledgement;
        public ILlmInferenceClient Client { get; } = new Client(transport);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref transport.DisposalCalls);
            return transport.ThrowOnDispose ? ValueTask.FromException(new IOException("cleanup failed")) : ValueTask.CompletedTask;
        }
    }

    private sealed class Client(CountingTransport transport) : ILlmInferenceClient
    {
        public Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, Func<string, CancellationToken, Task>? responseChunkHandler = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, Func<string, CancellationToken, Task>? responseChunkHandler, CancellationToken cancellationToken, InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        {
            if (!transport.SkipBoundary)
            {
                await providerTransportCommitBoundary(_ =>
                {
                    Interlocked.Increment(ref transport.Writes);
                    return Task.CompletedTask;
                }, cancellationToken);
            }
            if (transport.DoubleCallback)
            {
                await providerTransportCommitBoundary(_ =>
                {
                    Interlocked.Increment(ref transport.Writes);
                    return Task.CompletedTask;
                }, cancellationToken);
            }
            if (transport.ThrowAfterWrite)
            {
                throw new IOException("provider read failed after write");
            }
            if (transport.ReturnNullResponse)
            {
                return null!;
            }
            transport.InferenceRequest = request;
            if (responseChunkHandler is not null)
            {
                foreach (var chunk in transport.ResponseChunks)
                {
                    await responseChunkHandler(chunk, cancellationToken);
                }
            }
            return new LlmInferenceResponse(transport.OutputText, transport.ResponseSurface, transport.Usage, transport.ResponseModel, transport.ProviderResponseId, transport.ResponseProviderId);
        }
    }

    private sealed class CountingTransport(
        bool doubleCallback = false,
        bool throwAfterWrite = false,
        bool returnNullResponse = false,
        bool throwOnDispose = false,
        bool forgeEnforcement = false,
        string responseModel = "model/test",
        LlmInferenceSurface responseSurface = LlmInferenceSurface.OpenAiCodex,
        string? providerResponseId = "response/1",
        string outputText = "ok",
        string responseProviderId = "provider/local",
        IReadOnlyList<string>? responseChunks = null,
        LlmInferenceUsageEvidence? usage = null,
        bool skipBoundary = false,
        Action? onResolve = null)
    {
        internal int Writes;
        internal int ResolverCalls;
        internal int DisposalCalls;
        internal bool DoubleCallback { get; } = doubleCallback;
        internal bool ThrowAfterWrite { get; } = throwAfterWrite;
        internal bool ReturnNullResponse { get; } = returnNullResponse;
        internal bool ThrowOnDispose { get; } = throwOnDispose;
        internal bool ForgeEnforcement { get; } = forgeEnforcement;
        internal string ResponseModel { get; } = responseModel;
        internal LlmInferenceSurface ResponseSurface { get; } = responseSurface;
        internal string? ProviderResponseId { get; } = providerResponseId;
        internal string OutputText { get; } = outputText;
        internal string ResponseProviderId { get; } = responseProviderId;
        internal IReadOnlyList<string> ResponseChunks { get; } = responseChunks ?? ["o", "k"];
        internal LlmInferenceUsageEvidence Usage { get; } = usage ?? LlmInferenceUsageEvidence.Create(
            1,
            responseProviderId,
            "v1",
            GovernedModelUsageMeasurement.Authoritative(1),
            GovernedModelUsageMeasurement.Authoritative(1),
            GovernedModelUsageMeasurement.Authoritative(0),
            GovernedModelUsageMeasurement.Authoritative(2),
            GovernedModelMonetaryUsageMeasurement.Authoritative("USD", 1));
        internal bool SkipBoundary { get; } = skipBoundary;
        internal Action? OnResolve { get; } = onResolve;
        internal ExactModelProfileInferenceClientRequest? ResolverRequest { get; set; }
        internal LlmInferenceRequest? InferenceRequest { get; set; }
    }

    private static GovernedModelProfilePin ProfilePin(CapabilityCatalogEntry entry)
    {
        var metadata = GovernedModelProfileMetadata.Create(
            1,
            entry.Lifecycle.DescriptorIdentity,
            "provider/local",
            "adapter/local",
            "model/test",
            "v1",
            1,
            Hash('3'),
            "Safe local test profile.",
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling],
            8_000,
            512,
            GovernedModelPrivacyPosture.Create(1, GovernedModelLocality.LocalProcess, CapabilityEgressMode.None, [], [DataClass("public")], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited),
            GovernedModelUsageSupportPolicy.Create(GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch, GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch),
            ["sequential-role"],
            ["inference"]);
        var descriptor = entry.Descriptor;
        var pin = new CapabilityAdmissionPin(entry.Lifecycle.DescriptorIdentity, descriptor.Kind, descriptor.Implementation, descriptor.Provenance, new CapabilityDependencyArtifactMetadata(null, null), descriptor.Purpose);
        return GovernedModelProfilePin.Create(pin, metadata, Hash('4'), Hash('5'));
    }

    private static GovernedModelProfileRequirements Requirements(bool fullBudget = false)
    {
        var attempt = Ceiling(10, fullBudget);
        var node = Ceiling(20, fullBudget);
        var run = Ceiling(30, fullBudget);
        return GovernedModelProfileRequirements.Create(
            1,
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling],
            8_000,
            512,
            GovernedModelPrivacyRequirement.Create(1, true, CapabilityEgressMode.None, [], [DataClass("public")], ["us"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited),
            GovernedModelBudgetPolicy.Create(1, attempt, node, run));
    }

    private static GovernedModelUsageCeiling Ceiling(long output, bool fullBudget)
        => fullBudget
            ? GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Bounded(output), GovernedModelUsageLimit.Bounded(output), GovernedModelUsageLimit.Bounded(output), GovernedModelUsageLimit.Bounded(output), GovernedModelMonetaryLimit.Bounded("USD", output * 10))
            : GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Bounded(output), GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelMonetaryLimit.Unbounded);

    private static CapabilityCatalogEntry Entry(string idValue, CapabilityKind kind)
    {
        Assert.True(CapabilityId.TryParse(idValue, out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        var descriptor = new CapabilityDescriptor(1, id!, kind, version!, new CapabilityImplementationIdentity(provider!, idValue[(idValue.IndexOf('/') + 1)..]), new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://example.test/model", "revision-1", null), new CapabilityCompatibility(range!, [CapabilityPlatform.Any]), "Safe test capability.", schema!, schema!, new CapabilityResourceLimits(1_000, 1_024, 1_024, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var lifecycle = new CapabilityLifecycleSnapshot(1, identity!, CapabilityDeclarationState.Declared, CapabilityInstallationState.Installed, CapabilityEnablementState.Enabled, CapabilityHealthState.Healthy, CapabilityRetirementState.Active, CapabilityTrustState.Verified);
        return new CapabilityCatalogEntry(descriptor, lifecycle, 7, _now, "test-operation");
    }

    private static CapabilityDataClass DataClass(string value)
    {
        Assert.True(CapabilityDataClass.TryParse(value, out var result, out _));
        return result!;
    }

    private static string Hash(char value) => new(value, 64);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubToolBroker : IToolBroker
    {
        public IReadOnlyList<ToolCommand> AvailableCommands => [];

        public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
