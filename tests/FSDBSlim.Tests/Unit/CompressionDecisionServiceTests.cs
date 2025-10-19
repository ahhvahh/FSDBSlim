namespace FSDBSlim.Tests.Unit;

using System;
using System.Security.Cryptography;
using System.Text;
using FSDBSlim.Compression;
using FSDBSlim.Configuration;
using FSDBSlim.Tests.TestSupport;
using FluentAssertions;
using Xunit;

public class CompressionDecisionServiceTests
{
    private static CompressionDecisionService CreateService(Action<AppConfiguration>? configure = null)
    {
        var configuration = new AppConfiguration();
        configuration.Storage.FileTypes.NoCompression.Add("image/png");
        configuration.Storage.AllowedContentTypes.Clear();
        configure?.Invoke(configuration);
        return new CompressionDecisionService(new TestOptionsMonitor<AppConfiguration>(configuration));
    }

    [Fact]
    public void Should_skip_compression_for_configured_content_type()
    {
        var service = CreateService();
        var decision = service.Decide("image/png");
        decision.ShouldCompress.Should().BeFalse();
        decision.Method.Should().BeNull();
    }

    [Fact]
    public void Should_use_default_method_when_enabled()
    {
        var service = CreateService(cfg =>
        {
            cfg.Storage.FileTypes.NoCompression.Clear();
            cfg.Storage.Compression.Default.Enabled = true;
            cfg.Storage.Compression.Default.Method = "brotli";
        });

        var decision = service.Decide("application/json");
        decision.ShouldCompress.Should().BeTrue();
        decision.Method.Should().Be("brotli");
    }

    [Fact]
    public void Should_respect_disabled_global_setting()
    {
        var service = CreateService(cfg => cfg.Storage.Compression.Default.Enabled = false);
        var decision = service.Decide("application/json");
        decision.ShouldCompress.Should().BeFalse();
    }

    [Fact]
    public void Process_should_compute_hash_of_original_payload()
    {
        var service = CreateService(cfg =>
        {
            cfg.Storage.FileTypes.NoCompression.Clear();
            cfg.Storage.Compression.Default.Enabled = true;
            cfg.Storage.Compression.Default.Method = "gzip";
        });

        var payload = Encoding.UTF8.GetBytes("hello world");
        var result = service.Process(payload, "text/plain");
        result.Sha256Hex.Should().Be(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        result.Decision.ShouldCompress.Should().BeTrue();
        result.StoredBytes.Should().NotBeNull();
    }
}
