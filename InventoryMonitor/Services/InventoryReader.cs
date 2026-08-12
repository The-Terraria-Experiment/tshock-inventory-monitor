using InventoryMonitor.Models;
using Terraria;
using TShockAPI;

namespace InventoryMonitor.Services;

/// <summary>
/// Identifying fields for a report, resolved separately from the item data so a report can be
/// built for a <see cref="Player"/> whose <see cref="TSPlayer"/> wrapper is already gone.
/// </summary>
public readonly record struct PlayerIdentity(
    int Index, string Name, string? Account, string Group, string Ip, string Position);

/// <summary>
/// Builds a <see cref="PlayerReport"/> from a live player. Pure read: it allocates a fresh object
/// graph and mutates nothing.
///
/// Normally called on the main server thread (REST callers marshal via the dispatcher). The one
/// exception is leave-snapshot capture, which must run inline on the server loop thread because
/// <c>RemoteClient.Reset()</c> replaces <c>Main.player[i]</c> the moment the hook returns — see
/// <see cref="SnapshotService.OnPlayerLeaving"/>.
/// </summary>
public static class InventoryReader
{
    public static PlayerIdentity IdentityOf(TSPlayer tsp) => new(
        tsp.Index,
        tsp.Name,
        tsp.Account?.Name,
        tsp.Group?.Name ?? "",
        tsp.IP ?? "",
        $"{tsp.TileX},{tsp.TileY}");

    /// <summary>Fallback identity for a raw <see cref="Player"/> with no TShock wrapper available.</summary>
    public static PlayerIdentity IdentityOf(Player p, int index) => new(
        index,
        p.name ?? "",
        null,
        "",
        "",
        $"{(int)(p.position.X / 16f)},{(int)(p.position.Y / 16f)}");

    public static PlayerReport BuildReport(TSPlayer tsp, ReportGroups groups) =>
        BuildReport(tsp.TPlayer, IdentityOf(tsp), groups);

    public static PlayerReport BuildReport(Player p, PlayerIdentity identity, ReportGroups groups)
    {
        var report = new PlayerReport
        {
            Index = identity.Index,
            Name = identity.Name,
            Account = identity.Account,
            Group = identity.Group,
            Ip = identity.Ip,
            Position = identity.Position,
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

    /// <summary>
    /// Narrows an already-built report to a subset of container groups. Used to serve cached
    /// snapshots (always captured in full) against a caller's <c>include=</c> filter.
    /// </summary>
    public static PlayerReport Project(PlayerReport report, ReportGroups groups)
    {
        if (groups == ReportGroups.All)
            return report;

        return new PlayerReport
        {
            Index = report.Index,
            Name = report.Name,
            Account = report.Account,
            Group = report.Group,
            Ip = report.Ip,
            Position = report.Position,
            ServerSideCharacter = report.ServerSideCharacter,
            Stats = report.Stats,
            Buffs = report.Buffs,
            Containers = report.Containers
                .Where(c => Enum.TryParse<ReportGroups>(c.Group, out var g) && (g & groups) != 0)
                .ToList(),
        };
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
