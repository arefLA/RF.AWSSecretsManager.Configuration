## RF.AWSSecretsManager.Configuration

**RF.AWSSecretsManager.Configuration** is a small, production-ready NuGet package that adds **AWS Secrets Manager** as an `IConfiguration` source for .NET applications.

It loads a secret from AWS Secrets Manager and exposes it as standard configuration key/value pairs. The secret can be a JSON object (nested objects are flattened) or a single plaintext value such as a password or an API key.

### Install

Using the .NET CLI:

```bash
dotnet add package RF.AWSSecretsManager.Configuration --version 1.2.0
```

**Supported target frameworks:** `net8.0` and `net10.0`.

### Secret Formats

A secret can be stored in two ways. The package looks at the first character of the secret text to decide which one it is:

- It starts with `{` or `[` → it is read as **JSON**.
- Anything else → it is read as a single **plaintext** value.

#### JSON secrets

This is what the AWS console produces in its "Key/value" editor:

```json
{
  "A": "1",
  "B": 2,
  "C": true
}
```

Nested objects are flattened using `:` as a separator:

```json
{
  "ConnectionStrings": {
    "Default": "Server=my;Database=db;User Id=user;Password=password"
  },
  "MySetting": "Some value"
}
```

Becomes configuration keys:

- `ConnectionStrings:Default` → connection string
- `MySetting` → `"Some value"`

> **Note:** V1 intentionally **ignores JSON arrays**. They do not produce configuration keys and do not cause errors. See the roadmap below.
>
> Secret JSON is parsed with **System.Text.Json** only; there is no dependency on Newtonsoft.Json. JSON nested deeper than 64 levels is rejected.

#### Plaintext secrets

