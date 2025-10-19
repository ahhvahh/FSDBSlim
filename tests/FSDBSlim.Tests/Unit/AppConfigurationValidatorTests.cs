namespace FSDBSlim.Tests.Unit;

using FSDBSlim.Configuration;
using FluentAssertions;

using Xunit;

public class AppConfigurationValidatorTests
{
    private readonly AppConfigurationValidator _validator = new();

    [Fact]
    public void Should_fail_when_upload_limit_invalid()
    {
        var config = new AppConfiguration();
        config.Auth.Keys.Add("key");
        config.Server.MaxUploadMB = 0;
        var result = _validator.Validate(null, config);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Should_fail_when_compression_method_invalid()
    {
        var config = new AppConfiguration();
        config.Auth.Keys.Add("key");
        config.Storage.Compression.Default.Method = "deflate";
        var result = _validator.Validate(null, config);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Should_succeed_for_valid_configuration()
    {
        var config = new AppConfiguration();
        config.Auth.Keys.Add("key");
        var result = _validator.Validate(null, config);
        result.Succeeded.Should().BeTrue();
    }
}
