using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RF.AWSSecretsManager.Configuration;

// Minimal sample demonstrating how to use the RF.AWSSecretsManager.Configuration package
// to load configuration from AWS Secrets Manager.
//
// The secret name is read from appsettings.json (AWSSecretsManager:SecretName) or,
// if empty, from the AWS_SAMPLE_SECRET_NAME environment variable. This keeps
// "dotnet run" safe when neither is set.

var bootstrapConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var secretName = bootstrapConfig["AWSSecretsManager:SecretName"]
    ?? Environment.GetEnvironmentVariable("AWS_SAMPLE_SECRET_NAME");

Console.WriteLine($"[DEBUG] SecretName from config/env: '{secretName}'");

if (string.IsNullOrWhiteSpace(secretName))
{
    Console.WriteLine("AWS Secrets Manager sample");
    Console.WriteLine("----------------------------------------------------");
    Console.WriteLine("Set the secret name in appsettings.json:");
    Console.WriteLine("  \"AWSSecretsManager\": { \"SecretName\": \"my/secret/name\" }");
    Console.WriteLine();
    Console.WriteLine("Or set the AWS_SAMPLE_SECRET_NAME environment variable.");
    Console.WriteLine();
    Console.WriteLine("AWS credentials and region are resolved using the");
    Console.WriteLine("default AWS SDK credential/region chain (environment");
    Console.WriteLine("variables, shared credentials file, IAM role, etc.).");
    Console.WriteLine();
    Console.WriteLine("Example JSON stored in the secret:");
    Console.WriteLine("{");
    Console.WriteLine("  \"ConnectionStrings\": {");
    Console.WriteLine("    \"Database\": \"Server=my;Database=db;User Id=u;Password=p\"");
    Console.WriteLine("  },");
    Console.WriteLine("  \"MySetting\": \"Some value\"");
    Console.WriteLine("}");
    return;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

var logger = loggerFactory.CreateLogger("Sample");

// Build configuration with appsettings.json and AWS Secrets Manager.
// Using the ILoggerFactory overload so the provider logs with a dedicated category (DI-friendly).
var configurationBuilder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddAWSSecretsManager(
        secretName,
        client: null,
        configure: options => options.MaskSecretNameInLogs = true,
        logger: null,
        loggerFactory: loggerFactory,
        loggerCategoryName: "Sample.AwsSecrets");

var configuration = configurationBuilder.Build();

// Read a value from configuration. If your secret JSON contains a value like:
// { "MySetting": "Some value" } then this will print "Some value".
var mySetting = configuration["MySetting"] ?? "(null)";

logger.LogInformation("MySetting from configuration: {Value}", mySetting);

Console.WriteLine("Configuration loaded successfully. Press ENTER to exit.");
Console.ReadLine();

