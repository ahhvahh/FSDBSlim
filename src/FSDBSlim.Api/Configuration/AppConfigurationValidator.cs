namespace FSDBSlim.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

public sealed class AppConfigurationValidator : IValidateOptions<AppConfiguration>
{
    public ValidateOptionsResult Validate(string? name, AppConfiguration options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Auth.Header))
        {
            errors.Add("Auth header must be provided");
        }

        options.Auth.Keys = options.Auth.Keys
            .Select(k => k.Trim())
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (options.Auth.Keys.Count == 0)
        {
            errors.Add("At least one API key must be configured");
        }

        if (options.Server.MaxUploadMB <= 0)
        {
            errors.Add("MaxUploadMB must be greater than zero");
        }

        if (!Uri.TryCreate(options.Server.Urls, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Scheme))
        {
            errors.Add("Server.Urls must be a valid absolute URI");
        }

        options.Storage.AllowedContentTypes = options.Storage.AllowedContentTypes
            .Select(ct => ct.Trim())
            .Where(ct => !string.IsNullOrEmpty(ct))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (options.Storage.AllowedContentTypes.Any(ct => string.IsNullOrWhiteSpace(ct)))
        {
            errors.Add("Allowed content types must not contain blank entries");
        }

        options.Storage.FileTypes.NoCompression = options.Storage.FileTypes.NoCompression
            .Select(ct => ct.Trim())
            .Where(ct => !string.IsNullOrEmpty(ct))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (options.Storage.Compression.Default.Enabled)
        {
            var method = options.Storage.Compression.Default.Method.ToLowerInvariant();
            if (method is not ("gzip" or "brotli"))
            {
                errors.Add("Compression method must be gzip or brotli when enabled");
            }

            var level = options.Storage.Compression.Default.Level.ToLowerInvariant();
            if (level is not ("optimal" or "fastest"))
            {
                errors.Add("Compression level must be Optimal or Fastest");
            }
        }

        if (options.Storage.FileTypes.NoCompression.Any(ct => string.IsNullOrWhiteSpace(ct)))
        {
            errors.Add("FileTypes.NoCompression cannot contain blank entries");
        }

        var normalized = NormalizePathBase(options.Server.PathBase);
        if (normalized is null)
        {
            errors.Add("Server.PathBase must start with '/' and not end with '/' (unless it is '/')");
        }
        else
        {
            options.Server.PathBase = normalized;
        }

        if (errors.Count > 0)
        {
            return ValidateOptionsResult.Fail(errors);
        }

        return ValidateOptionsResult.Success;
    }

    private static string? NormalizePathBase(string? pathBase)
    {
        if (string.IsNullOrWhiteSpace(pathBase))
        {
            return string.Empty;
        }

        if (!pathBase!.StartsWith('/'))
        {
            return null;
        }

        if (pathBase.Length > 1 && pathBase.EndsWith('/'))
        {
            pathBase = pathBase.TrimEnd('/');
        }

        return pathBase;
    }
}
