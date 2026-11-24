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
    /// Creates a client for one load when this package owns the clients; <c>null</c> when the caller supplied
    /// the client.
    /// </summary>
    private readonly Func<IAmazonSecretsManager>? _clientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AWSSecretsManagerConfigurationSource"/> class.
    /// </summary>
    /// <param name="secretName">The name of the secret to read.</param>
    /// <param name="client">The AWS Secrets Manager client.</param>
    /// <param name="options">The options controlling provider behavior.</param>
    /// <param name="logger">The logger to use, or <c>null</c> to disable logging.</param>
    /// <param name="delayStrategy">The delay strategy used for retry backoff.</param>
    /// <param name="disposeClient">
    /// If <c>true</c>, every provider built from this source disposes <paramref name="client"/> when the provider
    /// is disposed. Because all those providers share the one client, pass <c>true</c> only when this source is
    /// built once; a provider built after another one was disposed would get a disposed client. The
    /// <c>AddAWSSecretsManager</c> extension methods never pass <c>true</c> for a client supplied by the caller,
    /// and use a separate client for every load when they create the clients themselves.
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
    /// Initializes a source whose clients are created, and therefore owned, by this package: every load uses a
    /// client of its own and disposes it when the load ends.
    /// </summary>
    /// <remarks>
    /// One client is created here, so a missing region or missing credentials fail at registration. It is
    /// exposed through <see cref="SecretsManagerClient"/> and is not used for loading: every load creates a
    /// client of its own from <paramref name="clientFactory"/> and disposes it when the load ends. Providers
    /// hold no client. One client shared by all providers and disposed by each would break any provider built
    /// after an earlier one was disposed.
    /// </remarks>
    internal AWSSecretsManagerConfigurationSource(
        string secretName,
        Func<IAmazonSecretsManager> clientFactory,
        AWSSecretsManagerOptions options,
        ILogger? logger,
        IDelayStrategy delayStrategy)
    {
        SecretName = secretName ?? throw new ArgumentNullException(nameof(secretName));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Logger = logger;
        DelayStrategy = delayStrategy ?? throw new ArgumentNullException(nameof(delayStrategy));

        // DisposeClient stays false: it is about disposing a supplied client with the provider. Package-owned
        // clients are disposed when their load ends instead (see OwnsClients).

        // Last, so that no client is created (and left undisposed) when an argument is rejected.
        SecretsManagerClient = CreateClient();
    }

    /// <summary>
    /// Gets the name of the secret to read.
    /// </summary>
    public string SecretName { get; }

    /// <summary>
    /// Gets the AWS Secrets Manager client.
    /// </summary>
    /// <remarks>
    /// For a client supplied by the caller, this is that client, used by every provider. When the
    /// <c>AddAWSSecretsManager</c> extension methods created the client, this is the client created at
    /// registration. It stays valid: the package does not use it for loading (each load has a client of its
    /// own) and never disposes it. The source is not disposable, so that one client lives as long as the
    /// source does.
    /// </remarks>
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
    /// Gets a value indicating whether the supplied client should be disposed with the provider. Always
    /// <c>false</c> when <see cref="OwnsClients"/> is <c>true</c>: package-owned clients are disposed when their
    /// load ends, not with the provider.
    /// </summary>
    internal bool DisposeClient { get; }

    /// <summary>
    /// Gets a value indicating whether this package creates and disposes the clients (no client was supplied).
    /// </summary>
    internal bool OwnsClients => _clientFactory is not null;

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new AWSSecretsManagerConfigurationProvider(this);
    }

    /// <summary>
    /// Package-owned mode: returns a new client for one load. The caller disposes it when the load ends.
    /// </summary>
    internal IAmazonSecretsManager TakeClientForLoad() => CreateClient();

    private IAmazonSecretsManager CreateClient()
        => _clientFactory!() ?? throw new InvalidOperationException("The AWS Secrets Manager client factory returned null.");
}

