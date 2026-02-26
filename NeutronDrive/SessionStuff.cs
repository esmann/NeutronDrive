namespace NeutronDrive;

public record SessionStuff()
{
    public string Id { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string? Username { get; init; }
    public string? UserEmailAddress { get; init; }
};