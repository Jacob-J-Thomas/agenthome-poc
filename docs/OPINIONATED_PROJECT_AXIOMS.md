# EmbodySense Axioms

This document defines the durable scope, philosophy, and operating constraints of EmbodySense.

It is not a feature list, roadmap, implementation plan, UI specification, provider matrix, or repository status report. Those artifacts may derive from this document, but they do not override it.

When implementation, documentation, memory, comments, diagrams, current behavior, or agent output conflict with these axioms, the conflict should be surfaced before work proceeds.

---

## 0. Level Rule

An axiom states what must remain true across valid implementations.

A design implication describes one plausible way to satisfy an axiom.

This document should only consist of highly opinionated axioms. Any boring "boiler-plate" industry guidance has been purposefully omitted here for brevity. If all agent harnesses do a thing, and that thing would not contradict the below axioms, we can reasonably assume that this agent harness also agrees with this axiom. (For example, agents should require approval when performing risky actions... This should be obvious to any engineer that has any business implementing an AI based solution).

This document may include implications when they clarify intent, but those implications are not binding unless promoted into design, architecture, or product requirements.

EmbodySense should not pretend to be less opinionated than it is. Some implementation preferences are strong enough to belong here when they protect the shape of the agent body, but they should be seperated out into the bottom section of the document, where the concepts are not truly axiomatic.

---

## 1. EmbodySense Is An Agent Body

EmbodySense is an operating environment for durable agents, not merely a chat interface, IDE plugin, chat/prompt wrapper, or tool-calling shell.

Its purpose is to give agents a practical body: scoped memory, tools, permissions, routines, state, time-awareness, observation, review, recovery, interaction surfaces, and a context engine that allows inference to repeatedly reinhabit that body. To do this, elements of the harness are persisted across sessions, including elements that help support and enforce the durable identity, and contextually loaded role based behaviors that are outlined in more detail in item 3.

Chat is one interface to the body. It is not the body.

Voice, vision, wake inputs, orchestration commands, scheduled routines, background loops, and external signals are also valid ways to interact with an agent body.

The harness should support ephemeral model calls, but ephemeral calls are not the center of the product.

---

## 2. The Core Artifact Is The Governed Loop

The primary product artifact is a durable, inspectable, modifiable, governed agent loop.

A loop will include role instructions, memory injections, events, wake inputs, actions, tool assignments, review gates, queues, external observations, failure handlers, and visible state, even if that is just to inform the model that it is currently behaving as a tool in a larger pipeline.

A loop may be entered by chat, schedule, file change, webhook, message, tool output, system event, human input, another agent role, another loop, or a monitored condition.

A loop may contain other loops, but every valid loop must be inspectable, interruptible, resumable, and amendable.

A loop must carry enough context to explain what it is doing, why it is doing it, what authority it has, what tools it may use, what memory it may touch, and what part of the environment it may alter.

A loop that cannot explain these things is not sufficiently governed.

---

## 3. Durable Identity, Contextual Agent Roles

The user-facing agent identity should be durable and continuous across contexts.

Roles are contextual projections of that identity. A role may have scoped goals, tone, memory access, capabilities, permissions, routines, and environmental assumptions.

Role separation does not require identity fragmentation.

The product should bias toward one continuous agent identity that occupies different roles across contexts, rather than many disconnected pseudo-people with isolated personalities.

Internal agents, subagents, evaluators, monitors, and workers may exist, but they should not present themselves as separate durable entities.

Delegated roles should inherit only the context, memory, loops, and authority required for their task.

The framework may contain many roles, but only one overaching personality and identity.

---

## 4. Human Authority Is Primary

The user owns the environment, goals, permissions, memory, and final authority chain.

Agents may recommend, negotiate, warn, refuse, escalate, or ask for review, but they must not obscure authority from the user.

The harness should make clear who requested an action, which role performed it, what loop executed it, what authority allowed it, what data informed it, and what changed as a result.

The agent must assume humans, agents, editors, scripts, and external systems may act on the same environment at the same time.

It should preserve user-owned changes, detect conflicts, avoid destructive overwrites, and surface concurrency risks before proceeding.

