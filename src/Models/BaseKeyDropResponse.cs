namespace KeyDropGiveawayBot.Models;

public class BaseKeyDropResponse<T>
{
    public bool Success { get; set; }
    public T Data { get; set; } = default!;

    public string? Message { get; set; }
}
