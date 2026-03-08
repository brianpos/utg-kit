namespace UtgKit.Core;

/// <summary>
/// Configuration settings for the local THO repository clone.
/// Bound from the "ThoRepo" configuration section.
/// </summary>
public class ThoRepoSettings
{
    public const string SectionName = "ThoRepo";

    /// <summary>
    /// Path to the THO source-of-truth directory
    /// (e.g. the UTG repo's <c>input/sourceOfTruth</c> folder).
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
