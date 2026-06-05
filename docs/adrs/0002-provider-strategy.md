# ADR 0002: Provider strategy

## Status

Accepted for POC planning. Not implemented in Phase 0.

## Context

Supporting every direct LLM API creates maintenance burden and encourages provider churn.

## Decision

First-class provider direction is:

- OpenAI direct API for individual developers.
- Azure AI / Azure OpenAI for enterprise Microsoft environments.
- AWS Bedrock for enterprise AWS environments.
- Local/self-hosted inference for privacy, cost, experimentation, and resilience.

Direct Anthropic is not a founding pillar.

## Consequences

- Provider abstraction should be role/capability-based.
- Direct Anthropic support should not block the core design.
- Anthropic-family models may still be accessible through AWS or Azure provider paths.
