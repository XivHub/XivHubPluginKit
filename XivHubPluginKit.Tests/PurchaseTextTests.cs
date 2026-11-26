using XivHubPluginKit.Board;
using XivHubPluginKit.Board.Buying;

namespace XivHubPluginKit.Tests;

public class PurchaseTextTests
{
    [Theory]
    [InlineData("Purchase 960 fire shards for 54,432 gil (2,592 fee included)?", 960, 54432, 2592)]
    [InlineData("Purchase 180 fire shards for 10,584 gil (504 fee included)?", 180, 10584, 504)]
    [InlineData("Purchase 120 ice shards for 7,938 gil (378 fee included)?", 120, 7938, 378)]
    public void ParsesTheRecordedPrompts(string text, int qty, long gil, long fee)
    {
        var prompt = PurchaseText.ParsePrompt(text)!.Value;

        Assert.Equal(qty, prompt.Qty);
        Assert.Equal(gil, prompt.Gil);
        Assert.Equal(fee, prompt.Fee);
    }

    [Fact]
    public void ParsesTheRecordedSingleUnitPrompt()
    {
        var prompt = PurchaseText.ParsePrompt(
            "Purchase 1 glade mansion roof (composite) for 5,227 gil (248 fee included)?")!.Value;

        Assert.Equal(1, prompt.Qty);
        Assert.Equal(5227, prompt.Gil);
        Assert.Equal(248, prompt.Fee);
    }

    [Fact]
    public void ParsesTheRecordedSingleUnitPotionPrompt()
    {
        var prompt = PurchaseText.ParsePrompt("Purchase 1 potion for 24 gil (1 fee included)?")!.Value;

        Assert.Equal(1, prompt.Qty);
        Assert.Equal(24, prompt.Gil);
        Assert.Equal(1, prompt.Fee);
    }

    [Fact]
    public void CollapsesLineBreaksAndSpaces()
    {
        var prompt = PurchaseText.ParsePrompt(
            "Purchase   120\nice  shards for 7,938\n gil (378 fee   included)?")!.Value;

        Assert.Equal(120, prompt.Qty);
        Assert.Equal(7938, prompt.Gil);
        Assert.Equal(378, prompt.Fee);
    }

    [Fact]
    public void OtherTextIsNotAPrompt()
    {
        Assert.Null(PurchaseText.ParsePrompt("Retrieve this item from the Market Prohibited Items box?"));
    }

    [Theory]
    [InlineData("Purchase 960 fire shards for 54,432 gil (2,592 fee included)?")]
    [InlineData("  Purchase 1 potion for 24 gil (1 fee included)?")]
    [InlineData("Purchase 3 potions for 999 gil?")]
    public void PurchaseTextIsAPurchasePrompt(string text)
    {
        Assert.True(PurchaseText.IsPurchasePrompt(text));
    }

    [Theory]
    [InlineData("Retrieve this item from the Market Prohibited Items box?")]
    [InlineData("Purchased 3 potions?")]
    [InlineData("")]
    public void OtherTextIsNotAPurchasePrompt(string text)
    {
        Assert.False(PurchaseText.IsPurchasePrompt(text));
    }

    [Fact]
    public void MatchesTheRecordedListing()
    {
        var prompt = new PurchasePrompt(960, 54432, 2592);
        var listing = new BoardListing(1, 1, 2, 54, 960, 2592, false);

        Assert.True(PurchaseText.Matches(prompt, listing));
    }

    [Fact]
    public void QuantityMismatchDoesNotMatch()
    {
        var prompt = new PurchasePrompt(960, 54432, 2592);
        var listing = new BoardListing(1, 1, 2, 54, 959, 2592, false);

        Assert.False(PurchaseText.Matches(prompt, listing));
    }

    [Fact]
    public void GilMismatchDoesNotMatch()
    {
        var prompt = new PurchasePrompt(960, 54432, 2592);
        var listing = new BoardListing(1, 1, 2, 53, 960, 2592, false);

        Assert.False(PurchaseText.Matches(prompt, listing));
    }

    [Fact]
    public void FeeMismatchDoesNotMatch()
    {
        var prompt = new PurchasePrompt(960, 54432, 2592);
        var listing = new BoardListing(1, 1, 2, 54, 960, 2591, false);

        Assert.False(PurchaseText.Matches(prompt, listing));
    }

    [Fact]
    public void ParsesTheRecordedPurchaseLog()
    {
        Assert.Equal(960, PurchaseText.ParseBoughtLog("You purchase 960 fire shards."));
    }

    [Fact]
    public void ParsesThousandsInTheLog()
    {
        Assert.Equal(1000, PurchaseText.ParseBoughtLog("You purchase 1,000 fire shards."));
    }

    [Fact]
    public void ParsesTheRecordedSingleUnitLog()
    {
        Assert.Equal(1, PurchaseText.ParseBoughtLog("You purchase a potion."));
        Assert.Equal(1, PurchaseText.ParseBoughtLog("You purchase an ice shard."));
    }

    [Fact]
    public void OtherLogTextIsNull()
    {
        Assert.Null(PurchaseText.ParseBoughtLog("Natsuki W. uses an MGP platinum card."));
    }

    [Fact]
    public void ParsesTheLivePromptWithTheGilIcon()
    {
        // As the game showed it (Furnisher devlog 2026-09-23 17:04:56): U+E049 after the fee.
        var prompt = PurchaseText.ParsePrompt("Purchase 2 chunks of copper ore for 37 gil (1\uE049 fee included)?");

        Assert.Equal(new PurchasePrompt(2, 37, 1), prompt);
    }

    [Fact]
    public void IconsInTheLogAreIgnored() =>
        Assert.Equal(2, PurchaseText.ParseBoughtLog("You purchase 2 chunks of copper ore\uE049."));

    [Fact]
    public void BothPurchaseLogIdsCount()
    {
        Assert.True(PurchaseText.IsPurchasedLog(734));
        Assert.True(PurchaseText.IsPurchasedLog(735));
        Assert.False(PurchaseText.IsPurchasedLog(501));
    }

    [Fact]
    public void ParsesTheRecordedFurnishingLog() =>
        Assert.Equal(1, PurchaseText.ParseBoughtLog("You purchase a butterfly specimen."));

    [Fact]
    public void ParsesAOneUnitLogWithTheItemsOwnArticle() =>
        Assert.Equal(1, PurchaseText.ParseBoughtLog("You purchase the Bismarck."));

    [Fact]
    public void AZeroFeePromptHasNoFeeClause() =>
        Assert.Equal(new PurchasePrompt(2, 2, 0), PurchaseText.ParsePrompt("Purchase 2 chunks of tin ore for 2 gil?"));
}
