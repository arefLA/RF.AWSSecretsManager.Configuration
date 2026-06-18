using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RF.AWSSecretsManager.Configuration;

/// <summary>
/// Default logger category name used when creating a logger from <see cref="ILoggerFactory"/> and no category is specified.
/// </summary>
public static class AWSSecretsManagerLoggerCategory
{
    /// <summary>
    /// Default category name for AWS Secrets Manager configuration provider log messages.
    /// </summary>
    public const string DefaultCategoryName = "RF.AWSSecretsManager.Configuration";
}

/// <summary>
/// Extension methods for adding AWS Secrets Manager as a configuration source.
/// </summary>
public static class AWSSecretsManagerConfigurationExtensions
{
    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client configuration.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName)
        => builder.AddAWSSecretsManager(secretName, client: null, configure: null, logger: null, loggerFactory: null,
            loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client configuration and a logger.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="logger">The logger to use.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        ILogger logger)
        => builder.AddAWSSecretsManager(secretName, client: null, configure: null, logger: logger, loggerFactory: null,
            loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client and logging from an <see cref="ILoggerFactory"/>.
    /// Useful in DI scenarios where you have <see cref="ILoggerFactory"/> (e.g. from host or <see cref="IServiceProvider"/>) and want a dedicated category.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="loggerFactory">The logger factory to create a logger from.</param>
    /// <param name="loggerCategoryName">Optional category name for the logger. When <c>null</c>, <see cref="AWSSecretsManagerLoggerCategory.DefaultCategoryName"/> is used.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        ILoggerFactory loggerFactory,
        string? loggerCategoryName = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger(loggerCategoryName ?? AWSSecretsManagerLoggerCategory.DefaultCategoryName);
        return builder.AddAWSSecretsManager(secretName, client: null, configure: null, logger: logger, loggerFactory: null, loggerCategoryName: null);
    }

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using a custom <see cref="IAmazonSecretsManager"/> client.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="client">The AWS Secrets Manager client to use.</param>
    /// <param name="logger">The logger to use, or <c>null</c> to disable logging.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        IAmazonSecretsManager client,
        ILogger? logger = null)
        => builder.AddAWSSecretsManager(secretName, client, configure: null, logger: logger, loggerFactory: null,
            loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client configuration and options.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="configure">Delegate used to configure <see cref="AWSSecretsManagerOptions"/>.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        Action<AWSSecretsManagerOptions> configure)
        => builder.AddAWSSecretsManager(secretName, client: null, configure: configure, logger: null,
            loggerFactory: null, loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source with full control over client, options, and logging.
    /// Logging can be supplied as an <see cref="ILogger"/> directly, or via <see cref="ILoggerFactory"/> and an optional category name (e.g. for DI).
    /// If both <paramref name="logger"/> and <paramref name="loggerFactory"/> are provided, <paramref name="logger"/> is used.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="client">The AWS Secrets Manager client to use. When <c>null</c>, a default client is created.</param>
    /// <param name="configure">Delegate used to configure <see cref="AWSSecretsManagerOptions"/>.</param>
    /// <param name="logger">The logger to use, or <c>null</c> to disable logging (or use <paramref name="loggerFactory"/> when provided).</param>
    /// <param name="loggerFactory">Optional logger factory; when provided and <paramref name="logger"/> is <c>null</c>, a logger is created using <paramref name="loggerCategoryName"/>.</param>
    /// <param name="loggerCategoryName">Category name when creating a logger from <paramref name="loggerFactory"/>. Ignored if <paramref name="loggerFactory"/> is <c>null</c>. When <c>null</c>, <see cref="AWSSecretsManagerLoggerCategory.DefaultCategoryName"/> is used.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        IAmazonSecretsManager? client = null,
        Action<AWSSecretsManagerOptions>? configure = null,
        ILogger? logger = null,
        ILoggerFactory? loggerFactory = null,
        string? loggerCategoryName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(secretName))
            throw new ArgumentException("Secret name must be provided.", nameof(secretName));

        var options = new AWSSecretsManagerOptions();
        configure?.Invoke(options);

        var resolvedLogger = logger ?? loggerFactory?.CreateLogger(loggerCategoryName ?? AWSSecretsManagerLoggerCategory.DefaultCategoryName);

        var delayStrategy = new ThreadSleepDelayStrategy();
        var useProvidedClient = client is not null;
        client ??= new AmazonSecretsManagerClient();

        var source = new AWSSecretsManagerConfigurationSource(
            secretName,
            client,
            options,
            resolvedLogger,
            delayStrategy,
            disposeClient: !useProvidedClient);

        builder.Add(source);
        return builder;
    }
}

