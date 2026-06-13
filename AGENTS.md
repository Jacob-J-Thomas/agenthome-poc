# Agent instructions for this repository

You are working on EmbodySense.

## Scope authority

- Treat `docs/OPINIONATED_PROJECT_AXIOMS.md` as the hardest repo-local scope anchor for EmbodySense product direction, harness capabilities, architecture, tooling, governance, and roadmap decisions.
- Read the axiom file before making design, scope, architecture, tooling, governance, or roadmap decisions for this application.
- Do not infer product scope from README usage notes, AGENTS instructions, stale status text, code comments, diagrams, or the current implementation shape.
- README and AGENTS text can describe how to operate or contribute to the repo, but they do not narrow the product vision or define the intended final scope.
- If source code, README, AGENTS, diagrams, memories, or prior implementation notes conflict with the axioms or with the user's latest direction, stop and report the conflict before implementing.
- If a requested harness capability is broad or ambiguous, perform a read-only design pass first and explicitly tie the design back to the axioms before editing code.
- Do not reduce "agent tooling" to human-only slash commands unless the user explicitly asks for slash-command tooling. Agent tooling normally means model-accessible, governed capabilities with permissions, approvals, and auditability.

## Code style

- Prefer single-line method calls and argument lists.
- Do not split method arguments across multiple lines unless there are more than 3 arguments, or keeping one line would make the code genuinely hard to read.
- When a call must be split, use the smallest readable split and avoid cascading vertical formatting through nearby code.

## Implementation discipline

- Keep changes direct and aligned with the existing C# solution unless the axioms and the user's request justify a broader design.
- Avoid dependencies unless they buy something concrete.
- Do not describe aspirational agent-loop behavior as implemented unless it exists in source.
- If documentation claims a capability that the source does not contain, treat that as a documentation/source mismatch and report it before filling in code.
- If source contains partial or accidental work, do not treat it as project direction without user confirmation.

## Documentation maintenance

- Keep `README.md` aligned with the real CLI behavior.
- Keep `docs/AGENT_LOOP.drawio` aligned with the real implementation whenever the harness loop, inference path, workspace scaffolding, permissions, or audit behavior changes.
- Treat `docs/AGENT_LOOP.drawio` as editable source for diagrams.net / draw.io, not as a generated screenshot.
- Do not let README runtime-status language read like scope. Label status snapshots as status, and route scope questions back to the axioms.

## Repository orientation

- `docs/OPINIONATED_PROJECT_AXIOMS.md`: product-direction and scope anchor.
- `README.md`: human-facing usage notes and implementation-status snapshot, not scope authority.
- `docs/AGENT_LOOP.drawio`: editable draw.io source diagram that must match implemented loop behavior.
- `EmbodySense.sln`: solution entry point for Visual Studio and solution builds.
- `src/`: application source.
- `tests/`: repository test projects.
