namespace HwidBots.Shared.Models;

public record ProductVersion(
    long Id,
    string Version,
    string FileId,
    string FileName,
    long FileSize,
    string? UpdateLog,
    long UploadedBy,
    DateTime CreatedAt,
    bool IsLatest);
