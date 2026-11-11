using System;
using System.Threading;

namespace InfoPanel.Panels.Hosted;

/// <summary>
/// A panel package's host as its proxy drivers reach it (the engine's host runner; a test's own): the connection to the
/// host now (started when none runs), where the sessions' slot files go, whether its drivers can be used, the sessions
/// open on it, and whether a device its families name is plugged in (a host runs while one is, or a session is open).
/// </summary>
public interface IPanelHostLink
{
    /// <summary>The package's folder name, <c>InfoPanel.{Name}</c>.</summary>
    string Folder { get; }

    /// <summary>The folder the sessions' slot files are made in: private to the user, swept at start.</summary>
    string SlotDirectory { get; }

    /// <summary>Null while its drivers can be used; else why not (its package turned off, or failed for good).</summary>
    PanelDriverProblem? Problem { get; }

    /// <summary>
    /// The host's connection now, its host started when none runs; waits while it starts or is started again, until
    /// <paramref name="stop"/>. Every call goes through the connection this answers then, never one kept from before.
    /// </summary>
    /// <exception cref="PanelDriverException">The package is off or failed, or its host didn't start.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="stop"/> was cancelled.</exception>
    PanelHostConnection Connect(CancellationToken stop);

    void SessionOpened();

    void SessionClosed();

    /// <summary>Whether a device its families name is listed now (the manager's every pass): its host stops once none is and no session is open.</summary>
    void SetPresent(bool present);
}

/// <summary>Why a package's drivers can't be used: <see cref="Off"/> (the user turned it off), else failed; <see cref="Message"/> says so, for the API.</summary>
public sealed record PanelDriverProblem(bool Off, string Message);

/// <summary>A hosted driver couldn't be reached: its package is off or failed, or its host ended or didn't start. The panel's reason is <c>driver</c>.</summary>
public sealed class PanelDriverException(string message) : Exception(message);
