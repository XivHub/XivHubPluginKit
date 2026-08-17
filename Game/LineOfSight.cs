using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace ZhyraPluginKit.Game;

/// <summary>Collision raycast between the player and a target, the same check the game uses to decide
/// whether an action can be fired. A ranged job stops at its attack range, so terrain between it and
/// the mob has to be walked around instead of shot through.</summary>
public static class LineOfSight
{
    /// <summary>True when nothing blocks the line between the player and <paramref name="target"/>.</summary>
    public static unsafe bool Clear(IGameObject target)
    {
        if (!Player.Available)
            return true;

        // Cast at chest/eye height: a ray along the ground clips terrain neither party is blocked by.
        var p = Player.Position;
        var t = target.Position;
        var source = new Vector3(p.X, p.Y + 2f, p.Z);
        var dest = new Vector3(t.X, t.Y + 2f, t.Z);

        var direction = dest - source;
        var distance = direction.Length();
        if (distance < 0.1f)
            return true;
        direction /= distance;

        RaycastHit hit;
        int* flags = stackalloc int[] { 0x4000, 0, 0x4000, 0 };
        return !Framework.Instance()->BGCollisionModule->RaycastMaterialFilter(
            &hit, &source, &direction, distance, 1, flags);
    }
}
