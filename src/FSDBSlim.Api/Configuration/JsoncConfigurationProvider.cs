namespace FSDBSlim.Configuration;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

public sealed class JsoncConfigurationSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new JsoncConfigurationProvider(this);
    }
}

public sealed class JsoncConfigurationProvider : FileConfigurationProvider
{
    public JsoncConfigurationProvider(JsoncConfigurationSource source) : base(source)
    {
    }

    public override void Load(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        var sanitized = JsoncSanitizer.StripComments(json);
        using var doc = JsonDocument.Parse(sanitized);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        VisitElement(doc.RootElement, parentPath: null, data);
        Data = data;
    }

    private static void VisitElement(JsonElement element, string? parentPath, IDictionary<string, string?> data)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = parentPath is null ? property.Name : ConfigurationPath.Combine(parentPath, property.Name);
                    VisitElement(property.Value, path, data);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var path = ConfigurationPath.Combine(parentPath!, index.ToString());
                    VisitElement(item, path, data);
                    index++;
                }
                if (index == 0)
                {
                    data[parentPath!] = string.Empty;
                }
                break;
            case JsonValueKind.Null:
                data[parentPath!] = null;
                break;
            default:
                data[parentPath!] = element.ToString();
                break;
        }
    }
}

internal static class JsoncSanitizer
{
    public static string StripComments(string jsonWithComments)
    {
        var result = new StringBuilder(jsonWithComments.Length);
        bool insideString = false;
        bool escaping = false;

        for (var i = 0; i < jsonWithComments.Length; i++)
        {
            var current = jsonWithComments[i];

            if (insideString)
            {
                result.Append(current);
                if (escaping)
                {
                    escaping = false;
                }
                else if (current == '\\')
                {
                    escaping = true;
                }
                else if (current == '"')
                {
                    insideString = false;
                }

                continue;
            }

            if (current == '"')
            {
                insideString = true;
                result.Append(current);
                continue;
            }

            if (current == '/' && i + 1 < jsonWithComments.Length)
            {
                var next = jsonWithComments[i + 1];
                if (next == '/')
                {
                    i += 2;
                    while (i < jsonWithComments.Length && jsonWithComments[i] != '\n')
                    {
                        i++;
                    }
                    result.Append('\n');
                    continue;
                }

                if (next == '*')
                {
                    i += 2;
                    while (i + 1 < jsonWithComments.Length && !(jsonWithComments[i] == '*' && jsonWithComments[i + 1] == '/'))
                    {
                        i++;
                    }
                    i++;
                    continue;
                }
            }

            result.Append(current);
        }

        return result.ToString();
    }
}
