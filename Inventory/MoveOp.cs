using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ZhyraPluginKit.Inventory;

/// <summary>
/// A single generic player-internal move unit consumed by the kit <see cref="MoveQueue"/>
/// (<c>InventoryManager.MoveItemSlot</c>-based). Not used by plugins that move items via a native
/// game command instead of <c>MoveItemSlot</c> (e.g. retainer withdrawal).
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\ZhyraPluginKit\Inventory\MoveOp.cs" Link="Kit\Inventory\MoveOp.cs" /&gt;
/// </summary>
public readonly record struct MoveOp(
    InventoryType SrcType,
    ushort SrcSlot,
    uint ItemId,
    IReadOnlyList<InventoryType> DstCandidates);
