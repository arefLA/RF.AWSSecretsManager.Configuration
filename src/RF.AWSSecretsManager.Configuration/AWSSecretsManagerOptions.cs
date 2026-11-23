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
    /// When <c>true</c>, the <see cref="SecretNameMaskStyle"/> is used to mask the secret name, and AWS
    /// exceptions are not passed to the logger (their messages can name the secret). Whether or not masking is
    /// enabled, every AWS error log entry (not found, transient retry, retries exhausted, other errors) carries
    /// the exception type and AWS error code, plus the HTTP status and request id when AWS supplied them and
    /// the type (not the message) of the inner exception when there is one, so
    /// the failure stays diagnosable without the exception. Exceptions thrown to the caller are not altered and
    /// may still name the secret. The actual secret value is never logged regardless of this setting.
    /// </remarks>
    public bool MaskSecretNameInLogs { get; set; }

    /// <summary>
    /// Gets or sets the masking style used when <see cref="MaskSecretNameInLogs"/> is enabled.
    /// </summary>
    public SecretNameMaskStyle SecretNameMaskStyle { get; set; } = SecretNameMaskStyle.PrefixAndSuffix;

    /// <summary>
    /// Gets or sets the configuration key used when the secret is a single plaintext value rather than a JSON object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A secret whose first significant character (ignoring whitespace and a leading byte-order mark) is not
    /// <c>{</c> or <c>[</c> is loaded as one configuration entry holding the secret text exactly as stored.
    /// When this property is <c>null</c> or whitespace, the key is the last <c>/</c>-separated segment of the
    /// secret name (for example <c>prod/myapp/db-password</c> becomes <c>db-password</c>). The name AWS returns
    /// with the secret is used when available, so a secret requested by ARN still gets a readable key;
    /// otherwise the requested secret id is used. A name that ends in <c>/</c> has no last segment, so the
    /// whole name is used as the key.
    /// </para>
    /// <para>
    /// The byte-order mark is skipped only to decide whether the secret is JSON or plaintext. A JSON secret
    /// that starts with a byte-order mark is still not supported: it fails startup as invalid JSON. Save such a
    /// secret without the byte-order mark.
    /// </para>
    /// <para>
    /// Set this to choose the key yourself, including hierarchical keys such as <c>ConnectionStrings:Default</c>.
    /// Whitespace around the key and around each <c>:</c> segment is removed, so
    /// <c>"ConnectionStrings : Default"</c> is stored as <c>ConnectionStrings:Default</c>. A key with an empty
    /// <c>:</c> segment (such as <c>:A</c>, <c>A:</c> or <c>A::B</c>) is rejected with an
    /// <see cref="InvalidOperationException"/> when configuration is loaded. A valid key has no effect on JSON
    /// secrets.
    /// </para>
    /// <para>
    /// Two plaintext secrets whose names end in the same segment (for example <c>a/password</c> and
    /// <c>b/password</c>) produce the same derived key, and the one added later wins; set this property to keep
    /// them apart.
    /// </para>
    /// <para>
    /// A plaintext value that itself starts with <c>{</c> or <c>[</c> is treated as JSON. It fails startup when
    /// it is not valid JSON; when it happens to be valid JSON it is not loaded as a value either: <c>{}</c> or
    /// <c>[1]</c> produce no configuration keys and no error, and <c>{"a":1}</c> produces the key <c>a</c>.
    /// Store such a value inside a JSON object instead. Keep this in mind for rotated passwords: generated
    /// passwords can begin with either character unless those characters are excluded.
    /// </para>
    /// </remarks>
    public string? PlaintextKey { get; set; }

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

