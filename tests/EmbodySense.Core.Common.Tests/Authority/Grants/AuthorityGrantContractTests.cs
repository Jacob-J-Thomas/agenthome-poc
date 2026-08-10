using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests.Authority.Grants;

public sealed class AuthorityGrantContractTests
{
    [Fact]
    public void Grant_identity_and_revision_parsers_enforce_exact_bounded_forms()
    {
        var maximum = new string('a', AuthorityGrantContractLimits.MaxGrantIdCharacters);
        Assert.True(AuthorityGrantId.TryParse(maximum, out var first, out var firstError), firstError?.ToString());
        Assert.True(AuthorityGrantId.TryParse(maximum, out var equal, out _));
        Assert.Equal(first, equal);
        Assert.True(first!.Equals((object)equal!));
        Assert.Equal(maximum, first.ToString());
        Assert.Equal(0, first.CompareTo(equal));
        Assert.Equal(1, first.CompareTo(null));

        foreach (var invalid in new[] { null, string.Empty, "Uppercase", ".leading", "trailing.", "grant/child", new string('a', AuthorityGrantContractLimits.MaxGrantIdCharacters + 1) })
        {
            Assert.False(AuthorityGrantId.TryParse(invalid, out _, out _));
        }

        Assert.True(AuthorityGrantRevision.TryParse(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _));
        Assert.Equal(int.MaxValue, revision!.Value);
        Assert.Equal(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), revision.ToString());
        Assert.Equal(1, revision.CompareTo(null));
        foreach (var invalid in new[] { null, string.Empty, "0", "01", "-1", "+1", " 1", "1 ", "2147483648" })
        {
            Assert.False(AuthorityGrantRevision.TryParse(invalid, out _, out _));
        }
    }

    [Fact]
    public void Hash_is_deterministic_and_covers_every_immutable_behavior_field()
    {
        var capability = AuthorityGrantTestFixture.Capability();
        var original = AuthorityGrantTestFixture.Grant(ceiling: AuthorityGrantTestFixture.Ceiling([capability]));
        var mutations = new AuthorityGrant[]
        {
            AuthorityGrantTestFixture.Rehash(original with { GrantId = AuthorityGrantTestFixture.Id("workspace-helper-2") }),
            AuthorityGrantTestFixture.Rehash(original with { Status = AuthorityGrantLifecycleStatus.Suspended }),
            AuthorityGrantTestFixture.Rehash(original with { Binding = AuthorityGrantTestFixture.Binding(profileRevision: 5) }),
            AuthorityGrantTestFixture.Rehash(original with { Binding = AuthorityGrantTestFixture.Binding(roleRevision: 5) }),
            AuthorityGrantTestFixture.Rehash(original with { Binding = AuthorityGrantTestFixture.Binding(loopRevisionId: "revision-8") }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = original.RequestedCeiling with { MaxTargetCount = 4 } }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = original.RequestedCeiling with { MaxSideEffectClass = CapabilitySideEffectClass.None } }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = original.RequestedCeiling with { AllowsRecurrence = true } }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = original.RequestedCeiling with { AllowsExternalPublication = true } }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = original.RequestedCeiling with { AllowsIrreversibleAction = true } }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = AuthorityGrantTestFixture.Ceiling([], original.RequestedCeiling.DataClasses) }),
            AuthorityGrantTestFixture.Rehash(original with { RequestedCeiling = AuthorityGrantTestFixture.Ceiling(original.RequestedCeiling.Capabilities, [AuthorityGrantTestFixture.DataClass("user-content")]) }),
            AuthorityGrantTestFixture.Rehash(original with { Boundary = original.Boundary with { EffectiveAtUtc = original.Boundary.EffectiveAtUtc.AddSeconds(1) } }),
            AuthorityGrantTestFixture.Rehash(original with { Boundary = original.Boundary with { ExpiresAtUtc = original.Boundary.ExpiresAtUtc!.Value.AddSeconds(-1) } }),
            AuthorityGrantTestFixture.Rehash(original with { Boundary = original.Boundary with { CompletionConstraint = AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion } }),
            AuthorityGrantTestFixture.Rehash(original with { ChangedByActorId = AuthorityGrantTestFixture.Actor("user-owner-2") }),
            AuthorityGrantTestFixture.Rehash(original with { Reason = AuthorityGrantTestFixture.Purpose("A distinct bounded delegation reason.") }),
            AuthorityGrantTestFixture.Rehash(original with { RecordedAtUtc = original.RecordedAtUtc.AddSeconds(1) }),
        };

        Assert.True(AuthorityGrantHash.Matches(original));
        Assert.Equal(original.ContentHash, AuthorityGrantHash.Compute(original));
        Assert.StartsWith("sha256:", original.ContentHash, StringComparison.Ordinal);
        Assert.All(mutations, mutation =>
        {
            Assert.NotEqual(original.ContentHash, mutation.ContentHash);
            Assert.True(AuthorityGrantContractValidator.Validate(mutation).IsValid);
        });

        var successor = AuthorityGrantTestFixture.Successor(original);
        var changedPredecessor = AuthorityGrantTestFixture.Rehash(successor with { PredecessorContentHash = "sha256:" + new string('9', 64) });
        Assert.NotEqual(successor.ContentHash, changedPredecessor.ContentHash);
    }

    [Fact]
    public void Validator_rejects_malformed_schema_lineage_status_pins_ceiling_boundaries_and_hash()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        var invalid = new (AuthorityGrant Grant, AuthorityGrantValidationErrorCode Code)[]
        {
            (grant with { SchemaVersion = 2 }, AuthorityGrantValidationErrorCode.UnsupportedSchemaVersion),
            (grant with { GrantId = null! }, AuthorityGrantValidationErrorCode.InvalidIdentity),
            (grant with { Revision = null! }, AuthorityGrantValidationErrorCode.InvalidIdentity),
            (grant with { PredecessorRevision = AuthorityGrantTestFixture.Revision(1), PredecessorContentHash = grant.ContentHash }, AuthorityGrantValidationErrorCode.InvalidLineage),
            (grant with { Status = AuthorityGrantLifecycleStatus.Unknown }, AuthorityGrantValidationErrorCode.InvalidLifecycle),
            (grant with { Binding = null! }, AuthorityGrantValidationErrorCode.InvalidIdentity),
            (grant with { RequestedCeiling = null! }, AuthorityGrantValidationErrorCode.InvalidCeiling),
            (grant with { Boundary = grant.Boundary with { EffectiveAtUtc = grant.Boundary.EffectiveAtUtc.ToOffset(TimeSpan.FromHours(1)) } }, AuthorityGrantValidationErrorCode.InvalidBoundary),
            (grant with { Boundary = grant.Boundary with { ExpiresAtUtc = grant.Boundary.EffectiveAtUtc } }, AuthorityGrantValidationErrorCode.InvalidBoundary),
            (grant with { RecordedAtUtc = grant.RecordedAtUtc.ToOffset(TimeSpan.FromHours(-5)) }, AuthorityGrantValidationErrorCode.InvalidBoundary),
            (grant with { ContentHash = "sha256:" + new string('0', 64) }, AuthorityGrantValidationErrorCode.InvalidHash),
        };

        Assert.Contains(AuthorityGrantContractValidator.Validate((AuthorityGrant?)null).Errors, error => error.Code == AuthorityGrantValidationErrorCode.Required);
        foreach (var (candidate, code) in invalid)
        {
            var validation = AuthorityGrantContractValidator.Validate(candidate);
            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, error => error.Code == code);
        }
    }

    [Fact]
    public void Lifecycle_transitions_accept_only_contiguous_operation_specific_successors()
    {
        var active = AuthorityGrantTestFixture.Grant();
        var narrow = AuthorityGrantTestFixture.Successor(active, ceiling: active.RequestedCeiling with { MaxTargetCount = active.RequestedCeiling.MaxTargetCount - 1 });
        Assert.True(AuthorityGrantContractValidator.ValidateTransition(active, narrow, AuthorityGrantOperationKind.Narrow).IsValid);

        var suspended = AuthorityGrantTestFixture.Successor(active, status: AuthorityGrantLifecycleStatus.Suspended);
        Assert.True(AuthorityGrantContractValidator.ValidateTransition(active, suspended, AuthorityGrantOperationKind.Suspend).IsValid);

        var replacement = AuthorityGrantTestFixture.Successor(active, binding: AuthorityGrantTestFixture.Binding(profileRevision: 9));
        Assert.True(AuthorityGrantContractValidator.ValidateTransition(active, replacement, AuthorityGrantOperationKind.Replace).IsValid);

        var revoked = AuthorityGrantTestFixture.Successor(active, status: AuthorityGrantLifecycleStatus.Revoked);
        Assert.True(AuthorityGrantContractValidator.ValidateTransition(active, revoked, AuthorityGrantOperationKind.Revoke).IsValid);

        var afterExpiry = active.Boundary.ExpiresAtUtc!.Value.AddSeconds(1);
        var expired = AuthorityGrantTestFixture.Successor(active, status: AuthorityGrantLifecycleStatus.Expired, recordedAtUtc: afterExpiry);
        Assert.True(AuthorityGrantContractValidator.ValidateTransition(active, expired, AuthorityGrantOperationKind.Expire).IsValid);

        AssertInvalidTransition(active, AuthorityGrantTestFixture.Successor(active), AuthorityGrantOperationKind.Narrow, AuthorityGrantValidationErrorCode.AuthorityWidening);
        AssertInvalidTransition(active, AuthorityGrantTestFixture.Successor(active, status: AuthorityGrantLifecycleStatus.Suspended), AuthorityGrantOperationKind.Create, AuthorityGrantValidationErrorCode.InvalidLifecycle);
        AssertInvalidTransition(active, AuthorityGrantTestFixture.Successor(active, status: AuthorityGrantLifecycleStatus.Expired), AuthorityGrantOperationKind.Expire, AuthorityGrantValidationErrorCode.InvalidBoundary);
        AssertInvalidTransition(revoked, AuthorityGrantTestFixture.Successor(revoked), AuthorityGrantOperationKind.Replace, AuthorityGrantValidationErrorCode.InvalidLifecycle);
        AssertInvalidTransition(active, AuthorityGrantTestFixture.Rehash(narrow with { PredecessorContentHash = "sha256:" + new string('8', 64) }), AuthorityGrantOperationKind.Narrow, AuthorityGrantValidationErrorCode.InvalidLineage);
    }

    [Fact]
    public void Operation_evidence_enforces_terminal_disposition_failure_and_contiguous_result_shapes()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        var committed = Evidence(grant, AuthorityGrantOperationKind.Create, AuthorityGrantOperationOutcome.Committed, AuthorityGrantOperationFailureCode.None, 0, Reference(grant));
        Assert.True(AuthorityGrantContractValidator.Validate(committed).IsValid);

        var invalid = new AuthorityGrantOperationEvidence[]
        {
            committed with { Outcome = AuthorityGrantOperationOutcome.Unknown },
            committed with { Kind = AuthorityGrantOperationKind.Unknown },
            committed with { FailureCode = AuthorityGrantOperationFailureCode.InvalidRequest },
            committed with { ResultingGrant = null },
            committed with { ExpectedRevision = 1 },
            committed with { ResultingGrant = new AuthorityGrantReference(grant.GrantId, AuthorityGrantTestFixture.Revision(2), grant.ContentHash) },
            committed with { RecordedAtUtc = committed.RecordedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            committed with { AuthorityEvidenceHash = new string('A', 64) },
            Evidence(grant, AuthorityGrantOperationKind.Narrow, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict, 0, null),
            Evidence(grant, AuthorityGrantOperationKind.Create, AuthorityGrantOperationOutcome.Invalid, AuthorityGrantOperationFailureCode.None, 0, null),
            Evidence(grant, AuthorityGrantOperationKind.Create, AuthorityGrantOperationOutcome.Invalid, AuthorityGrantOperationFailureCode.InvalidRequest, 0, Reference(grant)),
        };

        Assert.Contains(AuthorityGrantContractValidator.Validate((AuthorityGrantOperationEvidence?)null).Errors, error => error.Code == AuthorityGrantValidationErrorCode.Required);
        Assert.All(invalid, evidence => Assert.False(AuthorityGrantContractValidator.Validate(evidence).IsValid));

        var successor = AuthorityGrantTestFixture.Successor(grant, ceiling: grant.RequestedCeiling with { MaxTargetCount = 4 });
        var narrowCommitted = Evidence(grant, AuthorityGrantOperationKind.Narrow, AuthorityGrantOperationOutcome.Committed, AuthorityGrantOperationFailureCode.None, 1, Reference(successor));
        Assert.True(AuthorityGrantContractValidator.Validate(narrowCommitted).IsValid);
    }

    [Fact]
    public void Operation_evidence_accepts_only_closed_outcome_and_failure_compositions()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        var allowed = new Dictionary<AuthorityGrantOperationOutcome, AuthorityGrantOperationFailureCode[]>
        {
            [AuthorityGrantOperationOutcome.Committed] = [AuthorityGrantOperationFailureCode.None],
            [AuthorityGrantOperationOutcome.Invalid] = [AuthorityGrantOperationFailureCode.InvalidRequest],
            [AuthorityGrantOperationOutcome.Denied] = [AuthorityGrantOperationFailureCode.AuthorityDenied],
            [AuthorityGrantOperationOutcome.NotFound] =
            [
                AuthorityGrantOperationFailureCode.LifecycleConflict,
                AuthorityGrantOperationFailureCode.ProfileUnavailable,
                AuthorityGrantOperationFailureCode.RoleUnavailable,
                AuthorityGrantOperationFailureCode.LoopUnavailable,
            ],
            [AuthorityGrantOperationOutcome.Conflict] =
            [
                AuthorityGrantOperationFailureCode.LifecycleConflict,
                AuthorityGrantOperationFailureCode.OperationConflict,
                AuthorityGrantOperationFailureCode.CeilingExceeded,
                AuthorityGrantOperationFailureCode.BoundaryConflict,
            ],
            [AuthorityGrantOperationOutcome.LimitExceeded] = [AuthorityGrantOperationFailureCode.LimitExceeded],
            [AuthorityGrantOperationOutcome.Unavailable] =
            [
                AuthorityGrantOperationFailureCode.AuthorityUnavailable,
                AuthorityGrantOperationFailureCode.ProfileUnavailable,
                AuthorityGrantOperationFailureCode.RoleUnavailable,
                AuthorityGrantOperationFailureCode.LoopUnavailable,
                AuthorityGrantOperationFailureCode.StoreUnavailable,
            ],
            [AuthorityGrantOperationOutcome.Ambiguous] = [AuthorityGrantOperationFailureCode.StoreAmbiguous],
        };

        foreach (var outcome in Enum.GetValues<AuthorityGrantOperationOutcome>().Where(value => value != AuthorityGrantOperationOutcome.Unknown))
        {
            foreach (var failureCode in Enum.GetValues<AuthorityGrantOperationFailureCode>())
            {
                var committed = outcome == AuthorityGrantOperationOutcome.Committed;
                var requiresDependencyEvidence = committed || outcome == AuthorityGrantOperationOutcome.Conflict && failureCode == AuthorityGrantOperationFailureCode.CeilingExceeded;
                var evidence = Evidence(
                    grant,
                    AuthorityGrantOperationKind.Create,
                    outcome,
                    failureCode,
                    0,
                    committed ? Reference(grant) : null) with
                {
                    DependencyEvidenceHash = requiresDependencyEvidence ? new string('3', 64) : null,
                };

                var validation = AuthorityGrantContractValidator.Validate(evidence);
                var expected = allowed[outcome].Contains(failureCode);
                Assert.Equal(expected, validation.IsValid);
            }
        }
    }

    [Fact]
    public void Receipt_only_evidence_bounds_expected_revision_to_the_grant_revision_domain()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        var maximum = Evidence(
            grant,
            AuthorityGrantOperationKind.Narrow,
            AuthorityGrantOperationOutcome.Conflict,
            AuthorityGrantOperationFailureCode.LifecycleConflict,
            int.MaxValue,
            null) with
        {
            DependencyEvidenceHash = null,
        };

        Assert.True(AuthorityGrantContractValidator.Validate(maximum).IsValid);

        foreach (var outsideBound in new[] { (long)int.MaxValue + 1, long.MaxValue })
        {
            var validation = AuthorityGrantContractValidator.Validate(maximum with { ExpectedRevision = outsideBound });

            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, error => error.Code == AuthorityGrantValidationErrorCode.InvalidLineage && error.Path == "$.expectedRevision");
        }
    }

    [Fact]
    public void Dependency_evidence_is_required_only_for_authority_producing_commits_and_ceiling_exceeded()
    {
        var grant = AuthorityGrantTestFixture.Grant();
        var successor = AuthorityGrantTestFixture.Successor(grant, ceiling: grant.RequestedCeiling with { MaxTargetCount = 4 });
        foreach (var kind in Enum.GetValues<AuthorityGrantOperationKind>().Where(value => value != AuthorityGrantOperationKind.Unknown))
        {
            var isCreate = kind == AuthorityGrantOperationKind.Create;
            var requiresDependencyEvidence = kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace;
            var evidence = Evidence(
                grant,
                kind,
                AuthorityGrantOperationOutcome.Committed,
                AuthorityGrantOperationFailureCode.None,
                isCreate ? 0 : 1,
                Reference(isCreate ? grant : successor)) with
            {
                DependencyEvidenceHash = requiresDependencyEvidence ? new string('3', 64) : null,
            };

            Assert.True(AuthorityGrantContractValidator.Validate(evidence).IsValid, kind.ToString());
            Assert.False(AuthorityGrantContractValidator.Validate(evidence with
            {
                DependencyEvidenceHash = requiresDependencyEvidence ? null : new string('3', 64),
            }).IsValid);
        }

        var ceilingExceeded = Evidence(
            grant,
            AuthorityGrantOperationKind.Create,
            AuthorityGrantOperationOutcome.Conflict,
            AuthorityGrantOperationFailureCode.CeilingExceeded,
            0,
            null);
        Assert.True(AuthorityGrantContractValidator.Validate(ceilingExceeded).IsValid);
        Assert.False(AuthorityGrantContractValidator.Validate(ceilingExceeded with { DependencyEvidenceHash = null }).IsValid);

        var lifecycleConflict = ceilingExceeded with
        {
            FailureCode = AuthorityGrantOperationFailureCode.LifecycleConflict,
            DependencyEvidenceHash = null,
        };
        Assert.True(AuthorityGrantContractValidator.Validate(lifecycleConflict).IsValid);
        Assert.False(AuthorityGrantContractValidator.Validate(lifecycleConflict with { DependencyEvidenceHash = new string('3', 64) }).IsValid);
    }

    [Fact]
    public void Validation_and_subset_behavior_owners_return_defensive_result_snapshots()
    {
        var result = AuthorityGrantContractValidator.Validate((AuthorityGrant?)null);
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityGrantValidationError>)result.Errors).Clear());

        var subset = AuthorityCeilingSubset.Validate(null, null, null, null);
        Assert.False(subset.IsSubset);
        Assert.Single(subset.Violations);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityCeilingSubsetViolation>)subset.Violations).Clear());
    }

    private static AuthorityGrantOperationEvidence Evidence(
        AuthorityGrant grant,
        AuthorityGrantOperationKind kind,
        AuthorityGrantOperationOutcome outcome,
        AuthorityGrantOperationFailureCode failureCode,
        long expectedRevision,
        AuthorityGrantReference? reference)
    {
        return new AuthorityGrantOperationEvidence(
            1,
            "grant-operation-1",
            new string('1', 64),
            kind,
            outcome,
            failureCode,
            grant.GrantId,
            expectedRevision,
            reference,
            grant.ChangedByActorId,
            grant.Reason,
            new string('2', 64),
            new string('3', 64),
            grant.RecordedAtUtc);
    }

    private static AuthorityGrantReference Reference(AuthorityGrant grant) => new(grant.GrantId, grant.Revision, grant.ContentHash);

    private static void AssertInvalidTransition(AuthorityGrant current, AuthorityGrant next, AuthorityGrantOperationKind kind, AuthorityGrantValidationErrorCode expected)
    {
        var validation = AuthorityGrantContractValidator.ValidateTransition(current, next, kind);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == expected);
    }
}
