namespace FSDBSlim.Configuration;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

public sealed class AppConfiguration
{
    public ProjectConfiguration Project { get; set; } = new();
    public ServerConfiguration Server { get; set; } = new();
    public DatabaseConfiguration Database { get; set; } = new();
    public AuthConfiguration Auth { get; set; } = new();
    public StorageConfiguration Storage { get; set; } = new();

    public sealed class ProjectConfiguration
    {
        [Required]
        [MinLength(1)]
        public string Name { get; set; } = "FSDBSlim";

        [Required]
        [MinLength(1)]
        public string Schema { get; set; } = "storage";
    }

    public sealed class ServerConfiguration
    {
        public string PathBase { get; set; } = "/fsdbslim";

        [Required]
        [MinLength(1)]
        public string Urls { get; set; } = "http://0.0.0.0:8080";

        [Range(1, int.MaxValue)]
        public int MaxUploadMB { get; set; } = 50;
    }

    public sealed class DatabaseConfiguration
    {
        [Required]
        [MinLength(1)]
        public string Host { get; set; } = "127.0.0.1";

        [Range(1, 65535)]
        public int Port { get; set; } = 5432;

        [Required]
        [MinLength(1)]
        public string Database { get; set; } = "fsdbslim";

        [Required]
        [MinLength(1)]
        public string Username { get; set; } = "fsuser";

        [Required]
        [MinLength(1)]
        public string Password { get; set; } = "StrongPass123";

        public bool Pooling { get; set; } = true;
        public int MinPoolSize { get; set; } = 0;
        public int MaxPoolSize { get; set; } = 20;

        [Required]
        [RegularExpression("^(Disable|Require|VerifyCA|VerifyFull)$", ErrorMessage = "Invalid SSL mode")]
        public string SslMode { get; set; } = "Disable";

        public string? SslRootCert { get; set; } = string.Empty;
    }

    public sealed class AuthConfiguration
    {
        [Required]
        [MinLength(1)]
        public string Header { get; set; } = "X-Api-Key";

        public List<string> Keys { get; set; } = new();
    }

    public sealed class StorageConfiguration
    {
        public CompressionConfiguration Compression { get; set; } = new();
        public FileTypeConfiguration FileTypes { get; set; } = new();
        public List<string> AllowedContentTypes { get; set; } = new();

        public sealed class CompressionConfiguration
        {
            public CompressionDefaultConfiguration Default { get; set; } = new();

            public sealed class CompressionDefaultConfiguration
            {
                public bool Enabled { get; set; } = true;
                public string Method { get; set; } = "gzip";
                public string Level { get; set; } = "Optimal";
            }
        }

        public sealed class FileTypeConfiguration
        {
            public List<string> NoCompression { get; set; } = new();
        }
    }
}