The agent should not assume that the world stayed still while it was thinking.

---

## 5. Authority Is Scoped To Role-Bound Loops

Authority is scoped to loops.

Loops are scoped to agent roles.

Agent roles are contextual projections of a durable singular identity, and this should be reflected in the identity structure (see number 6 for more detail).

Tools are assigned to loops, not granted globally to models, providers, identities, or the harness as a whole.

A role may have many loops. A loop may have a specific purpose, memory scope, tool set, permission profile, review behavior, wake condition, failure policy, and recovery path.

The default chat experience should itself be a loop: the default conversation loop.

This default conversation loop may feel similar to current chat interfaces, but architecturally it is still a governed loop with scoped tools, memory access, review rules, and authority boundaries.

The user should be able to invoke alternate loops intentionally, such as with a command like `/myloop`, where that loop runs with its own preconfigured authority, tools, routines, memory scope, and review expectations.

A model does not become trusted in general.

A tool does not become safe in general.

An agent role does not gain ambient permission to act everywhere merely because it was useful somewhere.

A loop may earn authority for a specific class of work, in a specific scope, under specific constraints, with specific review and recovery expectations.

That authority may grow through repeated success, clear traces, user correction, evaluation, and explicit delegation.

Review is not a separate ceremony bolted onto autonomy. It is what happens when a loop approaches or crosses an authority boundary.

Authority boundaries may include new targets, new data classes, new side effects, increased blast radius, recurrence, external publication, irreversible action, stale assumptions, conflicting state, or uncertain user intent.

The goal is not maximal restriction.

The goal is durable delegation that becomes more capable without becoming less accountable.

---

## 6. Personality, Memory and other facets of Identity Must Be Written, Injected, Cited, And Consolidated

Identity is not transcript residue.

Identity is part of the agent body and should be treated as governed, inspectable state. 

At the local scope, identity should work through an explicit memory pointer. In ordinary project and workspace contexts, that pointer should resolve to a concrete memory document such as `MEMORY.md`. Local contextually based identity concepts should override higher order ones (a local personality trait ).

`MEMORY.md` and `PERSONALITY.md` should contain appended preferences, attitudes, opinions, memories, corrections, demotions, and forgetting records. It should not be a vague rolling summary of the transcript.

Agents should update this document while running when meaningful durable information is learned, corrected, demoted, or forgotten.

Every appended memory should cite the exact conversation-history event, tool event, file event, user instruction, or environmental observation that triggered the memory formation.

A memory without provenance is suspect.

The local memory/personality documents should be injected once during conversation, session, or loop bootstrap when operating inside its scope.

It should be re-evaluated during compaction, because compaction is precisely the moment when durable state must be separated from disposable transcript.

It may also be refreshed after explicit memory writes, workspace changes, role changes, resumed loops, stale-context detection, or user request.

The memory/personality document should not be blindly re-injected every turn.

If the document is too large to inject directly, the harness should inject a curated memory pack, recent memory changes, high-relevance records, and a visible pointer to the full memory document.

Local memory/personality should remain scoped. A role memory/personality artifact, and an identity level memory/personality artifact are not the same thing.

The harness should also support a higher-order memory system that operates above local context, similar to how a durable personality profile exists above role-specific behavior. 

This higher-order memory system may use embeddings, vector stores, summaries, clustering, typed records, or other retrieval structures, but it must remain inspectable and governed.

The higher-order memory system should not silently absorb everything. It should promote, demote, merge, forget, and expand memories through explicit memory workflows.

The harness should strongly consider a dreaming or consolidation system: a recurring process where agents reason concretely over accumulated memories, identify contradictions, compress stale detail, promote stable patterns, demote weak assumptions, forget obsolete facts, and propose expansions to durable identity, personality, preference, and project knowledge. Personality should likely have a similar higher order construct built into it as well.

Dreaming is not mystical. It is deliberate offline memory (and likely personality) maintenance and more importantly GROWTH.

A memory system that cannot explain where a belief came from, where it applies, when it was last reinforced, how it enters context, and how it can be corrected will eventually poison the agent body.

---

## 7. Failure Is A Core Workflow

