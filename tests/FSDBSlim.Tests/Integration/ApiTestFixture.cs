namespace FSDBSlim.Tests.Integration;

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using FSDBSlim.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Xunit;

public class ApiTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlTestcontainer _postgres;
    private readonly string _configPath;
    private readonly string _apiKey = "test-key";
    private Respawner? _respawner;

    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string ConnectionString => _postgres.ConnectionString;

    public ApiTestFixture()
    {
        _postgres = new TestcontainersBuilder<PostgreSqlTestcontainer>()
            .WithDatabase(new PostgreSqlTestcontainerConfiguration
            {
                Database = "fsdbslim",
                Username = "fsuser",
                Password = "StrongPass123"
            })
            .WithImage("postgres:16-alpine")
            .Build();
        _configPath = Path.Combine(Path.GetTempPath(), $"fsdbslim-{Guid.NewGuid():N}.jsonc");
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        WriteConfiguration();
        Environment.SetEnvironmentVariable("FSDBSLIM_CONFIG", _configPath);

        Factory = new ApiFactory();
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await EnsureDatabaseReady();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        Environment.SetEnvironmentVariable("FSDBSLIM_CONFIG", null);
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        if (_respawner is null)
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
            await connection.OpenAsync();
            _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = new[] { "storage" }
            });
        }

        await using var resetConnection = new NpgsqlConnection(_postgres.ConnectionString);
        await resetConnection.OpenAsync();
        if (_respawner is not null)
        {
            await _respawner.ResetAsync(resetConnection);
        }
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", _apiKey);
        return request;
    }

    private void WriteConfiguration()
    {
        var builder = new
        {
            Project = new { Name = "FSDBSlim", Schema = "storage" },
            Server = new { PathBase = "/fsdbslim", Urls = "http://127.0.0.1:0", MaxUploadMB = 50 },
            Database = new
            {
                Host = _postgres.Hostname,
                Port = _postgres.Port,
                Database = _postgres.Database,
                Username = _postgres.Username,
                Password = _postgres.Password,
                Pooling = false,
                MinPoolSize = 0,
                MaxPoolSize = 10,
                SslMode = "Disable",
                SslRootCert = ""
            },
            Auth = new { Header = "X-Api-Key", Keys = new[] { _apiKey } },
            Storage = new
            {
                Compression = new
                {
                    Default = new { Enabled = true, Method = "gzip", Level = "Optimal" }
                },
                FileTypes = new
                {
                    NoCompression = new[] { "image/png" }
                },
                AllowedContentTypes = Array.Empty<string>()
            }
        };

        var json = JsonSerializer.Serialize(builder, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private async Task EnsureDatabaseReady()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        await storage.InitializeAsync(CancellationToken.None);
    }
}
