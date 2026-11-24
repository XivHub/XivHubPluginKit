using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XivHubPluginKit.License;

/// <summary>Where a licensed plugin stands with gamba.xivhub.net.</summary>
public enum LicenseStatus
{
    /// <summary>No character is logged in yet, or the first answer has not arrived.</summary>
    Checking,
    /// <summary>The server granted this character just now.</summary>
    Licensed,
    /// <summary>The server is unreachable; a cached grant for this character is still in date.</summary>
    Offline,
    /// <summary>The server refused; <see cref="LicenseState.Message"/> says why, in its words.</summary>
    Refused,
    /// <summary>The server is unreachable and no cached grant covers this character.</summary>
    Unreachable,
    /// <summary>The plugin was not installed from a gamba repo URL, so it has no token.</summary>
    NoToken,
}

/// <summary>One immutable reading of the license, safe to read from the draw thread.</summary>
public sealed record LicenseState(LicenseStatus Status, LicenseGrant? Grant, string Message)
{
    /// <summary>Whether the plugin may run its licensed features right now.</summary>
    public bool Allows => Status is LicenseStatus.Licensed or LicenseStatus.Offline;
}

/// <summary>
/// The client side of gamba licensing, for any plugin sold through gamba.xivhub.net.
///
/// The repo token comes from the URL the plugin was installed from
/// (<see cref="IDalamudPluginInterface.SourceRepository"/>), so a dealer never types a key; a dev
/// build passes one in instead. The character is identified by
/// <see cref="LicenseIdentity.CidHash"/>. On login and every hour it asks
/// <c>/api/license/activate</c>; a granted answer is cached in the plugin's config folder, so the
/// plugin still starts while the server is down, until the grant's <c>until</c>. Features that talk
/// to the server (table sessions) need the live server regardless; this only gates the plugin.
///
/// The character is read on the framework thread; the HTTP call runs off it. <see cref="State"/>
/// is replaced whole, never mutated, so the UI reads a consistent snapshot without locking.
/// </summary>
public sealed class GambaLicense : IDisposable
{
    public const string DefaultServer = "https://gamba.xivhub.net";

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private const string CacheFile = "gamba-grant.json";

    private readonly IPlayerState player;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly string pluginName;
    private readonly string pluginVersion;
    private readonly string publicKey;
    private readonly string server;
    private readonly string? token;
    private readonly string cachePath;
    private readonly HttpClient http;
    private readonly CancellationTokenSource cancel = new();

    private DateTime nextCheck = DateTime.MinValue;
    private int checking;

    /// <summary>The current reading; replaced as a whole on every change.</summary>
    public LicenseState State { get; private set; }

    /// <param name="publicKeySpki">The server's grant key, base64 SubjectPublicKeyInfo DER, as
    /// <c>gamba-server public-key</c> prints it. Embedded in the plugin.</param>
    /// <param name="tokenOverride">A repo token for builds not installed from a gamba repo URL (dev
    /// builds). Null or empty uses the install's own URL.</param>
    public GambaLicense(
        IDalamudPluginInterface pluginInterface,
        IPlayerState player,
        IClientState clientState,
        IFramework framework,
        IPluginLog log,
        string publicKeySpki,
        string? tokenOverride = null,
        string server = DefaultServer)
    {
        this.player = player;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;
        publicKey = publicKeySpki;
        this.server = server.TrimEnd('/');
        pluginName = pluginInterface.InternalName;
        pluginVersion = pluginInterface.Manifest.AssemblyVersion.ToString();
        cachePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, CacheFile);
        token = string.IsNullOrEmpty(tokenOverride)
            ? LicenseIdentity.TokenFromRepoUrl(pluginInterface.SourceRepository)
            : tokenOverride;
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        State = token is null
            ? new(LicenseStatus.NoToken, null,
                "Install this plugin from your gamba repo URL to use it.")
            : new(LicenseStatus.Checking, null, "Checking the license.");

