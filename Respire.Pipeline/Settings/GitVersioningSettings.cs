namespace Respire.Pipeline.Settings;

public record GitVersioningSettings
{
    public string BaseVersion { get; init; } = "0.1.0";
    public string[] ReleaseBranches { get; init; } = new[] { "main", "master" };
}
