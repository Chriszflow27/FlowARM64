namespace KeyDropGiveawayBot.Models;

public class JoinGiveawayResponse
{
    public int? IdGiveaway { get; set; }
    public string IdSteam { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SteamAvatar { get; set; } = string.Empty;
    public string ClientSeed { get; set; } = string.Empty;
    public int? Ticket { get; set; }
    public int? Slot { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
}
