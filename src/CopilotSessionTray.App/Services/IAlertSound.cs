namespace CopilotSessionTray.App.Services;

/// <summary>Plays the sound that goes with an alert that a session needs the user. Kept apart from the balloons so tests can count the sounds instead of hearing them.</summary>
public interface IAlertSound
{
    /// <summary>Starts the sound and returns at once; it never throws.</summary>
    void Play();
}
