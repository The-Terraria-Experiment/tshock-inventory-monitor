using InventoryMonitor.Services;
using Xunit;

namespace InventoryMonitor.Tests;

/// <summary>
/// Guards the SSC gate on removals. Without ServerSideCharacters the vanilla client discards an
/// inbound PlayerSlot packet aimed at its own player (Terraria <c>MessageBuffer</c> case 5), so a
/// removal would only desync the server's copy — every removal path must refuse instead.
/// Exercises the injectable overload: reading <c>Main.ServerSideCharacter</c> would run
/// <c>Terraria.Main</c>'s static constructor, which throws outside a live server.
/// </summary>
public class RemovalGateTests
{
    [Fact]
    public void RemovalIsBlockedWithoutSsc()
    {
        string? reason = InventoryManager.RemovalBlockedReason(serverSideCharacters: false);

        Assert.NotNull(reason);
        Assert.Contains("ServerSideCharacters", reason);
    }

    [Fact]
    public void RemovalIsAllowedUnderSsc()
    {
        Assert.Null(InventoryManager.RemovalBlockedReason(serverSideCharacters: true));
    }
}
