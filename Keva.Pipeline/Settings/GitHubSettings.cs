namespace Respire.Pipeline.Settings;

public record GitHubSettings
{
    public string? Token { get; init; }
    public string Repository { get; init; } = "Respire";
    public string? Owner { get; init; }
}