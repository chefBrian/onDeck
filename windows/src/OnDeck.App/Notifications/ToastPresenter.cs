using Microsoft.Toolkit.Uwp.Notifications;
using OnDeck.App.Platform;

namespace OnDeck.App.Notifications;

/// <summary>
/// The edge between a decided <see cref="ToastPlan"/> and Windows. It exists so
/// <see cref="ToastService"/> can be tested: <c>ToastNotificationManagerCompat</c> is static and
/// needs a live notification platform, so nothing calling it directly can run in a test.
/// </summary>
public interface IToastPresenter
{
    void Show(ToastPlan plan, string? imagePath);

    void Remove(string tag);

    void RemoveGroup(string group);

    void Clear();
}

/// <summary>
/// The real presenter. Deliberately branch-free beyond "is this field set" — every decision was
/// already made by <see cref="ToastPlanner"/>.
/// <para>
/// Every method swallows its exceptions. The toast API throws when the notification platform is
/// unavailable or COM registration fails, and a missed notification must never take down the poll
/// cycle that produced it — Core wraps sink calls in its own guard, but the purge methods are
/// called synchronously from the transition path.
/// </para>
/// </summary>
public sealed class WindowsToastPresenter : IToastPresenter
{
    public void Show(ToastPlan plan, string? imagePath)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(plan.Title)
                .AddText(plan.Body);

            if (plan.ClickUrl is { } url)
            {
                builder.AddArgument(ToastActivation.UrlKey, url.AbsoluteUri);
            }

            if (imagePath is not null)
            {
                // The circle crop is what Windows uses for a person; a headshot in a square
                // frame reads as a screenshot.
                builder.AddAppLogoOverride(new Uri(imagePath), ToastGenericAppLogoCrop.Circle);
            }

            builder.Show(toast =>
            {
                if (plan.Tag is { } tag) toast.Tag = tag;
                if (plan.Group is { } group) toast.Group = group;
                if (plan.Expiry is { } window) toast.ExpirationTime = DateTimeOffset.Now + window;
            });
        }
        catch (Exception exception)
        {
            ShellLog.Append($"[Toast] show failed for \"{plan.Title}\": {exception.Message}");
        }
    }

    public void Remove(string tag) =>
        Guarded(() => ToastNotificationManagerCompat.History.Remove(tag), $"remove {tag}");

    public void RemoveGroup(string group) =>
        Guarded(
            () => ToastNotificationManagerCompat.History.RemoveGroup(group),
            $"remove group {group}");

    public void Clear() =>
        Guarded(() => ToastNotificationManagerCompat.History.Clear(), "clear");

    private static void Guarded(Action work, string description)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            ShellLog.Append($"[Toast] {description} failed: {exception.Message}");
        }
    }
}
