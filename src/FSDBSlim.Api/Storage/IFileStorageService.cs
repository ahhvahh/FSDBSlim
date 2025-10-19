namespace FSDBSlim.Storage;

public interface IFileStorageService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredFileVersion> SaveAsync(FileUploadRequest request, CancellationToken cancellationToken);
    Task<StoredFileVersion?> GetAsync(string normalizedPath, int? version, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileVersionMetadata>> ListVersionsAsync(string normalizedPath, CancellationToken cancellationToken);
}

public sealed record FileUploadRequest(
    string NormalizedPath,
    string ContentType,
    long OriginalLength,
    string Sha256Hex,
    string? Compression,
    byte[] StoredBytes);

public sealed record FileVersionMetadata(
    long Version,
    string ContentType,
    long Size,
    string Sha256Hex,
    string? Compression,
    DateTimeOffset InsertedAt);

public sealed record StoredFileVersion(
    string Path,
    long Version,
    string ContentType,
    long Size,
    string Sha256Hex,
    string? Compression,
    byte[] StoredBytes,
    DateTimeOffset InsertedAt);
