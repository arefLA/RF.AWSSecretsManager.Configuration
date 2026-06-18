namespace RF.AWSSecretsManager.Configuration;

/// <summary>
/// Options controlling how AWS Secrets Manager configuration is loaded and logged.
/// </summary>
public sealed class AWSSecretsManagerOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the secret name should be masked in logs.
    /// </summary>
    /// <remarks>
    /// When <c>true</c>, the <see cref="SecretNameMaskStyle"/> is used to mask the secret name.
    /// The actual secret value is never logged regardless of this setting.
    /// </remarks>
    public bool MaskSecretNameInLogs { get; set; }

    /// <summary>
    /// Gets or sets the masking style used when <see cref="MaskSecretNameInLogs"/> is enabled.
    /// </summary>
    public SecretNameMaskStyle SecretNameMaskStyle { get; set; } = SecretNameMaskStyle.PrefixAndSuffix;

    /// <summary>
    /// Masks the specified secret name according to the current options.
    /// </summary>
    /// <param name="secretName">The secret name to mask.</param>
    /// <returns>The masked secret name if masking is enabled; otherwise the original secret name.</returns>
    public string GetMaskedSecretName(string secretName)
    {
        if (!MaskSecretNameInLogs)
        {
            return secretName;
        }

        return SecretNameMaskStyle switch
        {
            SecretNameMaskStyle.PrefixAndSuffix => MaskPrefixAndSuffix(secretName),
            _ => "***"
        };
    }

    private static string MaskPrefixAndSuffix(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "***";
        }

        // Show first 3 and last 2 characters when possible.
        const int prefixLength = 3;
        const int suffixLength = 2;

        if (value.Length <= prefixLength + suffixLength)
        {
            return "***";
        }

        var prefix = value[..prefixLength];
        var suffix = value[^suffixLength..];
        return $"{prefix}***{suffix}";
    }
}

/// <summary>
/// Defines how secret names are masked when written to logs.
/// </summary>
public enum SecretNameMaskStyle
{
    /// <summary>
    /// Masks the secret name by showing the first three and last two characters, with <c>***</c> in the middle.
    /// </summary>
    PrefixAndSuffix = 0
}

