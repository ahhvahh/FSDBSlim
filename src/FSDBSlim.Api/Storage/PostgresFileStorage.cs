namespace FSDBSlim.Storage;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FSDBSlim.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class PostgresFileStorage : IFileStorageService
{
    private readonly ILogger<PostgresFileStorage> _logger;
    private readonly IOptionsMonitor<AppConfiguration> _options;

    public PostgresFileStorage(ILogger<PostgresFileStorage> logger, IOptionsMonitor<AppConfiguration> options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var schema = GetSchemaName();
        var builder = new StringBuilder();
        builder.AppendLine($"CREATE SCHEMA IF NOT EXISTS {schema};");
        builder.AppendLine($"CREATE TABLE IF NOT EXISTS {schema}.files (\n  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,\n  path text NOT NULL UNIQUE,\n  latest_version int NOT NULL DEFAULT 0,\n  created_at timestamptz NOT NULL DEFAULT now()\n);");
        builder.AppendLine($"CREATE TABLE IF NOT EXISTS {schema}.file_versions (\n  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,\n  file_id BIGINT NOT NULL REFERENCES {schema}.files(id) ON DELETE CASCADE,\n  version_number int NOT NULL,\n  content_type text NOT NULL,\n  file_size bigint NOT NULL,\n  sha256_hex text NOT NULL,\n  compression text,\n  is_compressed boolean GENERATED ALWAYS AS (compression IS NOT NULL) STORED,\n  data bytea NOT NULL,\n  inserted_at timestamptz NOT NULL DEFAULT now(),\n  UNIQUE (file_id, version_number)\n);");
        builder.AppendLine($"CREATE INDEX IF NOT EXISTS idx_files_path ON {schema}.files(path);");
        builder.AppendLine($"CREATE INDEX IF NOT EXISTS idx_fv_file_version ON {schema}.file_versions(file_id, version_number DESC);");

        await using (var command = new NpgsqlCommand(builder.ToString(), connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<StoredFileVersion> SaveAsync(FileUploadRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var schema = GetSchemaName();
        var fileId = await GetOrCreateFileIdAsync(connection, transaction, schema, request.NormalizedPath, cancellationToken);
        var version = await GetLatestVersionAsync(connection, transaction, schema, fileId, cancellationToken) + 1;

        var insertCommandText = $"INSERT INTO {schema}.file_versions (file_id, version_number, content_type, file_size, sha256_hex, compression, data)\nVALUES (@file_id, @version_number, @content_type, @file_size, @sha256_hex, @compression, @data)\nRETURNING inserted_at";

        await using (var insertCommand = new NpgsqlCommand(insertCommandText, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("file_id", fileId);
            insertCommand.Parameters.AddWithValue("version_number", version);
            insertCommand.Parameters.AddWithValue("content_type", request.ContentType);
            insertCommand.Parameters.AddWithValue("file_size", request.OriginalLength);
            insertCommand.Parameters.AddWithValue("sha256_hex", request.Sha256Hex);
            if (request.Compression is null)
            {
                insertCommand.Parameters.AddWithValue("compression", DBNull.Value);
            }
            else
            {
                insertCommand.Parameters.AddWithValue("compression", request.Compression);
            }

            insertCommand.Parameters.AddWithValue("data", request.StoredBytes);
            var insertedAtRaw = (DateTime)await insertCommand.ExecuteScalarAsync(cancellationToken);
            var insertedAt = new DateTimeOffset(DateTime.SpecifyKind(insertedAtRaw, DateTimeKind.Utc));

            var updateCommandText = $"UPDATE {schema}.files SET latest_version = @latest WHERE id = @id";
            await using var updateCommand = new NpgsqlCommand(updateCommandText, connection, transaction);
            updateCommand.Parameters.AddWithValue("latest", version);
            updateCommand.Parameters.AddWithValue("id", fileId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new StoredFileVersion(
                request.NormalizedPath,
                version,
                request.ContentType,
                request.OriginalLength,
                request.Sha256Hex,
                request.Compression,
                request.StoredBytes,
                insertedAt);
        }
    }

    public async Task<StoredFileVersion?> GetAsync(string normalizedPath, int? version, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var schema = GetSchemaName();
        string sql;
        if (version.HasValue)
        {
            sql = $"SELECT f.path, fv.version_number, fv.content_type, fv.file_size, fv.sha256_hex, fv.compression, fv.data, fv.inserted_at\nFROM {schema}.file_versions fv\nJOIN {schema}.files f ON f.id = fv.file_id\nWHERE f.path = @path AND fv.version_number = @version\nLIMIT 1";
        }
        else
        {
            sql = $"SELECT f.path, fv.version_number, fv.content_type, fv.file_size, fv.sha256_hex, fv.compression, fv.data, fv.inserted_at\nFROM {schema}.file_versions fv\nJOIN {schema}.files f ON f.id = fv.file_id\nWHERE f.path = @path\nORDER BY fv.version_number DESC\nLIMIT 1";
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("path", normalizedPath);
        if (version.HasValue)
        {
            command.Parameters.AddWithValue("version", version.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var insertedAt = reader.GetFieldValue<DateTime>(7);
        var insertedOffset = new DateTimeOffset(DateTime.SpecifyKind(insertedAt, DateTimeKind.Utc));
        return new StoredFileVersion(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            (byte[])reader[6],
            insertedOffset);
    }

    public async Task<IReadOnlyList<FileVersionMetadata>> ListVersionsAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var schema = GetSchemaName();
        var sql = $"SELECT fv.version_number, fv.content_type, fv.file_size, fv.sha256_hex, fv.compression, fv.inserted_at\nFROM {schema}.file_versions fv\nJOIN {schema}.files f ON f.id = fv.file_id\nWHERE f.path = @path\nORDER BY fv.version_number DESC";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("path", normalizedPath);

        var results = new List<FileVersionMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var insertedAt = reader.GetFieldValue<DateTime>(5);
            var insertedOffset = new DateTimeOffset(DateTime.SpecifyKind(insertedAt, DateTimeKind.Utc));
            results.Add(new FileVersionMetadata(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                insertedOffset));
        }

        return results;
    }

    private async Task<long> GetOrCreateFileIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string path, CancellationToken cancellationToken)
    {
        var selectSql = $"SELECT id FROM {schema}.files WHERE path = @path FOR UPDATE";
        await using var selectCommand = new NpgsqlCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("path", path);
        var existing = await selectCommand.ExecuteScalarAsync(cancellationToken);
        if (existing is long id)
        {
            return id;
        }

        var insertSql = $"INSERT INTO {schema}.files (path) VALUES (@path) RETURNING id";
        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("path", path);
        var inserted = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return (long)inserted!;
    }

    private async Task<int> GetLatestVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, long fileId, CancellationToken cancellationToken)
    {
        var sql = $"SELECT latest_version FROM {schema}.files WHERE id = @id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", fileId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int version ? version : 0;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
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

    private string GetSchemaName()
    {
        return QuoteIdentifier(_options.CurrentValue.Project.Schema);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}
