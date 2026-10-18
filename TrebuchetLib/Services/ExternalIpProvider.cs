using System.Net;

namespace TrebuchetLib.Services;

/// <summary>
/// Looks up the public IP of this machine (used for connection strings, {ip} macros and IP change notifications).
/// Results are cached; failures keep the last known address.
/// </summary>
public static class ExternalIpProvider
{
    private static readonly string[] Endpoints =
    [
        "https://api.ipify.org",
        "https://checkip.amazonaws.com",
        "https://icanhazip.com"
    ];

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastFetchUtc = DateTime.MinValue;
    private static string _cached = string.Empty;

    /// <summary>Last known external IP, empty when never resolved.</summary>
    public static string Cached => _cached;

    public static async Task<string> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !string.IsNullOrEmpty(_cached) && DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromMinutes(10))
            return _cached;

        await Gate.WaitAsync(ct);
        try
        {
            if (!force && !string.IsNullOrEmpty(_cached) && DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromMinutes(10))
                return _cached;

            foreach (var endpoint in Endpoints)
            {
                try
                {
                    var text = (await Client.GetStringAsync(endpoint, ct)).Trim();
                    if (IPAddress.TryParse(text, out _))
                    {
                        _cached = text;
                        _lastFetchUtc = DateTime.UtcNow;
                        return _cached;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // try the next provider
                }
            }

            _lastFetchUtc = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }
}
