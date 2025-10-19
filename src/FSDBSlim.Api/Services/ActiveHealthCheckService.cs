namespace FSDBSlim.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FSDBSlim.Compression;
using FSDBSlim.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public interface IActiveHealthCheckService
{
    Task<HealthCheckResult> ExecuteAsync(CancellationToken cancellationToken);
}

public sealed record HealthCheckResult(string Status, IReadOnlyDictionary<string, string> Checks, IReadOnlyDictionary<string, string> Errors)
{
    public bool IsHealthy => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> Failed => Errors.Keys.ToList();
}

public sealed class ActiveHealthCheckService : IActiveHealthCheckService
{
    private readonly ILogger<ActiveHealthCheckService> _logger;
    private readonly IOptionsMonitor<AppConfiguration> _options;
    private readonly ICompressionDecisionService _compression;

    public ActiveHealthCheckService(ILogger<ActiveHealthCheckService> logger, IOptionsMonitor<AppConfiguration> options, ICompressionDecisionService compression)
    {
        _logger = logger;
        _options = options;
        _compression = compression;
    }

    public async Task<HealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await CheckDatabaseAsync(checks, errors, cancellationToken);
        CheckConfig(checks, errors);
        CheckCompression(checks, errors);
        CheckDiskFree(checks, errors);
        await CheckClockAsync(checks, errors, cancellationToken);