Agent work will fail.

The harness should expect partial completion, stale assumptions, invalid tool calls, interrupted loops, permission denial, concurrent edits, bad memories, provider outages, malformed outputs, broken integrations, and repeated loop failure.

Failures should be captured, explained, recoverable, and resumable when practical.

Repeatedly failing loops should not thrash forever. They should degrade, pause, escalate, repair themselves when safe, or surface the problem to the user.

Failure should be available as an explicit agent action when the model is genuinely stuck.

A failed loop should leave behind enough trace for a human or agent to understand what happened and decide what to do next.

Failure is not merely an exception path. It is one of the normal ways durable work progresses.

---

## 8. Evaluation And Replay Are Core Capabilities

A serious agent body needs ways to inspect past behavior and improve future behavior.

The harness should support traces, replay, comparison, regression checks, policy tests, memory audits, tool-call inspection, loop health evaluation, and failure review where practical.

Agents should improve through evidence, not vibes.

A system that cannot learn from its own behavior will accumulate superstition.

Replay should support both debugging and governance: what happened, why it happened, what loop ran, what role executed it, what context was available, what authority was active, what memory was injected, what tools were assigned, and what would happen differently under a revised loop, model, memory, permission, or policy.

---

## 9. Models Are Replaceable Cognitive Engines

The harness should be model-agnostic.

Models are interchangeable cognitive engines with different costs, strengths, weaknesses, latencies, modalities, context windows, inference locations, and trust profiles.

The harness should own routing, fallback, provenance, evaluation, and replacement.

No provider, model family, inference location, or API contract should become the identity of the system.

Model agnosticism does not require provider neutrality.

The project should prioritize providers that are compatible with open, user-owned, inspectable agent harnesses.

The project should not bend over backward to support providers that are hostile to open-source agent harness development, restrictive toward agentic use, or structurally opposed to user-owned agent bodies.

Anthropic should be treated as the current concrete example of this concern.

Support for such providers may exist, but they should not shape the architecture, constrain the product philosophy, or become a dependency of the agent body.

---

## 10. Local-First, Cloud-Compatible

The long-term direction should favor local ownership, local state, local auditability, and local execution where practical. A virtually free close to cutting edge model will soon be practically indistinguishable, and rarely less preferable than a bleeding edge model for most common work, meaning local first inferencing should be adopted as an assumed trajectory for the next generation of agentic tooling.

Cloud models and services are valid when they materially improve capability, reliability, cost, or convenience, but it is our opinion that the current gap in capability will materially degrade over time.

Local-first does not mean local-only, particularly in regard to UI surfaces.

It means the user should retain meaningful ownership, portability, and inspectability. This decision is primarily one surrounding cost and security (a local agent reading a secret token is much less concerning due to the lack of http transmission).

As local inference improves, the harness should be positioned to move more cognition, memory, evaluation, and routine execution onto user-owned infrastructure.

---

## 11. State Must Be Visible to Both Agents and Users (Honesty Above All else)

Both users and agents are most effective when they are fully informed. The harness should externalize meaningful state to accomplish this.

Plans, pending actions, memory candidates, queued reviews, failed tasks, permission requests, observations, retries, unresolved conflicts, loop health, tool calls, and authority decisions should be inspectable.

Invisible state breeds superstition. Visible state enables trust, repair, collaboration, and accountability.

The user should not need to reverse-engineer the agent’s operational state from a transcript.

A durable agent body should have observable posture, not merely observable messages.

Applying --verbose should also allow the user to view what happens to context when it gets compacted, system prompting being injected, and any context loaded into the engine at the current time as they are interacting with a chat, loop, or some other interaction surface. 

---

## 12. Time Is A First-Class Runtime Dimension

Agents must be able to reason about time.

Messages, events, observations, tool calls, memory writes, scheduled jobs, failed actions, wakeups, reviews, and external state should be timestamped where relevant.

The harness should support work across turns, sessions, jobs, interruptions, and long-running routines.

A durable agent that cannot tell what happened, when it happened, and whether something is stale is not durable enough.

Staleness should be treated as a runtime concern, not merely a reasoning mistake.

---

