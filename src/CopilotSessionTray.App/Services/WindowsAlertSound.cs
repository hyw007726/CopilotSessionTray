using System.Media;
using System.Runtime.InteropServices;

namespace CopilotSessionTray.App.Services;

/// <summary>
/// Plays the sound Windows itself uses for a notification — the one chosen for "Notification" in
/// the user's Sound settings, through the System Sounds volume — so the alert follows their
/// scheme and can be silenced the usual way. Falls back to the system exclamation sound where
/// that event is not defined.
/// </summary>
public sealed class WindowsAlertSound : IAlertSound
{
    private const string NotificationEvent = "Notification.Default";
    private const uint SndAsync = 0x00000001;
    private const uint SndNoDefault = 0x00000002;
    private const uint SndAlias = 0x00010000;
    private const uint SndSystem = 0x00200000;

    public void Play()
    {
        try
        {
            if (!PlaySound(NotificationEvent, IntPtr.Zero, SndAlias | SndAsync | SndNoDefault | SndSystem))
            {
                SystemSounds.Exclamation.Play();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
