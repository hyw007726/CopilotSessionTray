using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace CopilotSessionTray.App;

/// <summary>
/// Creates the tray icon and, while Windows has no taskbar to put it on yet (typically when the app
/// is started at logon, a moment before Explorer), keeps trying in the background instead of failing.
/// </summary>
public sealed class TrayIconStarter
{
    private readonly Action _create;
    private readonly TimeSpan _fastRetry;
    private readonly TimeSpan _slowRetry;
    private readonly int _fastAttempts;
    private readonly TimeSpan _reportAfter;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);
    private readonly Stopwatch _waited = new();
    private int _attempts;
    private bool _reported;

    public TrayIconStarter(Action create)
        : this(create, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 30, TimeSpan.FromMinutes(5))
    {
    }

    /// <param name="create">Creates the icon; throws <see cref="InvalidOperationException"/> or <see cref="Win32Exception"/> while the taskbar is missing.</param>
    /// <param name="fastRetry">Delay between the first <paramref name="fastAttempts"/> retries.</param>
    /// <param name="slowRetry">Delay between later retries.</param>
    /// <param name="fastAttempts">How many retries use <paramref name="fastRetry"/>.</param>
    /// <param name="reportAfter">When the icon is still missing after this long, the failure is raised once as an unhandled dispatcher exception (the app's error dialog); retrying continues regardless.</param>
    public TrayIconStarter(Action create, TimeSpan fastRetry, TimeSpan slowRetry, int fastAttempts, TimeSpan reportAfter)
    {
        _create = create;
        _fastRetry = fastRetry;
        _slowRetry = slowRetry;
        _fastAttempts = fastAttempts;
        _reportAfter = reportAfter;
        _timer.Tick += (_, _) => Attempt();
    }

    public void Start()
    {
        _attempts = 0;
        _reported = false;
        _waited.Restart();
        Attempt();
    }

    public void Stop() => _timer.Stop();

    private void Attempt()
    {
        try
        {
            _create();
            _timer.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _attempts++;
            _timer.Interval = _attempts <= _fastAttempts ? _fastRetry : _slowRetry;
            _timer.Start();
            if (!_reported && _waited.Elapsed >= _reportAfter)
            {
                _reported = true;
                var failure = ExceptionDispatchInfo.Capture(ex);
                _timer.Dispatcher.BeginInvoke(new Action(failure.Throw));
            }
        }
    }
}
