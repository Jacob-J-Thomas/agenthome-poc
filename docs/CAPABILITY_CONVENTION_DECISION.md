# Capability contract convention decision

Status: accepted for experimental schema version 1

Scope: dependency-free capability identity, descriptor, compatibility, provenance, lifecycle-state, serialization, and hashing contracts

Issue: [#204](https://github.com/Jacob-J-Thomas/agenthome-poc/issues/204)

## Decision drivers

EmbodySense needs one stable vocabulary across triggers, graph nodes, actuators, context sources, model profiles, observations, evaluations, skills, hooks, and surface adapters. That vocabulary must remain useful for local capabilities and future packaged extensions without allowing supplied metadata to grant trust, enablement, assignment, or authority. Axiom 5 keeps authority on role-bound loops, and Axiom 14 prefers compatible conventions when they fit.

Schema 1 therefore uses established conventions for version meaning, ranges, schemas, content identity, platforms, and provenance concepts. It introduces a small EmbodySense identity syntax only where no surveyed convention expresses a provider-namespaced capability across all of those kinds.

## Survey and disposition

| Convention | Disposition | Schema-1 use or reason |
| --- | --- | --- |
| [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html) | Adopted with bounds | Exact capability versions use strict `MAJOR.MINOR.PATCH`, optional prerelease, and optional build metadata. Abbreviations, leading zeroes, whitespace, normalization, and non-ASCII input are rejected. Numeric components are bounded to signed 32-bit values so hostile input cannot force unbounded arithmetic. SemVer precedence ignores build metadata; total ordering uses the exact canonical string only as a deterministic tie-breaker. |
| [NuGet version ranges](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning#version-ranges) | Adapted | Compatible ranges use NuGet's explicit interval forms, including `[1.2.3]`, `[1.0.0,2.0.0)`, and unbounded endpoints. Schema 1 additionally uses `*` as its single canonical any-version value and rejects bare/floating versions, whitespace, empty intervals, build metadata in endpoints, and alternate spellings. This contract checks membership only; dependency resolution remains outside #204. |
| [JSON Schema Draft 2020-12](https://json-schema.org/draft/2020-12) | Adopted | Input and output contracts are bounded JSON objects that must explicitly identify the Draft 2020-12 dialect. #204 preserves them as canonical machine-readable schemas but does not add a schema evaluator. |
| [RFC 8785 JSON Canonicalization Scheme](https://www.rfc-editor.org/rfc/rfc8785.html) | Adapted | Descriptor and embedded-schema canonicalization adopts compact output, recursive ordinal property ordering, stable array order, duplicate-property rejection, finite IEEE-754 numbers, negative-zero rejection, and invalid-Unicode rejection. The BCL-only schema-1 writer is the authoritative byte representation; it does not claim to be a general-purpose JCS implementation for arbitrary JSON. Declared set-like collections are sorted before writing so caller enumeration order cannot affect identity. |
| [OCI content descriptors and digests](https://github.com/opencontainers/image-spec/blob/main/descriptor.md) | Adapted | Canonical hashes and integrity evidence use OCI's `sha256:` plus 64 lowercase hexadecimal form. Platform compatibility uses the OCI-style operating-system/architecture tuple. OCI media types, artifact transport, embedded data, annotation bags, and registry behavior are not adopted here. |
| [SLSA provenance](https://slsa.dev/spec/v1.2/provenance) | Adapted | Provenance means bounded evidence of where an implementation came from: source category, safe canonical source URI, optional revision, and optional integrity digest. It is deliberately not a trust claim. Complete attestations and verification policy belong to later lifecycle and packaging work. |
| [Package URL / ECMA-427](https://github.com/package-url/purl-spec) | Deferred for package location | A query-free canonical `pkg:` URI may identify a package source, but PURL is not the capability ID: it identifies a package and may encode version and qualifiers, while one package can supply several capability kinds. Full package import/export remains owned by #192. |
| [Model Context Protocol capability negotiation](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle) | Rejected as the shared identity/lifecycle model | MCP capability negotiation is connection-scoped feature discovery. It does not supply the exact version, provenance, installation, health, removal, or loop-authority model required here. MCP tools may later map into governed capability descriptors, but discovery never implies assignment or permission. |

## Schema-1 identity and compatibility

- `CapabilityId` is `lowercase.provider/provider-owned/path`. The provider portion is a bounded DNS-style namespace with at least two labels; each path segment is bounded lowercase ASCII. Inputs must already be canonical. This avoids Unicode confusables, locale-sensitive comparison, implicit normalization, and collisions between unrelated providers.
- `CapabilityProviderId` uses the same bounded provider namespace. `CapabilityImplementationIdentity` pairs it with a provider-owned implementation path. Capability identity and implementation identity are separate so implementations can change without renaming the capability contract.
- `CapabilityKind` is a closed schema-1 category list. Domain-specific behavior stays with the owning roadmap issues.
- `CapabilityVersion` and `CapabilityVersionRange` compare culture-independently. Range membership uses SemVer precedence, so build metadata remains part of exact version identity but is rejected from range endpoints where it would create a meaningless alternate spelling.
- `CapabilityCompatibility` declares a host-contract range plus one or more unique platforms. `any/any` cannot be mixed with specific platforms.

## Descriptor and authority boundary

`CapabilityDescriptor` contains only stable identity, exact version, implementation identity, safe provenance, compatibility, purpose, input/output schemas, resource limits, side-effect class, and declarations of data, egress, and secret-reference needs.

It has no metadata bag and no fields for trusted, authorized, enabled, installed, assigned, granted, approved, or private configuration. The closed JSON reader rejects every unknown field, including a field attempting to smuggle those claims or a secret value. Source URIs reject user information, query strings, and fragments; secret requirements contain names only.

Existence, installation, enablement, health, retirement, and server verification are separate axes in `CapabilityLifecycleSnapshot`. That server-owned snapshot still contains no loop assignment or authority. Grants and delegation remain owned by #187, and secret-value resolution remains owned by #188.

## Deterministic identity and bounds

The canonical descriptor JSON excludes lifecycle state because mutable server state cannot be part of descriptor identity. `CapabilityDescriptorHash` hashes those canonical UTF-8 bytes using SHA-256, and `CapabilityDescriptorIdentity` pins the stable ID, exact version, and digest.

All strings, collections, schemas, recursion, numeric resource declarations, and the overall descriptor JSON are bounded by `CapabilityContractLimits`. Parsing and descriptor validation return stable code/field/message errors. Malformed versions, alternate spellings, unsafe Unicode, duplicate JSON properties, oversized schemas, unknown fields, ambiguous platform sets, and inconsistent egress declarations fail closed.

## Explicitly deferred

- catalog persistence and lifecycle mutation;
- dependency resolution and admission;
- installation, artifact retrieval, executable hosting, signatures, and marketplaces;
- loop assignment, grants, approvals, and authority evaluation;
- secret values and private implementation configuration;
- domain-specific trigger, node, actuator, context, model, observation, or evaluation semantics;
- migration or compatibility readers beyond schema version 1;
- changes to `LoopCapabilityIds`, runtime composition, persistence, Startup, CLI, Web, or API surfaces.
