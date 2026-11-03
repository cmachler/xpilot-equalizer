using System;
using System.Collections.Generic;
using Vatsim.Xpilot.PluginSdk.Events;
using Vatsim.Xpilot.PluginSdk.Exceptions;
using Vatsim.Xpilot.PluginSdk.Models;

namespace Vatsim.Xpilot.PluginSdk;

/// <summary>
/// The xPilot plugin broker interface. Provides events and methods for interacting with xPilot.
/// </summary>
public interface IBroker
{
    /// <summary>
    /// Gets the version of the SDK assembly the host was built with. Compare against the
    /// version your plugin was built against to detect an incompatible host in <see cref="IPlugin.Initialize"/>.
    /// </summary>
    public Version ApiVersion { get; }

    /// <summary>
    /// Gets a value indicating whether xPilot is currently connected to the network.
    /// Use this at initialization to learn the current state, since <see cref="NetworkConnected"/>
    /// is only raised on transitions.
    /// </summary>
    public bool IsConnected { get; }

    /// <summary>
    /// Gets the callsign of the current connection, or <see langword="null"/> when not connected.
    /// </summary>
    public string? Callsign { get; }

    /// <summary>
    /// Raised when the xPilot window is about to close.
    /// </summary>
    public event EventHandler? SessionEnded;

    /// <summary>
    /// Raised when a connection to the VATSIM network has been established.
    /// </summary>
    public event EventHandler<NetworkConnectedEventArgs>? NetworkConnected;

    /// <summary>
    /// Raised when the VATSIM network connection has been closed.
    /// </summary>
    public event EventHandler? NetworkDisconnected;

    /// <summary>
    /// Raised when a private message arrives.
    /// </summary>
    public event EventHandler<PrivateMessageReceivedEventArgs>? PrivateMessageReceived;

    /// <summary>
    /// Raised when the network server itself sends a text message. These are not private messages from another
    /// user, so they are not delivered through <see cref="PrivateMessageReceived"/>.
    /// </summary>
    public event EventHandler<ServerMessageReceivedEventArgs>? ServerMessageReceived;

    /// <summary>
    /// Raised when a text radio message arrives.
    /// </summary>
    public event EventHandler<RadioMessageReceivedEventArgs>? RadioMessageReceived;

    /// <summary>
    /// Raised when a broadcast message arrives.
    /// </summary>
    public event EventHandler<BroadcastMessageReceivedEventArgs>? BroadcastMessageReceived;

    /// <summary>
    /// Raised when a text radio message is sent by the user.
    /// </summary>
    public event EventHandler<RadioMessageSentEventArgs>? RadioMessageSent;

    /// <summary>
    /// Raised when a private message is sent by the user.
    /// </summary>
    public event EventHandler<PrivateMessageSentEventArgs>? PrivateMessageSent;

    /// <summary>
    /// Raised when a METAR is received.
    /// </summary>
    public event EventHandler<MetarReceivedEventArgs>? MetarReceived;

    /// <summary>
    /// Raised when a text ATIS (a.k.a. 'controller info') is received.
    /// </summary>
    public event EventHandler<AtisReceivedEventArgs>? AtisReceived;

    /// <summary>
    /// Raised when a new controller is encountered.
    /// </summary>
    public event EventHandler<ControllerAddedEventArgs>? ControllerAdded;

    /// <summary>
    /// Raised when a controller is deleted.
    /// </summary>
    public event EventHandler<ControllerDeletedEventArgs>? ControllerDeleted;

    /// <summary>
    /// Raised when a controller changes to a new primary frequency.
    /// </summary>
    public event EventHandler<ControllerFrequencyChangedEventArgs>? ControllerFrequencyChanged;

    /// <summary>
    /// Raised when a controller moves to a new location.
    /// </summary>
    public event EventHandler<ControllerLocationChangedEventArgs>? ControllerLocationChanged;

    /// <summary>
    /// Raised when a SELCAL alert is received.
    /// </summary>
    public event EventHandler<SelcalAlertReceivedEventArgs>? SelcalAlertReceived;

