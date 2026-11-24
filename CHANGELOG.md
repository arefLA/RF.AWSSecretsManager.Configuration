# Changelog

All notable changes to **RF.AWSSecretsManager.Configuration** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

_(nothing yet)_

## [1.2.0] - 2026-11-24

Plaintext secrets, an options-based way to configure the provider, and three fixes. Existing
JSON-object secrets and existing `AddAWSSecretsManager` calls keep working; the behavior changes
are listed under "Changed".

### Added
- **Plaintext secrets.** A secret whose text does not start with `{` or `[` is loaded as one
  configuration key holding the text exactly as stored. The key is the last `/`-separated part of
  the secret name (`prod/myapp/db-password` gives `db-password`).
- `AWSSecretsManagerOptions.PlaintextKey` to choose that key yourself, including hierarchical keys
  such as `ConnectionStrings:Default`.
- **Options-based entry point:** `AddAWSSecretsManager(Action<AWSSecretsManagerOptions>)`, with new
  options `SecretName`, `Client`, `Logger`, `LoggerFactory` and `LoggerCategoryName`.
- Overloads `AddAWSSecretsManager(secretName, client)` and
  `AddAWSSecretsManager(secretName, loggerFactory)`.
- The package now includes its XML documentation, so editors show the descriptions of methods and
  options.
- Warnings in the log for plaintext values that are probably not what was meant: a value that looks
  like JSON with something in front of or around it, a single-line value ending in a line break, a
  value starting with a byte-order mark, and a JSON secret that yields no keys while `PlaintextKey`
  is set.

### Fixed
- `AddAWSSecretsManager("name", client)` did not compile (CS0121, ambiguous overloads). The same
  applied to a nullable client, `client: client` and `loggerFactory: factory`. Present in 1.0.0 and
  1.1.0.
- When the package created the AWS client, building configuration again after an earlier provider
  had been disposed failed with `ObjectDisposedException` (also reachable through repeated provider
  rebuilds on `ConfigurationManager`). The package now uses a separate client for each load and
  disposes it when the load ends. Present in 1.0.0 and 1.1.0.

### Security
- An invalid-JSON error could quote one character of the secret, in the log and in the exception.
  The error now reports only the line and byte position, and the `InvalidOperationException` no
  longer has the `JsonException` as its inner exception.
- With `MaskSecretNameInLogs` enabled, AWS exceptions are no longer attached to log entries, because
  AWS error messages can contain the secret name. Exceptions thrown to the caller are unchanged.

### Changed
- Text that is not JSON and does not start with `{` or `[` no longer fails startup; it loads as one
  plaintext key (see "Added"). Text starting with `{` or `[` is always read as JSON, even when
  `PlaintextKey` is set, and still fails startup when it is not valid JSON.
- A secret that is a single JSON value (`12345`, `true`, `"text"`, `null`) now loads as one key
  holding the raw text. Before, it loaded no keys.
- New or changed error messages: a whitespace-only secret reports "is empty"; valid JSON nested
  deeper than 64 levels reports the depth limit; a `PlaintextKey` with an empty `:` segment fails
  startup.
- AWS error log entries now read `(ExceptionType, error code X, HTTP N, request id R)`. The retry
  warning reads "attempt N of 3 failed, retrying after … ms", and an error is logged when all
  attempts have failed. Empty, binary and whitespace-only secrets are logged as errors before the
  exception is thrown.
- `logger` and `loggerCategoryName` are no longer optional on the overloads
  `(secretName, client, logger)` and `(secretName, loggerFactory, loggerCategoryName)`; the two new
  two-parameter overloads take their place. A blank logger category falls back to the default
  category.
- A `configure` delegate runs after the parameters of its overload have been applied, so it can
  replace the secret name, client and logging passed as parameters.
- **Source-breaking in one corner case:** `AddAWSSecretsManager(null!)` with an untyped `null` is
  now ambiguous (CS0121). Use `(string)null!`. This call always failed at run time.
- When the package creates the AWS clients, one is created per load in addition to the one created
  at registration. A client you supply is used as before and is never disposed by the package.

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

[Unreleased]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/arefLA/RF.AWSSecretsManager.Configuration/releases/tag/v1.0.0
