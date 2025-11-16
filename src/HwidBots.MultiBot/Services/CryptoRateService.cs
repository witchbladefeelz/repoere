using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HwidBots.Shared.Services;

public class CryptoRateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CryptoRateService> _logger;
    private readonly Dictionary<string, decimal> _cachedRates = new();
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

    public CryptoRateService(HttpClient httpClient, ILogger<CryptoRateService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<Dictionary<string, decimal>> GetRatesAsync(CancellationToken cancellationToken = default)
    {
        // Return cached rates if still valid
        if (DateTime.UtcNow - _lastUpdate < _cacheExpiration && _cachedRates.Count > 0)
        {
            return _cachedRates;
        }

        try
        {
            // CoinGecko API - free, no API key required
            var response = await _httpClient.GetAsync(
                "https://api.coingecko.com/api/v3/simple/price?ids=tether,the-open-network,bitcoin,ethereum-classic&vs_currencies=usd",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch crypto rates: {StatusCode}", response.StatusCode);
                return GetFallbackRates();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            _cachedRates.Clear();
            _cachedRates["usdt"] = data.GetProperty("tether").GetProperty("usd").GetDecimal();
            _cachedRates["ton"] = data.GetProperty("the-open-network").GetProperty("usd").GetDecimal();
            _cachedRates["btc"] = data.GetProperty("bitcoin").GetProperty("usd").GetDecimal();
            _cachedRates["etc"] = data.GetProperty("ethereum-classic").GetProperty("usd").GetDecimal();

            _lastUpdate = DateTime.UtcNow;

            _logger.LogInformation("Updated crypto rates: USDT=${Usdt}, TON=${Ton}, BTC=${Btc}, ETC=${Etc}",
                _cachedRates["usdt"], _cachedRates["ton"], _cachedRates["btc"], _cachedRates["etc"]);

            return _cachedRates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching crypto rates");
            return GetFallbackRates();
        }
    }

    private Dictionary<string, decimal> GetFallbackRates()
    {
        // Fallback rates if API fails
        return new Dictionary<string, decimal>
        {
            ["usdt"] = 1.00m,
            ["ton"] = 5.50m,
            ["btc"] = 95000m,
            ["etc"] = 26m
        };
    }

    public async Task<(decimal usdt, decimal ton, decimal btc, decimal etc)> CalculateCryptoAmountsAsync(
        decimal usdAmount,
        CancellationToken cancellationToken = default)
    {
        var rates = await GetRatesAsync(cancellationToken);

        return (
            usdt: usdAmount / rates["usdt"],
            ton: usdAmount / rates["ton"],
            btc: usdAmount / rates["btc"],
            etc: usdAmount / rates["etc"]
        );
    }
}
