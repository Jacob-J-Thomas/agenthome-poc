# ADR 0001: Engineer-first local workspace

## Status

Accepted for POC.

## Context

Agent harnesses often bind state to a UI, provider, or chat session. That makes workflows unstable when users move between tools.

## Decision

AgentHome will treat the local `.agent/` workspace as the durable source of truth.

The first audience is engineers and technical users who are comfortable with files, config, CLIs, and source control.

## Consequences

- The POC can avoid consumer onboarding concerns.
- Plain files are preferred over a database.
- The CLI can be the first surface.
- The environment can be inspected and versioned.
