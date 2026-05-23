using KeyDropGiveawayBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Threading.Tasks;

namespace KeyDropGiveawayBot.Utils;

public class ApiClient : IApiClient
{
    private readonly IConfiguration _configuration;
    private readonly ISessionService _sessionService;

    public ApiClient(IConfiguration configuration, ISessionService sessionService)
    {
        _configuration = configuration;
        _sessionService = sessionService;
    }

    private async Task<string?> GetSafeTokenAsync(IPage page)
    {
        var token = await page.EvaluateAsync<string>("() => window.localStorage.getItem('token')");
        return string.IsNullOrEmpty(token) ? null : token;
    }

    // ---------------------------
    // GET
    // ---------------------------
    public async Task<T?> GetAsync<T>(string url) where T : class
    {
        var page = _sessionService.ActivePage ?? throw new Exception("No hay página activa.");
        var token = await GetSafeTokenAsync(page);

        var jsonResult = await page.EvaluateAsync<string>($@"
            async () => {{
                const response = await fetch('{url}', {{
                    method: 'GET',
                    headers: {{
                        'accept': 'application/json',
                        'authorization': { (token != null ? "'Bearer " + token + "'" : "''") },
                        'x-currency': 'USD'
                    }},
                    credentials: 'include'
                }});
                return await response.text();
            }}
        ");

        if (string.IsNullOrEmpty(jsonResult))
        {
            Log.Error("GET {Url} devolvió respuesta vacía.", url);
            return null;
        }

        // 🕵️‍♂️ Logging crudo para depuración
        Log.Debug("Respuesta cruda GET {Url}: {Snippet}", url, jsonResult.Substring(0, Math.Min(300, jsonResult.Length)));

        if (jsonResult.TrimStart().StartsWith("<"))
        {
            Log.Error("Bloqueo de CF o respuesta HTML en {Url}", url);
            return null;
        }

        return JsonConvert.DeserializeObject<T>(jsonResult);
    }

    // ---------------------------
    // POST
    // ---------------------------
    public async Task<TOut?> PostAsync<TIn, TOut>(string url, TIn payload)
        where TIn : class where TOut : class
    {
        var page = _sessionService.ActivePage ?? throw new Exception("No hay página activa.");
        var token = await GetSafeTokenAsync(page);
        var payloadJson = JsonConvert.SerializeObject(payload);

        var jsonResult = await page.EvaluateAsync<string>($@"
            async () => {{
                const response = await fetch('{url}', {{
                    method: 'POST',
                    headers: {{
                        'accept': 'application/json',
                        'authorization': { (token != null ? "'Bearer " + token + "'" : "''") },
                        'x-currency': 'USD',
                        'content-type': 'application/json'
                    }},
                    body: '{payloadJson}',
                    credentials: 'include'
                }});
                return await response.text();
            }}
        ");

        if (string.IsNullOrEmpty(jsonResult))
        {
            Log.Error("POST {Url} devolvió respuesta vacía.", url);
            return null;
        }

        Log.Debug("Respuesta cruda POST {Url}: {Snippet}", url, jsonResult.Substring(0, Math.Min(300, jsonResult.Length)));

        if (jsonResult.TrimStart().StartsWith("<"))
        {
            Log.Error("Bloqueo de CF o respuesta HTML en {Url}", url);
            return null;
        }

        return JsonConvert.DeserializeObject<TOut>(jsonResult);
    }

    // ---------------------------
    // PUT
    // ---------------------------
    public async Task<TOut?> PutAsync<TIn, TOut>(string url, TIn? payload)
        where TIn : class where TOut : class
    {
        var page = _sessionService.ActivePage ?? throw new Exception("No hay página activa.");
        var token = await GetSafeTokenAsync(page);
        var payloadJson = payload != null ? JsonConvert.SerializeObject(payload) : "{}";

        var jsonResult = await page.EvaluateAsync<string>($@"
            async () => {{
                const response = await fetch('{url}', {{
                    method: 'PUT',
                    headers: {{
                        'accept': 'application/json',
                        'authorization': { (token != null ? "'Bearer " + token + "'" : "''") },
                        'x-currency': 'USD',
                        'content-type': 'application/json'
                    }},
                    body: '{payloadJson}',
                    credentials: 'include'
                }});
                return await response.text();
            }}
        ");

        if (string.IsNullOrEmpty(jsonResult))
        {
            Log.Error("PUT {Url} devolvió respuesta vacía.", url);
            return null;
        }

        Log.Debug("Respuesta cruda PUT {Url}: {Snippet}", url, jsonResult.Substring(0, Math.Min(300, jsonResult.Length)));

        if (jsonResult.TrimStart().StartsWith("<"))
        {
            Log.Error("Bloqueo de CF o respuesta HTML en {Url}", url);
            return null;
        }

        return JsonConvert.DeserializeObject<TOut>(jsonResult);
    }

    // ---------------------------
    // JOIN GIVEAWAY
    // ---------------------------
    public async Task<string?> JoinGiveawayAsync(string giveawayId)
    {
        var page = _sessionService.ActivePage ?? throw new Exception("No hay página activa.");
        var token = await GetSafeTokenAsync(page);

        var host = _configuration["GiveawayJoinHost"];
        if (string.IsNullOrEmpty(host))
        {
            Log.Error("JoinGiveawayAsync: Host dinámico JOIN no capturado.");
            return null;
        }

        var jsonResult = await page.EvaluateAsync<string>($@"
            async () => {{
                const response = await fetch('{host}/v1/giveaway/joinGiveaway/{giveawayId}', {{
                    method: 'PUT',
                    headers: {{
                        'accept': 'application/json',
                        'authorization': { (token != null ? "'Bearer " + token + "'" : "''") },
                        'x-currency': 'USD'
                    }},
                    credentials: 'include'
                }});
                return await response.text();
            }}
        ");

        if (string.IsNullOrEmpty(jsonResult))
        {
            Log.Error("JoinGiveawayAsync: Respuesta vacía desde {Host}", host);
            return null;
        }

        Log.Debug("Respuesta cruda JOIN {Host}: {Snippet}", host, jsonResult.Substring(0, Math.Min(300, jsonResult.Length)));

        return jsonResult;
    }
}
