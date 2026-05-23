using KeyDropGiveawayBot.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Serilog;
using System.Text;

namespace KeyDropGiveawayBot.Services;

public class SessionService : ISessionService
{
    public IPage? ActivePage { get; private set; }

    private IPlaywright? _playwright;
    private IBrowserContext? _context;

    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient = new HttpClient();

    public SessionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SetKeyDropCookieAsync(CancellationToken cancellationToken = default, bool isRefresh = false)
    {
        Log.Information("Iniciando Playwright para bypass de Cloudflare...");

        var executablePath = "/usr/bin/chromium";
        var userDataDir = "/home/tanix/.config/chromium/KeyDropProfile";

        if (!File.Exists(executablePath))
        {
            Log.Error("No se encontró Chromium. Ejecuta: sudo apt install chromium");
            throw new FileNotFoundException("Chromium no instalado.");
        }

        try
        {
            if (_playwright == null) _playwright = await Playwright.CreateAsync();

            if (_context == null)
            {
                var launchOptions = new BrowserTypeLaunchPersistentContextOptions
                {
                    ExecutablePath = executablePath,
                    Headless = false,
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-blink-features=AutomationControlled",
                        "--disable-infobars",
                        "--disable-web-security",
                        "--disable-site-isolation-trials",
                        "--disable-features=IsolateOrigins,site-per-process"
                    }
                };

                Log.Information("Lanzando Chromium persistente...");
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions);
            }

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();

            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            // 🌐 Capturar host dinámico de las peticiones
            page.RequestFinished += async (sender, request) =>
            {
                try
                {
                    var url = request.Url;
                    if (url.Contains("/v1/giveaway/"))
                    {
                        var uri = new Uri(url);
                        var host = $"{uri.Scheme}://{uri.Host}";
                        if (!string.IsNullOrEmpty(host))
                        {
                            Log.Information("🌐 Host dinámico capturado: {Host}", host);
                            _configuration["GiveawayJoinHost"] = host;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error capturando host dinámico de KeyDrop.");
                }
            };

            Log.Information("Navegando a la web de KeyDrop...");
            await page.GotoAsync("https://key-drop.com/es/giveaways/list", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90000 });

            // 🛡️ Bucle anti-Cloudflare
            bool cfActive = true;
            for (int i = 0; i < 90; i++)
            {
                var title = await page.TitleAsync();
                if (!title.Contains("moment") && !title.Contains("Cloudflare") && !title.Contains("Attention"))
                {
                    cfActive = false;
                    break;
                }

                Log.Information("🛡️ Cloudflare detectado en pantalla. Esperando resolución automática ({Int}/90)...", i + 1);
                await Task.Delay(4000, cancellationToken);
            }

            if (cfActive)
            {
                Log.Error("❌ El navegador no pudo pasar Cloudflare. Es posible que tu IP esté bloqueada o requiera Captcha manual.");
            }
            else
            {
                Log.Information("✅ Cloudflare superado con éxito. Título de página: {Title}", await page.TitleAsync());
                Log.Information("⏳ Esperando 5 segundos para estabilización de la sesión...");
                await Task.Delay(5000, cancellationToken);
            }

            await DiagnosticarSesionAsync(page);

            this.ActivePage = page;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error crítico iniciando Playwright.");
            if (_context != null) { await _context.DisposeAsync(); _context = null; }
            if (_playwright != null) { _playwright.Dispose(); _playwright = null; }
        }
    }

    private async Task DiagnosticarSesionAsync(IPage page)
    {
        var token = await page.EvaluateAsync<string>("() => window.localStorage.getItem('token')");
        if (!string.IsNullOrEmpty(token))
            Log.Information("✅ Token DETECTADO (primeros 10 chars): {TokenStart}", token.Substring(0, Math.Min(10, token.Length)));
        else
            Log.Warning("⚠️ Token FALTA en localStorage");

        var sessionId = await page.EvaluateAsync<string>("() => window.localStorage.getItem('session_id')");
        if (!string.IsNullOrEmpty(sessionId))
            Log.Information("✅ SessionId DETECTADO");
        else
            Log.Warning("⚠️ SessionId FALTA (Es normal, ahora es una cookie segura)");

        var cookies = await page.Context.CookiesAsync();
        if (cookies.Any(c => c.Name.Contains("steamLoginSecure") || c.Name.Contains("session")))
            Log.Information("✅ Cookies de Steam/KeyDrop DETECTADAS");
        else
            Log.Warning("⚠️ Cookies de Steam/KeyDrop FALTAN");
    }

    private async Task SendDiscordNotificationAsync(string message)
    {
        var webhookUrl = _configuration["DiscordWebhookUrl"] ?? "https://discord.com/api/webhooks/...";
        var userIdMention = "<@860011909516361759>";
        var jsonMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var content = $"{userIdMention} {jsonMessage}";
        var jsonPayload = $"{{\"content\": \"{content}\"}}";
        try
        {
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(webhookUrl, httpContent);
        }
        catch { }
    }
}
