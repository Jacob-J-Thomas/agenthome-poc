# Security Baseline

GitHub Actions runs CodeQL for C# and JavaScript/TypeScript on pull requests, pushes to `main`, and a weekly schedule. Dependency Review runs only when manifests, lockfiles, project metadata, or workflows change and blocks newly introduced high- or critical-severity vulnerabilities.

Dependabot is configured for NuGet, npm, and GitHub Actions updates. Repository administrators should enable Dependabot alerts, secret scanning, and push protection in the GitHub security settings when those capabilities are available for this public repository.

Treat CodeQL, Dependency Review, Dependabot, and secret-scanning alerts as owned by the pull-request author until triaged. A dismissal or emergency exception must identify the alert, explain why it is not actionable or why the risk is temporarily accepted, name an owner, and set a review date. Do not weaken workflow permissions or run untrusted pull-request code with repository secrets to silence a finding.
