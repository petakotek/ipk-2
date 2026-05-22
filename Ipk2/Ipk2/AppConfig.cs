namespace Ipk2;

public record AppConfig
{
    public int     Port       { get; init; }
    public string? Address    { get; init; }
    public string? Input      { get; init; }
    public string? Output     { get; init; }
    public int     TimeoutSec { get; init; }
};