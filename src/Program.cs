using Figgle;
using KeyDropGiveawayBot.Models;
using KeyDropGiveawayBot.Services;
using KeyDropGiveawayBot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Serilog;
using Serilog.Events;
using System.Text;

#region Service Configuration

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", false, true)
    .AddEnvironmentVariables();
var configuration = builder.Build();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Error)
    .MinimumLevel.Override("System", LogEventLevel.Error)
    .WriteTo.Console(outputTemplate: "[{Timestamp:y-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSerilog();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddScoped<IApiClient, ApiClient>();
        services.AddSingleton<IKeyDropService, KeyDropService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddHttpClient();
        services.AddSingleton(context.Configuration);
        services.TryAddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();

        services.TryAddSingleton(serviceProvider =>
        {
            var provider = serviceProvider.GetRequiredService<ObjectPoolProvider>();
            return provider.Create(new StringBuilderPooledObjectPolicy());
        });
    })
    .Build();

#endregion

using var httpClient = new HttpClient();

DisplayWelcomeMessage();

Console.WriteLine("=========================================================");
Console.WriteLine("⚙️ SELECCIONA EL MODO DE OPERACIÓN DEL BOT:");
Console.WriteLine("[1] - Unirse ÚNICAMENTE a sorteos Amateur");
Console.WriteLine("[2] - Unirse a sorteos Contender Y Amateur");
Console.WriteLine("=========================================================");
Console.Write("👉 Ingresa 1 o 2 (Enter por defecto es 2): ");
string? input = Console.ReadLine();
int botMode = (input != null && input.Trim() == "1") ? 1 : 2;

Log.Information("Starting KeyDrop Giveaway Bot on ARM64 (Debian 13)");
string modeName = botMode == 1 ? "Solo Amateur" : "Contender y Amateur";
Log.Information("🚀 Modo Seleccionado: {Mode}", modeName);
await SendDiscordNotificationAsync($"✅ Bot iniciado en la Tanix W2. Modo activo: {modeName}");

var keyDropService = host.Services.GetRequiredService<IKeyDropService>();
var sessionService = host.Services.GetRequiredService<ISessionService>();

var singletonLockPath = "/home/tanix/.config/chromium/KeyDropProfile/SingletonLock";
if (File.Exists(singletonLockPath))
{
    File.Delete(singletonLockPath);
    Log.Information("🧹 SingletonLock eliminado. Sesión anterior no cerró correctamente.");
}

await sessionService.SetKeyDropCookieAsync();

var permanentlyIgnored = new HashSet<string>();
var tempErrorCounters  = new Dictionary<string, int>();

// 🧠 Sensor de estancamiento para evadir páginas zombie
int consecutiveIdleScans = 0; 

