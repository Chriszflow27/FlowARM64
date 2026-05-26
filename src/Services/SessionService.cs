using KeyDropGiveawayBot.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Serilog;
using System.Diagnostics;
using System.Text;

namespace KeyDropGiveawayBot.Services;

public class SessionService : ISessionService
{
    public IPage? ActivePage { get; private set; }

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    
    // 🧠 MEJORA SENIOR: Memoria en caché para transferir cookies sin depender del disco duro
    private IReadOnlyList<BrowserContextCookiesResult>? _nativeCookies = null;

    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient = new HttpClient();

    private const string ChromiumPath     = "/usr/bin/chromium";
    private const string UserDataDir      = "/home/tanix/.config/chromium/KeyDropProfile";
    private const string SingletonLock    = "/home/tanix/.config/chromium/KeyDropProfile/SingletonLock";
    private const string KeyDropListUrl   = "https://key-drop.com/es/giveaways/list";

    public SessionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SetKeyDropCookieAsync(CancellationToken cancellationToken = default, bool isRefresh = false)
    {
        await IniciarSesionAsync(cancellationToken, isRefresh, nativeFallbackPermitido: true);
    }

    private async Task IniciarSesionAsync(CancellationToken cancellationToken, bool isRefresh, bool nativeFallbackPermitido)
    {
        Log.Information("Iniciando Playwright para bypass de Cloudflare...");

        if (!File.Exists(ChromiumPath))
        {
            Log.Error("No se encontró Chromium. Ejecuta: sudo apt install chromium");
            throw new FileNotFoundException("Chromium no instalado.");
        }

        try
        {
            if (isRefresh)
            {
                Log.Information("🔁 Refresh forzado: destruyendo sesión anterior para liberar RAM...");
                ActivePage = null;
                if (_context != null)  { try { await _context.DisposeAsync(); }  catch { } _context   = null; }
                if (_playwright != null){ try { _playwright.Dispose(); }          catch { } _playwright = null; }
            }

            if (_playwright == null) _playwright = await Playwright.CreateAsync();

            if (_context == null)
            {
                var launchOptions = new BrowserTypeLaunchPersistentContextOptions
                {
                    ExecutablePath = ChromiumPath,
                    Headless       = false,
                    ViewportSize   = new ViewportSize { Width = 1280, Height = 720 },
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-blink-features=AutomationControlled",
                        "--disable-infobars",
                        "--disable-web-security",
                        "--disable-site-isolation-trials",
                        "--disable-features=IsolateOrigins,site-per-process",
                        "--disable-gpu",
                        "--disable-software-rasterizer",
                        "--disable-webgl",
                        "--disable-3d-apis",
                        "--js-flags=--max-old-space-size=512",
                        "--mute-audio",
                        "--renderer-process-limit=1" 
                    }
                };

                Log.Information("Lanzando Chromium persistente con optimizaciones ARM64...");
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(UserDataDir, launchOptions);

                // 💉 INYECCIÓN EN CALIENTE: Si extrajimos cookies del nativo, se las ponemos a Playwright aquí mismo
                if (_nativeCookies != null && _nativeCookies.Count > 0)
{
    var cookies = _nativeCookies.Select(c => new Cookie
    {
        Name     = c.Name,
        Value    = c.Value,
        Domain   = c.Domain,
        Path     = c.Path,
        Expires  = c.Expires,
        HttpOnly = c.HttpOnly,
        Secure   = c.Secure,
        SameSite = c.SameSite
    }).ToList();

    await _context.AddCookiesAsync(cookies);
    _nativeCookies = null;
}

            }

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();

            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            await page.RouteAsync("**/*", async route =>
            {
                var type = route.Request.ResourceType;
                if (type == "media" || type == "font" || type == "image") 
                    await route.AbortAsync();
                else
                    await route.ContinueAsync();
            });

            page.RequestFinished += async (sender, request) =>
            {
                try
                {
                    var url = request.Url;
                    if (url.Contains("/v1/giveaway/") && !url.Contains("joinGiveaway") && !url.Contains("getPrices"))
                    {
                        var uri  = new Uri(url);
                        var host = $"{uri.Scheme}://{uri.Host}";
                        if (!string.IsNullOrEmpty(host) && _configuration["GiveawayJoinHost"] != host)
                        {
                            Log.Information("🌐 Host dinámico de datos capturado: {Host}", host);
                            _configuration["GiveawayJoinHost"] = host;
                        }
                    }
                }
                catch (Exception ex) { Log.Error(ex, "Error capturando host dinámico."); }
            };

            Log.Information("Navegando a la web de KeyDrop...");
            await page.GotoAsync(KeyDropListUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90000 });