        var status = errors.Count == 0 && checks.All(kvp => kvp.Value == "ok") ? "OK" : "Degraded";
        return new HealthCheckResult(status, checks, errors);
    }

    private async Task CheckDatabaseAsync(Dictionary<string, string> checks, Dictionary<string, string> errors, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(BuildConnectionString());
            await connection.OpenAsync(cancellationToken);
            checks["database"] = "ok";

            var tablesSql = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = ANY(@tables)";
            await using (var command = new NpgsqlCommand(tablesSql, connection))
            {
                command.Parameters.AddWithValue("schema", _options.CurrentValue.Project.Schema);
                command.Parameters.AddWithValue("tables", new[] { "files", "file_versions" });
                var count = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
                if (count < 2)
                {
                    checks["schema"] = "failed";
                    errors["schema"] = "required tables are missing";
                }
                else
                {
                    checks["schema"] = "ok";
                }
            }

            var indexSql = $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = @schema AND indexname = ANY(@names)";
            await using (var indexCommand = new NpgsqlCommand(indexSql, connection))
            {
                indexCommand.Parameters.AddWithValue("schema", _options.CurrentValue.Project.Schema);
                indexCommand.Parameters.AddWithValue("names", new[] { "idx_files_path", "idx_fv_file_version" });
                var count = (long)(await indexCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
                if (count < 2)
                {
                    checks["indexes"] = "failed";
                    errors["indexes"] = "required indexes missing";
                }
                else
                {
                    checks["indexes"] = "ok";
                }
            }

            await using (var dataDirectoryCommand = new NpgsqlCommand("SELECT setting FROM pg_settings WHERE name = 'data_directory'", connection))
            {
                var directory = (string?)await dataDirectoryCommand.ExecuteScalarAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    EvaluateDisk(directory, checks, errors);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            checks["database"] = "failed";
            errors["database"] = ex.Message;
        }
    }

    private void CheckConfig(Dictionary<string, string> checks, Dictionary<string, string> errors)
    {
        var config = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.Auth.Header) || config.Auth.Keys.Count == 0)
        {
            checks["config"] = "failed";
            errors["config"] = "authentication configuration invalid";
            return;
        }

        if (config.Server.MaxUploadMB <= 0)
        {
            checks["config"] = "failed";
            errors["config"] = "MaxUploadMB must be greater than zero";
            return;
        }

        if (config.Storage.Compression.Default.Enabled)
        {
            var method = config.Storage.Compression.Default.Method.ToLowerInvariant();
            if (method is not ("gzip" or "brotli"))
            {
                checks["config"] = "failed";
                errors["config"] = "Invalid compression method";
                return;
            }
        }

        checks["config"] = "ok";
    }

    private void CheckCompression(Dictionary<string, string> checks, Dictionary<string, string> errors)
    {
        try
        {
            var sample = RandomNumberGenerator.GetBytes(1024);
            var result = _compression.Process(sample, "application/octet-stream");
            byte[] roundTrip;
            if (result.Decision.ShouldCompress && result.Decision.Method is not null)
            {
                roundTrip = _compression.Decompress(result.StoredBytes, result.Decision.Method);
            }
            else
            {
                roundTrip = result.StoredBytes;
            }

            if (!sample.SequenceEqual(roundTrip))
            {
                checks["compressionRoundTrip"] = "failed";
                errors["compressionRoundTrip"] = "round-trip mismatch";
                return;
            }

            checks["compressionRoundTrip"] = "ok";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compression round-trip failed");
            checks["compressionRoundTrip"] = "failed";
            errors["compressionRoundTrip"] = ex.Message;
        }
    }

    private async Task CheckClockAsync(Dictionary<string, string> checks, Dictionary<string, string> errors, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(BuildConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT now()", connection);
            var databaseNow = (DateTimeOffset)await command.ExecuteScalarAsync(cancellationToken);
            var delta = (databaseNow - DateTimeOffset.UtcNow).Duration();
            if (delta > TimeSpan.FromMinutes(2))
            {
                checks["clock"] = "failed";
                errors["clock"] = $"Clock skew {delta.TotalSeconds:F0}s";
            }
            else
            {
                checks["clock"] = "ok";
            }
        }
        catch (Exception ex)
        {
            checks["clock"] = "failed";
            errors["clock"] = ex.Message;
        }
    }

    private void CheckDiskFree(Dictionary<string, string> checks, Dictionary<string, string> errors)
    {
        if (checks.ContainsKey("diskFree"))
        {
            return;
        }

        var rootDrive = DriveInfo.GetDrives().FirstOrDefault();
        if (rootDrive is null)
        {
            checks["diskFree"] = "failed";
            errors["diskFree"] = "Unable to determine disk information";
            return;
        }

        var percentFree = rootDrive.TotalSize == 0 ? 1 : (double)rootDrive.AvailableFreeSpace / rootDrive.TotalSize;
        if (percentFree < 0.05)
        {
            checks["diskFree"] = "warning";
            errors["diskFree"] = $"free space below 5% ({percentFree:P2})";
        }
        else
        {
            checks["diskFree"] = "ok";
        }
    }

    private void EvaluateDisk(string directory, Dictionary<string, string> checks, Dictionary<string, string> errors)
    {
        try
        {
            var root = Path.GetPathRoot(directory) ?? Path.DirectorySeparatorChar.ToString();
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase));
            if (drive is null)
            {
                CheckDiskFree(checks, errors);
                return;
            }

            var percentFree = drive.TotalSize == 0 ? 1 : (double)drive.AvailableFreeSpace / drive.TotalSize;
            if (percentFree < 0.05)
            {
                checks["diskFree"] = "warning";
                errors["diskFree"] = $"free space below 5% ({percentFree:P2})";
            }
            else
            {
                checks["diskFree"] = "ok";
            }
        }
        catch (Exception ex)
        {
            checks["diskFree"] = "failed";
            errors["diskFree"] = ex.Message;
        }
    }

    private string BuildConnectionString()
    {
        var db = _options.CurrentValue.Database;
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = db.Host,
            Port = db.Port,
            Database = db.Database,
            Username = db.Username,
            Password = db.Password,
            Pooling = db.Pooling,
            MinPoolSize = db.MinPoolSize,
            MaxPoolSize = db.MaxPoolSize,
            SslMode = Enum.Parse<SslMode>(db.SslMode, ignoreCase: true)
        };

        if (!string.IsNullOrWhiteSpace(db.SslRootCert))
        {
            builder.RootCertificate = db.SslRootCert;
        }

        return builder.ToString();
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
