using Amazon.SecretsManager;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace RF.AWSSecretsManager.Configuration.Tests;

public class AddAWSSecretsManagerExtensionsTests
{
    private static AWSSecretsManagerConfigurationSource SingleSource(IConfigurationBuilder builder)
        => builder.Sources.OfType<AWSSecretsManagerConfigurationSource>().Should().ContainSingle().Which;

    [Fact]
    public void Options_overload_registers_a_source_from_the_options()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var logger = Substitute.For<ILogger>();
        var builder = new ConfigurationBuilder();

        // Act
        var returned = builder.AddAWSSecretsManager(options =>
        {
            options.SecretName = "prod/myapp/settings";
            options.Client = client;
            options.Logger = logger;
            options.MaskSecretNameInLogs = true;
            options.PlaintextKey = "ApiKey";
        });

        // Assert
        returned.Should().BeSameAs(builder);
        var source = SingleSource(builder);
        source.SecretName.Should().Be("prod/myapp/settings");
        source.SecretsManagerClient.Should().BeSameAs(client);
        source.Logger.Should().BeSameAs(logger);
        source.Options.MaskSecretNameInLogs.Should().BeTrue();
        source.Options.PlaintextKey.Should().Be("ApiKey");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_overload_requires_a_secret_name(string? secretName)
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var builder = new ConfigurationBuilder();

        // Act
        Action act = () => builder.AddAWSSecretsManager(options =>
        {
            options.SecretName = secretName;
            options.Client = client;
        });

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*AWSSecretsManagerOptions.SecretName*")
            .WithParameterName("configure");
        builder.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Options_overload_rejects_null_arguments()
    {
        // Arrange
        var builder = new ConfigurationBuilder();
        Action<AWSSecretsManagerOptions> configure = options => options.SecretName = "n";

        // Act
        Action nullBuilder = () => ((IConfigurationBuilder)null!).AddAWSSecretsManager(configure);
        Action nullConfigure = () => builder.AddAWSSecretsManager((Action<AWSSecretsManagerOptions>)null!);

        // Assert
        nullBuilder.Should().Throw<ArgumentNullException>().WithParameterName("builder");
        nullConfigure.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [Fact]
    public void Client_overload_registers_the_client_without_a_logger()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var builder = new ConfigurationBuilder();

        // Act: the call that did not compile before 1.2.0 (CS0121).
        builder.AddAWSSecretsManager("my/secret", client);

        // Assert
        var source = SingleSource(builder);
        source.SecretName.Should().Be("my/secret");
        source.SecretsManagerClient.Should().BeSameAs(client);
        source.Logger.Should().BeNull();
    }

    [Fact]
    public void Client_and_logger_overload_registers_both()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var logger = Substitute.For<ILogger>();
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", client, logger);