            Log.Information("⏳ Verificando estado de seguridad...");
            await Task.Delay(4000, cancellationToken); 

            string title = await ObtenerTituloSeguridadAsync(page);

            // DETECCIÓN INSTANTÁNEA (Sin bucles de espera)
            if (title.Contains("moment") || title.Contains("Cloudflare") || title.Contains("Attention") || title.Contains("Just a moment"))
            {
                if (nativeFallbackPermitido)
                {
                    Log.Warning("⚠️ Cloudflare detectado. Saltando directamente a Chromium Nativo...");
                    await SendDiscordNotificationAsync("🔧 CF detectado. Lanzando Chromium nativo para resolverlo...");

                    await SolveCloudflareConChromiuNativoAsync(cancellationToken);

                    await IniciarSesionAsync(cancellationToken, isRefresh: true, nativeFallbackPermitido: false);
                    return; 
                }
                else
                {
                    Log.Error("❌ CF sin resolver ni con Playwright ni con Chromium nativo. Requiere intervención manual.");
                    await SendDiscordNotificationAsync("❌ Cloudflare sin resolver tras fallback nativo. Intervención manual requerida.");
                }
            }
            else
            {
                Log.Information("✅ Cloudflare superado con éxito. Título: {Title}", title);
                Log.Information("⏳ Esperando 5 segundos para estabilización de la sesión...");
                await Task.Delay(5000, cancellationToken);
            }

