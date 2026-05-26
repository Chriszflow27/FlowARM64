using KeyDropGiveawayBot.Models;
using KeyDropGiveawayBot.Utils;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace KeyDropGiveawayBot.Services;

public class KeyDropService : IKeyDropService
{
    private readonly IApiClient _apiClient;
    private readonly IConfiguration _configuration;
    private readonly ISessionService _sessionService;

    public KeyDropService(IApiClient apiClient, IConfiguration configuration, ISessionService sessionService)
    {
        _apiClient = apiClient;
        _configuration = configuration;
        _sessionService = sessionService;
    }

    public Task<PageConfigResponse?> GetPageConfigAsync()
    {
        return Task.FromResult<PageConfigResponse?>(null);
    }

    public async Task<List<Giveaway>?> GetGiveawaysAsync()
    {
        try
        {
            var page = _sessionService.ActivePage;
            if (page == null) throw new Exception("Página no activa.");

            if (!page.Url.Contains("/giveaways/list"))
            {
                await page.GotoAsync("https://key-drop.com/es/giveaways/list",
                    new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });
            }

            Log.Information("⏳ Esperando a que el servidor de KeyDrop dibuje los sorteos en la pantalla...");
            await page.EvaluateAsync("window.scrollBy(0, 800)");

            try
            {
                await page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('a[href*=\"/giveaways/keydrop/\"]').length >= 4",
                    new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 15000 });
            }
            catch
            {
                Log.Warning("⚠️ Los sorteos tardan mucho en cargar o no hay suficientes activos.");
            }

            Log.Information("🕵️‍♂️ Escaneando los enlaces renderizados...");

            string js = @"
                () => {
                    const links = Array.from(document.querySelectorAll('a[href*=""/giveaways/keydrop/""]'));
                    const ids = [];
                    links.forEach(a => {
                        const parts = a.href.split('/');
                        const id = parts[parts.length - 1];
                        if (id && id.length > 5 && !ids.some(g => g.Id === id)) {
                            ids.push({ Id: id, Title: 'Sorteo ' + id });
                        }
                    });
                    return JSON.stringify(ids);
                }";

            var rawJson = await page.EvaluateAsync<string>(js);

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                Log.Warning("GetGiveawaysAsync: No hay tarjetas en pantalla.");
                return null;
            }

            var giveaways = JsonConvert.DeserializeObject<List<Giveaway>>(rawJson);

            if (giveaways == null || giveaways.Count == 0)
            {
                Log.Warning("GetGiveawaysAsync: No se encontraron sorteos.");
                return null;
            }

            var officialGiveaways = giveaways.Take(4).ToList();

            Log.Information("✅ Se encontraron {Count} sorteos oficiales de KeyDrop.", officialGiveaways.Count);
            foreach (var g in officialGiveaways)
                Log.Information("Sorteo oficial detectado: {Id}", g.Id);

            return officialGiveaways;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error al extraer sorteos del DOM.");
            return null;
        }
    }

    public async Task<GiveawayDetails?> GetGiveawayDetailsByIdAsync(string giveawayId)
    {
        try
        {
            var host = _configuration["GiveawayJoinHost"];
            if (string.IsNullOrEmpty(host))
            {
                host = _configuration["GiveawayListHost"];
                if (string.IsNullOrEmpty(host)) return null;
            }

            string url = $"{host}/v1/giveaway/data/{giveawayId}";
            var page = _sessionService.ActivePage;
            if (page == null) throw new Exception("Página no activa.");

            string script = $@"
                async () => {{
                    const res = await fetch('{url}', {{
                        method: 'GET',
                        credentials: 'include'
                    }});
                    return await res.text();
                }}";

            var rawJson = await page.EvaluateAsync<string>(script);
            var response = JsonConvert.DeserializeObject<BaseKeyDropResponse<GiveawayDetails>>(rawJson);
            if (response == null || !response.Success) return null;

            var details = response.Data;
            if (details.Prizes != null && details.Prizes.Count > 0)
            {
                details.PrizePrice = details.Prizes[0].Price;
                details.WeaponName = details.Prizes[0].Title;
            }
            details.TournamentType = details.Frequency;
            details.Joined = details.HaveIJoined;

            Log.Information("🎁 Sorteo {Id} → Arma: {WeaponName} | Premio: {PrizePrice} USD | Tipo: {TournamentType} | Joined: {Joined}",
                details.Id, details.WeaponName, details.PrizePrice, details.TournamentType, details.Joined);

            return details;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error crítico al obtener detalles del sorteo.");
            return null;
        }
    }

    public async Task<JoinGiveawayResponse?> JoinGiveawayAsync(string giveawayId)
    {
        try
        {
            var page = _sessionService.ActivePage;
            if (page == null) throw new Exception("Página no activa.");

            Log.Information("🚀 Navegando al sorteo {GiveawayId}...", giveawayId);
            await page.GotoAsync($"https://key-drop.com/es/giveaways/keydrop/{giveawayId}",
                new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });

            await page.EvaluateAsync("window.scrollBy(0, 500)");

            var buttonSelector = "[data-testid='btn-giveaway-join-the-giveaway']";

            try
            {
                Log.Information("⏳ Esperando que el botón aparezca en el DOM...");
                await page.WaitForSelectorAsync(buttonSelector, new Microsoft.Playwright.PageWaitForSelectorOptions
                {
                    State = Microsoft.Playwright.WaitForSelectorState.Attached,
                    Timeout = 15000
                });
            }
            catch
            {
                Log.Warning("⚠️ El botón con testid tardó demasiado. Intentando buscar por texto...");
            }

            var joinButton = await page.QuerySelectorAsync(buttonSelector);
            if (joinButton == null)
            {
                joinButton = await page.QuerySelectorAsync("button:has-text('Únete'), button:has-text('Join'), div:has-text('Únete')");
            }

            if (joinButton != null)
            {
                await page.EvaluateAsync("el => el.click()", joinButton);
                Log.Information("🖱️ Click físico ejecutado en {GiveawayId}.", giveawayId);

                // --- VALIDACIÓN 1: TOAST ---
                try
                {
                    Log.Information("⏳ Buscando notificación emergente verde de éxito...");
                    var toastSelector = "[data-testid='toast-message-title']";
                    var toast = await page.WaitForSelectorAsync(toastSelector, new Microsoft.Playwright.PageWaitForSelectorOptions
                    {
                        State = Microsoft.Playwright.WaitForSelectorState.Visible,
                        Timeout = 5000
                    });

                    if (toast != null)
                    {
                        var toastText = await toast.InnerTextAsync();
                        if (toastText.Contains("Te has unido") || toastText.Contains("Joined") || toastText.Contains("success"))
                        {
                            Log.Information("✅ ¡Confirmado por Toast (ventana emergente)! Te uniste al sorteo {GiveawayId}", giveawayId);
                            return new JoinGiveawayResponse { Success = true };
                        }
                    }
                }
                catch
                {
                    Log.Information("⚠️ La notificación emergente no apareció. Verificando contador de entradas...");
                }

                // --- VALIDACIÓN 2: CONTADOR DE ENTRADAS (fix: span en lugar de p) ---
                await Task.Delay(1500);

                // El HTML real usa <span><strong>1</strong></span>, no <p>.
                // EvaluateAsync es más fiable que QuerySelectorAsync con :has-text.
                var entriesCount = await page.EvaluateAsync<int>(@"
                    () => {
                        const spans = Array.from(document.querySelectorAll('span'));
                        const target = spans.find(s =>
                            s.textContent.includes('Entradas:') || s.textContent.includes('Entries:')
                        );
                        if (!target) return -1;
                        const strong = target.querySelector('strong');
                        return strong ? (parseInt(strong.textContent.trim()) || 0) : -1;
                    }
                ");

                if (entriesCount > 0)
                {
                    Log.Information("✅ ¡Confirmado! Entradas: {Count} en {GiveawayId}", entriesCount, giveawayId);
                    return new JoinGiveawayResponse { Success = true };
                }
                else if (entriesCount == 0)
                {
                    Log.Warning("❌ Entradas siguen en 0 en {GiveawayId}. Falta nivel, fondos o límite.", giveawayId);
                    return null;
                }
                else
                {
                    Log.Warning("❌ No se encontró el contador de entradas en {GiveawayId}. Se asume fallo.", giveawayId);
                    return null;
                }
            }

            Log.Warning("❌ No se encontró ningún botón de unirse en {GiveawayId}.", giveawayId);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error crítico en JoinGiveawayAsync.");
            return null;
        }
    }
}