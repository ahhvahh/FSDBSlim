namespace FSDBSlim.Middleware;

using System;
using System.Threading.Tasks;
using FSDBSlim.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
    private readonly IOptionsMonitor<AppConfiguration> _options;

    public ApiKeyAuthenticationMiddleware(RequestDelegate next, ILogger<ApiKeyAuthenticationMiddleware> logger, IOptionsMonitor<AppConfiguration> options)
    {
        _next = next;
        _logger = logger;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsHealthEndpoint(context.Request))
        {
            await _next(context);
            return;
        }

        var authConfig = _options.CurrentValue.Auth;
        var headerName = authConfig.Header;

        if (!context.Request.Headers.TryGetValue(headerName, out var provided) || string.IsNullOrWhiteSpace(provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("missing api key");
            return;
        }

        var value = provided.ToString().Trim();
        if (!authConfig.Keys.Contains(value))
        {
            _logger.LogWarning("Rejected request with invalid API key for {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("invalid api key");
            return;
        }

        await _next(context);
    }

    private static bool IsHealthEndpoint(HttpRequest request)
    {
        return request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);
    }
}
