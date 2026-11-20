using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;

namespace WinWaechter.Tests;

public class ItemRowsTests
{
    sealed class Row : ItemRowBase;

    [Fact]
    public void A_line_with_several_items_runs_until_all_are_through_and_keeps_the_failure()
    {
        var row = new Row();
        object a = new(), b = new();
        var p = ItemRows.Start([(a, row), (b, row)]);
        Assert.Equal(ItemState.Waiting, row.RunState);

        p.Report(new ItemProgress(a, ItemState.Running));
        Assert.Equal(ItemState.Running, row.RunState);
        p.Report(new ItemProgress(a, ItemState.Failed, "Zugriff verweigert"));
        Assert.Equal(ItemState.Running, row.RunState);   // b still to come
        p.Report(new ItemProgress(b, ItemState.Running));
        p.Report(new ItemProgress(b, ItemState.Done));

        Assert.Equal(ItemState.Failed, row.RunState);
        Assert.Equal("Zugriff verweigert", row.RunText);
        Assert.True(row.ShowRunText);
    }

    [Fact]
    public void Lines_that_never_got_their_turn_say_not_run()
    {
        var done = new Row();
        var left = new Row();
        object a = new(), b = new();
        var p = ItemRows.Start([(a, done), (b, left)]);
        p.Report(new ItemProgress(a, ItemState.Running));
        p.Report(new ItemProgress(a, ItemState.Done));

        ItemRows.Finish([done, left]);

        Assert.Equal(ItemState.Done, done.RunState);
        Assert.False(done.ShowRunText);
        Assert.Equal(ItemState.Skipped, left.RunState);
        Assert.Equal("Nicht ausgeführt.", left.RunText);
        Assert.Equal("Ausgelassen", left.RunLabel);
    }

    [Theory]
    [InlineData(ItemState.None, null, false)]
    [InlineData(ItemState.Waiting, "Wartet", true)]
    [InlineData(ItemState.Running, "Läuft …", false)]
    [InlineData(ItemState.Done, "✓ Fertig", true)]
    [InlineData(ItemState.Failed, "✗ Fehlgeschlagen", true)]
    public void Labels_and_chip(ItemState state, string? label, bool chip)
    {
        var row = new Row();
        row.SetState(state);
        Assert.Equal(label, row.RunLabel);
        Assert.Equal(chip, row.ShowRunChip);
        Assert.Equal(state != ItemState.None, row.ShowRunState);
    }
}
