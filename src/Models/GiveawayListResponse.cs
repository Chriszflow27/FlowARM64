using Newtonsoft.Json;

namespace KeyDropGiveawayBot.Models;

public class GiveawayListResponse
{
    [JsonProperty("status")]
    public bool Status { get; set; }

    [JsonProperty("data")]
    public List<Giveaway> Data { get; set; } = new();
}
