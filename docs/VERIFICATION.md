# Verification Contract

This is the operational contract for repository verification. It is not product scope; product-direction questions remain governed by [OPINIONATED_PROJECT_AXIOMS.md](OPINIONATED_PROJECT_AXIOMS.md).

The contract keeps failures recoverable and diagnosable, preserves maximum-boundary evidence, makes verifier state visible, and treats elapsed time as evidence rather than an unexplained wait. Those properties follow the failure, evaluation/replay, visible-state, and time axioms.

## Owned tiers

`scripts/verify.ps1` exposes exactly two tiers:

| Tier | Invocation | Ownership |
| --- | --- | --- |
| `PullRequest` | `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-with-watchdog.ps1 -Configuration Release` | Owns promotion. One external monotonic watchdog runs the script contract tests, build, discovery, every non-stress test, formatting, frontend checks, exact inventory reconciliation, coverage collection, and the unchanged per-assembly 90 percent floor. |
| `Stress` | `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -VerificationTier Stress` | Runs the two exact adversarial maximum/capacity cases separately. `.github/workflows/verification-stress.yml` owns a Monday 07:17 UTC schedule and manual dispatch. |

The Pull Request tier has two workflow stages:

| Stage | Trigger and bound | Evidence and authority |
| --- | --- | --- |
| Qualification | One owner-dispatched exact edge; normally the label-isolated one-job ephemeral macOS ARM64 runner, with an explicit Windows hosted diagnostic fallback only when platform evidence is required. Both run `verify-with-watchdog.ps1 -Qualification -BaseCommit <base> -HeadCommit <head> -DeadlineSeconds 480`. | An exact merge-base-to-head ownership map selects only affected checks. Product C# changes receive a Release solution build, architecture tests, and changed-file formatting. Public, unknown, and ordinary production changes run complete owning and downstream consumer suites. An explicitly reviewed implementation file may select exact public-boundary test classes only when Roslyn proves both existing edge versions contain one top-level `internal sealed`, non-partial type and the exact head authenticates every filename-matching xUnit class with no cross-file consumers. Two narrower reviewed forms are also permitted: a body-only change to one named private method in a public sealed non-partial type when every byte outside that body is unchanged, and a one-member public integer-constant contract when its complete exact-head C# reference set and both behavioral boundary classes match the checked map. Changing any mapped source or test reruns the mapping contract. If a private-method edge no longer matches, qualification records the fallback and restores the complete ordinary owning and downstream suites; structural mapping, signature, reference, test-path, class, and consumer drift still fails closed. General Common changes run every direct test-project consumer; Application also retains Clients behavior; CLI Command runs Integration; Clients retains Startup composition and Integration; Persistence runs CLI Command initialization, Startup, hosted Web, non-browser E2E, and Integration; Web runs non-browser E2E; and Startup runs its complete CLI Command, non-browser E2E, Web, and Integration consumers. Common, Application, Clients, Persistence, and Startup consumer-map contracts derive those closures from current project references plus checked source-level behavioral edges. A changed test-project file runs its complete suite plus the architecture boundary lane. Test C# files that directly declare xUnit methods authenticate exactly one filename-matching top-level class from the exact head and run that class with empty selection treated as an error; an exact-head reference from another test source restores the full owning suite, and deleting a test source likewise restores the surviving project to an unfiltered run. Test helpers default to the full owning suite; a helper may use a checked class or namespace map only when syntax-tree contracts prove every current consumer. Changes to a focused helper, its test project, or a syntax-proven consumer on either side of the edge revalidate that map. Project-wide test configuration and shared test infrastructure select full suites. Exact-head existence prevents deleted test projects from being scheduled and limits draw.io parsing to surviving authenticated blobs. Frontend and verifier changes receive their dedicated contracts; every path is classified and the exact diff is checked. Qualification collects no coverage, does not claim complete test inventory, and grants no merge authority. |
| Promotion | A non-draft pull request and every push to `main`; `verify-with-watchdog.ps1 -DeadlineSeconds 900`. Pull requests retain the established GitHub generated merge-ref checkout, binding certification to the current head/base pair. | The complete non-stress inventory, authenticated coverage reduction, and unchanged per-production-assembly 90 percent floor. The installed-browser workflow is promoted at the same boundary. Required `verify`, `browser-e2e`, `CodeQL`, and `dependency-review` contexts and human approval remain merge gates. |

Draft pushes do not automatically spend hosted-runner minutes. The owner or trusted agent starts one ephemeral local runner and explicitly dispatches the exact edge after a coherent batch; Windows hosted qualification is a deliberate diagnostic exception rather than the development default.

