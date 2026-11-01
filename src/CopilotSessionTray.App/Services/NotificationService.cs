using System.Windows;
using System.Windows.Media;
using CopilotSessionTray.Core.Contracts;
using CopilotSessionTray.Core.Models;

namespace CopilotSessionTray.App.Services;

/// <summary>
/// Real implementation of <see cref="INotificationService"/>: each finished session floats up the
/// right edge of the screen as a colorful balloon and gathers at the top right, stacked from the
/// top down in the order they arrived. Clicking a balloon pops just that one and the rest rise to
/// fill the gap; a repeat completion from a session that already has a balloon refreshes it in
/// place. A session that starts waiting for the user gets a red balloon of its own, with the
/// Windows notification sound; it pops by itself when the wait is over. Lives in the App project,
/// not <c>Core</c>, because it needs WPF windows.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private const double TopInset = 48;
    private const double BottomInset = 16;
    private const double RightInset = 18;
    private const double LaunchOverlap = 20;
    private const double FullPitch = BalloonWindow.BodyHeight + 12;
    private const double MinimumPitch = 28;

    private static readonly TimeSpan MinimumSoundGap = TimeSpan.FromSeconds(2);

    private static readonly Color AlertColor = Color.FromRgb(0xE5, 0x39, 0x35);

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x1E, 0x88, 0xE5),
        Color.FromRgb(0x38, 0x8E, 0x3C),
        Color.FromRgb(0x8E, 0x24, 0xAA),
        Color.FromRgb(0xD8, 0x1B, 0x60),
        Color.FromRgb(0x00, 0x89, 0x7B),
        Color.FromRgb(0x39, 0x49, 0xAB),
        Color.FromRgb(0xF5, 0x7C, 0x00),
        Color.FromRgb(0xFD, 0xD8, 0x35),
    };

    private readonly List<BalloonWindow> _balloons = new();
    private readonly IAlertSound _sound;
    private readonly TimeProvider _time;
    private long? _lastSoundAt;

    public NotificationService(IAlertSound sound)
        : this(sound, TimeProvider.System)
    {
    }

    public NotificationService(IAlertSound sound, TimeProvider time)
    {
        _sound = sound;
        _time = time;
    }

    public Task NotifySessionFinishedAsync(SessionNotification notification, CancellationToken cancellationToken = default) =>
        OnUiThread(() => Show(notification, alert: false));

    public Task NotifySessionNeedsInputAsync(SessionNotification notification, CancellationToken cancellationToken = default) =>
        OnUiThread(() =>
        {
            Show(notification, alert: true);
            PlayAlertSound();
        });

    public Task DismissNeedsInputAsync(string sessionId, CancellationToken cancellationToken = default) =>
        OnUiThread(() =>
        {
            var alert = _balloons.Find(b => b.IsAlert && b.SessionId == sessionId);
            if (alert is not null)
            {
                Pop(alert);
            }
        });

    private static Task OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private void Show(SessionNotification notification, bool alert)
    {
        var existing = _balloons.Find(b => b.IsAlert == alert && b.SessionId == notification.SessionId);
        if (existing is not null)
        {
            existing.Update(notification);
            existing.Wobble();
            return;
        }

        var area = SystemParameters.WorkArea;
        var slots = Slots(area, _balloons.Count + 1);
        var balloon = new BalloonWindow(notification, alert ? AlertColor : NextColor(notification.SessionId), Pop, alert);
        balloon.Launch(area.Right - RightInset, area.Bottom - LaunchOverlap, slots[^1]);
        _balloons.Add(balloon);
        Rearrange(slots, _balloons.Count - 1);
    }

    private void PlayAlertSound()
    {
        var now = _time.GetTimestamp();
        if (_lastSoundAt is { } last && _time.GetElapsedTime(last, now) < MinimumSoundGap)
        {
            return;
        }

        _lastSoundAt = now;
        _sound.Play();
    }

    private void Pop(BalloonWindow balloon)
    {
        if (!_balloons.Remove(balloon))
        {
            return;
        }

        balloon.Pop();
        Rearrange(Slots(SystemParameters.WorkArea, _balloons.Count), _balloons.Count);
    }

    private void Rearrange(double[] slots, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _balloons[i].MoveTo(slots[i]);
        }
    }

    private static double[] Slots(Rect area, int count)
    {
        var room = Math.Max(0, area.Height - TopInset - BottomInset - BalloonWindow.OverallHeight);
        var pitch = count > 1 ? Math.Clamp(room / (count - 1), MinimumPitch, FullPitch) : FullPitch;
        var slots = new double[count];
        for (var i = 0; i < count; i++)
        {
            slots[i] = area.Top + TopInset + Math.Min(i * pitch, room);
        }

        return slots;
    }

    private Color NextColor(string sessionId)
    {
        var hash = 2166136261u;
        foreach (var character in sessionId)
        {
            hash = (hash ^ character) * 16777619u;
        }

        var start = (int)(hash % (uint)Palette.Length);
        for (var offset = 0; offset < Palette.Length; offset++)
        {
            var candidate = Palette[(start + offset) % Palette.Length];
            if (!_balloons.Exists(b => b.BalloonColor == candidate))
            {
                return candidate;
            }
        }

        return Palette[start];
    }
}