    /// <summary>
    /// Raised when an aircraft is added to the simulator session.
    /// </summary>
    public event EventHandler<AircraftAddedEventArgs>? AircraftAdded;

    /// <summary>
    /// Raised when an aircraft position is updated.
    /// </summary>
    public event EventHandler<AircraftUpdatedEventArgs>? AircraftUpdated;

    /// <summary>
    /// Raised when an aircraft is deleted from the simulator session.
    /// </summary>
    public event EventHandler<AircraftDeletedEventArgs>? AircraftDeleted;

    /// <summary>
    /// Gets a snapshot of the controllers xPilot is currently tracking. Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// Tracking starts when xPilot starts and is reset on disconnect, so the snapshot matches the controller
    /// set xPilot has seen for the current connection no matter when the plugin loads or calls this.
    /// A controller is included from its first position update until its delete message.
    /// </remarks>
    /// <returns>A copy of the current controller set; empty when disconnected.</returns>
    public IReadOnlyList<ControllerInfo> GetControllers();

    /// <summary>
    /// Request connection to the network as a pilot.
    /// </summary>
    /// <param name="callsign">The callsign to use for the session. (Required)</param>
    /// <param name="typeCode">The ICAO aircraft type code. (Required)</param>
    /// <param name="selcalCode">The SELCAL code. (Optional; leave empty or null for none.)</param>
    /// <exception cref="AlreadyConnectedException">Thrown when already connected to the network.</exception>
    /// <exception cref="SimNotReadyException">Thrown when the simulator is not connected.</exception>
    public void RequestConnect(string callsign, string typeCode, string selcalCode);

    /// <summary>
    /// Request connection to the network as an observer.
    /// </summary>
    /// <param name="callsign">The callsign to use for the session. (Required)</param>
    /// <exception cref="AlreadyConnectedException">Thrown when already connected to the network.</exception>
    /// <exception cref="SimNotReadyException">Thrown when the simulator is not connected.</exception>
    public void RequestConnectAsObserver(string callsign);

    /// <summary>
    /// Request connection to the network as a tower view observer.
    /// </summary>
    /// <exception cref="AlreadyConnectedException">Thrown when already connected to the network.</exception>
    /// <exception cref="SimNotReadyException">Thrown when the simulator is not connected.</exception>
    public void RequestConnectAsTowerView();

    /// <summary>
    /// Request disconnection from the network.
    /// </summary>
    public void RequestDisconnect();

    /// <summary>
    /// Request retrieval of METAR for a station.
    /// </summary>
    /// <param name="station">The station (airport) for which to request the METAR.</param>
    /// <exception cref="NotConnectedException">Thrown when not connected to the network.</exception>
    public void RequestMetar(string station);

    /// <summary>
    /// Request controller ATIS information.
    /// </summary>
    /// <param name="callsign">The callsign of the controller.</param>
    /// <exception cref="NotConnectedException">Thrown when not connected to the network.</exception>
    public void RequestAtis(string callsign);

    /// <summary>
    /// Post a debug message to the xPilot message area.
    /// </summary>
    /// <param name="message">The message contents.</param>
    public void PostDebugMessage(string message);

    /// <summary>
    /// Set mode C on or off.
    /// </summary>
    /// <param name="modeC">Whether or not to squawk mode C.</param>
    /// <exception cref="SimNotReadyException">Thrown when the simulator is not connected.</exception>
    public void SetModeC(bool modeC);

    /// <summary>
    /// Squawk ident.
    /// </summary>
    /// <exception cref="SimNotReadyException">Thrown when the simulator is not connected.</exception>
    public void SquawkIdent();

    /// <summary>
    /// Set Push-To-Talk on or off.
    /// </summary>
    /// <param name="pressed">Whether or not Push-To-Talk is pressed.</param>
    /// <exception cref="NotConnectedException">Thrown when not connected to the network.</exception>
    public void SetPtt(bool pressed);
}