            await DiagnosticarSesionAsync(page);
            this.ActivePage = page;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error crítico iniciando Playwright.");
            if (_context   != null) { try { await _context.DisposeAsync(); } catch { } _context   = null; }
            if (_playwright != null) { try { _playwright.Dispose();         } catch { } _playwright = null; }
        }
    }

    private async Task SolveCloudflareConChromiuNativoAsync(CancellationToken cancellationToken)
    {
        Log.Warning("🔧 Cerrando Playwright para liberar el perfil...");

        ActivePage = null;
        if (_context != null) { try { await _context.DisposeAsync(); } catch { } _context = null; }

        await Task.Delay(1500, cancellationToken);
        if (File.Exists(SingletonLock)) File.Delete(SingletonLock);

        Log.Information("🔧 Lanzando Chromium nativo para resolver Cloudflare...");

        var psi = new ProcessStartInfo
        {
            FileName        = ChromiumPath,
            Arguments       = $"--user-data-dir={UserDataDir} --disable-gpu --no-sandbox --disable-software-rasterizer --remote-debugging-port=9222 {KeyDropListUrl}",
            UseShellExecute = true  
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            Log.Information("⏳ Esperando que Chromium abra el puerto de depuración (5s)...");
            await Task.Delay(5000, cancellationToken);

            if (_playwright == null) _playwright = await Playwright.CreateAsync();

            IBrowser? cdpBrowser = null;
            try 
            {
                Log.Information("🔌 Conectando a Chromium nativo vía protocolo CDP...");
                cdpBrowser = await _playwright.Chromium.ConnectOverCDPAsync("http://localhost:9222");
                var cdpContext = cdpBrowser.Contexts[0];
                
                bool resolved = false;
                for(int i = 0; i < 40; i++) // Monitorea en tiempo real
                {
                    var page = cdpContext.Pages.FirstOrDefault();
                    if (page != null)
                    {
                        string title = await ObtenerTituloSeguridadAsync(page);
                        if (!string.IsNullOrEmpty(title) && !title.Contains("moment") && !title.Contains("Cloudflare") && !title.Contains("Attention") && title.Contains("Giveaways"))
                        {
                            Log.Information("✅ ¡Cloudflare superado detectado en tiempo real! Título: {Title}", title);
                            
                            Log.Information("⏳ Dando 15 segundos extra para que la web cargue correctamente en segundo plano...");
                            await Task.Delay(15000, cancellationToken); 

                            // 🚨 EL ROBO DE COOKIES 🚨: Extraemos cf_clearance directamente de la RAM
                            try {
                                _nativeCookies = await cdpContext.CookiesAsync();
                                var cfCookie = _nativeCookies.FirstOrDefault(c => c.Name == "cf_clearance");
                                if (cfCookie != null) {
                                    Log.Information("✅ Cookie cf_clearance EXTRAÍDA de la memoria con éxito. Transferencia asegurada.");
                                } else {
                                    Log.Warning("⚠️ No se vio cf_clearance explícito, pero se inyectarán las cookies obtenidas.");
                                }
                            } catch (Exception ex) {
                                Log.Warning("No se pudieron extraer las cookies CDP ({Msg}), dependeremos de que Chromium las haya guardado.", ex.Message);
                            }

                            resolved = true;
                            break; 
                        }
                    }
                    Log.Information("🛡️ Esperando a que el nativo pase Cloudflare automáticamente ({I}/40)...", i + 1);
                    await Task.Delay(5000, cancellationToken);
                }
                
                if (!resolved) Log.Warning("⏳ Se agotó el tiempo de espera por CDP. Se cerrará el nativo.");
            }
            catch (Exception ex) 
            {
                Log.Warning("⚠️ No se pudo conectar vía CDP ({Msg}). Usando fallback ciego de 2 minutos...", ex.Message);
                await Task.Delay(120000, cancellationToken); 
            }
            finally 
            {
                if (cdpBrowser != null) await cdpBrowser.CloseAsync();
            }
        }
        finally
        {
            if (proc != null)
            {
                // Cierre gentil para intentar forzar escritura en disco como doble seguridad
                try { proc.CloseMainWindow(); } catch { } 
                try { await Task.Run(() => proc.WaitForExit(3000)); } catch { }
                try { proc.Kill(entireProcessTree: true); } catch { try { proc.Kill(); } catch { } }
            }

            await Task.Delay(2000, cancellationToken);
            if (File.Exists(SingletonLock)) File.Delete(SingletonLock);
        }

        Log.Information("✅ Chromium nativo cerrado. Relanzando Playwright...");
    }

    private static async Task<string> ObtenerTituloSeguridadAsync(IPage page)
    {
        try { return await page.TitleAsync(); }
        catch { return ""; }
    }

    private async Task DiagnosticarSesionAsync(IPage page)
    {
        var token = await page.EvaluateAsync<string>("() => window.localStorage.getItem('token')");
        if (!string.IsNullOrEmpty(token))
            Log.Information("✅ Token DETECTADO (primeros 10 chars): {TokenStart}",
                token.Substring(0, Math.Min(10, token.Length)));
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
        var webhookUrl    = _configuration["DiscordWebhookUrl"] ?? "https://discord.com/api/webhooks/...";
        var userIdMention = "<@860011909516361759>";
        var jsonMessage   = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var content       = $"{userIdMention} {jsonMessage}";
        var jsonPayload   = $"{{\"content\": \"{content}\"}}";
        try
        {
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(webhookUrl, httpContent);
        }
        catch { }
    }
}