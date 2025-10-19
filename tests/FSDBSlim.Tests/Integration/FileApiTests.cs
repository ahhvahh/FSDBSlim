namespace FSDBSlim.Tests.Integration;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

[Collection("fsdbslim-api")]
public class FileApiTests
{
    private readonly ApiTestFixture _fixture;
    public FileApiTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_should_create_expected_schema()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'storage'", connection);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task Uploading_same_path_concurrently_creates_incremental_versions()
    {
        await _fixture.ResetAsync();
        var path = "/fsdbslim/v1/file/documents/report.txt";
        var payload = Encoding.UTF8.GetBytes("versioned content");

        var request1 = _fixture.CreateRequest(HttpMethod.Post, path);
        request1.Content = new ByteArrayContent(payload)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") }
        };

        var request2 = _fixture.CreateRequest(HttpMethod.Post, path);
        request2.Content = new ByteArrayContent(payload)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") }
        };

        var responses = await Task.WhenAll(_fixture.Client.SendAsync(request1), _fixture.Client.SendAsync(request2));
        request1.Dispose();
        request2.Dispose();
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);

        var versions = new List<int>();
        foreach (var response in responses)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            versions.Add(json.GetProperty("version").GetInt32());
            response.Dispose();
        }

        versions.Should().Contain(new[] { 1, 2 });
        versions.Should().OnlyHaveUniqueItems();

        var listRequest = _fixture.CreateRequest(HttpMethod.Get, "/fsdbslim/v1/file/versions/documents/report.txt");
        var listResponse = await _fixture.Client.SendAsync(listRequest);
        listRequest.Dispose();
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        listJson.GetArrayLength().Should().Be(2);
        listResponse.Dispose();
        listJson[0].GetProperty("version").GetInt32().Should().Be(2);
        listJson[1].GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Download_should_return_latest_and_specific_versions_with_range_support()
    {
        await _fixture.ResetAsync();
        var path = "/fsdbslim/v1/file/assets/image.bin";
        var payload = Encoding.UTF8.GetBytes("1234567890");

        var uploadRequest = _fixture.CreateRequest(HttpMethod.Post, path);
        uploadRequest.Content = new ByteArrayContent(payload)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") }
        };
        var uploadResponse = await _fixture.Client.SendAsync(uploadRequest);
        uploadRequest.Dispose();
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        uploadResponse.Dispose();

        var downloadRequest = _fixture.CreateRequest(HttpMethod.Get, path);
        var downloadResponse = await _fixture.Client.SendAsync(downloadRequest);
        downloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        downloadRequest.Dispose();
        downloadResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        downloadResponse.Headers.ETag.Should().NotBeNull();
        var body = await downloadResponse.Content.ReadAsByteArrayAsync();
        body.Should().Equal(payload);
        downloadResponse.Dispose();

        var rangeRequest = _fixture.CreateRequest(HttpMethod.Get, path);
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        var rangeResponse = await _fixture.Client.SendAsync(rangeRequest);
        rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        rangeRequest.Dispose();
        rangeResponse.Content.Headers.ContentLength.Should().Be(4);
        rangeResponse.Content.Headers.ContentRange!.ToString().Should().Be("bytes 0-3/10");
        (await rangeResponse.Content.ReadAsByteArrayAsync()).Should().Equal(payload[..4]);
        rangeResponse.Dispose();

        var invalidRangeRequest = _fixture.CreateRequest(HttpMethod.Get, path);
        invalidRangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 200);
        var invalidRangeResponse = await _fixture.Client.SendAsync(invalidRangeRequest);
        invalidRangeResponse.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        invalidRangeRequest.Dispose();
        invalidRangeResponse.Headers.GetValues("Content-Range").Should().Contain("bytes */10");
        invalidRangeResponse.Dispose();

        var versionRequest = _fixture.CreateRequest(HttpMethod.Get, path + "?version=1");
        var versionResponse = await _fixture.Client.SendAsync(versionRequest);
        versionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        versionRequest.Dispose();
        (await versionResponse.Content.ReadAsByteArrayAsync()).Should().Equal(payload);
        versionResponse.Dispose();
    }

    [Fact]
    public async Task Health_endpoint_should_report_ok()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/fsdbslim/healthz");
        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("OK");
        response.Dispose();
        request.Dispose();
    }
}
