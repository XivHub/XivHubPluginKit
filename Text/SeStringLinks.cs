using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace XivHubPluginKit.Text;

/// <summary>
/// Reads link macros from game text through Lumina's <see cref="ReadOnlySeStringSpan"/>, the type
/// chat text has on both Dalamud API 15 (<c>IChatMessage.OriginalSender</c>/<c>OriginalMessage</c>)
/// and API 16 (every chat and object-name member). Dalamud's <c>ItemPayload</c> and
/// <c>PlayerPayload</c> do not exist on API 16.
/// </summary>
public static class SeStringLinks
{
    /// <summary>
    /// An item link is a <c>Link</c> macro whose expressions are (<see cref="LinkMacroPayloadType.Item"/>,
    /// raw item id, rarity, …). The raw id keeps the HQ, collectable and event-item offsets;
    /// <c>Dalamud.Utility.ItemUtil.GetBaseId</c> splits them.
    /// </summary>
    public static bool TryGetItemLink(ReadOnlySePayloadSpan payload, out uint rawItemId)
    {
        rawItemId = 0;
        return payload.Type == ReadOnlySePayloadType.Macro
            && payload.MacroCode == MacroCode.Link
            && payload.TryGetExpression(out var linkType, out var idExpr)
            && linkType.TryGetUInt(out var type)
            && type == (uint)LinkMacroPayloadType.Item
            && idExpr.TryGetUInt(out rawItemId);
    }

    /// <summary>
    /// The first player link in <paramref name="text"/>. Its expressions are
    /// (<see cref="LinkMacroPayloadType.Character"/>, home world id, unused, full name); the visible
    /// text after it may be abbreviated by the client's name setting, the name expression never is.
    /// </summary>
    public static bool TryGetPlayer(ReadOnlySeStringSpan text, out string name, out uint worldId)
    {
        foreach (var payload in text)
        {
            if (payload.Type != ReadOnlySePayloadType.Macro || payload.MacroCode != MacroCode.Link)
                continue;
            if (!payload.TryGetExpression(out var linkType, out var worldExpr, out _, out var nameExpr)
                || !linkType.TryGetUInt(out var type) || type != (uint)LinkMacroPayloadType.Character)
                continue;
            if (worldExpr.TryGetUInt(out worldId) && nameExpr.TryGetString(out var nameSpan))
            {
                name = nameSpan.ExtractText();
                return true;
            }
        }

        name = string.Empty;
        worldId = 0;
        return false;
    }
}
