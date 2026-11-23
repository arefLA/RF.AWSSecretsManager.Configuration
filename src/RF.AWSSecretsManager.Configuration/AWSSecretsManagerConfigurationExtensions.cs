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
/// <remarks>
/// <see cref="AddAWSSecretsManager(IConfigurationBuilder, Action{AWSSecretsManagerOptions})"/> is the primary entry
/// point: every setting is a property of <see cref="AWSSecretsManagerOptions"/>. The other overloads are shortcuts
/// that fill the same options from their parameters.
/// </remarks>
public static class AWSSecretsManagerConfigurationExtensions
{
    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source, configured entirely through
    /// <see cref="AWSSecretsManagerOptions"/>.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="configure">
    /// Delegate that configures the options. It must set <see cref="AWSSecretsManagerOptions.SecretName"/>.
    /// </param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="configure"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">The delegate did not set a secret name.</exception>
    /// <example>
    /// <code>
    /// builder.AddAWSSecretsManager(options =>
    /// {
    ///     options.SecretName = "prod/myapp/settings";
    ///     options.MaskSecretNameInLogs = true;
    /// });
    /// </code>
    /// </example>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        Action<AWSSecretsManagerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AWSSecretsManagerOptions();
        configure(options);

        return builder.AddSource(options, nameof(configure));
    }

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
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client and logging from an
    /// <see cref="ILoggerFactory"/>, with the default category
    /// (<see cref="AWSSecretsManagerLoggerCategory.DefaultCategoryName"/>).
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="loggerFactory">The logger factory to create a logger from.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <c>null</c>.</exception>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        ILoggerFactory loggerFactory)
        => builder.AddAWSSecretsManager(secretName, loggerFactory, loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client and logging from an <see cref="ILoggerFactory"/>.
    /// Useful in DI scenarios where you have <see cref="ILoggerFactory"/> (e.g. from host or <see cref="IServiceProvider"/>) and want a dedicated category.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="loggerFactory">The logger factory to create a logger from.</param>
    /// <param name="loggerCategoryName">Category name for the logger. When <c>null</c> or whitespace, <see cref="AWSSecretsManagerLoggerCategory.DefaultCategoryName"/> is used.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <c>null</c>.</exception>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        ILoggerFactory loggerFactory,
        string? loggerCategoryName)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return builder.AddAWSSecretsManager(secretName, client: null, configure: null, logger: null,
            loggerFactory: loggerFactory, loggerCategoryName: loggerCategoryName);
    }

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using a custom <see cref="IAmazonSecretsManager"/> client.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="client">
    /// The AWS Secrets Manager client to use. It is not disposed by the provider. When <c>null</c>, a default
    /// client is created (and disposed with the provider).
    /// </param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        IAmazonSecretsManager? client)
        => builder.AddAWSSecretsManager(secretName, client, configure: null, logger: null, loggerFactory: null,
            loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using a custom <see cref="IAmazonSecretsManager"/> client
    /// and a logger.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="client">
    /// The AWS Secrets Manager client to use. It is not disposed by the provider. When <c>null</c>, a default
    /// client is created (and disposed with the provider).
    /// </param>
    /// <param name="logger">The logger to use, or <c>null</c> to disable logging.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    public static IConfigurationBuilder AddAWSSecretsManager(
        this IConfigurationBuilder builder,
        string secretName,
        IAmazonSecretsManager? client,
        ILogger? logger)
        => builder.AddAWSSecretsManager(secretName, client, configure: null, logger: logger, loggerFactory: null,
            loggerCategoryName: null);

    /// <summary>
    /// Adds AWS Secrets Manager as a configuration source using the default AWS SDK client configuration and options.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="configure">
    /// Delegate used to configure <see cref="AWSSecretsManagerOptions"/>. It runs after
    /// <paramref name="secretName"/> has been copied into the options, so a
    /// <see cref="AWSSecretsManagerOptions.SecretName"/> it sets replaces the parameter.
    /// </param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="secretName"/> is blank, or <paramref name="configure"/> set
    /// <see cref="AWSSecretsManagerOptions.SecretName"/> to a blank value.
    /// </exception>
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
    /// <remarks>
    /// The parameters are copied into an <see cref="AWSSecretsManagerOptions"/> instance, and then
    /// <paramref name="configure"/> runs. A value the delegate sets therefore replaces the matching parameter.
    /// New code can use <see cref="AddAWSSecretsManager(IConfigurationBuilder, Action{AWSSecretsManagerOptions})"/>
    /// and set everything on the options instead.
    /// </remarks>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <param name="client">The AWS Secrets Manager client to use. When <c>null</c>, a default client is created.</param>
    /// <param name="configure">Delegate used to configure <see cref="AWSSecretsManagerOptions"/>.</param>
    /// <param name="logger">The logger to use, or <c>null</c> to disable logging (or use <paramref name="loggerFactory"/> when provided).</param>
    /// <param name="loggerFactory">Optional logger factory; when provided and <paramref name="logger"/> is <c>null</c>, a logger is created using <paramref name="loggerCategoryName"/>.</param>
    /// <param name="loggerCategoryName">Category name when creating a logger from <paramref name="loggerFactory"/>. Ignored if <paramref name="loggerFactory"/> is <c>null</c>. When <c>null</c> or whitespace, <see cref="AWSSecretsManagerLoggerCategory.DefaultCategoryName"/> is used.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="secretName"/> is blank, or <paramref name="configure"/> set
    /// <see cref="AWSSecretsManagerOptions.SecretName"/> to a blank value.
    /// </exception>
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

        var options = new AWSSecretsManagerOptions
        {
            SecretName = secretName,
            Client = client,
            Logger = logger,
            LoggerFactory = loggerFactory,
            LoggerCategoryName = loggerCategoryName
        };

        configure?.Invoke(options);

        return builder.AddSource(options, nameof(configure));
    }

    /// <summary>
    /// The single place where a configured <see cref="AWSSecretsManagerOptions"/> becomes a configuration source.
    /// Every public overload ends here.
    /// </summary>
    private static IConfigurationBuilder AddSource(
        this IConfigurationBuilder builder,
        AWSSecretsManagerOptions options,
        string configureParameterName)
    {
        if (string.IsNullOrWhiteSpace(options.SecretName))
        {
            throw new ArgumentException(
                $"Secret name must be provided: set {nameof(AWSSecretsManagerOptions)}.{nameof(AWSSecretsManagerOptions.SecretName)}.",
                configureParameterName);
        }

        // A blank category is treated as "not set", like the other text options.
        var categoryName = string.IsNullOrWhiteSpace(options.LoggerCategoryName)
            ? AWSSecretsManagerLoggerCategory.DefaultCategoryName
            : options.LoggerCategoryName;

        var logger = options.Logger ?? options.LoggerFactory?.CreateLogger(categoryName);

        // A client the caller supplied stays the caller's to dispose; one created here is disposed with the provider.
        var clientWasProvided = options.Client is not null;
        var client = options.Client ?? new AmazonSecretsManagerClient();

        builder.Add(new AWSSecretsManagerConfigurationSource(
            options.SecretName,
            client,
            options,
            logger,
            new ThreadSleepDelayStrategy(),
            disposeClient: !clientWasProvided));

        return builder;
    }
}
