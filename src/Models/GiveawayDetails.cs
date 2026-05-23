using System.Collections.Generic;

namespace KeyDropGiveawayBot.Models;

public class GiveawayDetails
{
    public string Id { get; set; } = string.Empty;
    public string? MySteamId { get; set; }
    public int? MaxUsers { get; set; }
    public int? MinUsers { get; set; }
    public int? DepositAmountRequired { get; set; }
    public double? DepositAmountMissing { get; set; }
    public string PublicHash { get; set; } = string.Empty;
    public long? DeadlineTimestamp { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<Prize> Prizes { get; set; } = new();
    public bool? CanIJoin { get; set; }
    public object? BlockedUntil { get; set; }
    public bool? HaveIJoined { get; set; }
    public int? MySlot { get; set; }
    public List<UserData> Participants { get; set; } = new();
    public int? ParticipantCount { get; set; }
    public string Frequency { get; set; } = string.Empty;
    public List<Winner> Winners { get; set; } = new();

    // Campo adicional para mostrar el título
    public string Title { get; set; } = string.Empty;

    // 🔑 Campos derivados para logs y confirmación
    public double? PrizePrice { get; set; }          // Precio del primer premio
    public string WeaponName { get; set; } = string.Empty; // Nombre del arma (title del primer prize)
    public string TournamentType { get; set; } = string.Empty; // Tipo de torneo (frequency)
    public bool? Joined { get; set; }                // Estado de unión (haveIJoined)
}