This split keeps feature development moving without pretending that an impact-selected signal is certification. The checked-in map fails closed on an unknown path, proves every tracked path has ownership, and conservatively selects full owning and consumer suites for unmapped production, helpers, shared infrastructure, runsettings, and linked fixtures. Reviewed implementation, private-method, constant, helper, and direct-test exceptions remain exact-edge syntax contracts rather than inferred dependency shortcuts: shape, signature, reference, test identity, or consumer drift restores the broader selection. Test execution and diagram validation are bound to exact-head objects rather than stale checkout paths.

Qualification uses two dependency-safe scheduler waves. Independent build, frontend, and verifier-contract work shares the first bounded wave; after immutable prerequisites are proved, selected tests, workflow validation, changed-file formatting, and exact diff-check share the second. No more than two process-heavy processes are admitted together, every selected project receives a collision-resistant short temporary root, and one `dotnet format` workspace load preserves both whitespace and IDE1006 diagnostics.

Agents publish coherent draft batches and explicitly dispatch qualification rather than running it after every push. A changed exact head or comparison base invalidates prior qualification evidence and requires a new dispatch; title/body-only edits do not change that immutable edge. Deterministic failures block the slice. One diagnostic rerun may classify an infrastructure timeout, after which it becomes verifier-health work instead of a feature-branch retry loop. Repeated timing samples belong to verifier-health changes, not every feature PR.

Marking a stable reviewed head/base pair ready starts merge-ref Promotion. Any later commit or retarget requires Promotion again and cancels superseded Windows verifier/browser work. Returning a ready pull request to draft starts one setup-free API cancellation job that may cancel only older runs for the same pull request. GitHub treats non-draft metadata edits as fresh required Promotion, CodeQL, and dependency edges, so freeze metadata before promotion. A skipped or reused context must never impersonate current certification.

`.github/workflows/trusted-local-qualification.yml` is a manual development accelerator for one explicitly dispatched exact edge. It can run only when both the original dispatch actor and any rerun-triggering actor are the repository owner, on a label-isolated, host-started, one-job ephemeral macOS ARM64 runner; it receives read-only repository permission, persists no checkout credential, validates both immutable commit identities and their ancestry, and executes the same bounded qualification child. The host must leave that runner offline except while waiting for the one intended dispatch. This lane is never a protected context, never promotion or merge authority, and cannot substitute for Windows-only fixtures, complete promotion inventory, browser certification, or coverage thresholds.

An edit to `BrowserFlowTests` runs the non-browser E2E slice during qualification and defers that installed-browser class to the required promotion workflow; the generated filter can therefore never include and exclude the same class. GitHub YAML syntax validation covers both `.yml` and `.yaml` workflow files plus `.github/dependabot.yml` through the pinned Prettier dependency.

The verifier defaults to `Release`; required workflows pass `-Configuration Release` explicitly. `Debug` remains an explicit local opt-in through `-Configuration Debug`. The watchdog accepts the 480-second qualification deadline and the workflow's 900-second promotion deadline, but rejects any larger value. The selected deadline is inclusive; any later tick, cancellation, child timeout, nonzero exit, or missing/duplicate terminal marker fails closed. Tool setup and diagnostic upload remain outside the measured child and the workflow job ceiling. The stress workflow always runs its diagnostic-upload step, retains available TRX timing and failure diagnostics for 30 days, and fails if the expected artifacts are absent. Exact filters plus `TreatNoTestsAsError` make removal, renaming, or trait loss fail the job instead of silently producing an empty pass.

The stress tier contains:

1. every-transition validation and retention, representative persistence replacements, and four near-maximum canonical-order mutations;
2. materialization of 10,000 deletion-operation artifacts followed by real quota, deletion-at-capacity, refusal, replay, and post-deletion quota operations.

The required maximum-contract test still constructs the declared 65-attempt/30-model-visible-tool-request terminal shape, validates it, verifies its 15 MiB contract, round-trips the canonical representation, and exercises cold/warm monitor, list, reload, inspection/hash, and quota projections. It omits only test-harness amplification: retaining and revalidating every intermediate transition, repeatedly replacing representative artifacts in fresh workspaces, and mutating four full JSON envelopes.

## Bounds, partition integrity, and progress

Qualification has an exact eight-minute limit and owns the development-feedback contract. Promotion is an exhaustive once-per-stable-head certification pass with a bounded fifteen-minute limit. `scripts/verify-with-watchdog.ps1` measures either child with `System.Diagnostics.Stopwatch`, terminates its process tree at the selected deadline, captures combined output, labels the evidence as `qualification` or `promotion`, and accepts exactly one terminal record of the form `VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=...`. Qualification retains its plan, logs, and selected TRX under `tests/QualificationResults`; promotion retains its complete phase, inventory, coverage, and watchdog evidence under `tests/VerificationResults`. A breach is a verifier incident to investigate, not authority to remove tests, reduce coverage, weaken assertions, or turn certification back into the draft edit loop.

