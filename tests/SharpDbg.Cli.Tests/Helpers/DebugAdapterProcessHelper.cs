using System.Diagnostics;
using System.Text.Json.Nodes;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.DapProtocol;

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
				//FileName = @"C:\Users\Matthew\Documents\Git\Misc\xdbg\src\xdbg\bin\Release\net10.0\xdbg.exe",
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

	public static DebugProtocolHost GetDebugProtocolHost(Stream inputStream, Stream outputStream, ITestOutputHelper testOutputHelper, bool terminateDebuggeeOnDispose = false)
	{
		var debugProtocolHost = new DebugProtocolHost(inputStream, outputStream, terminateDebuggeeOnDispose, log: message =>
		{
			//testOutputHelper.WriteLine($"Log [DAP Host]: {message}");
		});
		return debugProtocolHost;
	}

	public static InitializeArguments GetInitializeRequest()
	{
		return new InitializeArguments
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
			SupportsRunInTerminalRequest = true
		};
	}

	public static AttachArguments GetAttachRequest(int processId, bool justMyCode = true)
	{
		return new AttachArguments
		{
			AdditionalProperties = new JsonObject
			{
				["name"] = "AttachRequestName",
				["type"] = "coreclr",
				["processId"] = processId,
				["console"] = "internalConsole", // integratedTerminal, externalTerminal, internalConsole
				["justMyCode"] = justMyCode
			}
		};
	}

	public static LaunchArguments GetLaunchRequest(string program, bool stopAtEntry, bool justMyCode = true)
	{
		return new LaunchArguments
		{
			AdditionalProperties = new JsonObject
			{
				["name"] = "LaunchRequestName",
				["type"] = "coreclr",
				["program"] = program,
				["cwd"] = Path.GetDirectoryName(program),
				["console"] = "internalConsole",
				["justMyCode"] = justMyCode,
				["stopAtEntry"] = stopAtEntry
			}
		};
	}

	public static SetBreakpointsArguments GetSetBreakpointsRequest(int? line = null, string? filePath = null)
	{
		line ??= 22;
		filePath ??= Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		var debugFilePath = filePath;
		var debugFileBreakpointLine = line.Value;

		var setBreakpointsRequest = new SetBreakpointsArguments
		{
			Source = new Source { Path = debugFilePath },
			Breakpoints = [new SourceBreakpoint { Line = debugFileBreakpointLine }]
		};
		return setBreakpointsRequest;
	}

	public static SetBreakpointsArguments GetSetBreakpointsRequest(int[] lines, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsArguments
		{
			Source = new Source { Path = filePath },
			Breakpoints = lines.Select(line => new SourceBreakpoint { Line = line }).ToList()
		};
		return setBreakpointsRequest;
	}

	public static SetBreakpointsArguments GetSetBreakpointsRequest(List<SharpDbgBreakpointRequest> breakpointRequests, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsArguments
		{
			Source = new Source { Path = filePath },
			Breakpoints = breakpointRequests.Select(s => new SourceBreakpoint
			{
				Line = s.Line,
				Column = s.Column,
				Condition = s.Condition,
				HitCondition = s.HitCondition
			}).ToList()
		};
		return setBreakpointsRequest;
	}
}
