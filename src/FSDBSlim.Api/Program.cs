using System;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using FSDBSlim.Compression;
using FSDBSlim.Configuration;
using FSDBSlim.Middleware;
using FSDBSlim.Services;
using FSDBSlim.Storage;
using FSDBSlim.Utils;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var configPath = ResolveConfigPath(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.Sources.Clear();
builder.Configuration.Add(new JsoncConfigurationSource
{
    Path = configPath,
    Optional = false,
    ReloadOnChange = true
});
builder.Configuration.AddEnvironmentVariables(prefix: "FSDBSLIM_");

builder.WebHost.UseUrls(builder.Configuration.GetValue<string>("Server:Urls") ?? "http://0.0.0.0:8080");

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

builder.Services.AddOptions<AppConfiguration>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<AppConfiguration>, AppConfigurationValidator>();

builder.Services.AddSingleton<ICompressionDecisionService, CompressionDecisionService>();
builder.Services.AddScoped<IFileStorageService, PostgresFileStorage>();
builder.Services.AddSingleton<IActiveHealthCheckService, ActiveHealthCheckService>();

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

await InitializeStorageAsync(app.Services);

app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<PathBaseEnforcementMiddleware>();
app.UseResponseCompression();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

var api = app.MapGroup("/v1/file");

api.MapPost("/{**path}", async (HttpContext context, string path, IFileStorageService storage, ICompressionDecisionService compression, IOptionsMonitor<AppConfiguration> options, CancellationToken cancellationToken) =>
{
    string normalized;
    try
    {
        normalized = FilePathHelper.Normalize(path);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var config = options.CurrentValue;

    var contentType = context.Request.ContentType?.Split(';', 2)[0].Trim();
    if (string.IsNullOrWhiteSpace(contentType))
    {
        return Results.Problem("Content-Type header is required", statusCode: StatusCodes.Status400BadRequest);
    }

    if (config.Storage.AllowedContentTypes.Count > 0 && !config.Storage.AllowedContentTypes.Any(ct => string.Equals(ct, contentType, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    var limitBytes = config.Server.MaxUploadMB * 1024L * 1024L;
    using var buffer = new MemoryStream();
    var bytesCopied = await CopyWithLimitAsync(context.Request.Body, buffer, limitBytes, cancellationToken);
    if (bytesCopied is null)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var result = compression.Process(buffer.ToArray(), contentType, cancellationToken);

    var stored = await storage.SaveAsync(new FileUploadRequest(
        normalized,
        contentType,
        bytesCopied.Value,
        result.Sha256Hex,
        result.Decision.Method,
        result.StoredBytes), cancellationToken);

    context.Response.Headers.ETag = $"\"{stored.Sha256Hex}\"";
    var location = $"{context.Request.PathBase}/v1/file/{normalized}";
    return Results.Created(location, new
    {
        path = normalized,
        version = stored.Version,
        size = stored.Size,
        sha256 = stored.Sha256Hex,
        compression = stored.Compression
    });
});

api.MapGet("/{**path}", async (HttpContext context, string path, IFileStorageService storage, ICompressionDecisionService compression, CancellationToken cancellationToken, int? version) =>
{
    string normalized;
    try
    {
        normalized = FilePathHelper.Normalize(path);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var stored = await storage.GetAsync(normalized, version, cancellationToken);
    if (stored is null)
    {
        return Results.NotFound();
    }

    var etag = $"\"{stored.Sha256Hex}\"";
    if (context.Request.Headers.TryGetValue("If-None-Match", out var existing))
    {
        var etagValues = existing.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (etagValues.Contains("*", StringComparer.Ordinal) || etagValues.Contains(etag, StringComparer.Ordinal))
        {
            context.Response.Headers.ETag = etag;
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
    }

    var payload = stored.Compression switch
    {
        null => stored.StoredBytes,
        var method => compression.Decompress(stored.StoredBytes, method)
    };

    var range = RangeHeaderParser.ParseSingleRange(context.Request.Headers["Range"], payload.LongLength);
    context.Response.Headers.ETag = etag;
    context.Response.Headers["Accept-Ranges"] = "bytes";
    context.Response.ContentType = stored.ContentType;

    if (range is null && context.Request.Headers.ContainsKey("Range"))
    {
        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.Headers["Content-Range"] = $"bytes */{payload.LongLength}";
        return Results.Empty;
    }

    if (range is { } validRange)
    {
        var (from, to) = validRange;
        var lengthLong = to - from + 1;
        var length = (int)lengthLong;
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{payload.LongLength}";
        context.Response.ContentLength = lengthLong;
        await context.Response.Body.WriteAsync(payload, (int)from, length, cancellationToken);
        return Results.Empty;
    }

    context.Response.ContentLength = payload.LongLength;
    await context.Response.Body.WriteAsync(payload.AsMemory(), cancellationToken);
    return Results.Empty;
});

api.MapGet("/versions/{**path}", async (string path, IFileStorageService storage, CancellationToken cancellationToken) =>
{
    string normalized;
    try
    {
        normalized = FilePathHelper.Normalize(path);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var versions = await storage.ListVersionsAsync(normalized, cancellationToken);
    if (versions.Count == 0)
    {
        return Results.NotFound();
    }

    return Results.Ok(versions.Select(v => new
    {
        version = v.Version,
        contentType = v.ContentType,
        size = v.Size,
        sha256 = v.Sha256Hex,
        compression = v.Compression,
        insertedAt = v.InsertedAt
    }));
});

app.MapGet("/healthz", async (IActiveHealthCheckService health, CancellationToken cancellationToken) =>
{
    var result = await health.ExecuteAsync(cancellationToken);
    var response = new
    {
        status = result.Status,
        checks = result.Checks,
        failed = result.Failed,
        errors = result.Errors,
        version = $"FSDBSlim {Assembly.GetExecutingAssembly().GetName().Version} (.NET 9)"
    };

    return result.Status == "OK"
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

static async Task InitializeStorageAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
    await storage.InitializeAsync(CancellationToken.None);
}

static async Task<long?> CopyWithLimitAsync(Stream source, MemoryStream destination, long limit, CancellationToken cancellationToken)
{
    var buffer = new byte[81920];
    long total = 0;
    int read;
    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
    {
        total += read;
        if (total > limit)
        {
            return null;
        }

        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }

    destination.Position = 0;
    return total;
}

static string ResolveConfigPath(string[] arguments)
{
    const string defaultPath = "/etc/fsdbslim/config.jsonc";
    var envPath = Environment.GetEnvironmentVariable("FSDBSLIM_CONFIG");
    if (!string.IsNullOrEmpty(envPath))
    {
        return envPath;
    }

    for (var i = 0; i < arguments.Length; i++)
    {
        if (arguments[i] == "--config" && i + 1 < arguments.Length)
        {
            return arguments[i + 1];
        }

        if (arguments[i].StartsWith("--config=", StringComparison.Ordinal))
        {
            return arguments[i][9..];
        }
    }

    return defaultPath;
}


public partial class Program;