        clientState.Login += OnLogin;
        framework.Update += OnUpdate;
    }

    /// <summary>Asks the server again on the next frame, for a "Check again" button.</summary>
    public void Refresh() => nextCheck = DateTime.MinValue;

    private void OnLogin() => Refresh();

    private void OnUpdate(IFramework fw)
    {
        if (token is null || DateTime.UtcNow < nextCheck || !player.IsLoaded || player.ContentId == 0)
            return;
        if (Interlocked.Exchange(ref checking, 1) == 1)
            return;
        nextCheck = DateTime.UtcNow + RefreshEvery;

        // Game state is read here, on the framework thread; only plain values leave it.
        var request = new ActivateRequest(
            token,
            LicenseIdentity.CidHash(player.ContentId),
            $"{player.CharacterName}@{player.HomeWorld.Value.Name.ExtractText()}",
            pluginName,
            pluginVersion);
        _ = Task.Run(() => CheckAsync(request), cancel.Token);
    }

    private async Task CheckAsync(ActivateRequest request)
    {
        try
        {
            var next = await AskServerAsync(request);
            if (next.Status is LicenseStatus.Unreachable or LicenseStatus.Offline)
                nextCheck = DateTime.UtcNow + RetryAfterFailure;
            if (next.Status != State.Status || next.Message != State.Message)
                log.Info($"[GambaLicense] {next.Status}: {next.Message}");
            State = next;
        }
        catch (Exception e)
        {
            log.Error(e, "[GambaLicense] check failed");
            nextCheck = DateTime.UtcNow + RetryAfterFailure;
        }
        finally
        {
            Interlocked.Exchange(ref checking, 0);
        }
    }

    private async Task<LicenseState> AskServerAsync(ActivateRequest request)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync($"{server}/api/license/activate", request, Wire, cancel.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.Warning($"[GambaLicense] {server} unreachable: {e.Message}");
            return FromCache(request.CidHash);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var signed = await response.Content.ReadFromJsonAsync<SignedGrant>(cancel.Token);
                var grant = signed is null ? null : GrantVerifier.Verify(signed, publicKey);
                if (grant is null || !grant.Covers(request.CidHash, Now))
                    return new(LicenseStatus.Refused, null,
                        "The server's answer did not verify. Update the plugin.");
                Save(signed!);
                return new(LicenseStatus.Licensed, grant, $"Licensed for {grant.NameWorld}.");
            }
            if ((int)response.StatusCode >= 500)
                return FromCache(request.CidHash);

            // A refusal ends any cached grant: the server has said no for this character.
            Forget();
            var message = await ReadErrorAsync(response);
            return new(LicenseStatus.Refused, null, message);
        }
    }

    private LicenseState FromCache(string cidHash)
    {
        var grant = Load();
        return grant is not null && grant.Covers(cidHash, Now)
            ? new(LicenseStatus.Offline, grant,
                $"gamba.xivhub.net is unreachable; licensed offline until {Local(grant.Until)}.")
            : new(LicenseStatus.Unreachable, null,
                "gamba.xivhub.net is unreachable, and there is no recent license check to fall back on.");
    }

    private async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancel.Token);
            if (!string.IsNullOrEmpty(body?.Error))
                return body.Error;
        }
        catch (JsonException) { }
        return $"The license server refused ({(int)response.StatusCode}).";
    }

    private LicenseGrant? Load()
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            var signed = JsonSerializer.Deserialize<SignedGrant>(File.ReadAllText(cachePath));
            // Re-verified on every read, so an edited cache file grants nothing.
            return signed is null ? null : GrantVerifier.Verify(signed, publicKey);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(SignedGrant signed)
    {
        try
        {
            File.WriteAllText(cachePath, JsonSerializer.Serialize(signed));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning($"[GambaLicense] could not cache the grant: {e.Message}");
        }
    }

    private void Forget()
    {
        try
        {
            File.Delete(cachePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Local(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("d MMM HH:mm");

    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record ActivateRequest(
        string Token, string CidHash, string NameWorld, string Plugin, string Version);

    private sealed record ErrorBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        framework.Update -= OnUpdate;
        cancel.Cancel();
        cancel.Dispose();
        http.Dispose();
    }
}
