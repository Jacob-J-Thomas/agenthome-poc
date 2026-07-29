# Contributing

## Documentation

Production projects emit XML documentation files. Add accurate XML documentation to public and protected contracts and to any internal contract whose behavior crosses a layer, persistence boundary, tool authority boundary, or concurrency boundary. Cover meaningful parameters, return values, cancellation, exceptions, ordering, lifecycle constraints, and authority or integrity invariants.

Use ordinary comments for non-obvious algorithms, state transitions, failure handling, and protocol mappings. Comments must explain verified intent; they must not restate syntax or describe aspirational behavior as implemented.

The repository is completing its existing documentation baseline in issue #83. CS1591 remains explicitly suppressed only while that inventory is completed. A pull request must not claim the missing-documentation gate is enabled until the suppression is removed and the complete production baseline is verified.

## Pull requests

Describe the contract, behavior, or documentation slice changed; list the validation performed; and identify any deliberate follow-up work. Keep source, tests, diagrams, and README status statements aligned with the behavior that actually ships.
