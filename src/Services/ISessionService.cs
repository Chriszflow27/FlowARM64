using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace KeyDropGiveawayBot.Services
{
    public interface ISessionService
    {
        IPage? ActivePage { get; }  // <- ahora sí reconoce IPage
        Task SetKeyDropCookieAsync(CancellationToken cancellationToken = default, bool isRefresh = false);
    }
}