The Pull Request tier starts with one bounded build-overlap wave. The isolated Release build runs beside the independent ordered frontend install/lint/format/test chain; only the explicitly classified source/temp-only bounded-phase, inventory, and watchdog contracts may backfill capacity as one of those two phases finishes. The process-heavy coverage contract is admitted to that same schedule behind the longer Release-build phase: the single process-heavy slot and longest-first ordering prevent it from starting before the build, while its three-unit weight lets it backfill beside one remaining source-only contract after the build completes. A successful whole-wave boundary is required before the second wave can admit descendant-heavy scheduler contracts or Windows SDK process-tree diagnostics. Descendant-heavy script-contract suites reserve the second wave's full capacity so they cannot multiply the outer scheduler's process load. Every phase retains explicit weights, class limits, logs, timeouts, and fail-closed aggregate completion.

Required gates use twelve logical resource units but no more than four outer processes on the four-core hosted runner. Deterministic longest-processing-time-first ordering uses checked-in duration estimates. The four internally parallel assembly gates consume three units each, but at most three are admitted together; each VSTest process permits at most two xUnit threads. Ordinary assembly gates consume one unit. The two unchanged C# format gates consume two CPU-bound units and join this same schedule only after build-output isolation, discovery, and exact partition reconciliation, allowing one format gate at a time to overlap immutable test execution without racing build artifacts. Missing profiles, oversized weights, underweighted resource classes, and limits above the effective worker ceiling fail before execution. Discovery and reconciliation remain hardware-bounded. The current partition has exactly nine coverage lanes, one per production test assembly, eliminating repeated VSTest startup, deployment, instrumentation, and Cobertura-write overhead without removing tests. Tests that share Startup runtime state, default capability-trust state, ephemeral API-host resources, or process-global state remain serialized through explicit collections; process-global cases disable collection parallelization entirely. Every test assembly is still discovered once as a canonical selection and partitioned through its one declared lane. The partition reconciler uses xUnit's stable test-case unique ID to require exact, disjoint, nonempty lane coverage; execution reconciliation then requires every VSTest test-case ID in the expected inventory and rejects missing, unexpected, failed, or cross-report duplicate executions. Dynamic data may produce multiple execution rows for one discovered case only within its admitted assembly report.

Each lane executes an immutable copy of the exact Release build under `<isolation>/<project>/<lane>/bin/Release/net10.0`, preserving helper-host path semantics. Collectors, results, and process environment are lane-scoped. Hosted lanes use the runner-owned ephemeral temporary volume; local lanes fall back to the fully qualified system temporary root. `TEMP`, `TMP`, and `TMPDIR` all point at one collision-checked, run-and-lane-derived fixture root inherited by helper processes, and `EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT` is a disjoint child of that root. These roots are deliberately short and fail closed when the selected Unix temporary volume cannot leave room for .NET named-pipe endpoints below the platform byte limit. Parallel processes and their descendants therefore cannot share test workspace or default server trust state, file-heavy crash-safety fixtures avoid the slower Windows profile volume, and the exact fixture trees stay outside retained diagnostics and are removed after ordinary completion. Persistence child-process coverage receives a process-scoped immutable source and never mutates the shared build output. After execution, SHA-256 manifests prove the source, canonical copy, and every lane copy unchanged.

Coverage aggregation accepts only fresh immutable Cobertura byte snapshots named by an exact, typed schema-1 manifest containing path, length, SHA-256, timestamp, report-kind counts, and lane/TRX provenance. Authentication and exact-byte reduction share one capture pass and use at most two deterministic workers; tiny inventories remain sequential to avoid worker overhead and disk contention. Lane names, roots, TRX paths, and GUID collector paths are one-to-one. Child-process entries additionally bind the admitted test-project identity and exact `CoverageIsolation/<project>/canonical/bin/<configuration>/Results/<guid>/coverage.cobertura.xml` provenance. A VSTest staging copy is evidence only when it is the single byte-identical `<runDeploymentRoot>/In/<machine>/coverage.cobertura.xml` alias of that lane's canonical report; it is authenticated but never merged as a second input. Missing, stale, duplicate, substituted, linked, corrupt, or extra reports fail closed under the host filesystem's case semantics. Split canonical reports still merge duplicate source lines by maximum hit count, and every production assembly retains the unchanged 90 percent line-coverage floor.

Stress sessions remain separately bounded at 25 minutes; the maximum-artifact process bound is 30 minutes and the deletion-capacity process bound is 20 minutes. Browser-only verification retains its separate installed-browser process bound and is not smuggled into the standard watchdog child.

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

