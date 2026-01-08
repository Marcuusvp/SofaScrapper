using PuppeteerSharp;
using System.Text.Json;

namespace SofaScoreScraper;

public class SofaScraper
{
    private IBrowser? _browser;
    private IPage? _page;

    public async Task InitializeAsync()
    {
        try
        {
            Console.WriteLine("Baixando Chromium (se necessário)...");
            
            // Download do Chromium (primeira vez apenas)
            var browserFetcher = new BrowserFetcher();
            
            Console.WriteLine("Verificando instalação do Chromium...");
            await browserFetcher.DownloadAsync();
            
            Console.WriteLine("Iniciando navegador...");
            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true, // DEVE ser true no WSL
                Args = new[] 
                { 
                    "--no-sandbox", 
                    "--disable-setuid-sandbox",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--disable-software-rasterizer",
                    "--disable-extensions",
                    "--no-first-run",
                    "--no-zygote",
                    "--single-process" // Importante para WSL
                }
            });

            _page = await _browser.NewPageAsync();
            
            // User agent realista
            await _page.SetUserAgentAsync(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36"
            );
            
            Console.WriteLine("Navegador iniciado com sucesso!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao inicializar: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    public async Task<string> GetRoundEventsAsync(int tournamentId, int seasonId, int round)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado. Chame InitializeAsync() primeiro.");

        // Navegar para a página do torneio (estabelece sessão e cookies)
        await _page.GoToAsync($"https://www.sofascore.com/pt/torneio/futebol/england/premier-league/{tournamentId}");
        
        // Aguardar página carregar
        await Task.Delay(3000);

        // Fazer a requisição via JavaScript do navegador
        var apiUrl = $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}";
        
        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<string> GetRoundEventsWithSlugAsync(int tournamentId, int seasonId, int round, string slug, string? prefix = null)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado. Chame InitializeAsync() primeiro.");

        // Navegar para a página do torneio (estabelece sessão e cookies)
        await _page.GoToAsync($"https://www.sofascore.com/pt/torneio/futebol/europe/uefa-champions-league/{tournamentId}");
        
        // Aguardar página carregar
        await Task.Delay(3000);

        // Construir URL com slug e prefix (para fases de qualificação)
        var apiUrl = prefix != null
            ? $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}/slug/{slug}/prefix/{prefix}"
            : $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}/slug/{slug}";
        
        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<List<Match>> GetMatchesAsync(int tournamentId, int seasonId, int round)
    {
        var json = await GetRoundEventsAsync(tournamentId, seasonId, round);
        
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
        
        return response?.Events?.Select(e => new Match
        {
            Id = e.Id,
            HomeTeam = e.HomeTeam?.Name ?? "N/A",
            AwayTeam = e.AwayTeam?.Name ?? "N/A",
            HomeScore = e.HomeScore?.Current,
            AwayScore = e.AwayScore?.Current,
            Status = e.Status?.Description ?? "N/A",
            StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).DateTime
        }).ToList() ?? new List<Match>();
    }

    public async Task<List<Match>> GetQualificationMatchesAsync(int tournamentId, int seasonId, int round, string slug, string? prefix = null)
    {
        var json = await GetRoundEventsWithSlugAsync(tournamentId, seasonId, round, slug, prefix);
        
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
        
        return response?.Events?.Select(e => new Match
        {
            Id = e.Id,
            HomeTeam = e.HomeTeam?.Name ?? "N/A",
            AwayTeam = e.AwayTeam?.Name ?? "N/A",
            HomeScore = e.HomeScore?.Current,
            AwayScore = e.AwayScore?.Current,
            Status = e.Status?.Description ?? "N/A",
            StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).DateTime
        }).ToList() ?? new List<Match>();
    }

    public async Task<List<Match>> GetLiveMatchesAsync()
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado.");

        await _page.GoToAsync("https://www.sofascore.com/");
        await Task.Delay(3000);

        var apiUrl = "https://www.sofascore.com/api/v1/sport/football/events/live";
        
        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
        
        return response?.Events?.Select(e => new Match
        {
            Id = e.Id,
            HomeTeam = e.HomeTeam?.Name ?? "N/A",
            AwayTeam = e.AwayTeam?.Name ?? "N/A",
            HomeScore = e.HomeScore?.Current,
            AwayScore = e.AwayScore?.Current,
            Status = e.Status?.Description ?? "N/A",
            StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).DateTime
        }).ToList() ?? new List<Match>();
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    public async Task<string> GetEventDetailsAsync(int eventId)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado.");

        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}";

        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<string> GetEventStatisticsAsync(int eventId)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado.");

        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/statistics";

        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<EventDetail?> GetMatchDetailsAsync(int eventId)
    {
        var json = await GetEventDetailsAsync(eventId);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var response = JsonSerializer.Deserialize<EventDetailResponse>(json, options);
        return response?.Event;
    }

    public async Task<StatisticsResponse?> GetMatchStatisticsAsync(int eventId)
    {
        var json = await GetEventStatisticsAsync(eventId);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        return JsonSerializer.Deserialize<StatisticsResponse>(json, options);
    }

    public async Task<List<Incident>> GetMatchGoalsAsync(int eventId)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado.");

        // URL específica de incidentes
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/incidents";

        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<IncidentsResponse>(json, options);

        // Retorna apenas incidentes do tipo "goal", ordenados por tempo
        return response?.Incidents?
            .Where(i => i.IncidentType == "goal")
            .OrderBy(i => i.Time)
            .ToList() ?? new List<Incident>();
    }

}