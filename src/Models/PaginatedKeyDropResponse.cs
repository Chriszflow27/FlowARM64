using Newtonsoft.Json;
using System.Collections.Generic;

namespace KeyDropGiveawayBot.Models;

public class PaginatedKeyDropResponse<T>
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("data")]
    public List<T>? Data { get; set; }
}
