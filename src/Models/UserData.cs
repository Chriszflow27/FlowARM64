namespace KeyDropGiveawayBot.Models;

public class UserData
{
    public string IdSteam { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SteamAvatar { get; set; } = string.Empty;
    public int? Ticket { get; set; }
    public int? Slot { get; set; }
    public string ClientSeed { get; set; } = string.Empty;
}
