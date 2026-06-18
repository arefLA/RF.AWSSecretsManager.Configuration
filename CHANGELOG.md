# Changelog

All notable changes to **RF.AWSSecretsManager.Configuration** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

_(nothing yet)_

## [1.1.0] - 2026-06-18

Modernizes the target frameworks and fixes a dependency-compatibility issue from 1.0.0. No
public API or runtime behavior changes.

### Changed
- **Target frameworks are now `net8.0` and `net10.0`.** `Microsoft.Extensions.*` dependencies are
  referenced at per-framework floors (`8.0.0` for net8.0, `10.0.0` for net10.0).

### Fixed
- 1.0.0 targeted `net6.0` while referencing `Microsoft.Extensions.*` 10.0.3, which does not
  support net6.0 — net6.0 consumers were pulled onto an unsupported dependency graph. Resolved by
  the per-framework dependency floors above.

### Removed
- **`net6.0` target framework (out of support).** net6.0 applications should pin to `1.0.x` or
  upgrade to .NET 8 or later. This is the reason for the minor version bump.

## [1.0.0] - 2026-03-03

Initial release. Adds **AWS Secrets Manager** as an `IConfiguration` source for .NET (`net6.0`).

### Added
- `AddAWSSecretsManager(...)` configuration-builder extensions, with overloads for:
  - default AWS SDK client,
  - an `ILogger`,
  - an `ILoggerFactory` (+ optional category name),
  - a custom `IAmazonSecretsManager` client,
  - an `AWSSecretsManagerOptions` configure delegate,
  - and a full overload combining client, options, and logging.
- `AWSSecretsManagerOptions` with `MaskSecretNameInLogs` and `SecretNameMaskStyle` (`PrefixAndSuffix`).
- `AWSSecretsManagerLoggerCategory.DefaultCategoryName` (`RF.AWSSecretsManager.Configuration`).
- JSON secret loading via **System.Text.Json**, flattening nested objects with `:`.
- Retry with exponential backoff for transient AWS errors (`MaxRetries=3`, `BaseDelayMs=1000`).
- Secret-name masking in logs; **secret values are never logged**.
- SourceLink + symbol package (`snupkg`).

### Behavior (v1)
- Loads once at startup; no polling/refresh.
- Secret is required; missing secret or invalid/binary JSON fails startup with `InvalidOperationException`.
- JSON arrays are intentionally ignored; keys are loaded as-is with no prefix.

[Unreleased]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/releases/tag/v1.0.0
