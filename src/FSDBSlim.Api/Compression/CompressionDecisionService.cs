namespace FSDBSlim.Compression;

using System.IO.Compression;
using System.Security.Cryptography;
using FSDBSlim.Configuration;
using Microsoft.Extensions.Options;

public interface ICompressionDecisionService
{
    CompressionDecision Decide(string contentType);
    CompressionResult Process(ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default);
    byte[] Decompress(ReadOnlyMemory<byte> data, string compressionMethod);
    CompressionLevel ResolveLevel();
}

public sealed record CompressionDecision(bool ShouldCompress, string? Method);

public sealed record CompressionResult(
    CompressionDecision Decision,
    byte[] StoredBytes,
    string Sha256Hex);

public sealed class CompressionDecisionService : ICompressionDecisionService
{
    private readonly IOptionsMonitor<AppConfiguration> _options;

    public CompressionDecisionService(IOptionsMonitor<AppConfiguration> options)
    {
        _options = options;
    }

    public CompressionDecision Decide(string contentType)
    {
        var config = _options.CurrentValue.Storage;
        var normalized = contentType.ToLowerInvariant();
        if (config.FileTypes.NoCompression.Any(ct => string.Equals(ct, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return new CompressionDecision(false, null);
        }

        if (!config.Compression.Default.Enabled)
        {
            return new CompressionDecision(false, null);
        }

        return new CompressionDecision(true, config.Compression.Default.Method.ToLowerInvariant());
    }

    public CompressionLevel ResolveLevel()
    {
        var level = _options.CurrentValue.Storage.Compression.Default.Level.ToLowerInvariant();
        return level switch
        {
            "fastest" => CompressionLevel.Fastest,
            _ => CompressionLevel.Optimal,
        };
    }

    public CompressionResult Process(ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = Decide(contentType);
        var originalBytes = content.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();

        if (!decision.ShouldCompress)
        {
            return new CompressionResult(decision, originalBytes, sha);
        }

        var storedBytes = Compress(decision.Method!, originalBytes, ResolveLevel());
        return new CompressionResult(decision, storedBytes, sha);
    }

    private static byte[] Compress(string method, byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();
        Stream compressionStream = method switch
        {
            "brotli" => new BrotliStream(output, level, leaveOpen: true),
            _ => new GZipStream(output, level, leaveOpen: true)
        };

        using (compressionStream)
        {
            compressionStream.Write(data);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlyMemory<byte> data, string compressionMethod)
    {
        using var input = new MemoryStream(data.ToArray());
        using var output = new MemoryStream();
        using Stream stream = compressionMethod switch
        {
            "brotli" => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true),
            "gzip" => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
            _ => throw new InvalidOperationException($"Unsupported compression method '{compressionMethod}'")
        };
        stream.CopyTo(output);
        return output.ToArray();
    }
}
