using OnDeck.Core.Managers;
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Managers;

public class StateManagerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);

    private static PlayerState Upcoming() => new PlayerState.Upcoming(Start);

    [Fact]
    public void Update_StoresStateAndFiresCallbackWithOldAndNew()
    {
        var manager = new StateManager();
        var changes = new List<(int Id, PlayerState? Old, PlayerState New)>();
        manager.OnStateChange = (id, oldState, newState) => changes.Add((id, oldState, newState));

        manager.Update(1, Upcoming());
        var dayOff = new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff());
        manager.Update(1, dayOff);

        Assert.Equal(dayOff, manager.PlayerStates[1]);
        Assert.Equal(2, changes.Count);
        Assert.Null(changes[0].Old);
        Assert.Equal(Upcoming(), changes[1].Old);
        Assert.Equal(dayOff, changes[1].New);
    }

    [Fact]
    public void Update_FiresEvenWhenStateIsUnchanged()
    {
        var manager = new StateManager();
        var count = 0;
        manager.OnStateChange = (_, _, _) => count++;

        manager.Update(1, Upcoming());
        manager.Update(1, Upcoming());

        Assert.Equal(2, count);
    }

    [Fact]
    public void StartTimeFor_ReturnsTimeOnlyForUpcoming()
    {
        var manager = new StateManager();
        manager.Update(1, Upcoming());
        manager.Update(2, new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()));

        Assert.Equal(Start, manager.StartTimeFor(1));
        Assert.Null(manager.StartTimeFor(2));
        Assert.Null(manager.StartTimeFor(3));
    }

    [Fact]
    public void SetUpcoming_OnlyFillsPlayersWithNoExistingState()
    {
        var manager = new StateManager();
        var existing = new PlayerState.Inactive(new PlayerState.InactiveReason.GameOver(1));
        manager.Update(1, existing);

        manager.SetUpcoming([1, 2], Start);

        Assert.Equal(existing, manager.PlayerStates[1]);
        Assert.Equal(Upcoming(), manager.PlayerStates[2]);
    }

    [Fact]
    public void SetUpcoming_DoesNotFireTheChangeCallback()
    {
        var manager = new StateManager();
        var count = 0;
        manager.OnStateChange = (_, _, _) => count++;

        manager.SetUpcoming([1, 2], Start);

        Assert.Equal(0, count);
    }

    [Fact]
    public void SetGameOver_MarksEveryPlayerAndFiresTheCallback()
    {
        var manager = new StateManager();
        var count = 0;
        manager.OnStateChange = (_, _, _) => count++;

        manager.SetGameOver([1, 2], 776543);

        foreach (var id in new[] { 1, 2 })
        {
            var inactive = Assert.IsType<PlayerState.Inactive>(manager.PlayerStates[id]);
            Assert.Equal(776543, Assert.IsType<PlayerState.InactiveReason.GameOver>(inactive.Reason).GamePk);
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void Reset_ClearsEveryState()
    {
        var manager = new StateManager();
        manager.Update(1, Upcoming());

        manager.Reset();

        Assert.Empty(manager.PlayerStates);
    }
}
