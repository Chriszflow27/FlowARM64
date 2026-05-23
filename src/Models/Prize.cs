namespace KeyDropGiveawayBot.Models;

public class Prize
{
    public int? Id { get; set; }
    public string Color { get; set; } = string.Empty;
    public string ItemImg { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public double? Price { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string WeaponType { get; set; } = string.Empty;
}
