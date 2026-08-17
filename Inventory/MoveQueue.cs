using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ZhyraPluginKit.Inventory;

/// <summary>
/// Frame-spaced executor for player-internal <see cref="MoveOp"/>s (e.g. InventoryCleaner's
/// bag&lt;-&gt;armoury moves). One <see cref="Tick"/> per framework frame moves a single item into
/// the first free slot across its <see cref="MoveOp.DstCandidates"/> via
/// <see cref="InventoryManager.MoveItemSlot"/>. State is read live each tick; slot indices are never
/// cached. <see cref="Abort"/> hard-stops mid-run.
///
/// The <c>reverse</c> flag passed to <see cref="Enqueue"/> is scoped once per enqueue/run (a
/// queue-level parameter, not a per-<see cref="MoveOp"/> flag): it gates the
/// <see cref="InventoryScan.FreeSlotsInBag"/> pre-check in <see cref="Tick"/> (runs only when
/// <c>false</c>) and selects between a generic "no destination slot" abort message and a
/// destination-container-specific one — mirroring InventoryCleaner's original
/// <c>Execution/MoveQueue.cs</c> <c>Enqueue(IEnumerable&lt;MovePlan&gt; plans, bool reverse = false)</c>
/// behavior exactly.
///
/// Shared across plugins via linked source:
///   &lt;Compile Include="..\..\ZhyraPluginKit\Inventory\MoveQueue.cs" Link="Kit\Inventory\MoveQueue.cs" /&gt;
/// </summary>
public sealed class MoveQueue
{
    private readonly Queue<MoveOp> queue = new();
    private bool isReverse;
    private string destinationNoun = DefaultDestinationNoun;

    private const string DefaultDestinationNoun = "Destination container";

    /// <summary>Ops not yet executed this run.</summary>
    public int Pending => this.queue.Count;

    /// <summary>Successfully moved this run.</summary>
    public int Done { get; private set; }

    /// <summary>Failed moves this run.</summary>
    public int Failed { get; private set; }

    /// <summary>The op currently being processed (most recently dequeued), null when idle.</summary>
    public MoveOp? Current { get; private set; }

    /// <summary>True while there are ops to execute or one is in flight.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Resets all run state and loads <paramref name="ops"/> as the new work set. Sets
    /// <see cref="IsRunning"/> to true even if <paramref name="ops"/> is empty; the next
    /// <see cref="Tick"/> will immediately transition back to idle. When <paramref name="reverse"/>
    /// is true, the <see cref="InventoryScan.FreeSlotsInBag"/> pre-check in <see cref="Tick"/> is
    /// skipped and a full destination container aborts with a container-specific message instead of
    /// the generic main-inventory one. <paramref name="destinationNoun"/> is the leading noun of that
    /// container-specific abort message; a consumer passes its own wording (e.g. InventoryCleaner
    /// passes "Armoury container" to reproduce its exact shipped chat text) without editing this
    /// shared file.
    /// </summary>
    public void Enqueue(IEnumerable<MoveOp> ops, bool reverse = false, string destinationNoun = DefaultDestinationNoun)
    {
        this.queue.Clear();
        foreach (var op in ops)
            this.queue.Enqueue(op);
        this.Done = 0;
        this.Failed = 0;
        this.Current = null;
        this.isReverse = reverse;
        this.destinationNoun = destinationNoun;
        this.IsRunning = true;
    }

    /// <summary>
    /// Executes at most one move. Returns true if a move was attempted (or the queue still has
    /// pending work), false when the queue has drained and the run is over. Aborts with a chat
    /// message if the destination has no free slot before a dequeue, or — non-reverse mode only —
    /// if the main inventory has no free slots before a dequeue.
    /// </summary>
    public unsafe bool Tick()
    {
        if (!this.IsRunning)
            return false;

        if (this.queue.Count == 0)
        {
            this.IsRunning = false;
            this.Current = null;
            return false;
        }

        if (!this.isReverse && InventoryScan.FreeSlotsInBag() <= 0)
        {
            KitServices.Chat.Print($"{KitServices.LogPrefix} No free main inventory slots; aborting.");
            this.queue.Clear();
            this.IsRunning = false;
            this.Current = null;
            return false;
        }

        var op = this.queue.Dequeue();
        this.Current = op;

        var dst = InventoryScan.FindFreeSlot(op.DstCandidates);
        if (dst == null)
        {
            this.Failed++;
            if (this.isReverse)
                KitServices.Chat.Print($"{KitServices.LogPrefix} {this.destinationNoun} {DescribeCandidates(op.DstCandidates)} full; aborting.");
            else
                KitServices.Chat.Print($"{KitServices.LogPrefix} No free main inventory slot found; aborting.");
            this.queue.Clear();
            this.IsRunning = false;
            return false;
        }

        var mgr = InventoryManager.Instance();
        if (mgr == null)
        {
            this.Failed++;
            return true;
        }

        var ret = mgr->MoveItemSlot(
            op.SrcType,
            op.SrcSlot,
            dst.Value.Type,
            (ushort)dst.Value.Slot,
            true);
        KitServices.Log.Information($"MoveItemSlot ret={ret} item={op.ItemId} {op.SrcType}:{op.SrcSlot} -> {dst.Value.Type}:{dst.Value.Slot}");
        if (ret != 0)
            this.Failed++;
        else
            this.Done++;

        return true;
    }

    /// <summary>Hard-stops the current run: clears the queue and resets <see cref="IsRunning"/>.</summary>
    public void Abort()
    {
        this.queue.Clear();
        this.IsRunning = false;
        this.Current = null;
        this.isReverse = false;
        this.destinationNoun = DefaultDestinationNoun;
    }

    private static string DescribeCandidates(IReadOnlyList<InventoryType> candidates)
        => string.Join(",", candidates);
}
