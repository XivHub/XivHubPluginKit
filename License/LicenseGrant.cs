using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivHubPluginKit.License;

/// <summary>
/// What gamba.xivhub.net signs when a character may run a licensed plugin. Field names follow the
/// server's JSON (<c>gamba-server/src/grant.rs</c>, <c>GrantPayload</c>); times are unix seconds.
/// </summary>
public sealed record LicenseGrant(
    int V,
    long LicenseId,
    string CidHash,
    string NameWorld,
    string Plugin,
    long LicenseExpiresAt,
    long IssuedAt,
    long Until)
{
    /// <summary>Whether this grant lets the character with <paramref name="cidHash"/> run now.</summary>
    public bool Covers(string cidHash, long now) => CidHash == cidHash && now < Until;
}

/// <summary>The server's answer: the payload exactly as signed, and its signature.</summary>
public sealed record SignedGrant(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>
/// Checks grants against the server's public key. ECDSA P-256 over SHA-256 with a raw
/// <c>r || s</c> signature, which <see cref="ECDsa"/> verifies with nothing beyond the base library.
/// The signature covers the payload string's UTF-8 bytes as sent, so no JSON is re-serialised.
/// </summary>
public static class GrantVerifier
{
    /// <summary>The grant format this client understands.</summary>
    public const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The grant inside <paramref name="signed"/>, or null when the signature does not verify
    /// against <paramref name="publicKeySpki"/> (base64 SubjectPublicKeyInfo DER, from
    /// <c>gamba-server public-key</c>), the payload does not parse, or its version is unknown.
    /// </summary>
    public static LicenseGrant? Verify(SignedGrant signed, string publicKeySpki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            var signature = Convert.FromBase64String(signed.Signature);
            var payload = Encoding.UTF8.GetBytes(signed.Payload);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256))
                return null;
            var grant = JsonSerializer.Deserialize<LicenseGrant>(signed.Payload, Json);
            return grant is { V: Version } ? grant : null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>How a licensed plugin names itself and its character to the server.</summary>
public static class LicenseIdentity
{
    /// <summary>
    /// The character as the server stores it: lowercase hex SHA-256 of <c>"gamba:" + ContentId</c>.
    /// The server never sees the ContentId itself.
    /// </summary>
    public static string CidHash(ulong contentId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("gamba:" + contentId));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// The <c>token</c> query parameter of the repo URL the plugin was installed from
    /// (<c>IDalamudPluginInterface.SourceRepository</c>), or null when there is none: a dev build,
    /// or an install from anywhere but a gamba repo URL.
    /// </summary>
    public static string? TokenFromRepoUrl(string? repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "token")
            {
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }
}
