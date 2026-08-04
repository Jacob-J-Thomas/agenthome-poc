# Verification Contract

This is the operational contract for repository verification. It is not product scope; product-direction questions remain governed by [OPINIONATED_PROJECT_AXIOMS.md](OPINIONATED_PROJECT_AXIOMS.md).

The contract keeps failures recoverable and diagnosable, preserves maximum-boundary evidence, makes verifier state visible, and treats elapsed time as evidence rather than an unexplained wait. Those properties follow the failure, evaluation/replay, visible-state, and time axioms.

## Owned tiers

`scripts/verify.ps1` exposes exactly two tiers:

| Tier | Invocation | Ownership |
| --- | --- | --- |
| `PullRequest` | `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1` | Required for pull requests and pushes to `main`. It runs every test except cases explicitly carrying `VerificationTier=Stress`. The required maximum-artifact round-trip remains in this tier. |
| `Stress` | `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -VerificationTier Stress` | Runs the two exact adversarial maximum/capacity cases separately. `.github/workflows/verification-stress.yml` owns a Monday 07:17 UTC schedule and manual dispatch. |

The verifier defaults to `Release`; required workflows pass `-Configuration Release` explicitly. `Debug` remains an explicit local opt-in through `-Configuration Debug`. The stress workflow always runs its diagnostic-upload step, retains available TRX timing and failure diagnostics for 30 days, and fails if the expected artifacts are absent. Exact fully qualified filters plus `TreatNoTestsAsError` make removal, renaming, or trait loss fail the job instead of silently producing an empty pass. Pull-request selection also uses `TreatNoTestsAsError`.

The stress tier contains:

1. every-transition validation and retention, representative persistence replacements, and four near-maximum canonical-order mutations;
2. materialization of 10,000 deletion-operation artifacts followed by real quota, deletion-at-capacity, refusal, replay, and post-deletion quota operations.

The required maximum-contract test still constructs the declared 65-attempt/30-model-visible-tool-request terminal shape, validates it, verifies its 15 MiB contract, round-trips the canonical representation, and exercises cold/warm monitor, list, reload, inspection/hash, and quota projections. It omits only test-harness amplification: retaining and revalidating every intermediate transition, repeatedly replacing representative artifacts in fresh workspaces, and mutating four full JSON envelopes.

## Bounds and progress

Every native build, format, frontend, browser, test-project, coverage, and stress phase emits `VERIFY_PHASE_START` and `VERIFY_PHASE_COMPLETE` records. Nonzero exits and timeouts name the failed phase, elapsed time, and last completed phase. The pull-request test-session limit is 14 minutes per project beneath a 15-minute process bound. Stress sessions are bounded at 25 minutes; the maximum-artifact process bound is 30 minutes and the deletion-capacity process bound is 20 minutes. The outer required and stress jobs remain bounded at 45 and 75 minutes respectively.

The two profiled tests also emit JSON records:

- `VERIFY_TEST_CONTEXT`: OS/runtime/architecture, processor count, GC mode, process ID, CI runner fields, run attempt, and SHA;
- `VERIFY_TEST_PHASE_START`: phase classification plus proposed and diagnostic budgets;
- `VERIFY_TEST_PHASE_COMPLETE`: elapsed milliseconds and approximate process allocation delta;
- `VERIFY_TEST_PHASE_FAILED`: failed phase, exception, and last completed phase.

Allocation deltas use process-wide runtime counters. The profiled tests run alone in the stress tier; the required maximum test belongs to a non-parallel test collection. Results are still diagnostic evidence rather than exact heap-retention measurements.

### Proposed budgets

These are initial work-class expectations, not narrow cross-machine benchmarks. The broader diagnostic bound is enforced to avoid timing-only flakes. Synchronous CPU or directory-enumeration work may complete after its diagnostic target; the enclosing test-session/process timeout is the hard preemptive bound and the last emitted phase identifies where it stopped.

