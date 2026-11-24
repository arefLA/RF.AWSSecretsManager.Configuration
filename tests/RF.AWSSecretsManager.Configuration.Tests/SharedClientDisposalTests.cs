using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace RF.AWSSecretsManager.Configuration.Tests;

/// <summary>
/// BUG-03: a client the package creates must not be shared by, and disposed with, every provider built from
/// the same source. Each load uses a client of its own and disposes it when the load ends; the client created
/// at registration is only exposed on the source and is never used or disposed by the package.
/// </summary>
public class SharedClientDisposalTests
{
    /// <summary>A fake client that, like the real one, refuses calls after it has been disposed.</summary>
    private sealed class FakeClient
    {
        private int _disposeCount;

        public FakeClient(Exception? failure = null, bool disposeThrows = false)
        {
            Client = Substitute.For<IAmazonSecretsManager>();
            Client
                .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    if (DisposeCount > 0)
                    {
                        return Task.FromException<GetSecretValueResponse>(
                            new ObjectDisposedException(nameof(AmazonSecretsManagerClient)));
                    }

                    return failure is not null
                        ? Task.FromException<GetSecretValueResponse>(failure)
                        : Task.FromResult(new GetSecretValueResponse { SecretString = "{\"A\":\"1\"}" });
                });
            Client.When(c => c.Dispose()).Do(_ =>
            {
                Interlocked.Increment(ref _disposeCount);
                if (disposeThrows)
                {
                    throw new InvalidOperationException("Dispose failed");
                }
            });
        }

        public IAmazonSecretsManager Client { get; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int LoadCalls => Client.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "GetSecretValueAsync");
    }

    /// <summary>
    /// Everything a source's factory created: the client made at registration, then one per load, in order.
    /// </summary>
    private sealed class CreatedClients
    {
        private readonly List<FakeClient> _all = new();

        public FakeClient Registration
        {
            get
            {
                lock (_all)
                {
                    return _all[0];
                }
            }
        }

        public IReadOnlyList<FakeClient> ForLoads
        {
            get
            {
                lock (_all)
                {
                    return _all.Skip(1).ToList();
                }
            }
        }

        /// <param name="failingLoads">How many load clients, counted from the first, fail with an AWS error.</param>
        /// <param name="disposeThrows">Whether load clients throw when disposed.</param>
        public IAmazonSecretsManager Create(int failingLoads, bool disposeThrows)
        {
            lock (_all)
            {
                var isRegistrationClient = _all.Count == 0;
                var loadIndex = _all.Count - 1;
                var fake = isRegistrationClient
                    ? new FakeClient()
                    : new FakeClient(loadIndex < failingLoads ? AwsFailure() : null, disposeThrows);
                _all.Add(fake);
                return fake.Client;
            }
        }

        /// <summary>Asserts the invariant of the design for everything created so far.</summary>
        public void ShouldBeFullyAccountedFor(int expectedLoads)
        {
            Registration.LoadCalls.Should().Be(0, "the registration client is never used for loading");
            Registration.DisposeCount.Should().Be(0, "the registration client is never disposed by the package");
            ForLoads.Should().HaveCount(expectedLoads);
            ForLoads.Should().OnlyContain(fake => fake.LoadCalls == 1, "each load client serves exactly one load");
            ForLoads.Should().OnlyContain(fake => fake.DisposeCount == 1, "each load client is disposed when its load ends");
            ForLoads.Select(fake => fake.Client).Should().OnlyHaveUniqueItems();
        }
    }

    private static AmazonSecretsManagerException AwsFailure() => new("AWS said no");

    private static AWSSecretsManagerConfigurationSource CreateOwnedSource(
        CreatedClients created,
        int failingLoads = 0,
        bool disposeThrows = false,
        ILogger? logger = null,
        AWSSecretsManagerOptions? options = null,
        string secretName = "my/secret")
        => new(
            secretName,
            () => created.Create(failingLoads, disposeThrows),
            options ?? new AWSSecretsManagerOptions(),
            logger,
            new RecordingDelayStrategy());

    private static void ShouldHoldTheSecret(IConfigurationProvider provider)
    {
        provider.TryGet("A", out var value).Should().BeTrue();
        value.Should().Be("1");
    }

    /// <summary>Calls <c>builder.Build()</c> until it succeeds, as a startup retry loop would, with a limit.</summary>
    private static (IConfigurationRoot? Root, int Attempts) BuildWithRetries(IConfigurationBuilder builder, int maxAttempts = 10)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return (builder.Build(), attempt);
            }
            catch (AmazonSecretsManagerException)
            {
            }
        }

        return (null, maxAttempts);
    }

    [Fact]
    public void Each_load_uses_its_own_client_and_disposes_it_when_the_load_ends()
    {
        // Arrange
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var builder = new ConfigurationBuilder();

        // Act
        var first = source.Build(builder);
        var second = source.Build(builder);
        first.Load();
        ((IDisposable)first).Dispose();
        second.Load();

        // Assert
        created.ShouldBeFullyAccountedFor(expectedLoads: 2);
        ShouldHoldTheSecret(first);
        ShouldHoldTheSecret(second);
    }

    [Fact]
    public void Registration_client_stays_valid_on_the_source_and_is_not_used_for_loading()
    {
        // Arrange
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);

        // Assert: the client exists before anything is built, so registration fails fast on a bad AWS setup.
        source.SecretsManagerClient.Should().BeSameAs(created.Registration.Client);
        source.OwnsClients.Should().BeTrue();
        source.DisposeClient.Should().BeFalse();
        created.ForLoads.Should().BeEmpty();

        // Act
        var provider = source.Build(new ConfigurationBuilder());
        provider.Load();
        provider.Load();
        ((IDisposable)provider).Dispose();

        // Assert: code that reads SecretsManagerClient from the source can keep using it, as in 1.1.0.
        source.SecretsManagerClient.Should().BeSameAs(created.Registration.Client);
        created.ShouldBeFullyAccountedFor(expectedLoads: 2);
    }

    [Fact]
    public void Providers_that_never_load_neither_create_nor_dispose_a_client()
    {
        // Arrange
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var builder = new ConfigurationBuilder();

        // Act: providers hold no client, so building and disposing them touches none.
        var first = source.Build(builder);
        var second = source.Build(builder);
        ((IDisposable)first).Dispose();
        ((IDisposable)second).Dispose();
        ((IDisposable)second).Dispose();

        // Assert
        created.ShouldBeFullyAccountedFor(expectedLoads: 0);
    }

    [Fact]
    public void Directly_constructed_provider_does_not_share_a_client_with_built_providers()
    {
        // Arrange: new AWSSecretsManagerConfigurationProvider(source) is public API. On a package-owned source it
        // must behave like a built provider, in either order.
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var builder = new ConfigurationBuilder();

        // Act: a built provider loads first, then a directly constructed one.
        var built = source.Build(builder);
        built.Load();
        ((IDisposable)built).Dispose();
        var direct = new AWSSecretsManagerConfigurationProvider(source);
        direct.Load();

        // Assert
        ShouldHoldTheSecret(direct);
        created.ShouldBeFullyAccountedFor(expectedLoads: 2);

        // Act: the reverse order. Disposing a directly constructed provider must not break a built one.
        var otherDirect = new AWSSecretsManagerConfigurationProvider(source);
        otherDirect.Dispose();
        var otherBuilt = source.Build(builder);
        otherBuilt.Load();

        // Assert
        ShouldHoldTheSecret(otherBuilt);
        created.ShouldBeFullyAccountedFor(expectedLoads: 3);
    }

    [Fact]
    public void Building_again_after_disposing_the_previous_provider_still_loads()
    {
        // Arrange
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var builder = new ConfigurationBuilder();

        // Act: build, load, dispose, three times over.
        for (var round = 0; round < 3; round++)
        {
            var provider = source.Build(builder);
            provider.Load();
            ShouldHoldTheSecret(provider);
            ((IDisposable)provider).Dispose();
        }

        // Assert
        created.ShouldBeFullyAccountedFor(expectedLoads: 3);
    }

    [Fact]
    public void Configuration_root_built_twice_survives_disposal_of_the_first_root()
    {
        // Arrange
        var created = new CreatedClients();
        var builder = new ConfigurationBuilder();
        builder.Add(CreateOwnedSource(created));

        // Act
        var firstRoot = builder.Build();
        ((IDisposable)firstRoot).Dispose();
        var secondRoot = builder.Build();

        // Assert
        secondRoot["A"].Should().Be("1");
        created.ShouldBeFullyAccountedFor(expectedLoads: 2);
    }

    [Fact]
    public void ConfigurationManager_survives_repeated_provider_rebuilds()
    {
        // Arrange: ConfigurationManager (ASP.NET Core minimal hosting) builds and loads the provider as soon as
        // the source is added, and rebuilds every provider when a builder property changes.
        var created = new CreatedClients();
        var manager = new ConfigurationManager();
        IConfigurationBuilder builder = manager;
        builder.Add(CreateOwnedSource(created));

        // Act: before the fix, the second rebuild failed with ObjectDisposedException.
        for (var rebuild = 1; rebuild <= 3; rebuild++)
        {
            builder.Properties["rebuild"] = rebuild;
            manager["A"].Should().Be("1");
        }

        // Assert: the initial load plus three rebuilds.
        created.ShouldBeFullyAccountedFor(expectedLoads: 4);
    }

    [Fact]
    public void Failed_load_disposes_its_client_and_the_next_load_gets_a_new_one()
    {
        // Arrange: the first load client fails, the second works.
        var created = new CreatedClients();
        var source = CreateOwnedSource(created, failingLoads: 1);
        var provider = source.Build(new ConfigurationBuilder());

        // Act
        Action firstLoad = () => provider.Load();

        // Assert
        firstLoad.Should().Throw<AmazonSecretsManagerException>();
        created.ShouldBeFullyAccountedFor(expectedLoads: 1);

        // Act: loading the same provider again works with a fresh client.
        provider.Load();

        // Assert
        ShouldHoldTheSecret(provider);
        created.ShouldBeFullyAccountedFor(expectedLoads: 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Cleanup_failure_changes_the_outcome_of_the_load_in_neither_direction(int failingLoads)
    {
        // Arrange: the load client throws when it is disposed; with failingLoads = 1 its call fails too.
        var created = new CreatedClients();
        var source = CreateOwnedSource(created, failingLoads, disposeThrows: true);
        var provider = source.Build(new ConfigurationBuilder());

        // Act
        Action act = () => provider.Load();

        // Assert: a successful load stays successful, and a load error is not replaced by the cleanup error.
        if (failingLoads == 0)
        {
            act.Should().NotThrow();
            ShouldHoldTheSecret(provider);
        }
        else
        {
            act.Should().Throw<AmazonSecretsManagerException>().WithMessage("AWS said no");
        }

        created.ShouldBeFullyAccountedFor(expectedLoads: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_failure_is_logged_as_a_warning(bool maskSecretName)
    {
        // Arrange
        var logger = new TestLogger();
        var created = new CreatedClients();
        var source = CreateOwnedSource(
            created,
            disposeThrows: true,
            logger: logger,
            options: new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName },
            secretName: "prod/myapp/db-password");

        // Act
        source.Build(new ConfigurationBuilder()).Load();

        // Assert: a Warning, with the exception attached in both modes. The exception is swallowed, so the log
        // is the only place it can be seen; it comes from releasing a client, not from AWS, and cannot name the
        // secret.
        var expectedName = maskSecretName ? "pro***rd" : "prod/myapp/db-password";
        var warning = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith("Failed to dispose")).Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().StartWith(
            $"Failed to dispose the AWS Secrets Manager client used to load secret '{expectedName}' (InvalidOperationException).");
        warning.Message.Should().Contain("Dispose failed");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Client_creation_failure_during_a_load_is_logged_and_rethrown(bool maskSecretName)
    {
        // Arrange: the registration client is created fine; creating a client for the load fails, as it would
        // if the AWS region setting were removed after registration.
        var logger = new TestLogger();
        var factoryCalls = 0;
        var source = new AWSSecretsManagerConfigurationSource(
            "prod/myapp/db-password",
            () => ++factoryCalls == 1
                ? new FakeClient().Client
                : throw new InvalidOperationException("No RegionEndpoint or ServiceURL configured"),
            new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName },
            logger,
            new RecordingDelayStrategy());
        var provider = source.Build(new ConfigurationBuilder());

        // Act
        Action load = () => provider.Load();

        // Assert
        load.Should().Throw<InvalidOperationException>().WithMessage("No RegionEndpoint or ServiceURL configured");

        // An Error. With masking on the exception object is left out of the log (it is rethrown, so nothing is
        // lost), like the other load errors.
        var expectedName = maskSecretName ? "pro***rd" : "prod/myapp/db-password";
        var error = logger.Entries.Should().ContainSingle().Which;
        error.Level.Should().Be(LogLevel.Error);
        error.Message.Should().StartWith(
            $"Failed to create an AWS Secrets Manager client to load secret '{expectedName}' (InvalidOperationException).");
        error.Message.Contains("No RegionEndpoint").Should().Be(!maskSecretName);
    }

    [Fact]
    public void Startup_retry_loop_with_one_secret_leaves_no_load_client_undisposed()
    {
        // Arrange
        var created = new CreatedClients();
        var builder = new ConfigurationBuilder();
        builder.Add(CreateOwnedSource(created, failingLoads: 3));

        // Act
        var (root, attempts) = BuildWithRetries(builder);

        // Assert
        attempts.Should().Be(4);
        root.Should().NotBeNull();
        root!["A"].Should().Be("1");
        created.ShouldBeFullyAccountedFor(expectedLoads: 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_retry_loop_with_two_secrets_leaves_no_load_client_undisposed(bool failingSecretFirst)
    {
        // Arrange: secret "a" always loads; secret "b" fails three times. ConfigurationBuilder.Build() builds
        // every provider and then loads them in order, and drops them all, undisposed, when one fails.
        // - "b" added second: "a" has already loaded on every failed attempt.
        // - "b" added first: the provider for "a" is built but never loads on a failed attempt.
        var createdForA = new CreatedClients();
        var createdForB = new CreatedClients();
        var sourceA = CreateOwnedSource(createdForA);
        var sourceB = CreateOwnedSource(createdForB, failingLoads: 3);
        var builder = new ConfigurationBuilder();
        builder.Add(failingSecretFirst ? sourceB : sourceA);
        builder.Add(failingSecretFirst ? sourceA : sourceB);

        // Act
        var (root, attempts) = BuildWithRetries(builder);

        // Assert
        attempts.Should().Be(4);
        root.Should().NotBeNull();
        createdForB.ShouldBeFullyAccountedFor(expectedLoads: 4);
        createdForA.ShouldBeFullyAccountedFor(expectedLoads: failingSecretFirst ? 1 : 4);
    }

    [Fact]
    public void Load_after_dispose_does_not_leave_a_client_behind()
    {
        // Arrange
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var provider = source.Build(new ConfigurationBuilder());
        ((IDisposable)provider).Dispose();

        // Act: not a supported sequence, but it must not leak. The load gets a client for its own duration.
        provider.Load();

        // Assert
        ShouldHoldTheSecret(provider);
        created.ShouldBeFullyAccountedFor(expectedLoads: 1);
    }

    [Fact]
    public void Concurrent_loads_on_one_provider_never_share_a_client()
    {
        // Arrange
        const int loadCount = 32;
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var provider = source.Build(new ConfigurationBuilder());
        using var start = new ManualResetEventSlim();
        var errors = new List<Exception>();

        // Act
        var threads = Enumerable.Range(0, loadCount)
            .Select(_ => new Thread(() =>
            {
                start.Wait();
                try
                {
                    provider.Load();
                }
                catch (Exception exception)
                {
                    lock (errors)
                    {
                        errors.Add(exception);
                    }
                }
            }))
            .ToList();
        threads.ForEach(thread => thread.Start());
        start.Set();
        threads.ForEach(thread => thread.Join());

        // Assert: no load saw a disposed client.
        errors.Should().BeEmpty();
        created.ShouldBeFullyAccountedFor(expectedLoads: loadCount);
    }

    [Fact]
    public void Concurrent_builds_and_loads_across_providers_never_share_a_client()
    {
        // Arrange
        const int providerCount = 64;
        var created = new CreatedClients();
        var source = CreateOwnedSource(created);
        var builder = new ConfigurationBuilder();
        var providers = new IConfigurationProvider[providerCount];
        using var start = new ManualResetEventSlim();

        // Act: build from many threads at once, then load them all.
        var threads = Enumerable.Range(0, providerCount)
            .Select(index => new Thread(() =>
            {
                start.Wait();
                providers[index] = source.Build(builder);
            }))
            .ToList();
        threads.ForEach(thread => thread.Start());
        start.Set();
        threads.ForEach(thread => thread.Join());

        Parallel.ForEach(providers, provider => provider.Load());

        // Assert
        created.ShouldBeFullyAccountedFor(expectedLoads: providerCount);
    }

    [Fact]
    public void Arguments_are_checked_before_a_client_is_created()
    {
        // Arrange
        var factoryCalls = 0;
        Func<IAmazonSecretsManager> factory = () =>
        {
            factoryCalls++;
            return Substitute.For<IAmazonSecretsManager>();
        };

        // Act
        Action nullOptions = () => _ = new AWSSecretsManagerConfigurationSource(
            "my/secret", factory, options: null!, logger: null, new RecordingDelayStrategy());
        Action nullDelayStrategy = () => _ = new AWSSecretsManagerConfigurationSource(
            "my/secret", factory, new AWSSecretsManagerOptions(), logger: null, delayStrategy: null!);

        // Assert: no client was created, so none is left undisposed.
        nullOptions.Should().Throw<ArgumentNullException>().WithParameterName("options");
        nullDelayStrategy.Should().Throw<ArgumentNullException>().WithParameterName("delayStrategy");
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public void Client_factory_that_returns_null_is_rejected()
    {
        // Act
        Action act = () => _ = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            () => null!,
            new AWSSecretsManagerOptions(),
            logger: null,
            new RecordingDelayStrategy());

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*client factory returned null*");
    }

    [Fact]
    public void Caller_supplied_client_is_shared_and_never_disposed()
    {
        // Arrange
        var supplied = new FakeClient();
        var builder = new ConfigurationBuilder();
        builder.AddAWSSecretsManager(options =>
        {
            options.SecretName = "my/secret";
            options.Client = supplied.Client;
        });
        builder.Sources.OfType<AWSSecretsManagerConfigurationSource>().Single().OwnsClients.Should().BeFalse();

        // Act
        var firstRoot = builder.Build();
        ((IDisposable)firstRoot).Dispose();
        var secondRoot = builder.Build();
        ((IDisposable)secondRoot).Dispose();

        // Assert
        secondRoot["A"].Should().Be("1");
        supplied.DisposeCount.Should().Be(0);
        supplied.LoadCalls.Should().Be(2);
    }

    [Fact]
    public void Failed_load_does_not_dispose_a_caller_supplied_client()
    {
        // Arrange
        var supplied = new FakeClient(AwsFailure());
        var builder = new ConfigurationBuilder();
        builder.AddAWSSecretsManager(options =>
        {
            options.SecretName = "my/secret";
            options.Client = supplied.Client;
        });

        // Act
        Action act = () => builder.Build();

        // Assert
        act.Should().Throw<AmazonSecretsManagerException>();
        supplied.DisposeCount.Should().Be(0);
    }

    [Fact]
    public void Public_constructor_with_disposeClient_true_still_disposes_the_given_client_with_the_provider()
    {
        // Arrange: unchanged behavior for code that builds the source itself.
        var supplied = new FakeClient();
        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            supplied.Client,
            new AWSSecretsManagerOptions(),
            logger: null,
            new RecordingDelayStrategy(),
            disposeClient: true);
        source.OwnsClients.Should().BeFalse();

        // Act
        var provider = source.Build(new ConfigurationBuilder());
        provider.Load();

        // Assert: kept for the provider's lifetime, not disposed after the load.
        supplied.DisposeCount.Should().Be(0);

        // Act
        ((IDisposable)provider).Dispose();

        // Assert
        supplied.DisposeCount.Should().Be(1);
    }
}
