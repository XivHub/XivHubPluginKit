using System.Collections.Generic;
using XivHubPluginKit.Board;
using XivHubPluginKit.Board.Buying;

namespace XivHubPluginKit.Tests;

public class ListingSlotTests
{
    // Session 108ba2790349: the potion bought from slot 2 (seq 134, 137, 150) and the listing
    // that moved into slot 2 once it left (seq 156).
    private const uint Potion = 4551;
    private static readonly BoardListing Bought = new(6825769020545315, 33777097237323918, Potion, 23, 1, 1, false);
    private static readonly BoardListing Next = new(6825768215710814, 33777197237003066, Potion, 33, 27, 44, false);
    private static readonly BoardListing First = new(101, 201, Potion, 20, 1, 1, false);
    private static readonly BoardListing Second = new(102, 202, Potion, 22, 1, 1, false);

    private static BoardSnapshot Read(params BoardListing[] listings) =>
        new(Potion, Potion, 64, listings.Length, (uint)listings.Length, listings);

    [Fact]
    public void FindsTheListingById()
    {
        var slot = ListingSlot.Find(new List<BoardListing> { First, Second, Bought, Next }, Bought);

        Assert.Equal(new SlotRead(SlotState.Found, 2), slot);
    }

    [Fact]
    public void AMissingListingIsGone()
    {
        var slot = ListingSlot.Find(new List<BoardListing> { First, Second, Next }, Bought);

        Assert.Equal(new SlotRead(SlotState.Gone, -1), slot);
    }

    [Theory]
    [InlineData(24, 1, 1)]
    [InlineData(23, 2, 1)]
    [InlineData(23, 1, 2)]
    public void APriceQuantityOrFeeChangeIsChanged(long unitPrice, int qty, long fee)
    {
        var now = Bought with { UnitPrice = unitPrice, Qty = qty, TotalTax = fee };

        var slot = ListingSlot.Find(new List<BoardListing> { First, Second, now }, Bought);

        Assert.Equal(new SlotRead(SlotState.Changed, 2), slot);
    }

    [Fact]
    public void ConfirmBuysTheRecordedSlot()
    {
        Assert.True(ListingSlot.ConfirmBuys(Read(First, Second, Bought, Next), Bought, 2, 2));
    }

    [Fact]
    public void AListingThatShiftedDownIsNotBought()
    {
        // Someone bought slot 0 between the click and Yes: the listing now sits in slot 1, and
        // slot 2, the one Yes buys, holds the next listing.
        Assert.False(ListingSlot.ConfirmBuys(Read(Second, Bought, Next), Bought, 2, 2));
    }

    [Fact]
    public void AListingGoneBeforeYesIsNotBought()
    {
        Assert.False(ListingSlot.ConfirmBuys(Read(First, Second, Next), Bought, 2, 2));
    }

    [Fact]
    public void AChangedListingIsNotBought()
    {
        Assert.False(ListingSlot.ConfirmBuys(Read(First, Second, Bought with { UnitPrice = 30 }, Next), Bought, 2, 2));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void ASelectedIndexOnAnotherSlotIsNotBought(uint selected)
    {
        Assert.False(ListingSlot.ConfirmBuys(Read(First, Second, Bought, Next), Bought, 2, selected));
    }

    [Fact]
    public void AnUnreadableSelectedIndexIsNotBought()
    {
        Assert.False(ListingSlot.ConfirmBuys(Read(First, Second, Bought, Next), Bought, 2, null));
    }

    [Fact]
    public void ASlotAtOrPastEntryCountIsNotBought()
    {
        // The game refuses an index at or past EntryCount and sends the previous purchase.
        var read = Read(First, Second, Bought, Next) with { EntryCount = 2 };

        Assert.False(ListingSlot.ConfirmBuys(read, Bought, 2, 2));
    }

    [Fact]
    public void AnotherItemOrNoReadIsNotBought()
    {
        var other = Read(First, Second, Bought, Next) with { ResultItemId = 4552 };

        Assert.False(ListingSlot.ConfirmBuys(other, Bought, 2, 2));
        Assert.False(ListingSlot.ConfirmBuys(null, Bought, 2, 2));
    }

    [Fact]
    public void AnUnclickedListingIsNotBought()
    {
        Assert.False(ListingSlot.ConfirmBuys(Read(First, Second, Bought, Next), Bought, -1, 2));
    }
}
