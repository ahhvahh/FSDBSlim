namespace FSDBSlim.Middleware;

using System;
using System.Threading.Tasks;
using FSDBSlim.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

public sealed class PathBaseEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AppConfiguration> _options;

    public PathBaseEnforcementMiddleware(RequestDelegate next, IOptionsMonitor<AppConfiguration> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var basePath = _options.CurrentValue.Server.PathBase;
        if (string.IsNullOrEmpty(basePath) || basePath == "/")
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments(basePath, StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            var originalPath = context.Request.Path;
            var originalBase = context.Request.PathBase;
            context.Request.PathBase = basePath;
            context.Request.Path = remaining;
            try
            {
                await _next(context);
            }
            finally
            {
                context.Request.PathBase = originalBase;
                context.Request.Path = originalPath;
            }

            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
