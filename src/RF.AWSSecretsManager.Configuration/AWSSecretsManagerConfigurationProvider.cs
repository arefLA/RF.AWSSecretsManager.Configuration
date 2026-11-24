using System.Text.Json;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RF.AWSSecretsManager.Configuration;

/// <summary>
/// Configuration provider that loads configuration values from AWS Secrets Manager.
/// </summary>
public sealed class AWSSecretsManagerConfigurationProvider : ConfigurationProvider, IDisposable
{
    private const int DefaultMaxRetries = 3;
    private const int DefaultBaseDelayMilliseconds = 1000;

    /// <summary>Byte-order mark (U+FEFF), left at the start of text saved as "UTF-8 with BOM".</summary>
    private const char ByteOrderMark = '\uFEFF';

    /// <summary>
    /// How many stray characters may precede the opening bracket for plaintext to still be reported as
    /// "looks like JSON". See <see cref="LooksLikeJsonWithStrayPrefix"/>.
    /// </summary>
    private const int MaxStrayPrefixLength = 3;

    /// <summary>The option name as shown in messages that tell the user to set or correct it.</summary>
    private const string PlaintextKeyOptionName =
        $"{nameof(AWSSecretsManagerOptions)}.{nameof(AWSSecretsManagerOptions.PlaintextKey)}";

    /// <summary>
    /// Appended to every invalid-JSON error. A plaintext value that happens to start with <c>{</c> or <c>[</c>
    /// (a rotated password, for example) ends up in that error, and nothing else would point to the cause.
    /// </summary>
    private const string JsonStartHint =
        " Text that starts with '{' or '[' is always read as JSON; to store a plaintext value that starts with one of these characters, put it inside a JSON object.";

    /// <summary>
    /// The deepest JSON nesting that is loaded: the System.Text.Json default, which
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> applies when no depth is configured.
    /// </summary>
    private const int MaxJsonDepth = 64;

    /// <summary>Logged in place of the AWS error code when the exception carries none.</summary>
    private const string NoErrorCode = "none";

    private readonly string _secretName;

    /// <summary>
    /// The client supplied from outside, used for every load. <c>null</c> when the package owns the clients
    /// (then <see cref="_takeClientForLoad"/> is set).
    /// </summary>
    private readonly IAmazonSecretsManager? _suppliedClient;

    /// <summary>
    /// When the package owns the clients: gets the client for one load from the source. <c>null</c> when the
    /// client was supplied.
    /// </summary>
    private readonly Func<IAmazonSecretsManager>? _takeClientForLoad;

    private readonly AWSSecretsManagerOptions _options;
    private readonly ILogger? _logger;
    private readonly IDelayStrategy _delayStrategy;
    private readonly bool _disposeClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="AWSSecretsManagerConfigurationProvider"/> class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the source holds a client supplied from outside, the provider uses that client for every load and,
    /// if the source says so, disposes it with the provider.
    /// </para>
    /// <para>
    /// When the <c>AddAWSSecretsManager</c> extension methods created the client (the package owns the
    /// clients), the provider keeps no client at all: each load gets a new one from the source and disposes it
    /// when the load ends, whether it succeeded or failed. This package reads a secret once per load and keeps
    /// no connection, so nothing has to outlive the load. No load client is left behind when a load fails, when
    /// a provider is dropped without being disposed (as <c>ConfigurationBuilder.Build()</c> does with its other
    /// providers when one fails to load), or when a provider is loaded again; and providers never share a
    /// client, however they were created. The only client that is not disposed is the one the source creates
    /// at registration and exposes as
    /// <see cref="AWSSecretsManagerConfigurationSource.SecretsManagerClient"/>; loads do not use it.
    /// </para>
    /// </remarks>
    /// <param name="source">The configuration source.</param>
    public AWSSecretsManagerConfigurationProvider(AWSSecretsManagerConfigurationSource source)
    {
        _secretName = source.SecretName;
        _options = source.Options;
        _logger = source.Logger;
        _delayStrategy = source.DelayStrategy;

        if (source.OwnsClients)
        {
            _takeClientForLoad = source.TakeClientForLoad;
        }
        else
        {
            _suppliedClient = source.SecretsManagerClient;
            _disposeClient = source.DisposeClient;
        }
    }