## 13. Actuation Belongs To The Body

Tools are how the agent body touches the world. An agent that is properly embodied in its environment and a bad model can (and will) outperform a better model that lives in a weaker body/harness.

A model-facing tool, human-facing button, slash command, scheduled action, local script, MCP integration, API adapter, browser automation, file watcher, or background job is not merely an implementation detail when it produces side effects.

It is an actuator of the agent body.

Actuators should be assigned to loops.

Actuators should produce visible observations, traces, side-effect summaries, and replayable records wherever practical.

The harness should not allow implementation category to bypass accountability.

A cron job, button, model tool call, MCP server, script, and background worker may look different in code, but they are all part of the same body when they alter the user’s environment.

The important question is not “who clicked it?”

The important question is:

> What body touched the world, under what role's authority, using what loop's authority, and what changed?

Every actuator expands the body’s reach.

That reach should be visible, testable, bounded, and recoverable when practical.

External content, tool output, generated code, repository text, emails, documents, logs, and other agents may all contain hostile or misleading instructions.

Those materials may inform reasoning, but they must not silently gain authority over a loop or actuator.

---

## 14. Prefer Compatible Conventions Over Private Invention

The harness should prefer existing ecosystem conventions when they do not compromise the agent body model.

Private formats, custom manifest names, bespoke configuration schemes, and proprietary conventions should require a reason.

Compatibility is not obedience to the ecosystem.

It is a bias toward portability, familiarity, and reduced friction.

For example, EmbodySense should default to `AGENTS.md` and similar existing conventions rather than inventing `EMBODYSENSE.md` by default.

A private EmbodySense-specific convention is acceptable only when existing conventions cannot express the required body, loop, memory, permission, or governance semantics.

---

## 15. Surfaces Are Projections Of One Runtime

The same underlying runtime may be exposed through a web UI, TUI, CLI, editor, mobile surface, API, notification system, or embedded panel.

Surfaces should not create incompatible authority models.

A loop built visually, invoked from a terminal, triggered by a webhook, or reviewed in a browser should remain the same governed object.

The product should choose primary surfaces carefully, but surface choice is not the axiom.

One or two surfaces may be primary. Other surfaces should be projections or adapters over the same runtime, not competing runtimes with separate authority semantics.

---

# Guidance For Implementers

EmbodySense is not primarily an IDE plugin.

It is not primarily a chat application.

It is not a prompt marketplace.

It is not an opaque plugin runtime.

It is not a repo-only developer tool.

It is not a toy companion.

It is not a system for maximizing autonomous action at the expense of user authority.

It is not successful merely because it can call tools.

It is successful when durable agent loops can safely inhabit user-owned environments, act within explicit authority, preserve useful memory, expose their state, recover from failure, and become more trustworthy, effective, and valuable in their environmental context over time.

The harness should be useful to engineers first, but it should not be architecturally trapped as a developer-only tool.

Good defaults matter, but defaults are scaffolding, not doctrine.

The first-run experience should create a useful environment with conservative but powerful defaults.

Defaults should teach the system’s mental model without pretending to be universal.

Any default agent role, loop, folder, file, permission, routine, model, memory policy, or surface should be replaceable without compromising the harness.

A default should help the user understand the agent body, not trap the user inside the author’s preferred workflow.

---

# Review Checklist

A proposed feature, integration, agent role, UI, storage mechanism, memory behavior, loop, tool, or provider choice should be judged by these questions:

1. Does it make durable loops easier to create, inspect, govern, resume, or improve?
2. Is this authority scoped to a loop, and is that loop scoped to an agent role?
3. Does it preserve the singular durable identity while allowing contextual roles?
4. Does it clarify or muddy scope?
5. Does it make state more visible or more hidden?
6. Does it treat memory as written, cited, scoped, injected, and governable?
7. Does it support both local memory and higher-order memory consolidation?
8. Does it handle failure and recovery?
9. Does it remain portable and inspectable?
10. Does it reduce dependence on any one model, provider, surface, or implementation accident?
11. Does it prefer existing conventions like `AGENTS.md` where practical?
12. Does it make the agent body more real without making it less accountable?
