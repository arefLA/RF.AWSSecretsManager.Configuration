using System.Text.Json;

namespace RF.AWSSecretsManager.Configuration;

/// <summary>
/// Helper for converting JSON objects into a flat key-value representation suitable for configuration.
/// </summary>
internal static class JsonFlattener
{
    /// <summary>
    /// Flattens the specified <see cref="JsonElement"/> into a dictionary of configuration keys and values.
    /// </summary>
    /// <param name="root">The root JSON element. Expected to be an object.</param>
    /// <returns>A dictionary of flattened keys and string values.</returns>
    /// <remarks>
    /// Nested objects are flattened using <c>:</c> as the separator (e.g. <c>ConnectionStrings:Database</c>).
    /// Arrays are ignored in v1 by design and produce no configuration entries.
    /// </remarks>
    public static IDictionary<string, string?> Flatten(JsonElement root)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenElement(root, parentPath: null, result);
        return result;
    }

    private static void FlattenElement(JsonElement element, string? parentPath, IDictionary<string, string?> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = string.IsNullOrEmpty(parentPath)
                        ? property.Name
                        : $"{parentPath}:{property.Name}";

                    FlattenElement(property.Value, path, result);
                }

                break;

            case JsonValueKind.Array:
                // V1: Arrays are intentionally ignored. Future versions may project them as Key:0, Key:1, etc.
                break;

            case JsonValueKind.Null:
                if (!string.IsNullOrEmpty(parentPath))
                {
                    result[parentPath] = null;
                }

                break;

            default:
                if (!string.IsNullOrEmpty(parentPath))
                {
                    result[parentPath] = element.ToString();
                }

                break;
        }
    }
}

