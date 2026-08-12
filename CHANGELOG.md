# Changelog

All notable changes to ItemAPI are documented in this file. The format is based
on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
intends to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) for
future releases.

## [Unreleased]

### Added

- Community health documentation, GitHub contribution templates, repository
  hygiene rules, and expanded adoption information.

## [1.2.3] - 2026-06-05

### Fixed

- Corrected the console lookup command's `StringView` argument conversion.

## [1.2.2] - 2026-04-16

### Added

- Added `itemapi.lookup <shortName>` for server-console lookup testing.

## [1.2.1] - 2026-04-15

### Added

- Added `rarity` and `category` to normalized item data and public DTOs.

### Fixed

- Preserved rarity and category values while merging duplicate items.

## [1.2.0] - 2026-04-15

### Added

- Added the optional Carbon item endpoint and merged its results with the
  primary RustHelp data source.
- Expanded README and developer integration documentation.

## [1.0.0] - 2026-03-14

### Added

- Initial ItemAPI plugin, default configuration, cache support, documentation,
  lookup APIs, refresh commands, and update hooks.

[Unreleased]: https://github.com/scardotdev/ItemAPI/compare/v1.2.3...HEAD
[1.2.3]: https://github.com/scardotdev/ItemAPI/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/scardotdev/ItemAPI/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/scardotdev/ItemAPI/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/scardotdev/ItemAPI/compare/v1.0.0...v1.2.0
[1.0.0]: https://github.com/scardotdev/ItemAPI/releases/tag/v1.0.0
