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
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENSsPPL+HWY19kob1AiiRpbf5+NzXzW+GMUC6rELguZJTTFTfgzKa5vS2VWDbXBEo1o4tCV/n23rCvylOQfG7aQ==";
    private const string Payload =
        "{\"v\":2,\"cid_hash\":\"abababababababababababababababababababababababababababababababab\",\"name_world\":\"Zhyra Valen@Cactuar\",\"plugin\":\"CrystalTable\",\"issued_at\":1000,\"until\":1500,\"games\":[{\"game\":\"blackjack\",\"license_id\":7,\"expires_at\":2000},{\"game\":\"roulette\",\"license_id\":8,\"expires_at\":null}]}";
    private const string Signature =
        "fpooK3DEpuJGXqwcA+rVATjO784EdFw5ucGcw//+7J2wRbhAu7VCeq89ygb0lk5hT4aiy6WBFmya07LeVpKOIw==";

    [Fact]
    public void VerifiesAServerSignedGrant()
    {
        var grant = GrantVerifier.Verify(new SignedGrant(Payload, Signature), Key);
        Assert.NotNull(grant);
        Assert.Equal("Zhyra Valen@Cactuar", grant.NameWorld);
        Assert.Equal(2, grant.Games.Count);
        Assert.Equal(new GameGrant("blackjack", 7, 2000), grant.Games[0]);
        Assert.Null(grant.Games[1].ExpiresAt);
        Assert.Equal("CrystalTable", grant.Plugin);
        Assert.Equal(1500, grant.Until);
        var cid = string.Concat(Enumerable.Repeat("ab", 32));
        Assert.Equal(cid, grant.CidHash);
        Assert.True(grant.Covers(cid, 1499));
        Assert.False(grant.Covers(grant.CidHash, 1500));
        Assert.False(grant.Covers(new string('c', 64), 1000));

        Assert.True(grant.AllowsGame("blackjack", cid, 1499));
        Assert.True(grant.AllowsGame("roulette", cid, 1499));
        Assert.False(grant.AllowsGame("poker", cid, 1000));
        Assert.False(grant.AllowsGame("roulette", cid, 1500), "the grant itself ended");
    }

    [Fact]
    public void AGameEndsAtItsOwnDateWithinALiveGrant()
    {
        var grant = new LicenseGrant(2, "h", "A B@C", "P", 0, 10_000,
            [new GameGrant("blackjack", 1, 100), new GameGrant("roulette", 2, null)]);
        Assert.False(grant.AllowsGame("blackjack", "h", 100));
        Assert.True(grant.AllowsGame("roulette", "h", 9_999));
    }

    [Fact]
    public void RefusesATamperedPayloadOrSignature()
    {
        var tampered = Payload.Replace("\"until\":1500", "\"until\":9999");
        Assert.NotEqual(Payload, tampered);
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
