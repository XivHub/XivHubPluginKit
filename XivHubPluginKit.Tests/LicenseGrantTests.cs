using System.Linq;
using XivHubPluginKit.License;
using Xunit;

namespace XivHubPluginKit.Tests;

public class LicenseGrantTests
{
    // Signed by gamba-server with a throwaway key
    // (`cargo test print_fixture_for_the_csharp_client -- --ignored --nocapture`), so this proves
    // the two sides agree on key encoding, signature format and field names.
    private const string Key =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJgBRmOAvj3q4DVap8JwllV014u365lmjj4gZTXi20kiKQlczxokyQEtd4wDcrRO0bPV6L6xx2gLC7g82afH3sw==";
    private const string Payload =
        "{\"v\":1,\"license_id\":7,\"cid_hash\":\"abababababababababababababababababababababababababababababababab\",\"name_world\":\"Zhyra Valen@Cactuar\",\"plugin\":\"XivHubDealer\",\"license_expires_at\":2000,\"issued_at\":1000,\"until\":1500}";
    private const string Signature =
        "cp7qeN8MqTrxeSEHFej/qksQ69m4Zs34+2P1C+0OvsptdvidyR8IvKAH9vriWorUxn/zzsWIoVEV0sSkCkCqFQ==";

    [Fact]
    public void VerifiesAServerSignedGrant()
    {
        var grant = GrantVerifier.Verify(new SignedGrant(Payload, Signature), Key);
        Assert.NotNull(grant);
        Assert.Equal(7, grant.LicenseId);
        Assert.Equal("Zhyra Valen@Cactuar", grant.NameWorld);
        Assert.Equal("XivHubDealer", grant.Plugin);
        Assert.Equal(1500, grant.Until);
        var cid = string.Concat(Enumerable.Repeat("ab", 32));
        Assert.Equal(cid, grant.CidHash);
        Assert.True(grant.Covers(cid, 1499));
        Assert.False(grant.Covers(grant.CidHash, 1500));
        Assert.False(grant.Covers(new string('c', 64), 1000));
    }

    [Fact]
    public void RefusesATamperedPayloadOrSignature()
    {
        var tampered = Payload.Replace("\"until\":1500", "\"until\":9999");
        Assert.Null(GrantVerifier.Verify(new SignedGrant(tampered, Signature), Key));
        var badSig = "A" + Signature[1..];
        Assert.Null(GrantVerifier.Verify(new SignedGrant(Payload, badSig), Key));
        Assert.Null(GrantVerifier.Verify(new SignedGrant(Payload, "not base64!"), Key));
    }

    [Fact]
    public void CidHashIsLowercaseHexOfThePrefixedId()
    {
        var hash = LicenseIdentity.CidHash(18014398509481984UL);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, LicenseIdentity.CidHash(18014398509481984UL));
        Assert.NotEqual(hash, LicenseIdentity.CidHash(18014398509481985UL));
    }

    [Theory]
    [InlineData("https://gamba.xivhub.net/repo/pluginmaster.json?token=abc-_123", "abc-_123")]
    [InlineData("https://gamba.xivhub.net/repo/pluginmaster.json?x=1&token=t%2Bk", "t+k")]
    [InlineData("https://plugins.xivhub.net/pluginmaster.json", null)]
    [InlineData("https://gamba.xivhub.net/repo/pluginmaster.json?token=", null)]
    [InlineData("DevPlugin", null)]
    [InlineData(null, null)]
    public void ReadsTheTokenFromTheRepoUrl(string? url, string? token)
    {
        Assert.Equal(token, LicenseIdentity.TokenFromRepoUrl(url));
    }
}
