using System.Diagnostics;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.Models;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Where the debug session is in its life, from the launch or attach request to the end
/// </summary>
/// <remarks>
/// A session starts <see cref="NotStarted"/>. A launch or attach request records what to debug, and ConfigurationDone starts it.
/// A launch goes straight to <see cref="Debugging"/>. An attach goes through <see cref="Attaching"/>, and a remote attach through
/// <see cref="WaitingForRemoteDebuggee"/>, until the debuggee's runtime is ready. Terminating or
/// disconnecting ends the session for good: the debugger stops processing runtime events, so it cannot debug another process
/// </remarks>
public closed record SessionState
{
	/// <summary>No launch or attach request yet</summary>
	public sealed record NotStarted : SessionState
	{
		public static NotStarted Instance { get; } = new();
	}

	/// <summary>A launch request, carried out on ConfigurationDone</summary>
	public sealed record LaunchRequested(LaunchInfo LaunchInfo) : SessionState;

	/// <summary>An attach request, carried out on ConfigurationDone</summary>
	public sealed record AttachRequested(int ProcessId) : SessionState;

	/// <summary>A remote attach request, carried out on ConfigurationDone</summary>
	public sealed record RemoteAttachRequested(RemoteAttachInfo RemoteAttachInfo) : SessionState;

	/// <summary>
	/// Attaching in the background: the debuggee may be suspended until its runtime is resumed, which happens after ConfigurationDone returns
	/// </summary>
	public sealed record Attaching(int ProcessId) : SessionState;

	/// <summary>Listening for the remote debuggee, whose process arrives with the CreateProcess event</summary>
	public sealed record WaitingForRemoteDebuggee(ICorDebug CorDebug) : SessionState;

	/// <param name="DebuggeeProcess">The process when the debugger launched it, which gives its exit code</param>
	public sealed record Debugging(ICorDebug CorDebug, ICorDebugProcess Process, Process? DebuggeeProcess, bool IsRemote) : SessionState;

	/// <summary>Terminated or disconnected</summary>
	public sealed record Ended : SessionState
	{
		public static Ended Instance { get; } = new();
	}
}