        // Assert
        var source = SingleSource(builder);
        source.SecretsManagerClient.Should().BeSameAs(client);
        source.Logger.Should().BeSameAs(logger);
    }

    [Fact]
    public void Full_overload_copies_parameters_into_the_options()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var logger = Substitute.For<ILogger>();
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager(
            "my/secret",
            client,
            configure: options => options.MaskSecretNameInLogs = true,
            logger: logger);

        // Assert
        var source = SingleSource(builder);
        source.SecretName.Should().Be("my/secret");
        source.Options.SecretName.Should().Be("my/secret");
        source.Options.Client.Should().BeSameAs(client);
        source.Options.Logger.Should().BeSameAs(logger);
        source.Options.MaskSecretNameInLogs.Should().BeTrue();
    }

    [Fact]
    public void Configure_runs_last_and_replaces_parameter_values()
    {
        // Arrange
        var parameterClient = Substitute.For<IAmazonSecretsManager>();
        var optionsClient = Substitute.For<IAmazonSecretsManager>();
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("from/parameter", parameterClient, configure: options =>
        {
            options.SecretName.Should().Be("from/parameter");
            options.Client.Should().BeSameAs(parameterClient);

            options.SecretName = "from/options";
            options.Client = optionsClient;
        });

        // Assert
        var source = SingleSource(builder);
        source.SecretName.Should().Be("from/options");
        source.SecretsManagerClient.Should().BeSameAs(optionsClient);
    }

    [Fact]
    public void Configure_runs_last_and_replaces_the_logger_parameter()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var parameterLogger = Substitute.For<ILogger>();
        var optionsLogger = Substitute.For<ILogger>();
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", client, logger: parameterLogger, configure: options =>
        {
            options.Logger.Should().BeSameAs(parameterLogger);
            options.Logger = optionsLogger;
        });

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(optionsLogger);
    }

    [Fact]
    public void Configure_runs_last_and_replaces_the_logger_factory_parameters()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var parameterFactory = Substitute.For<ILoggerFactory>();
        var optionsFactory = Substitute.For<ILoggerFactory>();
        var createdLogger = Substitute.For<ILogger>();
        optionsFactory.CreateLogger("From.Options").Returns(createdLogger);
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager(
            "my/secret",
            client,
            loggerFactory: parameterFactory,
            loggerCategoryName: "From.Parameter",
            configure: options =>
            {
                options.LoggerFactory.Should().BeSameAs(parameterFactory);
                options.LoggerCategoryName.Should().Be("From.Parameter");

                options.LoggerFactory = optionsFactory;
                options.LoggerCategoryName = "From.Options";
            });

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(createdLogger);
        parameterFactory.DidNotReceiveWithAnyArgs().CreateLogger(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void String_overloads_require_a_secret_name(string? secretName)
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var builder = new ConfigurationBuilder();

        var configureWasCalled = false;

        // Act
        Action act = () => builder.AddAWSSecretsManager(secretName!, client, configure: _ => configureWasCalled = true);

        // Assert: rejected before the delegate runs, so the delegate cannot "rescue" a blank parameter.
        act.Should().Throw<ArgumentException>().WithParameterName("secretName");
        configureWasCalled.Should().BeFalse();
        builder.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Configure_that_blanks_the_secret_name_is_rejected()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var builder = new ConfigurationBuilder();

        // Act
        Action act = () => builder.AddAWSSecretsManager("my/secret", client, configure: options => options.SecretName = " ");

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*AWSSecretsManagerOptions.SecretName*")
            .WithParameterName("configure");
        builder.Sources.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Custom.Category", "Custom.Category")]
    [InlineData(null, AWSSecretsManagerLoggerCategory.DefaultCategoryName)]
    [InlineData("", AWSSecretsManagerLoggerCategory.DefaultCategoryName)]
    [InlineData("   ", AWSSecretsManagerLoggerCategory.DefaultCategoryName)]
    public void Logger_is_created_from_the_factory_with_the_right_category(string? categoryName, string expectedCategory)
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var createdLogger = Substitute.For<ILogger>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(expectedCategory).Returns(createdLogger);
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager(options =>
        {
            options.SecretName = "my/secret";
            options.Client = client;
            options.LoggerFactory = loggerFactory;
            options.LoggerCategoryName = categoryName;
        });

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(createdLogger);
        loggerFactory.Received(1).CreateLogger(expectedCategory);
    }

    [Fact]
    public void Logger_takes_precedence_over_the_logger_factory()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var logger = Substitute.For<ILogger>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", client, logger: logger, loggerFactory: loggerFactory);

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(logger);
        loggerFactory.DidNotReceiveWithAnyArgs().CreateLogger(default!);
    }

    [Fact]
    public void Logger_factory_overloads_reject_a_null_factory()
    {
        // Arrange
        var builder = new ConfigurationBuilder();

        // Act
        Action withoutCategory = () => builder.AddAWSSecretsManager("my/secret", (ILoggerFactory)null!);
        Action withCategory = () => builder.AddAWSSecretsManager("my/secret", (ILoggerFactory)null!, "Category");

        // Assert
        withoutCategory.Should().Throw<ArgumentNullException>().WithParameterName("loggerFactory");
        withCategory.Should().Throw<ArgumentNullException>().WithParameterName("loggerFactory");
    }

    [Fact]
    public void Provided_client_is_not_disposed_with_the_provider()
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var builder = new ConfigurationBuilder();
        builder.AddAWSSecretsManager(options =>
        {
            options.SecretName = "my/secret";
            options.Client = client;
        });
        var provider = SingleSource(builder).Build(builder);

        // Act
        ((IDisposable)provider).Dispose();

        // Assert
        client.DidNotReceive().Dispose();
    }
}

