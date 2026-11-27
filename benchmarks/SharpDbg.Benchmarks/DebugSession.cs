using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SharpDbg.DapProtocol;
using StackFrame = SharpDbg.DapProtocol.StackFrame;

namespace SharpDbg.Benchmarks;

/// <summary>A debug adapter process and a DAP client driving it, synchronously, so benchmarks time nothing but the adapter.</summary>
public sealed class DebugSession : IDisposable
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	private readonly AdapterDefinition _adapter;
	private readonly Process _process;
	private readonly DapConnection _connection;
	private readonly BlockingCollection<IDapEvent> _events = new();
	private readonly ConcurrentQueue<string> _stderr = new();
	private Task<DapResult<LaunchResponse>>? _launch;

	private DebugSession(AdapterDefinition adapter)
	{
		_adapter = adapter;
		var startInfo = new ProcessStartInfo(adapter.Path)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var arg in adapter.Args) startInfo.ArgumentList.Add(arg);
		RemoveInheritedRuntimeSettings(startInfo);

		_process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {adapter.Path}");
		_process.ErrorDataReceived += (_, e) =>
			// Keeps the last few lines, for error messages
		{
			if (e.Data is null) return;
			_stderr.Enqueue(e.Data);
			while (_stderr.Count > 20) _stderr.TryDequeue(out var _);
		};
		_process.BeginErrorReadLine();

		_connection = new DapConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
		_connection.OnEvent<InitializedEvent>(_events.Add);
		_connection.OnEvent<StoppedEvent>(_events.Add);
		_connection.OnEvent<TerminatedEvent>(_events.Add);
		_connection.OnEvent<ExitedEvent>(_events.Add);
		_connection.Start();
	}

	/// <summary>Starts the adapter and sends <c>initialize</c>.</summary>
	public static DebugSession Start(AdapterDefinition adapter)
	{
		var session = new DebugSession(adapter);
		try
		{
			session.Send(new InitializeArguments
			{
				ClientID = "vscode",
				ClientName = "Visual Studio Code",
				AdapterID = "coreclr",
				Locale = "en-us",
				LinesStartAt1 = true,
				ColumnsStartAt1 = true,
				PathFormat = InitializeArguments.PathFormatValue.Path,
				SupportsVariableType = true,
				SupportsVariablePaging = true,
				AdditionalProperties = new JsonObject { ["supportsHandshakeRequest"] = true },
			});
			return session;
		}
		catch
		{
			session.Dispose();
			throw;
		}
	}

	/// <summary>Launches the debuggee running <paramref name="scenario"/> and waits for it to stop at the first of <paramref name="breakpoints"/>.</summary>
	public StoppedEvent LaunchToBreakpoint(string scenario, params SourceBreakpoint[] breakpoints)
	{
		var program = DebuggeeSource.ExecutablePath;
		_launch = _connection.SendRequestAsync(new LaunchArguments
		{
			AdditionalProperties = new JsonObject
			{
				["name"] = "Benchmark",
				["type"] = "coreclr",
				["request"] = "launch",
				["program"] = program,
				["args"] = new JsonArray(scenario),
				["cwd"] = Path.GetDirectoryName(program),
				["console"] = "internalConsole",
				["justMyCode"] = true,
				["stopAtEntry"] = false,
			}
		});
		WaitForEvent<InitializedEvent>();
		SetBreakpoints(breakpoints);
		Send(new ConfigurationDoneArguments());
		return WaitForStop();
	}

	public SetBreakpointsResponse SetBreakpoints(params SourceBreakpoint[] breakpoints) => Send(new SetBreakpointsArguments
	{
		Source = new Source { Path = DebuggeeSource.ProgramPath },
		Breakpoints = breakpoints.ToList(),
	});

	public StoppedEvent WaitForStop() => WaitForEvent<StoppedEvent>();

	/// <summary>Continues and waits for the next stop.</summary>
	public StoppedEvent ContinueToStop(int threadId)
	{
		Send(new ContinueArguments { ThreadId = threadId });
		return WaitForStop();
	}

	public StackFrame TopFrame(int threadId) => Send(new StackTraceArguments { ThreadId = threadId, StartFrame = 0, Levels = 1 }).StackFrames.First();

	/// <summary>Fails unless the top frame of the stopped thread is on <paramref name="expected"/> in the debuggee's Program.cs.</summary>
	public StackFrame ExpectStoppedIn(StoppedEvent stop, LineRange expected)
	{
		var frame = TopFrame(stop.ThreadId ?? throw new InvalidOperationException("The stop has no thread"));
		var inProgram = frame.Source?.Path is { } path && Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase);
		if (inProgram is false || expected.Contains(frame.Line) is false)
		{
			throw new InvalidOperationException($"{_adapter.Name} stopped ({stop.Reason}) in '{frame.Name}' at {frame.Source?.Path ?? "<no source>"}:{frame.Line}, expected Program.cs:{expected}");
		}
		return frame;
	}

	public StackFrame ExpectStoppedAt(StoppedEvent stop, SourceLine expected) => ExpectStoppedIn(stop, new LineRange(expected.Line, expected.Line));

	public TResponse Send<TResponse>(IDapRequest<TResponse> request)
	{
		var result = _connection.SendRequestAsync(request).WaitAsync(Timeout).GetAwaiter().GetResult();
		return result switch
		{
			DapError error => throw new InvalidOperationException($"{_adapter.Name}: '{request.Command}' failed: {error.Message}{Stderr()}"),
			TResponse response => response,
			_ => throw new InvalidOperationException($"{_adapter.Name}: '{request.Command}' had no response"),
		};
	}

	/// <summary>Takes events in order until one of type <typeparamref name="T"/>, failing on the debuggee exiting or stopping when something else was expected.</summary>
	private T WaitForEvent<T>() where T : IDapEvent
	{
		var deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < Timeout)
		{
			if (_launch is { IsCompleted: true } launch && launch.Result is DapError launchError)
			{
				throw new InvalidOperationException($"{_adapter.Name}: launch failed: {launchError.Message}{Stderr()}");
			}
			if (_events.TryTake(out var dapEvent, TimeSpan.FromMilliseconds(50)) is false) continue;
			switch (dapEvent)
			{
				case T match: return match;
				case StoppedEvent stop: throw new InvalidOperationException($"{_adapter.Name} stopped ({stop.Reason}) while waiting for {typeof(T).Name}");
				case TerminatedEvent or ExitedEvent: throw new InvalidOperationException($"{_adapter.Name}: the debuggee exited while waiting for {typeof(T).Name}{Stderr()}");
			}
		}
		throw new TimeoutException($"{_adapter.Name}: no {typeof(T).Name} within {Timeout.TotalSeconds}s{Stderr()}");
	}

	private string Stderr() => _stderr.IsEmpty ? "" : $"{Environment.NewLine}Adapter stderr:{Environment.NewLine}{string.Join(Environment.NewLine, _stderr)}";

	public void Dispose()
	{
		try
		{
			_connection.SendRequestAsync(new DisconnectArguments { TerminateDebuggee = true }).Wait(TimeSpan.FromSeconds(3));
		}
		catch
		{
			// The adapter may already be gone
		}
		_connection.Stop();
		try
		{
			// Some adapters run the debugger in a child process, and the debuggee is a child too
			if (_process.HasExited is false) _process.Kill(entireProcessTree: true);
			_process.WaitForExit(5000);
		}
		catch
		{
			// Already exited
		}
		_process.Dispose();
	}

	// Runtime settings that IDEs set on processes they start (e.g. Rider's hot reload) would otherwise reach the adapter and the debuggee,
	// changing their startup time and behaviour depending on where the benchmarks were started from
	private static void RemoveInheritedRuntimeSettings(ProcessStartInfo startInfo)
	{
		foreach (var key in startInfo.Environment.Keys.ToList())
		{
			var isRuntimeSetting = key.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
				|| key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
				|| (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && key.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) is false);
			if (isRuntimeSetting) startInfo.Environment.Remove(key);
		}
		startInfo.Environment.Remove("_NO_DEBUG_HEAP");
	}
}