while (true)
{
    try
    {
        Log.Information("--- Buscando sorteos activos ---");
        var giveaways = await keyDropService.GetGiveawaysAsync();

        bool attemptedNewJoin = false;
        bool retryAmateur     = false;
        bool newGiveawayDetected = false; // 🛠️ NUEVO SENSOR: Detecta si al menos apareció un ID que no conocíamos

        if (giveaways == null || giveaways.Count == 0)
        {
            Log.Warning("⚠️ La página podría estar congelada (0 sorteos detectados). Evaluando situación...");
            var page = sessionService.ActivePage;

            if (page == null)
            {
                Log.Warning("🔁 ActivePage es null. Reiniciando sesión completa...");
                if (File.Exists(singletonLockPath)) File.Delete(singletonLockPath);
                await sessionService.SetKeyDropCookieAsync(isRefresh: true);
                await Task.Delay(5000);
                continue;
            }

            try
            {
                string title;
                try { title = await page.TitleAsync(); }
                catch { title = ""; }

                if (title.Contains("moment") || title.Contains("Cloudflare") ||
                    title.Contains("Attention") || title.Contains("Just a moment"))
                {
                    Log.Warning("🛡️ Cloudflare detectado mid-session. Reiniciando sesión con fallback nativo...");
                    if (File.Exists(singletonLockPath)) File.Delete(singletonLockPath);
                    await sessionService.SetKeyDropCookieAsync(isRefresh: true);
                    await Task.Delay(5000);
                    continue;
                }

                Log.Information("🔄 Purgando RAM estancada (about:blank)...");
                await page.GotoAsync("about:blank", new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.Commit });
                await Task.Delay(1500);

                Log.Information("🔄 Navegando a la Home para desmontar el módulo...");
                await page.GotoAsync("https://key-drop.com/es/",
                    new Microsoft.Playwright.PageGotoOptions
                    {
                        WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                        Timeout   = 60000
                    });
                await Task.Delay(4000);

                Log.Information("🔄 Volviendo a la Lista para reconectar WebSockets...");
                await page.GotoAsync("https://key-drop.com/es/giveaways/list",
                    new Microsoft.Playwright.PageGotoOptions
                    {
                        WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                        Timeout   = 60000
                    });

                await page.EvaluateAsync("window.scrollBy(0, 1000)");
                await Task.Delay(6000);
            }
            catch (Exception ex)
            {
                Log.Warning("Error durante la recuperación de página: {Msg}", ex.Message);
            }

            Log.Information("💤 Esperando 30 segundos antes de reintentar el escaneo...");
            await Task.Delay(30000);
            continue;
        }

        foreach (var giveaway in giveaways)
        {
            // 🛠️ Si el sorteo no está en nuestra lista de ignorados, significa que la página está VIVA
            if (!permanentlyIgnored.Contains(giveaway.Id))
            {
                newGiveawayDetected = true;
            }

            if (permanentlyIgnored.Contains(giveaway.Id))
            {
                continue; 
            }

            GiveawayDetails? details = null;
            try
            {
                using var detailsCts    = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var detailsTask         = keyDropService.GetGiveawayDetailsByIdAsync(giveaway.Id);
                var detailsCompleted    = await Task.WhenAny(detailsTask, Task.Delay(15000, detailsCts.Token));

                if (detailsCompleted != detailsTask)
                {
                    permanentlyIgnored.Add(giveaway.Id);
                    continue; 
                }
                details = await detailsTask;
            }
            catch (Exception)
            {
                permanentlyIgnored.Add(giveaway.Id);
                continue; 
            }

            if (details == null)
            {
                configuration["GiveawayJoinHost"] = "";

                tempErrorCounters.TryGetValue(giveaway.Id, out int failCount);
                failCount++;
                tempErrorCounters[giveaway.Id] = failCount;

                if (failCount >= 3)
                {
                    permanentlyIgnored.Add(giveaway.Id);
                    continue; 
                }

                retryAmateur = true;
                await RecuperarSesionAsync(giveaway.Id, purgarRam: true);
                continue;
            }

            if (details.Status == "ended")
            {
                permanentlyIgnored.Add(giveaway.Id);
                continue; 
            }

            bool isAmateur   = string.Equals(details.TournamentType, "amateur",   StringComparison.OrdinalIgnoreCase);
            bool isContender = string.Equals(details.TournamentType, "contender", StringComparison.OrdinalIgnoreCase);

            if (botMode == 1 && !isAmateur)
            {
                permanentlyIgnored.Add(giveaway.Id);
                continue;
            }
            if (botMode == 2 && !isAmateur && !isContender)
            {
                permanentlyIgnored.Add(giveaway.Id);
                continue;
            }

            Log.Information("🎯 Sorteo Compatible Detectado: {Id} | Tipo: {Type} | Premio: {Prize:F2} USD",
                giveaway.Id, details.TournamentType, details.PrizePrice ?? 0);

            if (isAmateur && (details.PrizePrice ?? 0) <= 4.5)
            {
                Log.Information("⏭️ Amateur {Id} omitido. Premio ({Prize:F2} USD) ≤ $4.50.",
                    giveaway.Id, details.PrizePrice ?? 0);
                permanentlyIgnored.Add(giveaway.Id);
                continue;
            }

            if (details.Joined == true)
            {
                Log.Information("✅ Ya unidos al sorteo {Id} (API). Omitiendo clic.", giveaway.Id);
                permanentlyIgnored.Add(giveaway.Id);
                continue;
            }

            bool joinSuccess = false;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                Log.Information("🔄 Intento de unión {Attempt}/5 para {Id}...", attempt, giveaway.Id);
                JoinGiveawayResponse? joinResponse = null;
                bool joinTimedOut = false;
                
                try
                {
                    var joinTask      = keyDropService.JoinGiveawayAsync(giveaway.Id);
                    var completedTask = await Task.WhenAny(joinTask, Task.Delay(60000));

                    if (completedTask != joinTask)
                    {
                        joinTimedOut = true;
                        Log.Warning("⏰ Timeout (60s) en JoinGiveawayAsync para {Id}.", giveaway.Id);
                    }
                    else
                    {
                        joinResponse = await joinTask;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Excepción en JoinGiveawayAsync para {Id}.", giveaway.Id);
                }

                if (!joinTimedOut && joinResponse != null && joinResponse.Success)
                {
                    Log.Information("✅ Te uniste al sorteo {Id} exitosamente en el intento {Attempt}", giveaway.Id, attempt);
                    await SendDiscordNotificationAsync(
                        $"🎉 Unido a {giveaway.Id} | {details.Title} | {details.PrizePrice:F2} USD");

                    permanentlyIgnored.Add(giveaway.Id);
                    attemptedNewJoin = true;
                    joinSuccess = true;
                    break;
                }
                else
                {
                    Log.Warning("❌ Falló el intento {Attempt}/5 para {Id}.", attempt, giveaway.Id);
                    if (attempt < 5)
                    {
                        Log.Information("⏳ Esperando 10 segundos antes del siguiente intento en la misma página...");
                        await Task.Delay(10000);
                    }
                }
            }

            if (joinSuccess)
            {
                await RecuperarSesionAsync(giveaway.Id, purgarRam: false);
                continue;
            }
            else
            {
                Log.Warning("❌ Se agotaron los 5 intentos. Purgando RAM y buscando nuevos sorteos.");
                if (isContender) permanentlyIgnored.Add(giveaway.Id);
                
                await RecuperarSesionAsync(giveaway.Id, purgarRam: true);
                retryAmateur = true;
                continue;
            }
        }

        // 🛠️ LÓGICA MAESTRA DE ESTANCAMIENTO
        if (retryAmateur)
        {
            consecutiveIdleScans = 0; // Reinicia el sensor porque hubo actividad
            Log.Information("⏱️ Reintento programado (Amateur fallido o error temporal). Esperando 20 segundos...");
            await Task.Delay(20000);
        }
        else if (attemptedNewJoin)
        {
            consecutiveIdleScans = 0; // Reinicia el sensor porque hubo éxito
            Log.Information("⏱️ Unión exitosa. Esperando 10 segundos antes del siguiente escaneo...");
            await Task.Delay(10000);
        }
        else
        {
            // El bot no intentó unirse ni tuvo errores. Verificamos si detectó al menos algo nuevo.
            if (newGiveawayDetected)
            {
                consecutiveIdleScans = 0; // La página está viva y mostrando cosas nuevas
                Log.Information("💤 Sorteos evaluados y omitidos. Esperando 1 minuto...");
                await Task.Delay(60000);
            }
            else
            {
                consecutiveIdleScans++; // Vio exactamente la misma lista que en el turno anterior
                
                if (consecutiveIdleScans >= 4)
                {
                    Log.Information("🔌 Estancamiento detectado ({Count}/4 escaneos viendo los mismos IDs omitidos). Purgando RAM para refrescar la lista...", consecutiveIdleScans);
                    await RecuperarSesionAsync("STALE_REFRESH", purgarRam: true);
                    consecutiveIdleScans = 0; // Reseteamos tras purgar
                }
                else
                {
                    Log.Information("💤 Sin sorteos nuevos. Esperando 1 minuto... (Inactividad: {Count}/4)", consecutiveIdleScans);
                    await Task.Delay(60000);
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error en el bucle principal. Reintentando en 30s.");
        await Task.Delay(30000);
    }
}

async Task RecuperarSesionAsync(string giveawayId, bool purgarRam)
{
    try
    {
        var activePage = sessionService.ActivePage;
        if (activePage != null)
        {
            if (purgarRam)
            {
                Log.Information("🧹 Liberando memoria RAM (Purge) tras agotamiento de intentos o inactividad...");
                await activePage.GotoAsync("about:blank", new Microsoft.Playwright.PageGotoOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.Commit });
            }
            
            Log.Information("🔄 Volviendo al listado de forma segura...");
            await activePage.GotoAsync(
                "https://key-drop.com/es/giveaways/list",
                new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                    Timeout   = 60000
                });

            Log.Information("⏳ Esperando estabilización de WebSockets (4s)...");
            await Task.Delay(4000);
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "No se pudo navegar al listado. El siguiente ciclo intentará la recuperación.");
    }
}

void DisplayWelcomeMessage()
{
    Console.WriteLine(FiggleFonts.Standard.Render("KeyDrop Bot ARM64"));
    Console.WriteLine("Optimizado con Playwright para Debian 13.");
    Console.WriteLine("¡Suerte en los sorteos!");
    Console.WriteLine();
}

async Task SendDiscordNotificationAsync(string message)
{
    var webhookUrl    = configuration["DiscordWebhookUrl"] ?? "https://discord.com/api/webhooks/1408427703442608300/aRtmsPCOzgtCmALaUGcVTSYXbgJxXgr4jYSkfmuwF7jmPZ07jwAA2IN0_jv09JnPre9U";
    var userIdMention = "<@860011909516361759>";

    var jsonMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
    var content     = $"{userIdMention} {jsonMessage}";
    var jsonPayload = $"{{\"content\": \"{content}\"}}";

    try
    {
        var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        await httpClient.PostAsync(webhookUrl, httpContent);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error enviando notificación a Discord.");
    }
}