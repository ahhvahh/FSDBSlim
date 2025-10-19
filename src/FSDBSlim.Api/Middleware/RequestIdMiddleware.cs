namespace FSDBSlim.Middleware;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

public sealed class RequestIdMiddleware
{
    private readonly RequestDelegate _next;
    private const string HeaderName = "X-Request-Id";

    public RequestIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            var requestId = Guid.NewGuid().ToString("N");
            context.Request.Headers[HeaderName] = requestId;
            context.Response.Headers[HeaderName] = requestId;
        }
        else
        {
            context.Response.Headers[HeaderName] = existing.ToString();
        }

        await _next(context);
    }
}
