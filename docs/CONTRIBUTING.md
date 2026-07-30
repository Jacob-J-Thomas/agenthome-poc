# Contributing

## Code style

Use `PascalCase` for public types and members, positional-record properties, and compile-time constants. Use `camelCase` for ordinary parameters and local variables, `_camelCase` for private instance, static, and readonly fields, `ITypeName` for interfaces, and `TName` for type parameters.

Use `_` for intentionally unused lambda and anonymous-method parameters, including when the anonymous function has only one parameter. C# keeps a lone `_` addressable rather than treating it as a compiler discard, but the repository uses it as an explicit unused-value convention. If the value is read, give it a descriptive `camelCase` name instead.

The root `.editorconfig` is the reviewed C# naming policy. Positional-record parameters and the unused `_` convention are checked by the syntax-aware integration gate because EditorConfig cannot distinguish those cases from ordinary parameters. Generated compiler output remains outside the gate through the solution/project boundary; do not add blanket IDE1006 suppressions for authored source.

Run the same deterministic checks used by pull-request verification:

```powershell
dotnet format whitespace EmbodySense.sln --verify-no-changes --no-restore
dotnet format style EmbodySense.sln --verify-no-changes --no-restore --severity warn --diagnostics IDE1006
npm run lint
npm run format:check
```

Remove `--verify-no-changes` from the whitespace command to apply whitespace fixes. IDE1006 renames should be made deliberately so call sites and serialized or reflection-sensitive names can be reviewed.

## Documentation

Production projects emit XML documentation files. Add accurate XML documentation to public and protected contracts and to any internal contract whose behavior crosses a layer, persistence boundary, tool authority boundary, or concurrency boundary. Cover meaningful parameters, return values, cancellation, exceptions, ordering, lifecycle constraints, and authority or integrity invariants.

Use ordinary comments for non-obvious algorithms, state transitions, failure handling, and protocol mappings. Comments must explain verified intent; they must not restate syntax or describe aspirational behavior as implemented.

CS1591 is unsuppressed for every production project and fails the warning-as-error build when a public or protected contract is undocumented. Do not add a broad `NoWarn`, authored-source exclusion, or replacement suppression. A genuinely necessary generated-code exclusion must identify the generator and explain why its emitted API is outside the authored contract.

## Pull requests

Describe the contract, behavior, or documentation slice changed; list the validation performed; and identify any deliberate follow-up work. Keep source, tests, diagrams, and README status statements aligned with the behavior that actually ships.
