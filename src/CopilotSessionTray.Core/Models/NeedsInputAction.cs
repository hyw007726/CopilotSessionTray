namespace CopilotSessionTray.Core.Models;

/// <summary>What the user should be told after a <see cref="SessionChangeEvent"/>, as decided by <see cref="Services.NeedsInputAlerts"/>.</summary>
public enum NeedsInputAction
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>The session has just started waiting for the user: alert them.</summary>
    Alert,

    /// <summary>The session was waiting for the user and no longer is (answered, aborted or closed): take its alert down.</summary>
    Dismiss,
}
