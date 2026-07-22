namespace AIToolbox.Web.Services;

public interface IFileStorageService
{
    long MaxUploadBytes { get; }
    Task<StoredFileResult> SavePdfAsync(IFormFile file, string toolKey, CancellationToken cancellationToken = default);
}

public sealed record StoredFileResult(
    string OriginalFileName,
    string StoredFilePath,
    long FileSizeBytes,
    byte[] Bytes);
