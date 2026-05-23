using System.Threading.Tasks;

namespace KeyDropGiveawayBot.Utils
{
    public interface IApiClient
    {
        Task<T?> GetAsync<T>(string url) where T : class;

        Task<TOut?> PostAsync<TIn, TOut>(string url, TIn payload)
            where TIn : class
            where TOut : class;

        Task<TOut?> PutAsync<TIn, TOut>(string url, TIn? payload)
            where TIn : class
            where TOut : class;

        Task<string?> JoinGiveawayAsync(string giveawayId);
    }
}