A secret that holds a single value (the AWS console's "Plaintext" editor, a database password, an API key) is loaded as **one configuration key**. The value is stored exactly as it is in AWS, with nothing trimmed or changed.

The key is the last part of the secret name, after the last `/`:

| Secret name | Secret text | You read it with |
|---|---|---|
| `prod/myapp/db-password` | `hunter2` | `configuration["db-password"]` |
| `ApiKey` | `abc123` | `configuration["ApiKey"]` |

Because only the last part is used, the same code works for `dev/myapp/db-password` and `prod/myapp/db-password`.

To choose the key yourself, set `PlaintextKey`. It can be a hierarchical key:

```csharp
var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager(options =>
    {
        options.SecretName = "prod/myapp/db-password";
        options.PlaintextKey = "ConnectionStrings:Default";
    })
    .Build();

var connectionString = configuration.GetConnectionString("Default");
```

Set `PlaintextKey` as well when you load two plaintext secrets whose names end in the same word (for example `a/password` and `b/password`); otherwise both use the key `password` and the one added later wins.

> **Important: a value that starts with `{` or `[` is always read as JSON, even when `PlaintextKey` is set.**
>
> - If such a value is not valid JSON, startup fails with "does not contain valid JSON".
> - If it happens to be valid JSON (for example `[1]` or `{}`), it loads **no keys**.
>
> This matters most for **rotated passwords**: a generated password can begin with `{` or `[`, so a rotation can break startup. To avoid it, exclude those characters when the password is generated (the `ExcludeCharacters` setting of `GetRandomPassword`), or store the password inside a JSON object, for example `{ "Password": "..." }`.

Other things to know about plaintext secrets:

- **A trailing line break is kept.** A secret created with `--secret-string file://secret.txt` usually ends in a newline, and that newline becomes part of the value. A warning is logged for a single-line value that ends in a line break.
- **Text that is not JSON no longer fails startup.** Up to 1.1.0, a secret that was not valid JSON stopped the app at startup. Now, unless it starts with `{` or `[`, it loads as one plaintext value. A warning is logged when a plaintext value looks like JSON with something in front of it or wrapped around it (for example extra quotes added by a shell).
- **Byte-order mark.** A JSON secret saved as "UTF-8 with BOM" still fails startup; the error message names the byte-order mark. A plaintext secret keeps the mark as part of the value, and a warning is logged.
- A secret that is empty or contains only whitespace fails startup. Binary secrets are not supported.

### Usage

#### 1. Quick start

This uses the default AWS SDK credential and region resolution (environment variables, shared credentials file, IAM role, etc.).

```csharp
using Microsoft.Extensions.Configuration;
using RF.AWSSecretsManager.Configuration;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddAWSSecretsManager("my/secret/name")
    .Build();

// Example: if secret JSON contains { "MySetting": "Some value" }
var value = configuration["MySetting"];
```

When you already have a configuration builder instance (for example in ASP.NET Core minimal hosting), you can call the extension on the `Configuration` property directly. The default host wiring already adds JSON configuration providers:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddAWSSecretsManager("my/secret/name");
```

#### 2. All settings in one place (recommended)

Every setting is a property of `AWSSecretsManagerOptions`. Pass one delegate and set what you need:

```csharp
using Microsoft.Extensions.Configuration;
using RF.AWSSecretsManager.Configuration;

var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager(options =>
    {
        options.SecretName = "my/secret/name";   // required
        options.MaskSecretNameInLogs = true;
        options.LoggerFactory = loggerFactory;   // optional
    })
    .Build();
```

| Option | What it does |
|---|---|
| `SecretName` | Name or ARN of the secret. Required. |
| `Client` | Your own `IAmazonSecretsManager`. When not set, the package creates default clients. |
| `Logger` | An `ILogger` to log to. |
| `LoggerFactory` | Used to create a logger when `Logger` is not set. |
| `LoggerCategoryName` | Category for that logger. Default: `RF.AWSSecretsManager.Configuration`. |
| `MaskSecretNameInLogs` | Hide most of the secret name in logs. Default: `false`. |
| `PlaintextKey` | Key for a plaintext secret. Default: the last part of the secret name. |

The other overloads shown below are shortcuts that set the same options from their parameters. When an overload also takes a `configure` delegate, the delegate runs last, so a value it sets replaces the matching parameter.

#### 3. Custom `IAmazonSecretsManager` client

If you want full control over credentials, region, HTTP pipeline, or to reuse an existing client:

```csharp
using Amazon;
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using RF.AWSSecretsManager.Configuration;

IAmazonSecretsManager client = new AmazonSecretsManagerClient(RegionEndpoint.USEast1);

var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", client)
    .Build();
```

The provider will **not dispose** the client in this case. The same client is used every time the configuration is built.

When you do not pass a client, the package creates default clients itself: one for each load of the secret, disposed as soon as that load ends. You can build the configuration more than once without any extra setup.

**Using a specific region or AWSOptions (e.g. ASP.NET Core)**  
This package does not take `AWSOptions` directly (to avoid an extra dependency). Build an `IAmazonSecretsManager` yourself and pass it to the custom-client overload. The package targets `net8.0` and `net10.0`, so it can be consumed by any .NET 8 or later application.

> **Note:** Do **not** use `GetAWSOptions().CreateServiceClient<IAmazonSecretsManager>()` from AWSSDK.Extensions.NETCore.Setup. That API is incompatible with `IAmazonSecretsManager` on .NET 10 (and with newer AWS SDK versions) due to static abstract interface members. Create the client explicitly instead.

**Option 1 – Region from config, credentials from default chain**

```csharp
using Amazon;
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using RF.AWSSecretsManager.Configuration;

var regionName = configuration["AWS:Region"];  // e.g. "us-east-1" from appsettings or env
var region = !string.IsNullOrWhiteSpace(regionName)
    ? RegionEndpoint.GetBySystemName(regionName.Trim())
    : null;

IAmazonSecretsManager? client = region != null
    ? new AmazonSecretsManagerClient(region)
    : null;  // null = provider uses default SDK client and region

var config = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", client)
    .Build();
```

**Option 2 – Fixed region in code**

```csharp
IAmazonSecretsManager client = new AmazonSecretsManagerClient(RegionEndpoint.USEast1);
var config = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", client)
    .Build();
```

Credentials always use the default AWS SDK chain (environment variables, shared credentials file, IAM role, etc.). In ASP.NET Core with DI, register `IAmazonSecretsManager` (e.g. create the client from your config as above and register it as a singleton) and pass the resolved client into `AddAWSSecretsManager`. A future version may add an overload that accepts `AWSOptions` and creates the client inside the package (see Roadmap).

#### 4. Logger overload

You can pass an `ILogger` to receive information and error logs (secret values are never logged):

```csharp
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RF.AWSSecretsManager.Configuration;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger("AwsSecrets");

var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", logger)
    .Build();
```

#### 5. Masking secret name in logs

To prevent the raw secret name from appearing in logs, enable masking via `AWSSecretsManagerOptions`:

```csharp
using Microsoft.Extensions.Configuration;
using RF.AWSSecretsManager.Configuration;

var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", options =>
    {
        options.MaskSecretNameInLogs = true;
        // Current masking style: first 3 characters + "***" + last 2 characters.
        // Example: "my/secret/name" -> "my/***me"
    })
    .Build();
