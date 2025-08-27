namespace Keva.Pipeline.Settings;

public record GitHubSettings
{
    public string? Token { get; init; }
    public string Repository { get; init; } = "Keva";
    public string? Owner { get; init; }
}