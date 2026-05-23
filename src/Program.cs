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

// Inicializar sesión Playwright
await sessionService.SetKeyDropCookieAsync();

// 🚀 Memoria del bot: Aquí guardamos los IDs que ya procesamos, ignoramos o fallamos.
var joinedGiveaways = new HashSet<string>();

while (true)
{
    try
    {
        Log.Information("--- Buscando sorteos activos ---");
        var giveaways = await keyDropService.GetGiveawaysAsync();
        bool attemptedNewJoin = false; // Bandera para saber si trabajamos o descansamos

        if (giveaways == null || giveaways.Count == 0)
        {
            Log.Warning("No se obtuvieron sorteos. Verifica login o Cloudflare.");
        }
        else
        {
            foreach (var giveaway in giveaways)
            {
                // Si el sorteo ya está en nuestra lista de omitidos/unidos, lo saltamos rápidamente
                if (joinedGiveaways.Contains(giveaway.Id))
                {
                    Log.Debug("Sorteo {Id} omitido, ya estamos en la lista local de unidos/ignorados.", giveaway.Id);
                    continue;
                }

                Log.Information("Sorteo disponible: {Id} - {Title}", giveaway.Id, giveaway.Title);

                var details = await keyDropService.GetGiveawayDetailsByIdAsync(giveaway.Id);

                if (details != null && details.Status != "ended")
                {
                    // 🔑 Filtro de Torneos: Solo Amateur y Contender
                    if (details.TournamentType != "amateur" && details.TournamentType != "contender")
                    {
                        Log.Information("⏭️ Sorteo {GiveawayId} omitido (Tipo: {TournamentType}). Ignorando permanentemente.", giveaway.Id, details.TournamentType);
                        joinedGiveaways.Add(giveaway.Id);
                        continue; // Pasamos al siguiente sorteo
                    }

                    // Si llegamos aquí, es válido y vamos a intentar unirnos
                    attemptedNewJoin = true; 
                    var joinResponse = await keyDropService.JoinGiveawayAsync(giveaway.Id);

                    if (joinResponse != null && joinResponse.Success)
                    {
                        Log.Information("✅ Te uniste al sorteo {Id}", giveaway.Id);
                        await SendDiscordNotificationAsync($"🎉 Te uniste al sorteo {giveaway.Id} ({details.Title})");
                        joinedGiveaways.Add(giveaway.Id);
                    }
                    else
                    {
                        Log.Warning("❌ No se pudo unir al sorteo {Id}. Lo añadimos a la lista para no hacer bucle infinito.", giveaway.Id);
                        joinedGiveaways.Add(giveaway.Id);
                    }
                }
            }
        }

        // ⏱️ LÓGICA DE TIEMPOS INTELIGENTE
        if (!attemptedNewJoin)
        {
            Log.Information("💤 No hay sorteos nuevos para unirse. Esperando 1 minuto y 30 segundos...");
            await Task.Delay(90000); // 90 segundos
        }
        else
        {
            Log.Information("⏱️ Esperando 10 segundos antes del siguiente escaneo rápido...");
            await Task.Delay(10000); // 10 segundos
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