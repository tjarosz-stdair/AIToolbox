using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Services;

public sealed class FileStorageService : IFileStorageService
{
    private static readonly byte[] PdfHeader = "%PDF"u8.ToArray();
    private readonly StorageOptions _options;

    public FileStorageService(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public long MaxUploadBytes => Math.Max(1, _options.MaxUploadBytes);

    public async Task<StoredFileResult> SavePdfAsync(IFormFile file, string toolKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BasePath))
        {
            throw new InvalidOperationException("Storage:BasePath must be configured before PDFs can be uploaded.");
        }

        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Upload an Adobe PDF file.");
        }

        if (file.Length > MaxUploadBytes)
        {
            throw new InvalidOperationException($"The PDF is too large. Maximum size is {MaxUploadBytes:N0} bytes.");
        }

        var originalName = Path.GetFileName(file.FileName);
        if (!originalName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only PDF uploads are supported.");
        }

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        if (bytes.Length < PdfHeader.Length || !bytes.AsSpan(0, PdfHeader.Length).SequenceEqual(PdfHeader))
        {
            throw new InvalidOperationException("The uploaded file does not look like a valid PDF.");
        }

        var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.BasePath.Trim()));
        var configuredSubpath = _options.ToolSubpaths.TryGetValue(toolKey, out var subpath)
            ? subpath
            : toolKey;
        var targetDirectory = Path.GetFullPath(Path.Combine(
            basePath,
            configuredSubpath,
            DateTime.UtcNow.ToString("yyyy"),
            DateTime.UtcNow.ToString("MM")));

        if (!targetDirectory.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The configured storage subpath resolves outside the project storage root.");
        }

        Directory.CreateDirectory(targetDirectory);
        var storedPath = Path.Combine(targetDirectory, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(storedPath, bytes, cancellationToken);

        return new StoredFileResult(originalName, storedPath, bytes.Length, bytes);
    }
}