```

The same options are available when you supply a custom client:

```csharp
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RF.AWSSecretsManager.Configuration;

IAmazonSecretsManager client = /* build or resolve client */;
ILogger? logger = /* optional logger */;

var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager(
        secretName: "my/secret/name",
        client: client,
        configure: options =>
        {
            options.MaskSecretNameInLogs = true;
        },
        logger: logger)
    .Build();
```

> **Important:** Secret **values are never logged**, regardless of masking settings. Only the secret name (masked or unmasked) and error details are logged.

With masking enabled, AWS error details in the log are reduced to the error type, the AWS error code, the HTTP status and the request id, because full AWS error messages can contain the secret name. Masking applies to **logs only**: an exception that reaches your own code (for example the startup error) is not changed and may still name the secret.

#### 6. Logging with ILoggerFactory (DI-friendly)

When you have an `ILoggerFactory` (e.g. from the host or `IServiceProvider`), use the overload that accepts it and an optional category name. This fits well with dependency injection and ensures log messages use a consistent category.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RF.AWSSecretsManager.Configuration;

ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", loggerFactory)
    .Build();
```

With a custom category name (default is `RF.AWSSecretsManager.Configuration`):

```csharp
var configuration = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", loggerFactory, loggerCategoryName: "MyApp.AwsSecrets")
    .Build();
```

#### 7. Dependency injection (Generic Host / WebApplicationBuilder)

You can wire AWS Secrets Manager configuration so that the **client** and/or **logger** come from the service provider. Build configuration in two steps: first add sources that don’t need DI (e.g. JSON files), then add the secret source using services.

**Option A – Resolve ILoggerFactory from a pre-built service provider**

```csharp
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RF.AWSSecretsManager.Configuration;

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
// Optionally register and use IAmazonSecretsManager from DI:
// services.AddSingleton<IAmazonSecretsManager>(sp => new AmazonSecretsManagerClient());
var serviceProvider = services.BuildServiceProvider();

var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddAWSSecretsManager(options =>
    {
        options.SecretName = "my/secret/name";
        options.Client = serviceProvider.GetService<IAmazonSecretsManager>();  // null = use default clients
        options.MaskSecretNameInLogs = true;
        options.LoggerFactory = loggerFactory;
        options.LoggerCategoryName = "MyApp.AwsSecrets";
    })
    .Build();
```

**Option B – With ASP.NET Core / Generic Host**

Configuration is usually built before the host’s `IServiceProvider` exists, so you typically use the default client and either no logger or a logger you create before the host (e.g. from a temporary `LoggerFactory`). If you build configuration inside host setup and have access to `ILoggerFactory` or `IAmazonSecretsManager` from the host builder’s services, you can do:

