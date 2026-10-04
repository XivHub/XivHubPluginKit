namespace XivHubPluginKit.Retainer;

/// <summary>
/// A single retainer market slot row from a live memory read
/// (<see cref="RetainerMarket.ReadLive"/>). Kept free of game types so the
/// test project can compile the Pinch models that carry it.
/// </summary>
public readonly record struct RetainerMarketRow(uint Slot, uint ItemId, bool Hq, uint Qty, uint Price);
