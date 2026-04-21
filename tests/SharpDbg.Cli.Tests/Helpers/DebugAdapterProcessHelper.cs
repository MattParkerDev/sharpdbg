using System.Diagnostics;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests.Helpers;

public static class DebugAdapterProcessHelper
{
	public static Process GetDebugAdapterProcess()
	{
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				//FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
				//FileName = @"C:\Users\Matthew\Documents\Git\sharpdbg\artifacts\bin\SharpDbg.Cli\debug\SharpDbg.Cli.exe",
				FileName = Path.JoinFromGitRoot("artifacts", "bin", "SharpDbg.Cli", "debug", OperatingSystem.IsWindows() ? "SharpDbg.Cli.exe" : "SharpDbg.Cli"),
				Arguments = "--interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		if (File.Exists(process.StartInfo.FileName) is false) throw new FileNotFoundException("SharpDbg executable not found", process.StartInfo.FileName);
		process.Start();
		return process;
	}
	public static DebugProtocolHost GetDebugProtocolHost(Process process, ITestOutputHelper testOutputHelper, TaskCompletionSource? initializedEventTcs = null) =>
		GetDebugProtocolHost(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, testOutputHelper, initializedEventTcs);

	public static DebugProtocolHost GetDebugProtocolHost(Stream inputStream, Stream outputStream, ITestOutputHelper testOutputHelper, TaskCompletionSource? initializedEventTcs = null)
	{
		var debugProtocolHost = new DebugProtocolHost(inputStream, outputStream, false);
		debugProtocolHost.LogMessage += (sender, args) =>
		{
			//testOutputHelper.WriteLine($"Log [DAP Host]: {args.Message}");
		};
		debugProtocolHost.RegisterClientRequestType<HandshakeRequest, HandshakeArguments, HandshakeResponse>(async void (responder) =>
		{
			var signatureResponse = await DebuggerHandshakeSigner.Sign(responder.Arguments.Value);
			responder.SetResponse(new HandshakeResponse(signatureResponse));
		});
		debugProtocolHost.RegisterEventType<InitializedEvent>(@event =>
		{
			initializedEventTcs?.SetResult();
		});
		// debugProtocolHost.RegisterEventType<StoppedEvent>(async void (@event) =>
		// {
		// 	testOutputHelper.WriteLine("Stopped Event");
		// });
		debugProtocolHost.VerifySynchronousOperationAllowed();
		return debugProtocolHost;
	}

	public static InitializeRequest GetInitializeRequest()
	{
		return new InitializeRequest
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
			SupportsRunInTerminalRequest = true,
			SupportsHandshakeRequest = true
		};
	}

	public static LaunchRequest GetLaunchRequest(string program, string[] args, bool stopAtEntry = false)
	{
		return new LaunchRequest
		{
			ConfigurationProperties = new Dictionary<string, JToken>
			{
				["name"] = "LaunchRequestName",
				["type"] = "coreclr",
				["program"] = program,
				["args"] = JToken.FromObject(args),
				["stopAtEntry"] = stopAtEntry
			}
		};
	}

	public static AttachRequest GetAttachRequest(int processId, bool justMyCode = true, bool stopAtEntry = false)
	{
		return new AttachRequest
		{
			ConfigurationProperties = new Dictionary<string, JToken>
			{
				["name"] = "AttachRequestName",
				["type"] = "coreclr",
				["processId"] = processId,
				["console"] = "internalConsole", // integratedTerminal, externalTerminal, internalConsole
				["justMyCode"] = justMyCode,
				["stopAtEntry"] = stopAtEntry
			}
		};
	}

	public static SetBreakpointsRequest GetSetBreakpointsRequest(int? line = null, string? filePath = null)
	{
		line ??= 22;
		filePath ??= Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		var debugFilePath = filePath;
		var debugFileBreakpointLine = line.Value;

		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = debugFilePath },
			Breakpoints = [new SourceBreakpoint { Line = debugFileBreakpointLine }]
		};
		return setBreakpointsRequest;
	}

	public static SetBreakpointsRequest GetSetBreakpointsRequest(int[] lines, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = filePath },
			Breakpoints = lines.Select(line => new SourceBreakpoint { Line = line }).ToList()
		};
		return setBreakpointsRequest;
	}

	public static SetBreakpointsRequest GetSetBreakpointsRequest(List<SharpDbgBreakpointRequest> breakpointRequests, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = filePath },
			Breakpoints = breakpointRequests.Select(s => new SourceBreakpoint
			{
				Line = s.Line,
				Condition = s.Condition,
				HitCondition = s.HitCondition
			}).ToList()
		};
		return setBreakpointsRequest;
	}
}