The pre-orchestration Windows `Verify` run 31519078581 spent 46 minutes 6 seconds in repository-controlled verification. Its longest serial work was Persistence governance (938.808 s), Startup (589.992 s), Persistence loops (546.536 s), Integration (144.770 s), Web (133.144 s), coverage reconciliation (55.410 s), and the two format gates (25.701 s and 46.016 s). Build was 70.447 s. This evidence determines the longest-first lane ordering; it is not permission to omit work.

Verifier-health work should continue to measure repeated Windows runs against the five-minute optimization target, with cold/warm variance recorded from retained manifests and per-phase logs. Merge-candidate certification has a separate 900-second hard bound so hosted-runner variance cannot force repeated full-suite development cycles. Any over-limit run still fails and must be escalated with its critical path before the certification bound is revised. Exact-head run 31671865209 admitted all four internally parallel assemblies together and reached the former watchdog at 600.089 seconds; that history remains evidence for scheduler optimization rather than authority to weaken the exhaustive gate.

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

## Maximum artifact codec allocation design

Issue [#230](https://github.com/Jacob-J-Thomas/agenthome-poc/issues/230) replaces the maximum artifact's full raw-run `JsonNode` projection with a content-id projection. Large content is registered in canonical first-use order before the bounded projection is serialized, repeated tool protocol fields are compared through their typed records, strict UTF-8 decoding uses returned array-pool buffers, and canonical UTF-8/base64 round trips are compared through fixed-size stack scratch. The artifact schema, public size limits, table hashes, reference shapes, and emitted canonical bytes are unchanged.

Decode no longer constructs a second expanded projection solely to re-encode it. It instead combines the same semantic validator with streaming duplicate-property rejection, exact schema-property order and serializer-omission rules, typed primitive and compact tool-enum spelling checks, explicit first-use order for all four registries, structural/content hash checks, and a streaming comparison between canonical JSON tokens and the persisted bytes. This still rejects alternate whitespace, escaping, property order, enum casing, serializer-ignored fields, table/reference order, missing LF termination, malformed UTF-8/base64, unknown properties, and semantically invalid reconstructed runs.

Repeated Windows Release measurements on 2026-07-31 used the same SDK/runtime/host and 15,283,889-byte maximum fixture as the baseline above. The final measurements use precise process-allocation counters; three fresh test processes produced stable codec maxima and included any shared-pool growth charged to each operation. Representative before/after deltas and elapsed times were:

| Phase | Before | After |
| --- | ---: | ---: |
| Canonical serialization | 1,015,006,648 bytes / 3.288 s | 521,098,280-521,152,112 bytes / 2.717-5.170 s |
| Canonical deserialization | 857,798,544 bytes / 3.003 s | 486,334,264-488,629,280 bytes / 1.278-2.429 s |
| Canonical reserialization | 608,557,856 bytes / 2.042 s | 484,874,992-484,885,848 bytes / 1.177-1.915 s |
| Cold monitor repair/projection | 2,507,458,232 bytes / 6.697 s | 552,260,232-554,563,208 bytes / 1.083-1.570 s |
| Warm monitor projection | 16,392 bytes / 0.004 s | 92,144-94,768 bytes / 0.027-0.035 s |
| Verified list projection | 978,259,944 bytes / 2.986 s | 205,160 bytes / 0.056 s |
| Full artifact reload | 1,248,669,872 bytes / 3.095 s | 520,848,120-523,140,984 bytes / 1.042-1.261 s |
| Trace inspection/hash | 978,135,648 bytes / 3.646 s | 250,363,640-520,992,184 bytes / 1.007-1.228 s |
| Trace quota projection | 1,248,659,176 bytes / 4.269 s | 247,058,704-500,795,448 bytes / 1.185-1.401 s |

The required maximum test enforces 512 MiB for direct serialize, deserialize, and reserialize operations and every public read/projection phase, including full artifact reload. Cold index repair has a separate 1 GiB ceiling because it validates the artifact and rebuilds derived evidence. These are conservative process-wide allocation deltas, so they include any shared-pool growth charged to the measured operation.

Cancellation remains fail-closed at the async store boundary. Synchronous validation and projection do not claim cooperative mid-codec cancellation; every rented buffer is cleared and returned in `finally`. Fresh index repair reuses the already validated summary only after a second bounded file read matches the exact artifact hash. Warm monitor reads also stream and compare the current artifact hash before trusting a previously verified summary, so same-metadata replacement is detected even before watcher delivery. Normal monitor/list paths retain watcher change-version, identity-ambiguity, metadata, hash, and mutation-lock checks; concurrent CAS behavior is unchanged.
