using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XivHubPluginKit.License;

/// <summary>A look the customer saved in the dealer portal; <see cref="Venue"/> is the name tables show while it is worn, empty for the page's name.</summary>
public sealed record SavedLook(long Id, string Name, string Venue, bool Worn);

/// <summary>The server refused or could not be reached; <see cref="Exception.Message"/> is for the dealer.</summary>
public sealed class GambaLooksException(string message) : Exception(message);

/// <summary>
/// The looks a licensed customer saved on gamba.xivhub.net, listed and worn from the plugin, so a dealer switches
/// their page and tables to a club's look from the game. Who may ask is checked as for <see cref="GambaSave"/>.
/// Server side: <c>gamba-server/src/looks_api.rs</c>.
/// </summary>
/// <para>BCL only, so it runs (and is tested) outside the game. Every member may be called from any thread.</para>
public sealed class GambaLooks : IDisposable
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNameCaseInsensitive = true };

    private readonly string server;
    private readonly string token;
    private readonly string plugin;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The character asking.</summary>
    public string CidHash { get; }

    /// <param name="server">The gamba server's https origin, as <see cref="GambaLicense"/> uses it.</param>
    public GambaLooks(string server, string token, string cidHash, string plugin)
    {
        this.server = server.TrimEnd('/');
        this.token = token;
        this.plugin = plugin;
        CidHash = cidHash;
    }

    /// <summary>The saved looks, by name; at most one is worn.</summary>
    /// <exception cref="GambaLooksException">The server refused, or could not be reached.</exception>
    public Task<IReadOnlyList<SavedLook>> ListAsync(CancellationToken cancel = default) => PostAsync("/api/looks", null, cancel);

    /// <summary>Wears look <paramref name="id"/> on the customer's page and every table, and answers with the list.</summary>
    /// <exception cref="GambaLooksException">The server refused, or could not be reached.</exception>
    public Task<IReadOnlyList<SavedLook>> WearAsync(long id, CancellationToken cancel = default) => PostAsync("/api/looks/wear", id, cancel);

    private async Task<IReadOnlyList<SavedLook>> PostAsync(string path, long? id, CancellationToken cancel)
    {
        var sb = new StringBuilder();
        sb.Append("{\"token\":").Append(JsonSerializer.Serialize(token))
            .Append(",\"cid_hash\":").Append(JsonSerializer.Serialize(CidHash))
            .Append(",\"plugin\":").Append(JsonSerializer.Serialize(plugin));
        if (id is { } look) sb.Append(",\"id\":").Append(look);
        sb.Append('}');
        string body;
        bool ok;
        int status;
        try
        {
            using var response = await http.PostAsync($"{server}{path}", new StringContent(sb.ToString(), Encoding.UTF8, "application/json"), cancel);
            body = await response.Content.ReadAsStringAsync(cancel);
            ok = response.IsSuccessStatusCode;
            status = (int)response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new GambaLooksException($"gamba.xivhub.net is unreachable: {e.Message}");
        }
        try
        {
            if (ok) return JsonSerializer.Deserialize<List<SavedLook>>(body, Wire) ?? [];
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } message)
                throw new GambaLooksException(message);
        }
        catch (JsonException) { }
        throw new GambaLooksException($"gamba.xivhub.net could not answer (error {status}).");
    }

    public void Dispose() => http.Dispose();
}
