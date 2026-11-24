using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace RF.AWSSecretsManager.Configuration.Tests;

public class PlaintextSecretTests
{
    private static AWSSecretsManagerConfigurationProvider CreateProvider(
        string secretId,
        string secretString,
        string? responseName = null,
        AWSSecretsManagerOptions? options = null,
        ILogger? logger = null)
    {
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse { SecretString = secretString, Name = responseName }));

        var source = new AWSSecretsManagerConfigurationSource(
            secretId,
            client,
            options ?? new AWSSecretsManagerOptions(),
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);

        return new AWSSecretsManagerConfigurationProvider(source);
    }

    private static IReadOnlyList<string> KeysOf(AWSSecretsManagerConfigurationProvider provider)
        => provider.GetChildKeys(Enumerable.Empty<string>(), parentPath: null).Distinct().ToList();

    [Fact]
    public void Plaintext_secret_loads_under_last_segment_of_response_name()
    {
        // Arrange
        var provider = CreateProvider(
            secretId: "arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/myapp/db-password-AbCdEf",
            secretString: "hunter2",
            responseName: "prod/myapp/db-password");

        // Act
        provider.Load();

        // Assert
        provider.TryGet("db-password", out var value).Should().BeTrue();
        value.Should().Be("hunter2");
        KeysOf(provider).Should().Equal("db-password");
    }

    [Fact]
    public void Key_falls_back_to_requested_secret_name_when_response_has_no_name()
    {
        // Arrange
        var provider = CreateProvider("prod/myapp/api-key", "abc 123");

        // Act
        provider.Load();

        // Assert
        provider.TryGet("api-key", out var value).Should().BeTrue();
        value.Should().Be("abc 123");
        KeysOf(provider).Should().Equal("api-key");
    }

    [Fact]
    public void Key_is_derived_from_arn_resource_part_when_response_has_no_name()
    {
        // Arrange
        var provider = CreateProvider(
            "arn:aws:secretsmanager:eu-west-1:123456789012:secret:prod/myapp/api-key-AbCdEf",
            "abc123");

        // Act
        provider.Load();

        // Assert
        KeysOf(provider).Should().Equal("api-key-AbCdEf");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Secret_id_that_gives_no_usable_key_fails_startup(bool maskSecretName)
    {
        // Arrange: nothing follows ":secret:" and the response has no name, so the only candidate key is the
        // whole id, which ends in an empty ':' segment.
        const string secretId = "arn:aws:secretsmanager:eu-west-1:123456789012:secret:";
        var logger = new TestLogger();
        var options = new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName };
        var provider = CreateProvider(secretId, "abc123", options: options, logger: logger);

        // Act
        Action act = () => provider.Load();

        // Assert
        var exception = act.Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot derive a configuration key*Set AWSSecretsManagerOptions.PlaintextKey to choose the key.").Which;
        var logged = logger.Messages.Should().ContainSingle().Which;
        logged.Should().StartWith("Cannot derive a configuration key");
        logged.Should().NotContain("abc123");

        if (maskSecretName)
        {
            exception.Message.Should().NotContain(secretId);
            logged.Should().NotContain(secretId);
        }
    }

    [Fact]
    public void PlaintextKey_rescues_a_secret_id_that_gives_no_usable_key()
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { PlaintextKey = "ApiKey" };
        var provider = CreateProvider("arn:aws:secretsmanager:eu-west-1:123456789012:secret:", "abc123", options: options);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("ApiKey", out var value).Should().BeTrue();
        value.Should().Be("abc123");
    }

    [Fact]
    public void Malformed_json_error_reports_one_based_line_number()
    {
        // Arrange: the offending character 'X' is on the second line, third byte (column 3 in an editor).
        var provider = CreateProvider("my/secret", "{\n  Xy9");

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*(line 2, byte 3; both counted from 1). Text that starts with*");
    }

    [Theory]
    [InlineData("ConnectionStrings : Default")]
    [InlineData(" ConnectionStrings: Default ")]
    [InlineData("ConnectionStrings\t:\tDefault")]
    public void PlaintextKey_segments_are_trimmed(string plaintextKey)
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { PlaintextKey = plaintextKey };
        var provider = CreateProvider("prod/myapp/db-password", "hunter2", options: options);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("ConnectionStrings:Default", out var value).Should().BeTrue();
        value.Should().Be("hunter2");
        provider.GetChildKeys(Enumerable.Empty<string>(), "ConnectionStrings").Should().Equal("Default");
    }

    [Fact]
    public void Invalid_PlaintextKey_logs_an_error()
    {
        // Arrange
        var logger = new TestLogger();
        var options = new AWSSecretsManagerOptions { PlaintextKey = "A::B" };
        var provider = CreateProvider("my/secret", "hunter2", options: options, logger: logger);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>();
        logger.Messages.Should().ContainSingle()
            .Which.Should().Be("AWSSecretsManagerOptions.PlaintextKey 'A::B' is not a valid configuration key: it contains an empty ':' segment.");
    }

    [Theory]
    [InlineData("\"{\\\"A\\\":\\\"1\\\"}\"X", false)]
    [InlineData("'{\"A\":\"1\"}'", true)]
    [InlineData("\"{\\\"A\\\":\\\"1\\\"}\"", true)]
    [InlineData(" '{\"A\":\"1\"}' \n", true)]
    [InlineData("\"[1,2]\"", true)]
    [InlineData("abc{\"A\":\"1\"}", true)]
    [InlineData("abcd{\"A\":\"1\"}", false)]
    [InlineData("Driver={ODBC Driver 18 for SQL Server};Server=db;Pwd={p@ss}", false)]
    [InlineData("''", false)]
    [InlineData("\"", false)]
    [InlineData("\u200B{\"A\":\"1\"}\n", true)]
    [InlineData("x[1,2]", true)]
    [InlineData("x[ 1 ]", true)]
    [InlineData("x[{\"A\":1}]", true)]
    [InlineData("a{}", true)]
    [InlineData("${DB_PASSWORD}", false)]
    [InlineData("a{b}", false)]
    [InlineData("x[abc]", false)]
    [InlineData("hunter2", false)]
    [InlineData("pass}word", false)]
    [InlineData("password}", false)]
    public void Plaintext_that_looks_like_json_with_a_stray_prefix_logs_a_warning(string secretString, bool expectWarning)
    {
        // Arrange
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", secretString, logger: logger);

        // Act
        provider.Load();

        // Assert: it still loads as one plaintext key either way; the warning only makes the case visible.
        provider.TryGet("secret", out var value).Should().BeTrue();
        value.Should().Be(secretString);
        logger.Messages.Count(m => m.Contains("looks like JSON preceded by other characters")).Should().Be(expectWarning ? 1 : 0);
        logger.Messages.Should().NotContain(m => m.Contains(secretString, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Aws_exception_is_not_logged_when_masking_is_enabled(bool maskSecretName)
    {
        // Arrange: an AWS error whose message names the secret, as access-denied errors can.
        const string secretName = "prod/myapp/db-password";
        var awsException = new AmazonSecretsManagerException(
            $"User is not authorized to perform secretsmanager:GetSecretValue on resource {secretName}")
        {
            ErrorCode = "AccessDeniedException",
            StatusCode = System.Net.HttpStatusCode.BadRequest
        };

        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(awsException));

        var logger = new TestLogger();
        var source = new AWSSecretsManagerConfigurationSource(
            secretName,
            client,
            new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName },
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<AmazonSecretsManagerException>();
        var logged = logger.Messages.Should().ContainSingle().Which;
        logged.Should().Contain("Error (AmazonSecretsManagerException, error code AccessDeniedException, HTTP 400)");

        if (maskSecretName)
        {
            logged.Should().NotContain(secretName).And.NotContain("not authorized");
        }
        else
        {
            logged.Should().Contain("not authorized");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Transient_aws_exception_is_not_logged_when_masking_is_enabled(bool maskSecretName)
    {
        // Arrange: every attempt fails with a transient error whose message names the secret.
        const string secretName = "prod/myapp/db-password";
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(
                new InternalServiceErrorException($"Service fault while reading {secretName}")
                {
                    StatusCode = System.Net.HttpStatusCode.InternalServerError
                }));

        var logger = new TestLogger();
        var source = new AWSSecretsManagerConfigurationSource(
            secretName,
            client,
            new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName },
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert: two retry warnings, then one error once the attempts are exhausted. Each names the
        // exception type, a placeholder error code and the status.
        act.Should().Throw<InvalidOperationException>().WithMessage("*after 3 attempts*");
        logger.Messages.Should().HaveCount(3);
        logger.Messages[0].Should().Contain("Transient error (InternalServiceErrorException, error code none, HTTP 500)")
            .And.Contain("attempt 1 of 3 failed, retrying after 1000 ms");
        logger.Messages[1].Should().Contain("Transient error (InternalServiceErrorException, error code none, HTTP 500)")
            .And.Contain("attempt 2 of 3 failed, retrying after 2000 ms");
        logger.Messages[2].Should().Contain("after 3 attempts; last error: InternalServiceErrorException, error code none, HTTP 500.");

        if (maskSecretName)
        {
            logger.Messages.Should().OnlyContain(m => !m.Contains(secretName) && !m.Contains("Service fault"));
            logger.Messages.Should().OnlyContain(m => m.Contains("pro***rd"));
        }
        else
        {
            logger.Messages.Should().OnlyContain(m => m.Contains("Service fault while reading"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Not_found_aws_exception_is_not_logged_when_masking_is_enabled(bool maskSecretName)
    {
        // Arrange
        const string secretName = "prod/myapp/db-password";
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(
                new ResourceNotFoundException($"Secrets Manager can't find {secretName}")));

        var logger = new TestLogger();
        var source = new AWSSecretsManagerConfigurationSource(
            secretName,
            client,
            new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName },
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*was not found*");
        var logged = logger.Messages.Should().ContainSingle().Which;

        if (maskSecretName)
        {
            logged.Should().Be("AWS Secrets Manager secret 'pro***rd' was not found (ResourceNotFoundException, error code none).");
        }
        else
        {
            logged.Should().Contain($"Secrets Manager can't find {secretName}");
        }
    }

    [Theory]
    [InlineData("AccessDeniedException", "AccessDeniedException")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData(null, "none")]
    public void Aws_error_entry_includes_request_id_and_omits_missing_status(string? errorCode, string loggedErrorCode)
    {
        // Arrange: no HTTP status on the exception, so "HTTP 0" must not be logged.
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(
                new AmazonSecretsManagerException("boom") { ErrorCode = errorCode, RequestId = "req-123" }));

        var logger = new TestLogger();
        var source = new AWSSecretsManagerConfigurationSource(
            "prod/myapp/db-password",
            client,
            new AWSSecretsManagerOptions { MaskSecretNameInLogs = true },
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<AmazonSecretsManagerException>();
        logger.Messages.Should().ContainSingle().Which.Should().Be(
            $"Error (AmazonSecretsManagerException, error code {loggedErrorCode}, request id req-123) retrieving AWS Secrets Manager secret 'pro***rd'.");
    }

    [Fact]
    public void Aws_error_entry_names_the_inner_exception_type_but_not_its_message()
    {
        // Arrange: no error code and no status, as when the request never reached AWS.
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(
                new AmazonSecretsManagerException(
                    "request failed",
                    new HttpRequestException("could not reach prod/myapp/db-password endpoint"))));

        var logger = new TestLogger();
        var source = new AWSSecretsManagerConfigurationSource(
            "prod/myapp/db-password",
            client,
            new AWSSecretsManagerOptions { MaskSecretNameInLogs = true },
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<AmazonSecretsManagerException>();
        logger.Messages.Should().ContainSingle().Which.Should().Be(
            "Error (AmazonSecretsManagerException, error code none, caused by HttpRequestException) retrieving AWS Secrets Manager secret 'pro***rd'.");
    }

    [Theory]
    [InlineData(":")]
    [InlineData(":A")]
    [InlineData("A:")]
    [InlineData(":A:")]
    [InlineData("A::B")]
    [InlineData("A: :B")]
    public void PlaintextKey_with_empty_segment_fails_startup_before_calling_aws(string plaintextKey)
    {
        // Arrange
        var client = Substitute.For<IAmazonSecretsManager>();
        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            new AWSSecretsManagerOptions { PlaintextKey = plaintextKey },
            logger: null,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*PlaintextKey*empty ':' segment*");
        client.DidNotReceiveWithAnyArgs().GetSecretValueAsync(default(GetSecretValueRequest)!, default);
    }

    [Fact]
    public void Secret_name_without_slash_is_used_whole_as_key()
    {
        // Arrange
        var provider = CreateProvider("ApiKey", "abc123");

        // Act
        provider.Load();

        // Assert
        provider.TryGet("ApiKey", out var value).Should().BeTrue();
        value.Should().Be("abc123");
    }

    [Fact]
    public void Secret_name_ending_in_slash_falls_back_to_whole_name()
    {
        // Arrange
        var provider = CreateProvider("prod/myapp/", "abc123");

        // Act
        provider.Load();

        // Assert
        KeysOf(provider).Should().Equal("prod/myapp/");
    }

    [Fact]
    public void PlaintextKey_option_overrides_derived_key()
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { PlaintextKey = "ConnectionStrings:Default" };
        var provider = CreateProvider("prod/myapp/db-password", "Server=db;Password=p", "prod/myapp/db-password", options);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("ConnectionStrings:Default", out var value).Should().BeTrue();
        value.Should().Be("Server=db;Password=p");
        provider.TryGet("db-password", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_PlaintextKey_falls_back_to_derived_key(string plaintextKey)
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { PlaintextKey = plaintextKey };
        var provider = CreateProvider("prod/myapp/db-password", "hunter2", options: options);

        // Act
        provider.Load();

        // Assert
        KeysOf(provider).Should().Equal("db-password");
    }

    [Fact]
    public void PlaintextKey_is_trimmed()
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { PlaintextKey = "  ConnectionStrings:Default \t" };
        var provider = CreateProvider("prod/myapp/db-password", "hunter2", options: options);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("ConnectionStrings:Default", out var value).Should().BeTrue();
        value.Should().Be("hunter2");
        KeysOf(provider).Should().Equal("ConnectionStrings");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_response_name_falls_back_to_requested_secret_name(string responseName)
    {
        // Arrange
        var provider = CreateProvider("prod/myapp/api-key", "abc123", responseName);

        // Act
        provider.Load();

        // Assert
        KeysOf(provider).Should().Equal("api-key");
    }

    [Fact]
    public void Json_object_with_byte_order_mark_is_not_loaded_as_plaintext()
    {
        // Arrange: a secret created from a "UTF-8 with BOM" file. It is meant to be JSON, so it must take the
        // strict JSON path (as in 1.1.0) instead of loading as one plaintext key.
        var provider = CreateProvider("my/secret", "\uFEFF{\"A\":\"1\"}");

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not contain valid JSON (line 1, byte 1; both counted from 1).*The secret text starts with a byte-order mark (U+FEFF)*");
        KeysOf(provider).Should().BeEmpty();
    }

    [Fact]
    public void Byte_order_mark_hint_is_given_when_whitespace_precedes_the_mark()
    {
        // Arrange
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", " \n\uFEFF{\"A\":\"1\"}", logger: logger);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*starts with a byte-order mark (U+FEFF)*");
        logger.Messages.Should().ContainSingle().Which.Should().Contain("starts with a byte-order mark (U+FEFF)");
    }

    [Fact]
    public void Byte_order_mark_hint_is_not_given_for_other_invalid_json()
    {
        // Arrange
        var provider = CreateProvider("my/secret", "{not-json");

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not contain valid JSON (line 1, byte 2; both counted from 1). Text that starts with '{' or '[' is always read as JSON; to store a plaintext value that starts with one of these characters, put it inside a JSON object.");
    }

    [Theory]
    [InlineData("\uFEFFhunter2", false)]
    [InlineData(" \n\uFEFFhunter2", false)]
    [InlineData("\uFEFFhunter2", true)]
    public void Plaintext_with_byte_order_mark_is_stored_exactly_and_logs_a_warning(string secretString, bool maskSecretName)
    {
        // Arrange: the mark is invisible, so the value looks right but would fail authentication.
        var logger = new TestLogger();
        var options = new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName };
        var provider = CreateProvider("prod/myapp/db-password", secretString, options: options, logger: logger);

        // Act
        provider.Load();

        // Assert: the value is kept unchanged (no trimming), and a warning makes the mark visible.
        provider.TryGet("db-password", out var value).Should().BeTrue();
        value.Should().Be(secretString);

        var warning = logger.Messages.Should().ContainSingle(m => m.Contains("byte-order mark (U+FEFF)")).Which;
        warning.Should().Contain("kept as part of the plaintext value");
        logger.Messages.Should().NotContain(m => m.Contains("hunter2"));

        if (maskSecretName)
        {
            logger.Messages.Should().NotContain(m => m.Contains("prod/myapp/db-password") || m.Contains("db-password"));
        }
    }

    [Theory]
    [InlineData("hunter2\n", true, false)]
    [InlineData("hunter2\r\n", true, false)]
    [InlineData("hunter2\n", true, true)]
    [InlineData("hunter2", false, false)]
    [InlineData("hun\nter2", false, false)]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEvQ\n-----END PRIVATE KEY-----\n", false, false)]
    [InlineData("line one\r\nline two\r\n", false, false)]
    [InlineData("hunter2 ", false, false)]
    public void Plaintext_ending_in_a_line_break_is_stored_exactly_and_logs_a_warning(
        string secretString,
        bool expectWarning,
        bool maskSecretName)
    {
        // Arrange: typical for a secret created with --secret-string file://secret.txt
        var logger = new TestLogger();
        var options = new AWSSecretsManagerOptions { MaskSecretNameInLogs = maskSecretName };
        var provider = CreateProvider("prod/myapp/db-password", secretString, options: options, logger: logger);

        // Act
        provider.Load();

        // Assert: the value is kept unchanged (no trimming); the warning only makes the line break visible.
        provider.TryGet("db-password", out var value).Should().BeTrue();
        value.Should().Be(secretString);
        logger.Messages.Count(m => m.Contains("ends with a line break")).Should().Be(expectWarning ? 1 : 0);
        logger.Messages.Should().NotContain(m => m.Contains("hunter2") || m.Contains("ter2") || m.Contains("MIIEvQ"));

        if (maskSecretName)
        {
            logger.Messages.Should().NotContain(m => m.Contains("db-password"));
        }
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Json_nested_deeper_than_64_levels_fails_with_a_message_that_names_the_limit(int depth, bool loads)
    {
        // Arrange: {"a":{"a":...{"a":1}...}} with the given number of nested objects.
        var secretString = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", secretString, logger: logger);

        // Act
        Action act = () => provider.Load();

        // Assert
        if (loads)
        {
            act.Should().NotThrow();
            provider.TryGet(string.Join(":", Enumerable.Repeat("a", depth)), out var value).Should().BeTrue();
            value.Should().Be("1");
            return;
        }

        const string expected = "AWS Secrets Manager secret 'my/secret' contains JSON nested deeper than the supported 64 levels.";
        act.Should().Throw<InvalidOperationException>().WithMessage(expected);
        logger.Messages.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void Deeply_nested_json_with_a_typo_reports_the_position_of_the_typo()
    {
        // Arrange: 65 nested objects (one past the limit), then a typo. The 65th '{' is byte 321; the typo
        // ('o' of "oops") is byte 328. The error must point at the typo, not at where the depth ran out.
        var secretString = string.Concat(Enumerable.Repeat("{\"a\":", 65)) + "1 oops" + new string('}', 65);
        var provider = CreateProvider("my/secret", secretString);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not contain valid JSON (line 1, byte 328; both counted from 1).*");
    }

    [Theory]
    [InlineData("[\"a\",\"b\"]", "Key", true)]
    [InlineData("{}", "Key", true)]
    [InlineData(" [1] ", "Key", true)]
    [InlineData("{}", null, false)]
    [InlineData("[1]", null, false)]
    [InlineData("{\"A\":\"1\"}", "Key", false)]
    public void Json_that_yields_no_keys_warns_when_PlaintextKey_is_set(string secretString, string? plaintextKey, bool expectWarning)
    {
        // Arrange: text starting with '{' or '[' is always read as JSON, even with PlaintextKey set.
        var logger = new TestLogger();
        var options = new AWSSecretsManagerOptions { PlaintextKey = plaintextKey, MaskSecretNameInLogs = true };
        var provider = CreateProvider("prod/myapp/db-password", secretString, options: options, logger: logger);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("Key", out _).Should().BeFalse();
        logger.Messages.Count(m => m.Contains("AWSSecretsManagerOptions.PlaintextKey was not used; it produced no configuration keys"))
            .Should().Be(expectWarning ? 1 : 0);
        logger.Messages.Should().OnlyContain(m => m.Contains("pro***rd") && !m.Contains("db-password"));
        logger.Messages.Should().NotContain(m => m.Contains(secretString.Trim(), StringComparison.Ordinal));
    }

    [Fact]
    public void Plaintext_without_byte_order_mark_logs_no_warning()
    {
        // Arrange
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", "hunter2", logger: logger);

        // Act
        provider.Load();

        // Assert
        logger.Messages.Should().ContainSingle().Which.Should().NotContain("byte-order mark");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Binary_or_empty_secret_fails_startup_and_logs_an_error(string? secretString)
    {
        // Arrange: a secret created with --secret-binary has no SecretString.
        var client = Substitute.For<IAmazonSecretsManager>();
        client
            .GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetSecretValueResponse
            {
                SecretString = secretString,
                SecretBinary = new MemoryStream([1, 2, 3]),
                Name = "my/secret"
            }));

        var logger = new TestLogger();
        var source = new AWSSecretsManagerConfigurationSource(
            "my/secret",
            client,
            new AWSSecretsManagerOptions(),
            logger,
            new RecordingDelayStrategy(),
            disposeClient: false);
        var provider = new AWSSecretsManagerConfigurationProvider(source);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("AWS Secrets Manager secret 'my/secret' does not contain a text secret. Binary secrets are not supported.");
        logger.Messages.Should().ContainSingle()
            .Which.Should().Be("AWS Secrets Manager secret 'my/secret' does not contain a text secret. Binary secrets are not supported.");
        KeysOf(provider).Should().BeEmpty();
    }

    [Theory]
    [InlineData("\u00A0{\"A\":\"1\"}", "whitespace character U+00A0")]
    [InlineData("\u00A0\uFEFF{\"A\":\"1\"}", "whitespace character U+00A0")]
    [InlineData("\u2003[1]", "whitespace character U+2003")]
    [InlineData("\t\uFEFF{\"A\":\"1\"}", "byte-order mark (U+FEFF)")]
    public void Invalid_json_hint_names_the_first_character_json_rejects(string secretString, string expectedHint)
    {
        // Arrange: these characters are skipped when choosing between JSON and plaintext, but JSON itself
        // allows only space, tab, carriage return and line feed before the document.
        var provider = CreateProvider("my/secret", secretString);

        // Act
        Action act = () => provider.Load();

        // Assert
        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("does not contain valid JSON").And.Contain(expectedHint);
        if (!expectedHint.Contains("byte-order mark"))
        {
            message.Should().NotContain("byte-order mark");
        }
    }

    [Fact]
    public void Looks_like_json_warning_masks_the_secret_name()
    {
        // Arrange
        var logger = new TestLogger();
        var options = new AWSSecretsManagerOptions { MaskSecretNameInLogs = true };
        var provider = CreateProvider("prod/myapp/db-password", "'{\"A\":\"1\"}'", options: options, logger: logger);

        // Act
        provider.Load();

        // Assert
        var warning = logger.Messages.Should().ContainSingle(m => m.Contains("looks like JSON preceded by other characters")).Which;
        warning.Should().Contain("pro***rd");
        logger.Messages.Should().NotContain(m => m.Contains("prod/myapp/db-password") || m.Contains("db-password"));
    }

    [Fact]
    public void Whitespace_only_secret_logs_an_error()
    {
        // Arrange
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", " \n", logger: logger);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>();
        logger.Messages.Should().ContainSingle().Which.Should().Contain("is empty");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"quoted\"")]
    [InlineData("007")]
    [InlineData("1e5")]
    [InlineData("p@ss{word}[1]")]
    [InlineData("  padded value \n")]
    [InlineData("12345\n")]
    public void Scalar_text_is_stored_exactly_as_received(string secretString)
    {
        // Arrange
        var provider = CreateProvider("my/secret", secretString);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("secret", out var value).Should().BeTrue();
        value.Should().Be(secretString);
        KeysOf(provider).Should().Equal("secret");
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" \t\r\n")]
    [InlineData("\uFEFF")]
    [InlineData("\uFEFF\r\n")]
    [InlineData(" \uFEFF ")]
    public void Whitespace_only_secret_fails_startup(string secretString)
    {
        // Arrange
        var provider = CreateProvider("my/secret", secretString);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*is empty*");
    }

    [Fact]
    public void Root_json_array_still_produces_no_keys()
    {
        // Arrange
        var provider = CreateProvider("my/secret", "[1,2]");

        // Act
        provider.Load();

        // Assert
        KeysOf(provider).Should().BeEmpty();
    }

    [Theory]
    [InlineData("[oops")]
    [InlineData("{\"a\": 1")]
    [InlineData("  {not-json")]
    public void Malformed_json_still_fails_startup(string secretString)
    {
        // Arrange
        var provider = CreateProvider("my/secret", secretString);

        // Act
        Action act = () => provider.Load();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*does not contain valid JSON*");
    }

    [Theory]
    [InlineData("{Xy9$secret", "'X'", 2)]
    [InlineData("{\"a\": Zq8!secret", "'Z'", 7)]
    [InlineData("[Kp7#secret", "'K'", 2)]
    public void Malformed_json_error_does_not_reveal_secret_characters(
        string secretString,
        string quotedCharacter,
        int expectedByte)
    {
        // Arrange: System.Text.Json quotes the offending character in its error message. That character
        // belongs to the secret, so it must reach neither the log nor the thrown exception.
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", secretString, logger: logger);

        // Act
        Action act = () => provider.Load();

        // Assert
        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.InnerException.Should().BeNull();
        exception.Message.Should().Contain($"(line 1, byte {expectedByte}; both counted from 1). Text that starts with");
        exception.ToString().Should().NotContain(quotedCharacter).And.NotContain(secretString).And.NotContain("invalid start");

        var logged = logger.Messages.Should().ContainSingle().Which;
        logged.Should().Contain($"(line 1, byte {expectedByte}; both counted from 1). Text that starts with");
        logged.Should().NotContain(quotedCharacter).And.NotContain("invalid start");
        logged.Should().NotContain(secretString);
    }

    [Fact]
    public void Json_object_with_leading_whitespace_is_flattened_and_ignores_PlaintextKey()
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { PlaintextKey = "Ignored" };
        var provider = CreateProvider("my/secret", " \n {\"A\":{\"B\":\"1\"}}", options: options);

        // Act
        provider.Load();

        // Assert
        provider.TryGet("A:B", out var value).Should().BeTrue();
        value.Should().Be("1");
        KeysOf(provider).Should().Equal("A");
        provider.TryGet("Ignored", out _).Should().BeFalse();
        provider.TryGet("secret", out _).Should().BeFalse();
    }

    [Fact]
    public void Plaintext_value_is_never_logged()
    {
        // Arrange
        var logger = new TestLogger();
        var provider = CreateProvider("my/secret", "super-secret-value", logger: logger);

        // Act
        provider.Load();

        // Assert
        logger.Messages.Should().ContainSingle()
            .Which.Should().Be("Loaded AWS Secrets Manager secret 'my/secret' as a single plaintext configuration value.");
        logger.Messages.Should().NotContain(m => m.Contains("super-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void Masked_logs_reveal_neither_secret_name_nor_derived_key()
    {
        // Arrange
        var options = new AWSSecretsManagerOptions { MaskSecretNameInLogs = true };
        var logger = new TestLogger();
        var provider = CreateProvider("prod/myapp/db-password", "hunter2", "prod/myapp/db-password", options, logger);

        // Act
        provider.Load();

        // Assert
        var combined = string.Join(Environment.NewLine, logger.Messages);
        combined.Should().Contain("pro***rd");
        combined.Should().NotContain("prod/myapp/db-password");
        combined.Should().NotContain("db-password");
    }
}