    /// <inheritdoc />
    public override void Load()
    {
        if (_takeClientForLoad is null)
        {
            LoadCore(_suppliedClient!);
            return;
        }

        // Package-owned client: created for this load only, so concurrent loads never share one.
        IAmazonSecretsManager client;
        try
        {
            client = _takeClientForLoad();
        }
        catch (Exception ex)
        {
            // Reached when a client can no longer be created, for example because the AWS region setting was
            // removed after registration. Logged like the other load errors: with masking on, the exception
            // itself is left out and only its type is named.
            _logger?.LogError(
                LoggableException(ex),
                "Failed to create an AWS Secrets Manager client to load secret '{SecretName}' ({ExceptionType}).",
                _options.GetMaskedSecretName(_secretName),
                ex.GetType().Name);

            throw;
        }

        try
        {
            LoadCore(client);
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                // Releasing the client must change the outcome of the load in neither direction: a load that
                // succeeded stays successful, and a load error is not replaced by a cleanup error.
                //
                // The exception is always passed to the logger, even with masking on. It is swallowed here, so
                // the log is the only place its message and stack trace can be seen, and it comes from
                // releasing a client, not from an AWS response, so it cannot name the secret.
                _logger?.LogWarning(
                    ex,
                    "Failed to dispose the AWS Secrets Manager client used to load secret '{SecretName}' ({ExceptionType}).",
                    _options.GetMaskedSecretName(_secretName),
                    ex.GetType().Name);
            }
        }
    }

    private void LoadCore(IAmazonSecretsManager client)
    {
        var plaintextKey = NormalizePlaintextKey();

        var request = new GetSecretValueRequest
        {
            SecretId = _secretName
        };

        var response = GetSecretValueWithRetry(client, request);
        var secretValue = response?.SecretString;

        if (string.IsNullOrEmpty(secretValue))
        {
            _logger?.LogError(
                "AWS Secrets Manager secret '{SecretName}' does not contain a text secret. Binary secrets are not supported.",
                _options.GetMaskedSecretName(_secretName));

            throw new InvalidOperationException(
                $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' does not contain a text secret. Binary secrets are not supported.");
        }

        var firstSignificantCharacter = FirstSignificantCharacter(secretValue);

        if (firstSignificantCharacter is null)
        {
            _logger?.LogError(
                "AWS Secrets Manager secret '{SecretName}' is empty: it contains only whitespace.",
                _options.GetMaskedSecretName(_secretName));

            throw new InvalidOperationException(
                $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' is empty: it contains only whitespace.");
        }

        if (!IsJsonStart(firstSignificantCharacter.Value))
        {
            LoadPlaintext(secretValue, plaintextKey ?? DerivePlaintextKey(response?.Name));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(secretValue, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            var flattened = JsonFlattener.Flatten(document.RootElement);

            Data = flattened is Dictionary<string, string?> dictionary
                ? dictionary
                : new Dictionary<string, string?>(flattened, StringComparer.OrdinalIgnoreCase);

            if (plaintextKey is not null && Data.Count == 0)
            {
                // The user expects a plaintext value under PlaintextKey, but text starting with '{' or '[' is
                // always read as JSON, and this one (for example {} or ["a","b"]) holds nothing to load.
                _logger?.LogWarning(
                    "AWS Secrets Manager secret '{SecretName}' starts with '{{' or '[', so it was read as JSON and {OptionName} was not used; it produced no configuration keys. To store a plaintext value that starts with one of these characters, put it inside a JSON object.",
                    _options.GetMaskedSecretName(_secretName),
                    PlaintextKeyOptionName);
            }

            _logger?.LogInformation(
                "Loaded configuration from AWS Secrets Manager secret '{SecretName}'.",
                _options.GetMaskedSecretName(_secretName));
        }
        catch (JsonException)
        {
            // The JsonException is deliberately neither logged nor attached as an inner exception: its message
            // quotes the offending character, which is part of the secret value. Only the position is reported.
            // The text is parsed again without the depth limit: either it then parses, which means the depth
            // limit was the only problem, or it yields the real error, whose position is the one worth reporting
            // (the first failure may only have been the point where the depth limit was reached).
            var error = FindJsonErrorIgnoringDepth(secretValue);

            if (error is null)
            {
                // Saying "not valid JSON" here would be wrong and would send the reader looking for a typo.
                _logger?.LogError(
                    "AWS Secrets Manager secret '{SecretName}' contains JSON nested deeper than the supported {MaxJsonDepth} levels.",
                    _options.GetMaskedSecretName(_secretName),
                    MaxJsonDepth);

                throw new InvalidOperationException(
                    $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' contains JSON nested deeper than the supported {MaxJsonDepth} levels.");
            }

            var position = DescribeJsonErrorPosition(error);

            var hint = JsonStartHint + DescribeLeadingCharacterProblem(secretValue);

            _logger?.LogError(
                "Failed to parse JSON for AWS Secrets Manager secret '{SecretName}'{Position}.{Hint}",
                _options.GetMaskedSecretName(_secretName),
                position,
                hint);

            throw new InvalidOperationException(
                $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' does not contain valid JSON{position}.{hint}");
        }
    }

    /// <summary>
    /// Loads a plaintext secret: a single value, stored exactly as received under one key, with a warning for
    /// each sign that the value may not be what the user meant to store.
    /// </summary>
    private void LoadPlaintext(string secretValue, string key)
    {
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = secretValue
        };

        var maskedSecretName = _options.GetMaskedSecretName(_secretName);

        if (secretValue.AsSpan().TrimStart().StartsWith([ByteOrderMark]))
        {
            // The mark is invisible, so a password that carries it looks correct everywhere and still fails.
            _logger?.LogWarning(
                "AWS Secrets Manager secret '{SecretName}' starts with a byte-order mark (U+FEFF), which is kept as part of the plaintext value. If that is not intended, store the secret without it (for example, save the file as UTF-8 without BOM).",
                maskedSecretName);
        }

        if (IsSingleLineEndingInLineBreak(secretValue))
        {
            // Typical cause: the secret was created from a file (--secret-string file://...) that ends in a newline.
            // Multi-line values such as a PEM key or certificate correctly end in one, so they are left alone.
            _logger?.LogWarning(
                "AWS Secrets Manager secret '{SecretName}' ends with a line break, which is kept as part of the plaintext value. If that is not intended, store the secret without the trailing line break.",
                maskedSecretName);
        }

        if (LooksLikeJsonWithStrayPrefix(secretValue))
        {
            // Typical causes: literal quotes added by a shell, or an invisible character before the '{'.
            _logger?.LogWarning(
                "AWS Secrets Manager secret '{SecretName}' was loaded as a single plaintext value, but it looks like JSON preceded by other characters. If it was meant to be JSON, check how the secret was stored.",
                maskedSecretName);
        }

        // The key is deliberately not logged: it is derived from the secret name, which may be masked.
        _logger?.LogInformation(
            "Loaded AWS Secrets Manager secret '{SecretName}' as a single plaintext configuration value.",
            maskedSecretName);
    }

    /// <summary>
    /// Returns <c>true</c> when the value is one line of text followed by a line break.
    /// </summary>
    private static bool IsSingleLineEndingInLineBreak(string secretValue)
    {
        if (secretValue[^1] is not ('\n' or '\r'))
        {
            return false;
        }

        return secretValue.AsSpan().TrimEnd("\r\n").IndexOfAny('\n', '\r') < 0;
    }

    /// <summary>
    /// Parses the text without a depth limit and returns the error, or <c>null</c> when it parses, which means
    /// the text is valid JSON that is merely nested deeper than <see cref="MaxJsonDepth"/>. Called on the
    /// failure path only.
    /// </summary>
    private static JsonException? FindJsonErrorIgnoringDepth(string secretValue)
    {
        try
        {
            using var document = JsonDocument.Parse(secretValue, new JsonDocumentOptions { MaxDepth = int.MaxValue });
            return null;
        }
        catch (JsonException exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Describes where a JSON parse error occurred, as a message suffix such as
    /// <c> (line 2, byte 3; both counted from 1)</c>, without revealing any of the secret text.
    /// </summary>
    /// <remarks>
    /// System.Text.Json reports a zero-based line and a zero-based byte offset within that line. Both are
    /// shifted by one so they match what an editor shows (for ASCII text the byte number is the column).
    /// Returns an empty string if the exception carries no position, so that no made-up position is reported;
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> always supplies one.
    /// </remarks>
    private static string DescribeJsonErrorPosition(JsonException exception)
    {
        if (exception.LineNumber is not { } zeroBasedLine || exception.BytePositionInLine is not { } zeroBasedByte)
        {
            return string.Empty;
        }

        return $" (line {zeroBasedLine + 1}, byte {zeroBasedByte + 1}; both counted from 1)";
    }

    /// <summary>
    /// Explains an invalid-JSON failure caused by an invisible character in front of the JSON, as a sentence to
    /// append to the error message, or returns an empty string when that is not the cause.
    /// </summary>
    /// <remarks>
    /// JSON allows only space, tab, carriage return and line feed before the document. A byte-order mark or
    /// another whitespace character (such as a non-breaking space) is skipped when deciding between JSON and
    /// plaintext but is rejected by the parser, at a position that looks like a valid first character in an
    /// editor. Only such a character is named here, so nothing of the secret's content is revealed.
    /// </remarks>
    private static string DescribeLeadingCharacterProblem(string secretValue)
    {
        var text = secretValue.AsSpan().TrimStart(" \t\r\n");
        if (text.IsEmpty)
        {
            return string.Empty;
        }

        var first = text[0];

        if (first == ByteOrderMark)
        {
            return " The secret text starts with a byte-order mark (U+FEFF), which is not valid JSON; store the secret without it (for example, save the file as UTF-8 without BOM).";
        }

        if (char.IsWhiteSpace(first))
        {
            return $" The secret text starts with the whitespace character U+{(int)first:X4}, which JSON does not allow; remove it.";
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns <c>true</c> when a plaintext secret looks like a JSON document that something was wrapped around
    /// or put in front of, which usually means the secret was stored wrongly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Surrounding whitespace, byte-order marks and one matching pair of wrapping quotes are set aside first
    /// (quotes added by a shell, or a JSON document that was encoded a second time as a JSON string). What
    /// remains must end in <c>}</c> or <c>]</c>, have the matching opening bracket within its first
    /// <see cref="MaxStrayPrefixLength"/> + 1 characters, and continue after that bracket the way JSON does
    /// (a quote, an escaped quote, another bracket, a digit or whitespace).
    /// </para>
    /// <para>
    /// Requiring the bracket near the start keeps ordinary plaintext quiet, for example an ODBC connection
    /// string such as <c>Driver={...};Pwd={...}</c>, whose first bracket comes much later.
    /// </para>
    /// </remarks>
    private static bool LooksLikeJsonWithStrayPrefix(string secretValue)
    {
        var text = secretValue.AsSpan().Trim().Trim(ByteOrderMark).Trim();

        if (text.Length >= 2 && text[0] is '"' or '\'' && text[^1] == text[0])
        {
            text = text[1..^1].Trim();
        }

        if (text.IsEmpty)
        {
            return false;
        }

        var openingBracket = text[^1] switch
        {
            '}' => '{',
            ']' => '[',
            _ => '\0'
        };

        if (openingBracket == '\0')
        {
            return false;
        }

        var openingIndex = text.IndexOf(openingBracket);
        if (openingIndex < 0 || openingIndex > MaxStrayPrefixLength)
        {
            return false;
        }

        // What follows the bracket must also look like JSON, so that ordinary plaintext such as
        // "${DB_PASSWORD}" or "a{b}" does not send the operator looking for a problem that is not there.
        // (A backslash covers a JSON document that was encoded a second time: {\"A\":1}.)
        var next = text[openingIndex + 1];
        return next is '"' or '\\' or '}' or ']' or '{' or '[' || char.IsAsciiDigit(next) || char.IsWhiteSpace(next);
    }

    /// <summary>
    /// Returns the first character of the secret text that is neither whitespace nor a byte-order mark
    /// (U+FEFF, left by files saved as "UTF-8 with BOM"), or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// This single rule drives both decisions in <see cref="Load"/>: no significant character means the secret
    /// is empty, and the significant character decides between JSON and plaintext. Sharing it keeps the two
    /// checks from disagreeing about what counts as blank.
    /// </remarks>
    private static char? FirstSignificantCharacter(string secretValue)
    {
        foreach (var character in secretValue)
        {
            if (!char.IsWhiteSpace(character) && character != ByteOrderMark)
            {
                return character;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when the secret text is meant to be JSON: its first significant character is
    /// <c>{</c> or <c>[</c>. Such text is parsed strictly, so malformed JSON still fails startup.
    /// </summary>
    private static bool IsJsonStart(char firstSignificantCharacter)
        => firstSignificantCharacter is '{' or '[';

    /// <summary>
    /// Returns <see cref="AWSSecretsManagerOptions.PlaintextKey"/> in the form it is stored under, or
    /// <c>null</c> when it is not set.
    /// </summary>
    /// <remarks>
    /// Whitespace around the key and around each <c>:</c> segment is removed, so
    /// <c>"ConnectionStrings : Default"</c> is stored as <c>ConnectionStrings:Default</c>. A key with an empty
    /// segment (such as <c>:A</c>, <c>A:</c> or <c>A::B</c>) would store the value where no normal configuration
    /// path can read it, so it fails startup instead. This runs before AWS is called.
    /// </remarks>
    private string? NormalizePlaintextKey()
    {
        if (string.IsNullOrWhiteSpace(_options.PlaintextKey))
        {
            return null;
        }

        var segments = _options.PlaintextKey
            .Split(ConfigurationPath.KeyDelimiter)
            .Select(segment => segment.Trim())
            .ToArray();

        if (segments.Any(segment => segment.Length == 0))
        {
            _logger?.LogError(
                "{OptionName} '{PlaintextKey}' is not a valid configuration key: it contains an empty '{KeyDelimiter}' segment.",
                PlaintextKeyOptionName,
                _options.PlaintextKey,
                ConfigurationPath.KeyDelimiter);

            throw new InvalidOperationException(
                $"{PlaintextKeyOptionName} '{_options.PlaintextKey}' is not a valid configuration key: it contains an empty '{ConfigurationPath.KeyDelimiter}' segment.");
        }

        return string.Join(ConfigurationPath.KeyDelimiter, segments);
    }

    /// <summary>
    /// Derives the plaintext key from the secret name: its last <c>/</c>-separated segment.
    /// </summary>
    private string DerivePlaintextKey(string? responseName)
    {
        // Prefer the friendly name AWS returns, so a secret requested by ARN still gets a readable key.
        var name = string.IsNullOrWhiteSpace(responseName) ? _secretName : responseName;

        const string arnResourceMarker = ":secret:";
        var markerIndex = name.IndexOf(arnResourceMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0 && markerIndex + arnResourceMarker.Length < name.Length)
        {
            name = name[(markerIndex + arnResourceMarker.Length)..];
        }

        var lastSlashIndex = name.LastIndexOf('/');
        var key = lastSlashIndex >= 0 ? name[(lastSlashIndex + 1)..] : name;

        if (key.Length == 0)
        {
            key = name;
        }

        // A derived key must meet the same rule as a user-supplied one. Real AWS secret names cannot contain
        // ':', so this is only reachable with an unusual secret id and no name in the response (for example
        // an id that ends in ":secret:"). The message does not include the key, because it comes from the name.
        if (key.Split(ConfigurationPath.KeyDelimiter).Any(string.IsNullOrWhiteSpace))
        {
            _logger?.LogError(
                "Cannot derive a configuration key for plaintext AWS Secrets Manager secret '{SecretName}' from its name. Set {OptionName} to choose the key.",
                _options.GetMaskedSecretName(_secretName),
                PlaintextKeyOptionName);

            throw new InvalidOperationException(
                $"Cannot derive a configuration key for plaintext AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' from its name. Set {PlaintextKeyOptionName} to choose the key.");
        }

        return key;
    }

    /// <summary>
    /// Returns the exception to hand to the logger, or <c>null</c> when secret-name masking is enabled.
    /// </summary>
    /// <remarks>
    /// AWS error messages can name the resource (for example an access-denied message that includes the secret's
    /// ARN), which would put the full secret name in the log despite masking. With masking enabled, the log entry
    /// carries the AWS error code and HTTP status instead of the exception.
    /// </remarks>
    private Exception? LoggableException(Exception exception)
        => _options.MaskSecretNameInLogs ? null : exception;

    /// <summary>
    /// Describes an AWS error for a log entry without using its message, which can name the secret:
    /// the exception type, the AWS error code, the HTTP status and request id when AWS supplied them, and the
    /// type of the inner exception when there is one.
    /// Example: <c>InternalServiceErrorException, error code none, HTTP 500, request id 1a2b</c>.
    /// </summary>
    private static string DescribeAwsError(AmazonServiceException exception)
    {
        var errorCode = string.IsNullOrWhiteSpace(exception.ErrorCode) ? NoErrorCode : exception.ErrorCode;
        var description = $"{exception.GetType().Name}, error code {errorCode}";

        var statusCode = (int)exception.StatusCode;
        if (statusCode != 0)
        {
            description += $", HTTP {statusCode}";
        }

        if (!string.IsNullOrWhiteSpace(exception.RequestId))
        {
            description += $", request id {exception.RequestId}";
        }

        // Only the type of the underlying failure, never its message: it tells a network failure from a
        // credentials failure when AWS supplied no error code or status.
        if (exception.InnerException is { } innerException)
        {
            description += $", caused by {innerException.GetType().Name}";
        }

        return description;
    }

    private GetSecretValueResponse? GetSecretValueWithRetry(IAmazonSecretsManager client, GetSecretValueRequest request)
    {
        AmazonServiceException? lastServiceException = null;

        for (var attempt = 0; attempt < DefaultMaxRetries; attempt++)
        {
            try
            {
                var response = client
                    .GetSecretValueAsync(request, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                return response;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger?.LogError(
                    LoggableException(ex),
                    "AWS Secrets Manager secret '{SecretName}' was not found ({AwsError}).",
                    _options.GetMaskedSecretName(_secretName),
                    DescribeAwsError(ex));

                throw new InvalidOperationException(
                    $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' was not found.",
                    ex);
            }
            catch (AmazonServiceException ex) when (IsTransient(ex))
            {
                lastServiceException = ex;

                var attemptNumber = attempt + 1;
                if (attemptNumber >= DefaultMaxRetries)
                {
                    break;
                }

                var delay = DefaultBaseDelayMilliseconds * attemptNumber;

                _logger?.LogWarning(
                    LoggableException(ex),
                    "Transient error ({AwsError}) retrieving AWS Secrets Manager secret '{SecretName}': attempt {Attempt} of {MaxRetries} failed, retrying after {DelayMs} ms.",
                    DescribeAwsError(ex),
                    _options.GetMaskedSecretName(_secretName),
                    attemptNumber,
                    DefaultMaxRetries,
                    delay);

                _delayStrategy.Delay(delay);
            }
            catch (AmazonServiceException ex)
            {
                lastServiceException = ex;

                _logger?.LogError(
                    LoggableException(ex),
                    "Error ({AwsError}) retrieving AWS Secrets Manager secret '{SecretName}'.",
                    DescribeAwsError(ex),
                    _options.GetMaskedSecretName(_secretName));

                throw;
            }
        }

        // If we reach here, all retries have been exhausted for transient failures.
        if (lastServiceException != null)
        {
            _logger?.LogError(
                LoggableException(lastServiceException),
                "Failed to retrieve AWS Secrets Manager secret '{SecretName}' after {MaxRetries} attempts; last error: {AwsError}.",
                _options.GetMaskedSecretName(_secretName),
                DefaultMaxRetries,
                DescribeAwsError(lastServiceException));

            throw new InvalidOperationException(
                $"Failed to retrieve AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' after {DefaultMaxRetries} attempts.",
                lastServiceException);
        }

        return null;
    }

    private static bool IsTransient(AmazonServiceException exception)
    {
        if (exception is InternalServiceErrorException)
        {
            return true;
        }

        // Throttling or rate limiting errors.
        if (string.Equals(exception.ErrorCode, "ThrottlingException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exception.ErrorCode, "Throttling", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // HTTP 408 Request Timeout or 5xx status codes.
        var statusCode = (int)exception.StatusCode;
        if (statusCode == 408 || statusCode == 429 || statusCode >= 500)
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // With package-owned clients there is nothing to do: every client is disposed when its load ends.
        if (_disposeClient)
        {
            _suppliedClient!.Dispose();
        }
    }
}

