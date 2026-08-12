# Contributing to ItemAPI

Thank you for helping improve ItemAPI. Contributions may include bug reports,
documentation, compatibility fixes, and focused feature proposals.

## Before you start

- For usage questions, read `README.md` and `developers.md` first.
- Search existing issues and pull requests to avoid duplicates.
- Discuss large features, API changes, configuration changes, or breaking changes
  in an issue before investing in an implementation.
- Report vulnerabilities privately as described in `SECURITY.md`; do not open a
  public issue for a suspected security problem.
- Follow `CODE_OF_CONDUCT.md` in every project space.

## Development prerequisites

ItemAPI is a C# Rust server plugin targeting Oxide/uMod and Carbon. To exercise
plugin behavior, use a current Rust dedicated server with one of those plugin
frameworks and Newtonsoft.Json available through the framework. There is no
standalone build project or automated test suite in this repository, so a
realistic server installation is the authoritative integration environment.

Fork and clone the repository, then create a topic branch from the default
branch. Do not commit secrets, server data, generated caches, logs, or editor
metadata.

## Making changes

- Keep changes narrowly scoped and preserve compatibility across Oxide/uMod and
  Carbon unless the proposal explicitly changes support.
- Follow `.editorconfig`: four spaces for C#, two spaces for JSON/YAML, UTF-8,
  LF line endings, final newlines, and no trailing whitespace.
- Match the existing C# naming and layout conventions.
- Keep public API return shapes and hooks backward compatible when possible.
- Treat endpoint data as untrusted: handle null, malformed, missing, and
  duplicate values defensively.
- Document changes to public APIs in both `README.md` and `developers.md`.
- Document user-visible changes under `Unreleased` in `CHANGELOG.md`.
- Update `configs/ItemAPI.json` and its README example together when defaults
  change. Never place credentials in the example configuration.

## Validation

Before opening a pull request:

1. Review `git diff --check` and ensure only intended files are tracked.
2. Load or reload `plugins/ItemAPI.cs` on the affected supported framework(s)
   and confirm it compiles without plugin errors.
3. Exercise relevant console commands and `Plugin.Call` methods.
4. For fetching or normalization changes, test successful, partial, malformed,
   empty, and unavailable endpoint responses where practical.
5. For cache changes, test startup with no cache, a valid cache, and an invalid
   cache.
6. Record exact checks, framework/version details, and any untested scenarios in
   the pull request.

Useful smoke checks include `itemapi.status`, `itemapi.refresh`, and
`itemapi.lookup <shortName>` from the server console.

## Commits

Write concise, imperative commit subjects (for example, `Add timeout validation`).
Keep unrelated changes in separate commits. Rebase or otherwise resolve merge
conflicts before requesting final review; avoid noisy formatting-only commits.

## Pull requests

Complete the pull-request template, link related issues, and explain behavior
and compatibility implications. Include logs with secrets, tokens, IP addresses,
and personal information removed. Maintainers may request revisions or close a
change that is out of scope. By contributing, you agree that your contribution
is distributed under this repository's GPL-3.0 license.