/// <summary>
/// Wiring tests for the overloads that take no client, so the package creates the default one.
/// </summary>
/// <remarks>
/// <c>new AmazonSecretsManagerClient()</c> resolves a region and credentials when it is constructed, but makes
/// no network call. The tests therefore set a region and dummy credentials through environment variables for
/// their duration, so they pass on a machine with no AWS setup at all, and restore the previous values
/// afterwards. No call to AWS is made: the sources are registered, and at most disposed, never loaded.
/// </remarks>
public sealed class AddAWSSecretsManagerDefaultClientTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> TestEnvironment = new Dictionary<string, string>
    {
        ["AWS_REGION"] = "eu-west-1",
        // Obviously fake values, shaped so that secret scanners do not mistake them for real keys.
        ["AWS_ACCESS_KEY_ID"] = "unit-test-access-key-id",
        ["AWS_SECRET_ACCESS_KEY"] = "unit-test-secret-access-key"
    };

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public AddAWSSecretsManagerDefaultClientTests()
    {
        foreach (var (name, value) in TestEnvironment)
        {
            _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static AWSSecretsManagerConfigurationSource SingleSource(IConfigurationBuilder builder)
    {
        var source = builder.Sources.OfType<AWSSecretsManagerConfigurationSource>().Should().ContainSingle().Which;
        source.SecretName.Should().Be("my/secret");
        source.SecretsManagerClient.Should().BeOfType<AmazonSecretsManagerClient>();
        source.Options.Client.Should().BeNull();
        return source;
    }

    [Fact]
    public void Name_overload_creates_a_default_client_and_no_logger()
    {
        // Arrange
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret");

        // Assert
        SingleSource(builder).Logger.Should().BeNull();
    }

    [Fact]
    public void Name_and_logger_overload_keeps_the_logger()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", logger);

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(logger);
    }

    [Fact]
    public void Name_and_logger_factory_overload_uses_the_default_category()
    {
        // Arrange
        var createdLogger = Substitute.For<ILogger>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(AWSSecretsManagerLoggerCategory.DefaultCategoryName).Returns(createdLogger);
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", loggerFactory);

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(createdLogger);
        loggerFactory.Received(1).CreateLogger(AWSSecretsManagerLoggerCategory.DefaultCategoryName);
    }

    [Fact]
    public void Name_logger_factory_and_category_overload_uses_the_given_category()
    {
        // Arrange
        var createdLogger = Substitute.For<ILogger>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger("Custom.Category").Returns(createdLogger);
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", loggerFactory, "Custom.Category");

        // Assert
        SingleSource(builder).Logger.Should().BeSameAs(createdLogger);
        loggerFactory.Received(1).CreateLogger("Custom.Category");
    }

    [Fact]
    public void Name_and_configure_overload_applies_the_options()
    {
        // Arrange
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager("my/secret", options =>
        {
            options.SecretName.Should().Be("my/secret");
            options.MaskSecretNameInLogs = true;
            options.PlaintextKey = "ApiKey";
        });

        // Assert
        var source = SingleSource(builder);
        source.Logger.Should().BeNull();
        source.Options.MaskSecretNameInLogs.Should().BeTrue();
        source.Options.PlaintextKey.Should().Be("ApiKey");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Package_owns_the_default_clients_and_gives_each_load_a_new_one(bool explicitNullClient)
    {
        // Arrange (BUG-03): the real extension wiring with the real client type. Nothing is loaded, so AWS is
        // not called; what a load does with its client is covered with fake clients in SharedClientDisposalTests.
        var builder = new ConfigurationBuilder();
        if (explicitNullClient)
        {
            builder.AddAWSSecretsManager("my/secret", (IAmazonSecretsManager?)null);
        }
        else
        {
            builder.AddAWSSecretsManager("my/secret");
        }

        var source = SingleSource(builder);
        var registrationClient = source.SecretsManagerClient;

        // Assert: the extension chose "package owns the clients", not one shared client.
        source.OwnsClients.Should().BeTrue();
        source.DisposeClient.Should().BeFalse();

        // Act: what two loads would receive.
        using var firstLoadClient = source.TakeClientForLoad();
        using var secondLoadClient = source.TakeClientForLoad();

        // Assert: two fresh default clients, neither of them the one exposed on the source.
        firstLoadClient.Should().BeOfType<AmazonSecretsManagerClient>().And.NotBeSameAs(registrationClient);
        secondLoadClient.Should().BeOfType<AmazonSecretsManagerClient>()
            .And.NotBeSameAs(registrationClient)
            .And.NotBeSameAs(firstLoadClient);
        source.SecretsManagerClient.Should().BeSameAs(registrationClient);
    }

    [Fact]
    public void Options_overload_without_a_client_creates_a_default_client()
    {
        // Arrange
        var builder = new ConfigurationBuilder();

        // Act
        builder.AddAWSSecretsManager(options => options.SecretName = "my/secret");

        // Assert
        SingleSource(builder).Logger.Should().BeNull();
    }
}

/// <summary>
/// Never executed. Every style of <c>AddAWSSecretsManager</c> call lives here so that an overload ambiguity
/// (CS0121, as in 1.0.0 and 1.1.0 for the calls marked below) fails the build of the test project.
/// </summary>
internal static class AddAWSSecretsManagerOverloadResolution
{
    private static IConfigurationBuilder Builder => new ConfigurationBuilder();
    private static IAmazonSecretsManager Client => null!;
    private static IAmazonSecretsManager? NullableClient => null;
    private static ILogger Logger => null!;
    private static ILoggerFactory Factory => null!;

    internal static void OptionsOnly() => Builder.AddAWSSecretsManager(o => o.SecretName = "n");
    internal static void Name() => Builder.AddAWSSecretsManager("n");
    internal static void NameClient() => Builder.AddAWSSecretsManager("n", Client);                          // was CS0121
    internal static void NameNullableClient() => Builder.AddAWSSecretsManager("n", NullableClient);          // was CS0121
    internal static void NameNamedClient() => Builder.AddAWSSecretsManager("n", client: Client);             // was CS0121
    internal static void NameNamedFactory() => Builder.AddAWSSecretsManager("n", loggerFactory: Factory);    // was CS0121
    internal static void NameLogger() => Builder.AddAWSSecretsManager("n", Logger);
    internal static void NameNamedLogger() => Builder.AddAWSSecretsManager("n", logger: Logger);
    internal static void NameFactory() => Builder.AddAWSSecretsManager("n", Factory);
    internal static void NameFactoryCategory() => Builder.AddAWSSecretsManager("n", Factory, "cat");
    internal static void NameNamedFactoryCategory() => Builder.AddAWSSecretsManager("n", loggerFactory: Factory, loggerCategoryName: "cat");
    internal static void NameClientLogger() => Builder.AddAWSSecretsManager("n", Client, Logger);
    internal static void NameNullableClientLogger() => Builder.AddAWSSecretsManager("n", NullableClient, Logger);
    internal static void NameClientNamedLogger() => Builder.AddAWSSecretsManager("n", Client, logger: Logger);
    internal static void NameClientNullLogger() => Builder.AddAWSSecretsManager("n", Client, null);
    internal static void NameConfigure() => Builder.AddAWSSecretsManager("n", o => o.MaskSecretNameInLogs = true);
    internal static void NameNamedConfigure() => Builder.AddAWSSecretsManager("n", configure: o => { });
    internal static void NameClientConfigure() => Builder.AddAWSSecretsManager("n", Client, o => o.MaskSecretNameInLogs = true);
    internal static void NameClientConfigureLogger() => Builder.AddAWSSecretsManager("n", Client, o => { }, Logger);
    internal static void FullNamed() => Builder.AddAWSSecretsManager("n", client: null, configure: o => { }, logger: null, loggerFactory: Factory, loggerCategoryName: "cat");
    internal static void FullPositional() => Builder.AddAWSSecretsManager("n", Client, o => { }, Logger, Factory, "cat");
}