| Phase or work class | Classification | Proposed budget | Diagnostic bound |
| --- | --- | ---: | ---: |
| Synthetic maximum execution/event construction | Test amplification used to create a valid boundary artifact | 30 s required / 8 min stress | 5 min required / 20 min stress |
| Final run validation | Reachable production boundary | 5 s | 1 min |
| Canonical serialize, deserialize, or reserialize | Reachable production boundary | 10 s each | 2 min each |
| Cold monitor index repair plus projection | Reachable recovery boundary | 30 s | 2 min |
| Warm monitor projection | Reachable production boundary | 2 s | 30 s |
| List projection | Reachable production boundary | 5 s | 1 min |
| Full reload, trace inspection/hash, or quota projection | Reachable production boundary | 15 s each | 2 min each |
| Maximum fixture file materialization | Test amplification | 10 s | 1 min |
| Representative transition reservation/replacement matrix | Test amplification around reachable writes | 5 min | 15 min |
| Canonical-reference and four order-negative cases | Test amplification around reachable decode rejection | 5 min | 10 min |
| 10,000-operation fixture materialization | Test amplification | 3 min | 10 min |
| 10,000-operation quota, delete/refuse/replay, or post-delete quota | Reachable production boundary | 30 s each | 3 min each |

Budgets may be revised only from retained Windows and CI evidence. A narrower enforceable performance regression threshold requires repeated stable measurements; sleeps are not a substitute.

## Cancellation and host monopolization

The async persistence boundaries accept cancellation tokens and pass them through mutation-lock acquisition and file I/O. Validation and canonical serialization/deserialization are synchronous and currently have no cooperative cancellation token. Directory enumeration and ordering of deletion-operation paths are also synchronous segments inside cancellable store operations. Cancellation therefore remains fail-closed at the public operation boundary but is not guaranteed to interrupt those CPU/enumeration segments immediately.

The required tier bounds each test project so one workspace cannot occupy the verifier indefinitely. Expensive adversarial amplification runs in its own scheduled job, with each owned test in a separate bounded process. This ticket does not change production cancellation, limits, persistence algorithms, or integrity behavior.

## Baseline ledger

Baseline records must include the emitted context, artifact/count class, phase records, configuration, tier, and whether the discovery index was warm or repaired. Do not compare aggregate test duration to one production API operation.

Initial Windows evidence on 2026-07-31 used Debug configuration, SDK 10.0.302, runtime .NET 10.0.10, Windows 10.0.26200 x64, 12 logical processors, workstation GC, and a 15,283,889-byte maximum artifact. Three optimized required runs completed in 40-49 seconds; two repeats after splitting cold and warm monitor behavior produced the following stable work classes:

| Phase | Observed elapsed | Approximate allocated bytes |
| --- | ---: | ---: |
| Synthetic maximum construction | 5.026-8.278 s | 1,313,599,040-1,315,829,136 |
| Final validation | 0.341-0.623 s | 80,147,920-80,160,040 |
| Canonical serialization | 3.843-4.815 s | 1,015,007,040-1,015,065,632 |
| Canonical deserialization | 3.175-4.729 s | 857,842,320-857,853,816 |
| Canonical reserialization | 1.878-2.808 s | 608,613,968-608,622,000 |
| Cold monitor repair/projection | 8.547-10.070 s | 2,237,150,496-2,507,676,752 |
| Warm monitor projection | 0.003-0.005 s | 16,368-34,776 |
| List projection | 3.648-4.925 s | 981,139,104-1,251,688,392 |
| Full reload | 4.519-4.976 s | 981,046,840-1,251,550,136 |
| Inspect/hash | 4.459-4.811 s | 981,053,896-1,251,597,376 |
| Quota | 3.743-5.203 s | 981,043,448-981,054,672 |

This is a repeated single-host baseline, not an agreed product regression threshold. It establishes a separate allocation-amplification concern for codec-backed reads while also showing that warm monitor projection is not the hot path. Follow-up [#230](https://github.com/Jacob-J-Thomas/agenthome-poc/issues/230) owns that product investigation. The required Windows CI run supplies a second host through its detailed persistence-test output, and the scheduled stress workflow supplies the retained adversarial CI baseline. Do not fold the optimization into this verifier-contract change.
