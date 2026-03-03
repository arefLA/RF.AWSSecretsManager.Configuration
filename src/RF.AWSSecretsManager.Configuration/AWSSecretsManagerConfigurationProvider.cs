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

    private readonly string _secretName;
    private readonly IAmazonSecretsManager _client;
    private readonly AWSSecretsManagerOptions _options;
    private readonly ILogger? _logger;
    private readonly IDelayStrategy _delayStrategy;
    private readonly bool _disposeClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="AWSSecretsManagerConfigurationProvider"/> class.
    /// </summary>
    /// <param name="source">The configuration source.</param>
    public AWSSecretsManagerConfigurationProvider(AWSSecretsManagerConfigurationSource source)
    {
        _secretName = source.SecretName;
        _client = source.SecretsManagerClient;
        _options = source.Options;
        _logger = source.Logger;
        _delayStrategy = source.DelayStrategy;
        _disposeClient = source.DisposeClient;
    }

    /// <inheritdoc />
    public override void Load()
    {
        var request = new GetSecretValueRequest
        {
            SecretId = _secretName
        };

        var secretValue = GetSecretValueWithRetry(request);

        if (string.IsNullOrEmpty(secretValue))
        {
            throw new InvalidOperationException(
                $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' does not contain a text secret. Binary secrets are not supported.");
        }

        try
        {
            using var document = JsonDocument.Parse(secretValue);
            var flattened = JsonFlattener.Flatten(document.RootElement);

            Data = flattened is Dictionary<string, string?> dictionary
                ? dictionary
                : new Dictionary<string, string?>(flattened, StringComparer.OrdinalIgnoreCase);

            _logger?.LogInformation(
                "Loaded configuration from AWS Secrets Manager secret '{SecretName}'.",
                _options.GetMaskedSecretName(_secretName));
        }
        catch (JsonException ex)
        {
            _logger?.LogError(
                ex,
                "Failed to parse JSON for AWS Secrets Manager secret '{SecretName}'.",
                _options.GetMaskedSecretName(_secretName));

            throw new InvalidOperationException(
                $"AWS Secrets Manager secret '{_options.GetMaskedSecretName(_secretName)}' does not contain valid JSON.",
                ex);
        }
    }

    private string? GetSecretValueWithRetry(GetSecretValueRequest request)
    {
        AmazonServiceException? lastServiceException = null;

        for (var attempt = 0; attempt < DefaultMaxRetries; attempt++)
        {
            try
            {
                var response = _client
                    .GetSecretValueAsync(request, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                return response.SecretString;
            }
            catch (ResourceNotFoundException ex)
            {
                _logger?.LogError(
                    ex,
                    "AWS Secrets Manager secret '{SecretName}' was not found.",
                    _options.GetMaskedSecretName(_secretName));

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
                    ex,
                    "Transient error retrieving AWS Secrets Manager secret '{SecretName}', retry {Attempt} of {MaxRetries} after {DelayMs} ms.",
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
                    ex,
                    "Error retrieving AWS Secrets Manager secret '{SecretName}'.",
                    _options.GetMaskedSecretName(_secretName));

                throw;
            }
        }

        // If we reach here, all retries have been exhausted for transient failures.
        if (lastServiceException != null)
        {
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
        if (_disposeClient)
        {
            _client.Dispose();
        }
    }
}

