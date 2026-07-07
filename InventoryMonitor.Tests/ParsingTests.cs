using InventoryMonitor.Models;
using InventoryMonitor.Rest;
using InventoryMonitor.Services;
using Xunit;

namespace InventoryMonitor.Tests;

/// <summary>Covers the REST/command string parsing that maps user input to report/clear scopes.</summary>
public class ParsingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]                 // unknown token => default to All
    public void ParseGroups_Defaults_To_All(string? include)
    {
        Assert.Equal(ReportGroups.All, RestEndpoints.ParseGroups(include));
    }

    [Theory]
    [InlineData("core", ReportGroups.Core)]
    [InlineData("storage", ReportGroups.Storage)]
    [InlineData("misc", ReportGroups.Misc)]
    [InlineData("loadouts", ReportGroups.Loadouts)]
    [InlineData("core,storage", ReportGroups.Core | ReportGroups.Storage)]
    [InlineData(" core , misc ", ReportGroups.Core | ReportGroups.Misc)]     // trims
    [InlineData("CORE,Loadouts", ReportGroups.Core | ReportGroups.Loadouts)] // case-insensitive
    [InlineData("core,bogus", ReportGroups.Core)]                            // ignores unknown
    public void ParseGroups_Combines_Known_Tokens(string include, ReportGroups expected)
    {
        Assert.Equal(expected, RestEndpoints.ParseGroups(include));
    }

    [Theory]
    [InlineData(null, "all")]
    [InlineData("", "all")]
    [InlineData("bogus", "all")]
    [InlineData("ALL", "all")]
    [InlineData("main", "main")]
    [InlineData(" Storage ", "storage")]
    [InlineData("core", "core")]
    [InlineData("misc", "misc")]
    [InlineData("loadouts", "loadouts")]
    public void ResolveClearScope_Normalizes_Name(string? scope, string expectedName)
    {
        var (_, name) = InventoryManager.ResolveClearScope(scope);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void ResolveClearScope_Main_Selects_Only_Inventory()
    {
        var (filter, _) = InventoryManager.ResolveClearScope("main");
        var selected = SlotMap.Segments.Where(filter).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "Inventory" }, selected);
    }

    [Fact]
    public void ResolveClearScope_Storage_Selects_All_Storage_Segments()
    {
        var (filter, _) = InventoryManager.ResolveClearScope("storage");
        var selected = SlotMap.Segments.Where(filter).ToList();

        Assert.NotEmpty(selected);
        Assert.All(selected, s => Assert.Equal(ReportGroups.Storage, s.Group));
        // sanity: the known storage containers are present
        Assert.Contains(selected, s => s.Name == "PiggyBank");
        Assert.Contains(selected, s => s.Name == "VoidVault");
        Assert.Contains(selected, s => s.Name == "Trash");
    }

    [Fact]
    public void ResolveClearScope_All_Selects_Everything()
    {
        var (filter, _) = InventoryManager.ResolveClearScope("all");
        Assert.Equal(SlotMap.Segments.Count, SlotMap.Segments.Count(filter));
    }
}
