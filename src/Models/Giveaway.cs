using System.Collections.Generic;

namespace KeyDropGiveawayBot.Models;

public class Giveaway
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? MaxUsers { get; set; }
    public int? MinUsers { get; set; }
    public bool? HaveIJoined { get; set; }
    public int? MySlot { get; set; }
    public string PublicHash { get; set; } = string.Empty;
    public object? DeadlineTimestamp { get; set; }
    public string Frequency { get; set; } = string.Empty;
    public List<Prize> Prizes { get; set; } = new();
    public int? ParticipantCount { get; set; }
    public List<Winner> Winners { get; set; } = new();
}
