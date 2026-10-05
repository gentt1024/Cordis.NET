namespace Cordis.Composition;

/// <summary>Request orderly launcher shutdown after the current startup attempt settles.</summary>
/// <remarks>The launcher disposes the application tree before returning the first requested exit code.
/// A CLI startup failure returns failure; Cordis reports effect cleanup errors without changing the requested code.
/// The CLI grants five seconds from the first request before forcing process exit. Other hosts own their shutdown policy.</remarks>
public delegate void ApplicationExit(int exitCode);

/// <summary>Launcher readiness committed after the plugin tree and host setup succeed.</summary>
public interface IApplicationReady
{
    /// <summary>Subscribe once, or invoke immediately when already ready. Dispose the registration to withdraw the listener.</summary>
    /// <remarks>Callbacks run synchronously on the committing or subscribing thread. A callback failure propagates to that caller.
    /// Register the returned disposable as a Context effect when the listener belongs to a plugin.</remarks>
    IDisposable OnReady(Action listener);
}