```csharp
// Example: inside ConfigureAppConfiguration when you have access to ILoggerFactory later
var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);
        // Default client; no logger until host is built, or use a one-off LoggerFactory if needed
        config.AddAWSSecretsManager("my/secret/name", options => options.MaskSecretNameInLogs = true);
    })
    .Build();
```

To use a logger from the host once the app is running, the provider loads at startup and does not re-read the secret; logging from the provider uses whatever logger you passed at configuration-build time. For DI-integrated logging at startup, build a `LoggerFactory` (or get one from a minimal service provider) and pass it into `AddAWSSecretsManager` as shown in Option A.

### Security Notes

- **Never log secrets.** This package is designed to avoid logging any secret values.
- Always use **least-privilege IAM policies**, scoping access to only the secrets your application needs.
- Consider network boundaries and VPC endpoints when accessing AWS Secrets Manager from production environments.
- Use AWS Key Management Service (KMS) and automatic rotation where appropriate.

#### Minimal IAM policy example

Adjust the resource ARN to your own secret:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "secretsmanager:GetSecretValue"
      ],
      "Resource": "arn:aws:secretsmanager:us-east-1:123456789012:secret:my/secret/name-*"
    }
  ]
}
```

### Behavior Summary (V1)

- Loads once at startup; **no polling or auto-refresh**.
- Uses the **default AWS SDK credential chain** by default, or a custom `IAmazonSecretsManager` client when provided.
- Secret is required. Each of these throws `InvalidOperationException` and fails startup:
  - The secret does not exist (`ResourceNotFoundException`).
  - The secret is empty, contains only whitespace, or is a binary secret.
  - The secret starts with `{` or `[` but is not valid JSON. The message gives the line and byte position of the problem. It never includes any part of the secret.
  - `PlaintextKey` has an empty `:` segment (for example `A::B`).
- A secret that starts with `{` or `[` is read as JSON; anything else is loaded as **one plaintext key** (see [Secret Formats](#secret-formats)).
- Arrays in the JSON are **ignored**, not projected into configuration keys.
- Keys are loaded **as-is**, with no prefixing.
- Transient AWS errors (throttling, timeouts, 5xx, `InternalServiceErrorException`) are retried with **exponential backoff**:
  - `MaxRetries = 3`, `BaseDelayMs = 1000` → delays of 1000 ms, 2000 ms between attempts.
  - Each failed attempt is logged as a warning, and an error is logged when all attempts have failed.
- Other AWS errors (for example access denied) are logged and rethrown as they are.
- Secret values are never logged.

### Sample App

A minimal console sample is provided in `sample/RF.AWSSecretsManager.Configuration.Sample`.

It:

- Uses `ConfigurationBuilder` with the options overload of `AddAWSSecretsManager`.
- Demonstrates enabling secret name masking and logging through an `ILoggerFactory`.
- Reads the secret name from `appsettings.json` (`AWSSecretsManager:SecretName`) or, if that is empty, from the `AWS_SAMPLE_SECRET_NAME` environment variable. It needs a real secret name, an AWS region and credentials to run; with no secret name set it prints usage instructions instead.

### Roadmap

Planned future enhancements:

- **Optional mode**: allow startup to succeed when the secret is missing.
- **Polling / refresh support**: periodic reload with `IChangeToken` integration.
- **Array support**: project arrays as `Key:0`, `Key:1`, etc.
- **Prefix/scoping options**: load secrets under a configurable prefix (e.g. `Secrets:`).
- **Multiple secrets support**: merge multiple secrets and/or match by name patterns.
- **Caching controls / TTL**: fine-grained control over how long secrets are cached.
- **Binary secrets support**: handle `SecretBinary` with configurable decoding.
- **KMS / rotation friendly patterns**: better integration with rotation, health checks, and metrics.
- **AWSOptions overload**: accept `AWSOptions` (e.g. from AWSSDK.Extensions.NETCore.Setup) and create `IAmazonSecretsManager` inside the package, so callers can pass options instead of a pre-built client.

