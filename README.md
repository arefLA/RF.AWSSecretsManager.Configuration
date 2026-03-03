## RF.AWSSecretsManager.Configuration

**RF.AWSSecretsManager.Configuration** is a small, production-ready NuGet package that adds **AWS Secrets Manager** as an `IConfiguration` source for .NET applications.

It allows you to load a JSON secret from AWS Secrets Manager and expose it as standard configuration key/value pairs, including flattened nested objects.

### Install

Using the .NET CLI:

```bash
dotnet add package RF.AWSSecretsManager.Configuration --version 1.0.0
```

### Secret JSON Format

The package expects the AWS Secrets Manager secret to be stored as **JSON text**. Examples:

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
> Secret JSON is parsed with **System.Text.Json** only; there is no dependency on Newtonsoft.Json.

### Usage

#### 1. Default AWS client (recommended for most apps)

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

#### 2. Custom `IAmazonSecretsManager` client

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

The provider will **not dispose** the client in this case.

**Using AWSOptions or DI (e.g. ASP.NET Core)**  
If your app uses [AWSSDK.Extensions.NETCore.Setup](https://www.nuget.org/packages/AWSSDK.Extensions.NETCore.Setup/) and configures AWS via `AWSOptions` (e.g. from `appsettings.json` or environment), create the client from those options and pass it in. The package does not take `AWSOptions` directly (to avoid an extra dependency); you build the client and use the custom-client overload:

```csharp
using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using RF.AWSSecretsManager.Configuration;

// If using AWSSDK.Extensions.NETCore.Setup and AddDefaultAWSOptions / GetAWSOptions():
var awsOptions = configuration.GetAWSOptions();  // or from builder.Configuration in host apps
IAmazonSecretsManager client = awsOptions.CreateServiceClient<IAmazonSecretsManager>();

var config = new ConfigurationBuilder()
    .AddAWSSecretsManager("my/secret/name", client)
    .Build();
```

In ASP.NET Core with DI, you can register the client and use it when building configuration (e.g. in a pre-built service provider), or resolve `IAmazonSecretsManager` from the host and pass it into `AddAWSSecretsManager` as above. A future version may add an overload that accepts `AWSOptions` and creates the client inside the package (see Roadmap).

#### 3. Logger overload

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

#### 4. Masking secret name in logs

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

#### 5. Logging with ILoggerFactory (DI-friendly)

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

#### 6. Dependency injection (Generic Host / WebApplicationBuilder)

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
    .AddAWSSecretsManager(
        "my/secret/name",
        client: serviceProvider.GetService<IAmazonSecretsManager>(),  // null = use default client
        configure: options => options.MaskSecretNameInLogs = true,
        logger: null,
        loggerFactory: loggerFactory,
        loggerCategoryName: "MyApp.AwsSecrets")
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
- Secret is required:
  - `ResourceNotFoundException` → throws `InvalidOperationException` and fails startup.
  - Invalid JSON → throws `InvalidOperationException` and fails startup.
- Arrays in the JSON are **ignored**, not projected into configuration keys.
- Keys are loaded **as-is**, with no prefixing.
- Transient AWS errors (throttling, timeouts, 5xx, `InternalServiceErrorException`) are retried with **exponential backoff**:
  - `MaxRetries = 3`, `BaseDelayMs = 1000` → delays of 1000 ms, 2000 ms between attempts.

### Sample App

A minimal console sample is provided in `sample/RF.AWSSecretsManager.Configuration.Sample`.

It:

- Uses `ConfigurationBuilder` with `AddAWSSecretsManager`.
- Demonstrates enabling secret name masking.
- Only calls AWS when the `AWS_SAMPLE_SECRET_NAME` environment variable is set, so `dotnet run` works locally without AWS.

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

