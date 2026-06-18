using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace RF.AWSSecretsManager.Configuration.Tests;

public class AWSSecretsManagerConfigurationProviderTests
{
    [Fact]
    public void Load_valid_flat_json_populates_keys()
    {
        // Arrange
        const string secretJson = "{\"A\":\"1\",\"B\":2,\"C\":true}";

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretJson }));

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("A", out var a).Should().BeTrue();
        a.Should().Be("1");

        provider.TryGet("B", out var b).Should().BeTrue();
        b.Should().Be("2");

        provider.TryGet("C", out var c).Should().BeTrue();
        c.Should().Be("True");
    }

    [Fact]
    public void Load_nested_json_flattens_with_colon()
    {
        // Arrange
        const string secretJson = "{\"ConnectionStrings\":{\"Database\":\"x\"}}";

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretJson }));

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("ConnectionStrings:Database", out var value).Should().BeTrue();
        value.Should().Be("x");
    }

    [Fact]
    public void Arrays_are_ignored_in_v1()
    {
        // Arrange
        const string secretJson = "{\"Arr\":[1,2]}";

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretJson }));

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert - no keys for array elements should be present
        provider.TryGet("Arr", out _).Should().BeFalse();
        provider.TryGet("Arr:0", out _).Should().BeFalse();
        provider.TryGet("Arr:1", out _).Should().BeFalse();
    }

    [Fact]
    public void Missing_secret_fails_startup()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(new ResourceNotFoundException("not found")));

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "missing/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*was not found*");
    }

    [Fact]
    public void Invalid_json_fails_startup()
    {
        // Arrange
        const string secretJson = "{not-json";

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretJson }));

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "invalid/json",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*does not contain valid JSON*");
    }

    [Fact]
    public void Transient_errors_are_retried()
    {
        // Arrange
        const string secretJson = "{\"A\":\"1\"}";

        var client = Substitute.For<IAmazonSecretsManager>();

        var callCount = 0;
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;

                if (callCount < 3)
                {
                    return Task.FromException<GetSecretValueResponse>(
                        new InternalServiceErrorException("Transient error"));
                }

                return Task.FromResult(new GetSecretValueResponse { SecretString = secretJson });
            });

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert
        callCount.Should().Be(3);
        delay.Delays.Should().BeEquivalentTo(new[] { 1000, 2000 }, options => options.WithStrictOrdering());

        provider.TryGet("A", out var a).Should().BeTrue();
        a.Should().Be("1");
    }

    [Fact]
    public void Non_transient_errors_do_not_retry()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();

        var callCount = 0;
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                return Task.FromException<GetSecretValueResponse>(
                    new AmazonSecretsManagerException("Non-transient error"));
            });

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = Substitute.For<ILogger>();

        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<AmazonSecretsManagerException>();
        callCount.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public void Never_logs_secret_values()
    {
        // Arrange
        const string secretJson = "{\"Password\":\"super-secret-value\"}";

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretJson }));

        var options = new AWSSecretsManagerOptions();
        var delay = new RecordingDelayStrategy();
        var logger = new TestLogger();

        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert
        logger.Messages.Should().NotContain(m =>
            m.Contains("super-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void Secret_name_masking_when_enabled()
    {
        // Arrange
        const string secretJson = "{\"A\":\"1\"}";

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretJson }));

        var options = new AWSSecretsManagerOptions
        {
            MaskSecretNameInLogs = true
        };

        var delay = new RecordingDelayStrategy();
        var logger = new TestLogger();

        const string secretName = "my/secret/name";

        var source = new AWSSecretsManagerConfigurationSource(
            secretName,
            client,
            options,
            logger,
            delay,
            disposeClient: false);

        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert
        var combined = string.Join(Environment.NewLine, logger.Messages);

        combined.Should().NotContain(secretName);
        combined.Should().Contain("my/***me");
    }
}

internal sealed class RecordingDelayStrategy : IDelayStrategy
{
    public List<int> Delays { get; } = new();

    public void Delay(int milliseconds)
    {
        Delays.Add(milliseconds);
    }
}

internal sealed class TestLogger : ILogger
{
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages;

    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;

        if (exception != null)
        {
            message = $"{message} {exception.Message}";
        }

        _messages.Add(message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

