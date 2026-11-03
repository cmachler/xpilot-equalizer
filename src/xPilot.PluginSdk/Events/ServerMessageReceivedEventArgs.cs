using System;

namespace Vatsim.Xpilot.PluginSdk.Events;

/// <summary>
/// Arguments for the ServerMessageReceived event.
/// </summary>
public class ServerMessageReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the message contents.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerMessageReceivedEventArgs"/> class.
    /// </summary>
    /// <param name="message">The message contents.</param>
    public ServerMessageReceivedEventArgs(string message)
    {
        Message = message;
    }
}
