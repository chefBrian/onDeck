using OnDeck.App.Notifications;

namespace OnDeck.App.Tests;

/// <summary>
/// An <see cref="IToastPresenter"/> that records what it was asked to do, in order. The real one
/// talks to a static Windows API that cannot run in a test.
/// </summary>
public sealed class RecordingToastPresenter : IToastPresenter
{
    public List<(ToastPlan Plan, string? ImagePath)> Shown { get; } = [];

    public List<string> Removed { get; } = [];

    public List<string> RemovedGroups { get; } = [];

    public int Cleared { get; private set; }

    public ToastPlan LastShown => Shown[^1].Plan;

    public void Show(ToastPlan plan, string? imagePath) => Shown.Add((plan, imagePath));

    public void Remove(string tag) => Removed.Add(tag);

    public void RemoveGroup(string group) => RemovedGroups.Add(group);

    public void Clear() => Cleared++;
}
