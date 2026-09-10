# AGENTS.md

These instructions apply to the entire repository and are intended for coding agents and developers. AI Usage Dashboard is a .NET/WPF desktop application for Windows x64.

## Documentation

| Task | Read first |
| --- | --- |
| User features and operation | [User guide](使用說明.md) |
| Architecture, account storage, and provider integrations | [Technical overview](docs/TECHNICAL_OVERVIEW.md) |
| Data formats, CLI support, and upgrades across versions | [Compatibility policy](docs/COMPATIBILITY_POLICY.md) and [CLI compatibility](docs/CLI_COMPATIBILITY.md) |
| Installation, recovery, and releases | [Distribution guide](INTERNAL_DISTRIBUTION.md) and [Release process](RELEASING.md) |
| Completed work and pending verification | [Implementation and verification checklist](IMPLEMENTATION_CHECKLIST.md) |

## Development

- Make the smallest change that completes the task. Follow existing layering, data access, process execution, and testing patterns; avoid incidental refactoring.
- Follow each C# file's conventions: Allman braces, tab indentation, explicit access modifiers, and nullable checks. Match surrounding type and member naming; do not casually rename persisted or protocol fields.
- Write ordinary comments in Traditional Chinese. Keep necessary rationale, constraints, and contracts across systems; put implementation explanations in documentation.
- Preserve error causes and actionable context; never silently ignore errors. Observe asynchronous results and provide complete cleanup paths for resources and child processes.
- Update relevant documentation when product behavior changes. User documentation describes the current product; keep development history and private verification records out of general guides.

## Compatibility and safety

- Future development must follow the [compatibility policy](docs/COMPATIBILITY_POLICY.md): prioritize preserving existing data, settings, valid connections, and CLI versions that remain safe and compatible, minimizing manual steps during upgrades.
- Changes to persisted formats, authentication bindings, providers, CLI/SDK dependencies, or the Updater must verify existing users' upgrade paths and include relevant validation. Necessary breaking changes must document their reasons, impact, and migration/recovery plan.
- Do not weaken source, signature, account isolation, protocol, or usage safety checks for compatibility. Do not import browser cookies or share credentials between account cards.
- Keep tokens, private keys, real account data, and raw usage output out of Git, distributed artifacts, and public logs. Ordinary tests use synthetic data and must not read personal login state.
- Follow the [controlled acceptance procedure](INTERNAL_DISTRIBUTION.md#cli-相容性維護) for live CLI login or usage queries. First confirm the authorized platforms, operations, and execution counts; Claude queries may consume usage or incur charges.

## Build and verification

- Use Windows x64 and the SDK pinned in [global.json](global.json). Follow [Windows CI](.github/workflows/windows-ci.yml) for restore, Release builds, tests, and coverage checks; do not lower warning, dependency audit, or verification thresholds.
- Run affected existing tests for routine changes. Compatibility behavior changes require de-identified fixtures for older formats/responses and regression tests across versions. Keep tests reproducible and independent of personal accounts or live external responses.
- Check links, UTF-8 encoding, and privacy when adding or editing documents. Documentation changes usually do not need new tests that only compare text.
- Report checks actually performed and items left unverified. Record unit tests, installation tests, and live CLI tests separately; do not apply historical results to a new candidate.

## Git and releases

- Git commits, branch or tag creation, pushes, and public releases require explicit user instructions. A general request to continue does not grant new authorization for Git writes.
- Use Conventional Commits with an English type and a short Traditional Chinese subject, without AI attribution. Destructive Git operations are prohibited.
- Propose a plan before substantial changes to CI, infrastructure, or release settings, and implement it only after explicit approval. Routine documentation and code fixes stay within the authorization for the current task.
- Validate release candidates according to [RELEASING.md](RELEASING.md). Do not replace frozen artifacts, publish different bytes under the same version, or treat a push request as authorization to publish a Release.
