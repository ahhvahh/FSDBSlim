namespace FSDBSlim.Utils;

using System;
using System.Collections.Generic;
using System.Linq;

public static class FilePathHelper
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty", nameof(path));
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sanitized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == ".")
            {
                continue;
            }

            if (trimmed is "..")
            {
                throw new InvalidOperationException("Path traversal is not allowed");
            }

            sanitized.Add(trimmed);
        }

        if (sanitized.Count == 0)
        {
            throw new ArgumentException("Path must contain at least one segment", nameof(path));
        }

        return string.Join('/', sanitized);
    }

    public static string GetFileName(string normalizedPath)
    {
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Last();
    }

    public static (string FileName, string? Extension) GetFileNameAndExtension(string normalizedPath)
    {
        var fileName = GetFileName(normalizedPath);
        var index = fileName.LastIndexOf('.');
        if (index <= 0 || index == fileName.Length - 1)
        {
            return (fileName, null);
        }

        return (fileName, fileName[(index + 1)..]);
    }
}
