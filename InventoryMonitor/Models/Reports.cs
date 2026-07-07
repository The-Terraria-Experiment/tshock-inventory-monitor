namespace InventoryMonitor.Models;

/// <summary>
/// Which container groups to include in a report or a clear operation. Mirrors the
/// canonical NetItem slot layout, grouped for the report scope decided with the user.
/// </summary>
[Flags]
public enum ReportGroups
{
    None = 0,
    Core = 1,       // main inventory (incl. coins/ammo), armor + accessories, dyes
    Storage = 2,    // piggy bank, safe, defender's forge, void vault, trash
    Misc = 4,       // misc equips (mount/hook/pet/minecart/light pet) + their dyes
    Loadouts = 8,   // the 3 saved equipment loadouts (armor + dyes)
    All = Core | Storage | Misc | Loadouts,
}

/// <summary>A single non-empty item slot in a player's inventory surface.</summary>
public sealed class SlotEntry
{
    /// <summary>Index within its own container (0-based).</summary>
    public int Slot { get; init; }

    /// <summary>Canonical NetItem/PlayerSlot index (0..349) — the value used for removal.</summary>
    public int GlobalSlot { get; init; }

    /// <summary>Terraria item id (netID / type).</summary>
    public int NetId { get; init; }

    public string Name { get; init; } = "";
    public int Stack { get; init; }
    public int Prefix { get; init; }
    public string? PrefixName { get; init; }
    public bool Favorited { get; init; }
}

/// <summary>One named container (e.g. "Inventory", "PiggyBank") and its occupied slots.</summary>
public sealed class ContainerReport
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public List<SlotEntry> Items { get; init; } = new();
}

/// <summary>An active buff / status effect on a player.</summary>
public sealed class BuffEntry
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Remaining duration in game ticks. -1 indicates an effectively permanent buff.</summary>
    public int TicksRemaining { get; init; }

    public int SecondsRemaining { get; init; }
}

/// <summary>Core player stats.</summary>
public sealed class StatsInfo
{
    public int Life { get; init; }
    public int LifeMax { get; init; }
    public int Mana { get; init; }
    public int ManaMax { get; init; }
}

/// <summary>Full monitoring report for a single player.</summary>
public sealed class PlayerReport
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string? Account { get; init; }
    public string Group { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Position { get; init; } = "";

    /// <summary>True if the server owns inventories (removals persist to the TShock DB).</summary>
    public bool ServerSideCharacter { get; init; }

    public StatsInfo Stats { get; init; } = new();
    public List<BuffEntry> Buffs { get; init; } = new();
    public List<ContainerReport> Containers { get; init; } = new();
}
