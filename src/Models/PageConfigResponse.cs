using Newtonsoft.Json;
using System.Collections.Generic;

namespace KeyDropGiveawayBot.Models;

public class PageConfigResponse
{
    [JsonProperty("status")]
    public bool Status { get; set; }

    [JsonProperty("data")]
    public PageConfigData Data { get; set; } = new();
}

public class PageConfigData
{
    [JsonProperty("__giveaways")]
    public GiveawaysConfig GiveawaysConfig { get; set; } = new();

    [JsonProperty("__meta")]
    public MetaData Meta { get; set; } = new();
}

public class GiveawaysConfig
{
    [JsonProperty("userMinPlayers")]
    public int UserMinPlayers { get; set; }

    [JsonProperty("userCanJoin")]
    public bool UserCanJoin { get; set; }

    [JsonProperty("userCanCreateGiveaway")]
    public bool UserCanCreateGiveaway { get; set; }

    [JsonProperty("basename")]
    public string Basename { get; set; } = string.Empty;

    // 👇 Aquí agregamos la lista de sorteos
    [JsonProperty("giveaways")]
    public List<Giveaway> Giveaways { get; set; } = new();
}

public class MetaData
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;
}
