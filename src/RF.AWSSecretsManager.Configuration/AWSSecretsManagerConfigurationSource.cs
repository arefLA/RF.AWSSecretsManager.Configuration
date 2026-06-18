using Amazon.SecretsManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RF.AWSSecretsManager.Configuration;

/// <summary>
/// Represents an AWS Secrets Manager configuration source.
/// </summary>
public sealed class AWSSecretsManagerConfigurationSource : IConfigurationSource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AWSSecretsManagerConfigurationSource"/> class.
    /// </summary>
    /// <param name="secretName">The name of the secret to read.</param>
    /// <param name="client">The AWS Secrets Manager client.</param>
    /// <param name="options">The options controlling provider behavior.</param>
    /// <param name="logger">The logger to use, or <c>null</c> to disable logging.</param>
    /// <param name="delayStrategy">The delay strategy used for retry backoff.</param>
    /// <param name="disposeClient">
    /// If <c>true</c>, the provider will dispose the supplied client when it is disposed. The default extensions
    /// currently do not dispose the client to match typical AWS SDK usage patterns.
    /// </param>
    public AWSSecretsManagerConfigurationSource(
        string secretName,
        IAmazonSecretsManager client,
        AWSSecretsManagerOptions options,
        ILogger? logger,
        IDelayStrategy delayStrategy,
        bool disposeClient)
    {
        SecretName = secretName ?? throw new ArgumentNullException(nameof(secretName));
        SecretsManagerClient = client ?? throw new ArgumentNullException(nameof(client));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Logger = logger;
        DelayStrategy = delayStrategy ?? throw new ArgumentNullException(nameof(delayStrategy));
        DisposeClient = disposeClient;
    }

    /// <summary>
    /// Gets the name of the secret to read.
    /// </summary>
    public string SecretName { get; }

    /// <summary>
    /// Gets the AWS Secrets Manager client.
    /// </summary>
    public IAmazonSecretsManager SecretsManagerClient { get; }

    /// <summary>
    /// Gets the logger to use, if any.
    /// </summary>
    public ILogger? Logger { get; }

    /// <summary>
    /// Gets the options controlling provider behavior.
    /// </summary>
    public AWSSecretsManagerOptions Options { get; }

    /// <summary>
    /// Gets the delay strategy used for retry backoff.
    /// </summary>
    public IDelayStrategy DelayStrategy { get; }

    /// <summary>
    /// Gets a value indicating whether the underlying client should be disposed with the provider.
    /// </summary>
    internal bool DisposeClient { get; }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new AWSSecretsManagerConfigurationProvider(this);
    }
}

