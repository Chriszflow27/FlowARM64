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

Log.Information("Starting KeyDrop Giveaway Bot on ARM64 (Debian 13)");
await SendDiscordNotificationAsync("✅ Bot iniciado correctamente en la Tanix W2 con Playwright.");

var keyDropService = host.Services.GetRequiredService<IKeyDropService>();
var sessionService = host.Services.GetRequiredService<ISessionService>();

await sessionService.SetKeyDropCookieAsync();

var joinedGiveaways = new HashSet<string>();

while (true)
{
    try
    {
        Log.Information("--- Buscando sorteos activos ---");
        var giveaways = await keyDropService.GetGiveawaysAsync();

        // ✅ La bandera solo se activa si nos unimos con éxito
        bool attemptedNewJoin = false;

        if (giveaways == null || giveaways.Count == 0)
        {
            Log.Warning("No se obtuvieron sorteos. Verifica login o Cloudflare.");
        }
        else
        {
            foreach (var giveaway in giveaways)
            {
                // Salto rápido: ya procesado en un ciclo anterior
                if (joinedGiveaways.Contains(giveaway.Id))
                {
                    Log.Debug("Sorteo {Id} omitido, ya estamos en la lista local de unidos/ignorados.", giveaway.Id);
                    continue;
                }

                Log.Information("Sorteo disponible: {Id} - {Title}", giveaway.Id, giveaway.Title);

                var details = await keyDropService.GetGiveawayDetailsByIdAsync(giveaway.Id);

                if (details == null || details.Status == "ended")
                {
                    // Si no hay detalles o ya terminó, lo ignoramos permanentemente
                    joinedGiveaways.Add(giveaway.Id);
                    continue;
                }

                bool isAmateur   = string.Equals(details.TournamentType, "amateur",   StringComparison.OrdinalIgnoreCase);
                bool isContender = string.Equals(details.TournamentType, "contender", StringComparison.OrdinalIgnoreCase);

                // ── Filtro 1: Tipos no deseados (champion, challenger, etc.) ──
                if (!isAmateur && !isContender)
                {
                    Log.Information("⏭️ Sorteo {Id} omitido (Tipo: {Type}). Ignorando permanentemente.",
                        giveaway.Id, details.TournamentType);
                    joinedGiveaways.Add(giveaway.Id);
                    continue;
                }

                // ── Filtro 2: Amateur solo si el premio supera $4.50 ──
                if (isAmateur)
                {
                    double prize = details.PrizePrice ?? 0;
                    if (prize <= 4.5)
                    {
                        Log.Information("⏭️ Amateur {Id} omitido. Premio ({Prize:F2} USD) no supera $4.50.",
                            giveaway.Id, prize);
                        joinedGiveaways.Add(giveaway.Id);
                        continue;
                    }
                }

                // ── Filtro 3: La API ya nos marca como unidos ──
                if (details.Joined == true)
                {
                    Log.Information("✅ Ya unidos al sorteo {Id} (confirmado por API). Omitiendo clic.",
                        giveaway.Id);
                    joinedGiveaways.Add(giveaway.Id);
                    continue;
                }

                // ── Intentar unirse ──
                Log.Information("🎯 Intentando unirse: {Id} | Tipo: {Type} | Premio: {Prize:F2} USD",
                    giveaway.Id, details.TournamentType, details.PrizePrice ?? 0);

                var joinResponse = await keyDropService.JoinGiveawayAsync(giveaway.Id);

                // Siempre se añade a la lista tras el intento (evita bucle infinito)
                joinedGiveaways.Add(giveaway.Id);

                if (joinResponse != null && joinResponse.Success)
                {
                    Log.Information("✅ Te uniste al sorteo {Id}", giveaway.Id);
                    await SendDiscordNotificationAsync(
                        $"🎉 Unido a {giveaway.Id} | {details.Title} | {details.PrizePrice:F2} USD");

                    // ✅ SOLO aquí activamos la bandera → dispara el descanso de 90s
                    attemptedNewJoin = true;
                }
                else
                {
                    // ❌ Falló (Contender sin depósito, límite, etc.)
                    // → NO se activa attemptedNewJoin → el bucle continúa con el siguiente sorteo
                    Log.Warning("❌ No se pudo unir al sorteo {Id} ({Type}). Continuando con el siguiente...",
                        giveaway.Id, details.TournamentType);
                }
            }
        }

        // ── Lógica de tiempos inteligente ──
        if (!attemptedNewJoin)
        {
            Log.Information("💤 Sin sorteos nuevos. Esperando 1 minuto y 30 segundos...");
            await Task.Delay(90000);
        }
        else
        {
            Log.Information("⏱️ Unión exitosa. Esperando 10 segundos antes del siguiente escaneo...");
            await Task.Delay(10000);
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error en el bucle principal. Reintentando en 30s.");
        await Task.Delay(30000);
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
    var webhookUrl = configuration["DiscordWebhookUrl"] ?? "https://discord.com/api/webhooks/1408427703442608300/aRtmsPCOzgtCmALaUGcVTSYXbgJxXgr4jYSkfmuwF7jmPZ07jwAA2IN0_jv09JnPre9U";
    var userIdMention = "<@860011909516361759>";

    var jsonMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
    var content = $"{userIdMention} {jsonMessage}";
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