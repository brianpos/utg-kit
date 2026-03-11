namespace UtgKit.Core.Services;

/// <summary>
/// Reports progress during file indexing.
/// </summary>
public sealed record IndexingProgress(int TotalFiles, int ProcessedFiles)
{
    public double Percentage => TotalFiles == 0 ? 0 : (double)ProcessedFiles / TotalFiles * 100;
}
