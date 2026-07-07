using InventoryMonitor.Models;
using Terraria;
using TShockAPI;

namespace InventoryMonitor.Services;

/// <summary>
/// Builds a <see cref="PlayerReport"/> from a live <see cref="TSPlayer"/>. Pure read; must be
/// invoked on the main server thread (callers marshal via the dispatcher).
/// </summary>
public static class InventoryReader
{
    public static PlayerReport BuildReport(TSPlayer tsp, ReportGroups groups)
    {
        var p = tsp.TPlayer;

        var report = new PlayerReport
        {
            Index = tsp.Index,
            Name = tsp.Name,
            Account = tsp.Account?.Name,
            Group = tsp.Group?.Name ?? "",
            Ip = tsp.IP ?? "",
            Position = $"{tsp.TileX},{tsp.TileY}",
            ServerSideCharacter = Main.ServerSideCharacter,
            Stats = new StatsInfo
            {
                Life = p.statLife,
                LifeMax = p.statLifeMax,
                Mana = p.statMana,
                ManaMax = p.statManaMax,
            },
            Buffs = ReadBuffs(p),
        };

        // Group occupied slots into their containers, preserving segment order.
        foreach (var seg in SlotMap.Segments)
        {
            if ((seg.Group & groups) == 0)
                continue;

            var container = new ContainerReport { Name = seg.Name, Group = seg.Group.ToString() };
            var arr = seg.ArrayOf(p);
            if (arr is not null)
            {
                for (int i = 0; i < seg.Count && i < arr.Length; i++)
                {
                    var item = arr[i];
                    if (item is null || !item.active || item.type == 0 || item.stack == 0)
                        continue;

                    container.Items.Add(new SlotEntry
                    {
                        Slot = i,
                        GlobalSlot = seg.Start + i,
                        NetId = item.type,
                        Name = item.Name ?? "",
                        Stack = item.stack,
                        Prefix = item.prefix,
                        PrefixName = PrefixName(item.prefix),
                        Favorited = item.favorited,
                    });
                }
            }

            if (container.Items.Count > 0)
                report.Containers.Add(container);
        }

        return report;
    }

    private static List<BuffEntry> ReadBuffs(Player p)
    {
        var buffs = new List<BuffEntry>();
        for (int i = 0; i < p.buffType.Length; i++)
        {
            int type = p.buffType[i];
            if (type <= 0)
                continue;

            int ticks = p.buffTime[i];
            buffs.Add(new BuffEntry
            {
                Id = type,
                Name = SafeBuffName(type),
                TicksRemaining = ticks,
                SecondsRemaining = ticks < 0 ? -1 : ticks / 60,
            });
        }

        return buffs;
    }

    private static string SafeBuffName(int type)
    {
        try { return Lang.GetBuffName(type) ?? $"Buff {type}"; }
        catch { return $"Buff {type}"; }
    }

    private static string? PrefixName(int prefix)
    {
        if (prefix <= 0)
            return null;

        try
        {
            var table = Lang.prefix;
            if (table is not null && prefix < table.Length)
                return table[prefix]?.Value;
        }
        catch
        {
            // localization not available — fall through
        }

        return null;
    }
}
