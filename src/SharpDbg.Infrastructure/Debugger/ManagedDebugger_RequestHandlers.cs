using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.Infrastructure.Debugger.PresentationHintModels;
using ZLinq;
using SharpDbg.Infrastructure.Debugger.Breakpoints;
using SharpDbg.Infrastructure.Debugger.Stepping;

namespace SharpDbg.Infrastructure.Debugger;

public record SharpDbgBreakpointRequest(int Line, string? Condition = null, string? HitCondition = null, int? Column = null);
public record SharpDbgFunctionBreakpointRequest(string Name, string? Condition = null, string? HitCondition = null);
public record SharpDbgExceptionBreakpointRequest(SharpDbgExceptionBreakpointFilter Filter, string? Condition = null);
public enum SharpDbgExceptionBreakpointFilter { All, UserUnhandled }

public partial class ManagedDebugger
{
	/// <summary>
	/// Stores the launch request info for use in handling ConfigurationDone
	/// </summary>
	public void Launch(LaunchInfo launchInfo, bool justMyCode)
	{
		EnsureSessionNotStarted();
		_logger?.Invoke($"Launching program: {launchInfo.Program} {string.Join(' ', launchInfo.Arguments)}");
		_justMyCode = justMyCode;
		_stopAtEntry = launchInfo.StopAtEntry;
		_session = new SessionState.LaunchRequested(launchInfo);
	}

	/// <summary>
	/// Actually perform the launch using DbgShim APIs
	/// </summary>
	private async Task PerformLaunch(LaunchInfo launchInfo)
	{
		var processStartInfo = new ProcessStartInfo
		{
			FileName = launchInfo.Program,
			WorkingDirectory = launchInfo.Cwd ?? Environment.CurrentDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = false,
		};
		foreach (var arg in launchInfo.Arguments)
		{
			processStartInfo.ArgumentList.Add(arg);
		}

		foreach (var (key, value) in launchInfo.Env)
		{
			processStartInfo.Environment[key] = value;
		}
		processStartInfo.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";

		var process = Process.Start(processStartInfo);
		if (process is null) throw new InvalidOperationException("Process start failed");

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data is not null) InvokeOnOutputWithTryCatch(e.Data + Environment.NewLine, isError: false);
		};
		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is not null) InvokeOnOutputWithTryCatch(e.Data + Environment.NewLine, isError: true);
		};
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		var processId = process.Id;

		_logger?.Invoke($"Process created suspended with PID: {processId}");

		var corDebug = await ClrDebugExtensions.Automatic(processId, resumeDiagnosticSuspension: true);
		AttachToProcess(corDebug, processId, process);
		OnProcessStarted?.Invoke(processId, launchInfo.Program);
		return;

		// The DataReceived callbacks run on background threads - a throwing subscriber (e.g. a disposed protocol layer) must not crash the adapter
		void InvokeOnOutputWithTryCatch(string output, bool isError)
		{
			try
			{
				OnOutput?.Invoke(output, isError);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Error forwarding debuggee output: {ex.Message}");
			}
		}
	}

	public bool RemoveBreakpoint(int id)
	{
		_logger?.Invoke($"RemoveBreakpoint: {id}");
		var bp = _breakpointManager.GetBreakpoint(id);
		if (bp is null) return false;
		var removed = _breakpointManager.RemoveBreakpoint(id);
		foreach (var corBreakpoint in bp.CorBreakpoints) DeactivateIfUnused(corBreakpoint);
		return removed;
	}

	// Breakpoints at the same location share a runtime breakpoint (as can the entry breakpoint), so it is only deactivated
	// once nothing uses it
	private void DeactivateIfUnused(ICorDebugFunctionBreakpoint corBreakpoint)
	{
		if (corBreakpoint == _entryBreakpoint?.CorBreakpoint || _breakpointManager.FindByCorBreakpoint(corBreakpoint) is not null) return;
		try
		{
			corBreakpoint.Activate(false);
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error deactivating breakpoint: {ex.Message}");
		}
	}

	/// <summary>
	/// Store process ID for later attach (actual attach happens in ConfigurationDone)
	/// </summary>
	public void Attach(int processId, bool justMyCode)
	{
		EnsureSessionNotStarted();
		_logger?.Invoke($"Storing attach target: {processId}");
		_justMyCode = justMyCode;
		_stopAtEntry = false;
		_session = new SessionState.AttachRequested(processId);
	}

	public void AttachRemote(RemoteAttachInfo remoteAttachInfo, bool justMyCode)
	{
		EnsureSessionNotStarted();
		_justMyCode = justMyCode;
		_stopAtEntry = false;
		_session = new SessionState.RemoteAttachRequested(remoteAttachInfo);
	}

	/// <summary>
	/// Called when DAP configuration is complete - performs deferred launch or attach
	/// </summary>
	public async Task ConfigurationDone()
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke("ConfigurationDone");

		switch (_session)
		{
			case SessionState.LaunchRequested { LaunchInfo: { LaunchRequestConsoleType: LaunchRequestConsoleType.ExternalTerminal or LaunchRequestConsoleType.IntegratedTerminal } launchInfo }:
				var launchedProcessId = await Task.Run(() => SendRunInTerminalRequest.Invoke(launchInfo)); // get off the dispatcher thread
				StartAttach(launchedProcessId);
				await DiagnosticClientHelper.DiagnosticClientResumeRuntime(launchedProcessId);
				break;
			case SessionState.LaunchRequested { LaunchInfo: var launchInfo }:
				await PerformLaunch(launchInfo);
				break;
			case SessionState.RemoteAttachRequested { RemoteAttachInfo: var remoteAttachInfo }:
				PerformRemoteAttach(remoteAttachInfo);
				break;
			case SessionState.AttachRequested { ProcessId: var processId }:
				StartAttach(processId);
				break;
			default:
				_logger?.Invoke($"Nothing to launch or attach to, in session state {_session.GetType().Name}");
				break;
		}
	}

	/// <summary>
	/// Continue execution
	/// </summary>
	public void HandleContinueRequest()
	{
		_logger?.Invoke("Continue");
		ContinueWithVariableClearAllowSuperfluousContinue();
	}

	private void ContinueWithVariableClearAllowSuperfluousContinue()
	{
		Guard.Against.Null(CorProcess);
		_variableManager.ClearAndDisposeHandleValues();
		_frameReferenceManager.Clear();

		var result = CorProcess.TryContinue(false);
		if (result is Cor.CORDBG_E_SUPERFLOUS_CONTINUE) return;
		Marshal.ThrowExceptionForHR(result);
	}

	private void ContinueWithVariableClear()
	{
		Guard.Against.Null(CorProcess);
		_variableManager.ClearAndDisposeHandleValues();
		_frameReferenceManager.Clear();

		CorProcess.Continue(false);
	}

	/// <summary>
	/// Pause execution
	/// </summary>
	public void Pause()
	{
		_logger?.Invoke("Pause");
		Guard.Against.Null(CorProcess);
		if (CorProcess.IsRunning is false) throw new InvalidOperationException("The process is not running, so it cannot be paused - it has either already stopped or has not finished starting");
		CorProcess.Stop(0);
		EndStep();
	}

	/// <summary>
	/// Step to the next line
	/// </summary>
	public async Task StepNext(int threadId)
	{
		_logger?.Invoke($"StepNext on thread {threadId}");
		await StartUserStep(threadId, StepType.StepOver);
	}

	/// <summary>
	/// Step into
	/// </summary>
	public async Task StepIn(int threadId)
	{
		_logger?.Invoke($"StepIn on thread {threadId}");
		await StartUserStep(threadId, StepType.StepIn);
	}

	/// <summary>
	/// Step out
	/// </summary>
	public async Task StepOut(int threadId)
	{
		_logger?.Invoke($"StepOut on thread {threadId}");
		await StartUserStep(threadId, StepType.StepOut);
	}

	/// <summary>
	/// Set breakpoints for a source file with optional conditions
	/// </summary>
	public List<BreakpointInfo> SetBreakpoints(string filePath, SharpDbgBreakpointRequest[] breakpoints)
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke($"SetBreakpoints: {filePath}, breakpoints: {string.Join(",", breakpoints.Select(b => $"L{b.Line} {(b.Column is not null ? $"C{b.Column}" : null)} {(b.Condition is not null ? $"[{b.Condition}]" : null)}"))}");

		// Clear and deactivate existing breakpoints for this file
		var existingCorBreakpoints = _breakpointManager.GetBreakpointsForFile(filePath).SelectMany(bp => bp.CorBreakpoints).Distinct().ToList();
		_breakpointManager.ClearBreakpointsForFile(filePath);
		foreach (var corBreakpoint in existingCorBreakpoints)
		{
			DeactivateIfUnused(corBreakpoint);
		}

		// Create new breakpoints
		var result = new List<BreakpointInfo>();
		foreach (var request in breakpoints)
		{
			var bp = _breakpointManager.CreateBreakpoint(filePath, request.Line, request.Column, request.Condition, request.HitCondition);
			if (ConditionContainsLambda(request.Condition))
			{
				bp.Binding = new BreakpointBinding.Rejected(LambdaConditionNotSupportedMessage);
				result.Add(bp);
				continue;
			}

			// Without a process the breakpoint stays unbound until modules load
			if (CorProcess is not null) TryBindBreakpoint(bp);

			result.Add(bp);
		}

		return result;
	}

	private const string LambdaConditionNotSupportedMessage = "Lambda expressions are not supported in breakpoint conditions.";

	private static bool ConditionContainsLambda(string? condition) =>
		condition is not null && SyntaxFactory.ParseExpression(condition).DescendantNodesAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax);

	public List<BreakpointInfo> SetFunctionBreakpoints(SharpDbgFunctionBreakpointRequest[] breakpoints)
	{
		foreach (var bp in _breakpointManager.GetFunctionBreakpoints())
		{
			foreach (var corBreakpoint in bp.CorBreakpoints)
			{
				try { corBreakpoint.Activate(false); }
				catch (Exception ex) { _logger?.Invoke($"Error deactivating function breakpoint: {ex.Message}"); }
			}
		}
		_breakpointManager.ClearFunctionBreakpoints();

		var result = new List<BreakpointInfo>();
		foreach (var request in breakpoints)
		{
			var bp = _breakpointManager.CreateFunctionBreakpoint(request.Name, request.Condition, request.HitCondition);
			if (ConditionContainsLambda(request.Condition))
			{
				bp.Binding = new BreakpointBinding.Rejected(LambdaConditionNotSupportedMessage);
				result.Add(bp);
				continue;
			}
			try
			{
				_ = FunctionBreakpointPattern.Parse(request.Name);
				foreach (var module in _modules.Values)
				{
					TryBindFunctionBreakpoint(bp, module);
				}
				if (bp.Binding is BreakpointBinding.Unbound) bp.Binding = new BreakpointBinding.Unbound(CorProcess is null
					? BreakpointInfo.NotProcessedMessage
					: $"No functions matching '{request.Name}' were found.");
			}
			catch (ArgumentException ex)
			{
				// The name is not a valid function breakpoint pattern, so no module could ever match it
				bp.Binding = new BreakpointBinding.Rejected(ex.Message);
			}
			result.Add(bp);
		}
		return result;
	}

	/// <summary>
	/// Get all threads
	/// </summary>
	public List<(int id, string name)> GetThreads()
	{
		var result = new List<(int, string)>();
		if (CorProcess is null) return result;

		try
		{
			foreach (var (id, threadInfo) in _threads)
			{
				threadInfo.Name ??= threadInfo.Thread.GetThreadNameOrNull();
				result.Add((id, threadInfo.Name ?? (id == _mainThreadId ? "Main Thread" : "<No Name>")));
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error getting threads: {ex.Message}");
		}

		return result;
	}

	/// <summary>
	/// Get stack trace for a thread
	/// </summary>
	public List<StackFrameInfo> GetStackTrace(int threadIdInt, int startFrame = 0, int? levels = null)
	{
		var result = new List<StackFrameInfo>();

		if (!_threads.TryGetValue(threadIdInt, out var threadInfo))
		{
			return result;
		}
		var thread = threadInfo.Thread;

		try
		{
			var endFrame = levels is null ? long.MaxValue : (long)startFrame + levels.Value;
			var index = 0;

			// Physical frames come first and are enumerated lazily, so a request for the topmost frames does not
			// have to walk the entire stack. We store the IL frames so their synthetic async caller frames can be
			// appended after all physical frames, once enumeration reaches past them.
			var ilFramesForSyntheticAsyncFrames = new List<(int physicalDepth, ICorDebugILFrame frame)>();
			var threadId = new ThreadId(threadIdInt);
			foreach (var (physicalDepth, frame) in EnumerateFramesForThread(thread).AsValueEnumerable().Index())
			{
				if (index >= endFrame) return result;
				if (index >= startFrame)
				{
					var frameId = _frameReferenceManager.GetOrCreateFrameId(threadId, new FrameStackDepth(physicalDepth));
					result.Add(CreateStackFrameInfo(frameId, frame, false));
				}
				if (frame is ICorDebugILFrame ilFrame) ilFramesForSyntheticAsyncFrames.Add((physicalDepth, ilFrame));
				index++;
			}

			foreach (var (physicalDepth, ilFrame) in ilFramesForSyntheticAsyncFrames)
			{
				var physicalFrameStackDepth = new FrameStackDepth(physicalDepth);
				foreach (var (syntheticFrameIndex, syntheticFrame) in GetSyntheticAsyncCallerFrames(ilFrame).AsValueEnumerable().Index())
				{
					if (index >= endFrame) return result;
					if (index < startFrame)
					{
						index++;
						continue;
					}
					index++;

					var syntheticFrameId = _frameReferenceManager.GetOrCreateSyntheticAsyncFrameId(threadId, physicalFrameStackDepth, syntheticFrameIndex, syntheticFrame);
					result.Add(CreateSyntheticAsyncStackFrameInfo(syntheticFrameId, threadId, physicalFrameStackDepth, syntheticFrame, decompileIfNeeded: false));
				}
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error getting stack trace: {ex.Message}");
		}

		return result;
	}

	public StackFrameInfo ResolveStackFrame(int frameId)
	{
		var syntheticFrame = _frameReferenceManager.GetSyntheticAsyncFrameById(frameId);
		if (syntheticFrame is not null) return CreateSyntheticAsyncStackFrameInfo(frameId, syntheticFrame.Value.threadId, syntheticFrame.Value.physicalFrameStackDepth, syntheticFrame.Value.frame, decompileIfNeeded: true);
		var frameInfo = _frameReferenceManager.GetFrameInfoById(frameId) ?? throw new ArgumentException($"Unknown stack frame ID '{frameId}'.", nameof(frameId));
		var frame = GetFrameForThreadIdAndStackDepth(frameInfo.threadId, frameInfo.frameStackDepth);
		if (frame is not ICorDebugILFrame) throw new InvalidOperationException($"Stack frame '{frameId}' cannot be resolved because it is not an IL frame.");

		return CreateStackFrameInfo(frameId, frame, true);
	}

	private StackFrameInfo CreateStackFrameInfo(int frameId, ICorDebugFrame frame, bool decompileIfNeeded)
	{
		var stackFrameInfo = new StackFrameInfo
		{
			Id = frameId,
			Name = null!,
			Line = 0,
			EndLine = null,
			Column = 0,
			EndColumn = null,
			Source = null,
			IsUserCode = false,
			IsResolved = true,
			DecompiledSourceInfo = null
		};

		if (frame is ICorDebugILFrame ilFrame)
		{
			var function = ilFrame.Function;
			var module = _modules[function.Module.BaseAddress];
			// returns null if the function is not a state machine method
			var kickoffMethodToken = module.MetadataReader.GetStateMachineKickoffMethodToken(function.Token);
			stackFrameInfo.Name = GetMethodFormattedName(module, kickoffMethodToken ?? function.Token, ilFrame.TypeParameters);
			var sourceInfo = GetSourceInfoAtFrame(ilFrame, decompileIfNeeded);
			stackFrameInfo.IsUserCode = module.IsUserCode;
			stackFrameInfo.IsResolved = module.MetadataReader.HasSymbols;
			if (sourceInfo is not null)
			{
				stackFrameInfo.Line = sourceInfo.Value.StartLine;
				stackFrameInfo.EndLine = sourceInfo.Value.EndLine;
				stackFrameInfo.Column = sourceInfo.Value.StartColumn;
				stackFrameInfo.EndColumn = sourceInfo.Value.EndColumn;
				stackFrameInfo.Source = sourceInfo.Value.FilePath;
				stackFrameInfo.DecompiledSourceInfo = sourceInfo.Value.DecompiledSourceInfo;
			}
		}
		else if (frame is ICorDebugInternalFrame internalFrame)
		{
			stackFrameInfo.Name = internalFrame.FrameType.ToDisplayName();
		}
		else if (frame is ICorDebugNativeFrame)
		{
			stackFrameInfo.Name = "[Native Frame]";
		}
		else throw new ArgumentOutOfRangeException(nameof(frame), "Unknown frame type");

		return stackFrameInfo;
	}

	private StackFrameInfo CreateSyntheticAsyncStackFrameInfo(int frameId, ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame, bool decompileIfNeeded)
	{
		var source = GetSyntheticAsyncFrameSourceInfo(frame, threadId, physicalFrameStackDepth, decompileIfNeeded);
		return new StackFrameInfo
		{
			Id = frameId,
			Name = frame.Name,
			Line = source?.StartLine ?? 0,
			EndLine = source?.EndLine,
			Column = source?.StartColumn ?? 0,
			EndColumn = source?.EndColumn,
			Source = source?.FilePath,
			IsUserCode = frame.Module.IsUserCode,
			IsResolved = frame.Module.MetadataReader.HasSymbols,
			DecompiledSourceInfo = source?.DecompiledSourceInfo
		};
	}

	/// <summary>
	/// Get scopes for a stack frame
	/// </summary>
	public List<ScopeInfo> GetScopes(int frameId)
	{
		var result = new List<ScopeInfo>();

		var syntheticFrame = _frameReferenceManager.GetSyntheticAsyncFrameById(frameId);
		if (syntheticFrame is not null)
		{
			var syntheticLocalsReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.SyntheticAsyncScope,
				syntheticFrame.Value.frame.StateMachine, syntheticFrame.Value.threadId, syntheticFrame.Value.physicalFrameStackDepth, null));
			result.Add(new ScopeInfo { Name = "Locals", VariablesReference = syntheticLocalsReference, Expensive = false });
			return result;
		}
		var frameInfo = _frameReferenceManager.GetFrameInfoById(frameId);
		if (frameInfo is not var (threadId, frameStackDepth)) return result;
		var frame = GetFrameForThreadIdAndStackDepth(threadId, frameStackDepth);
		if (frame is not ICorDebugILFrame ilFrame) return result;

		var localVariables = ilFrame.LocalVariables;
		var arguments = ilFrame.Arguments;
		var thread = _threads.GetValueOrDefault(threadId.Value)?.Thread;
		Guard.Against.Null(thread);
		var hasCurrentException = thread.TryGetCurrentException(out _) is Cor.S_OK;
		if (localVariables.Length is 0 && arguments.Length is 0 && !hasCurrentException) return result;

		// can this just be the same reference?
		var localsRef = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.Scope, null, threadId, frameStackDepth, null));
		result.Add(new ScopeInfo
		{
			Name = "Locals",
			VariablesReference = localsRef,
			Expensive = false
		});
		return result;
	}

	/// <summary>
	/// Get variables for a scope
	/// </summary>
	public async Task<List<VariableInfo>> GetVariables(int variablesReferenceInt)
	{
		var result = new List<VariableInfo>();

		var variablesReferenceNullable = _variableManager.GetReference(variablesReferenceInt);
		if (variablesReferenceNullable is not {} variablesReference) throw new ArgumentException("Invalid variables reference");
		try
		{
			if (variablesReference.ReferenceKind is StoredReferenceKind.Scope)
			{
				var ilFrame = GetIlFrameForThreadIdAndStackDepth(variablesReference.ThreadId, variablesReference.FrameStackDepth);
				var corDebugFunction = ilFrame.Function;
				var module = _modules[corDebugFunction.Module.BaseAddress];
				await AddCurrentException(result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
				var classContainingHoistedLocalsValue = await AddArguments(module, corDebugFunction, result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
				await AddLocalVariables(module, corDebugFunction, result, variablesReference.ThreadId, variablesReference.FrameStackDepth, classContainingHoistedLocalsValue);
			}
			else if (variablesReference.ReferenceKind is StoredReferenceKind.SyntheticAsyncScope)
			{
				var stateMachine = variablesReference.ObjectValue!;
				await AddMembers(stateMachine, stateMachine.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
			}
			else if (variablesReference.ReferenceKind is StoredReferenceKind.StackVariable)
			{
				await AddStackVariableChildren(variablesReference, result);
			}
			else if (variablesReference.ReferenceKind is StoredReferenceKind.StaticClassVariable)
			{
				var objectValue = variablesReference.ObjectValue!.UnwrapDebugValueToObject();
				await AddStaticMembers(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
			}
			else if (variablesReference.ReferenceKind is StoredReferenceKind.RawView)
			{
				var objectValue = variablesReference.ObjectValue!.UnwrapDebugValueToObject();
				await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
			}
			else if (variablesReference.ReferenceKind is StoredReferenceKind.EnumerableResults)
			{
				await AddEnumerableResults(variablesReference, result);
			}
			else if (variablesReference.ReferenceKind is StoredReferenceKind.ArrayRange)
			{
				var arrayValue = (ICorDebugArrayValue)variablesReference.ObjectValue!.UnwrapDebugValue();
				await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, variablesReference.ArrayIndices, variablesReference.ArrayStartOffset, variablesReference.ArrayCount, variablesReference.ObjectValue);
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error getting variables: {ex.Message}, {ex}");
			throw;
		}

		return result;
	}

	/// <summary>
	/// Evaluate an expression
	/// </summary>
	public async Task<VariableInfo> Evaluate(string expression, int? frameId)
	{
		_logger?.Invoke($"Evaluate: {expression}");
		if (frameId is null or 0) throw new InvalidOperationException("Frame ID is required for evaluation");
		if (_frameReferenceManager.GetSyntheticAsyncFrameById(frameId.Value) is not null)
			throw new InvalidOperationException("Expression evaluation is not supported for synthetic async caller frames");

		var frameInfo = _frameReferenceManager.GetFrameInfoById(frameId.Value);
		if (frameInfo is not var (threadId, frameStackDepth)) throw new InvalidOperationException("Frame ID does not exist");
		var thread = _threads.GetValueOrDefault(threadId.Value)?.Thread;
		Guard.Against.Null(thread);

		var evalContext = new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth);
		ArgumentNullException.ThrowIfNull(_expressionEvaluator);
		using var result = await _expressionEvaluator.Evaluate(expression, evalContext);

		if (result.Error is not null)
		{
			_logger?.Invoke($"Evaluation error: {result.Error}");
			return new VariableInfo
			{
				Name = null!,
				Value = result.Error,
				Type = null,
				PresentationHint = new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation },
				VariablesReference = 0
			};
		}
		var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(result.Value!, threadId, frameStackDepth, true);
		VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
		var variablesReference = GetVariablesReference(result.Value!, friendlyTypeName, threadId, frameStackDepth, debuggerProxyInstance);
		var variableInfo = new VariableInfo
		{
			Name = null!,
			Value = value,
			Type = friendlyTypeName,
			PresentationHint = variablePresentationHint,
			VariablesReference = variablesReference
		};
		if (variablesReference != 0) result.RelinquishResultHandleOwnership();
		return variableInfo;
	}

	/// <summary>
	/// Terminate the debugged process
	/// </summary>
	public void Terminate()
	{
		_logger?.Invoke("Terminate");
		if (CorProcess is not null)
		{
			try
			{
				// CORDBG_E_PROCESS_NOT_SYNCHRONIZED is thrown if attempting to terminate a running process. We need to stop it first.
				if (CorProcess.TryIsRunning(out var isRunning) is Cor.S_OK && isRunning)
				{
					var stopResult = CorProcess.TryStop(0);
					if (stopResult is not (Cor.S_OK or Cor.CORDBG_E_PROCESS_TERMINATED)) _logger?.Invoke($"Error stopping process before terminating it: {stopResult}");
				}
				CorProcess.Terminate(0);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Error terminating process: {ex.Message}");
			}
		}
		Dispose();
	}

	/// <summary>
	/// Disconnect from the debuggee and Dispose
	/// </summary>
	public void Disconnect(bool terminateDebuggee)
	{
		_logger?.Invoke($"Disconnect (terminate: {terminateDebuggee})");

		if (terminateDebuggee)
		{
			Terminate();
		}
		else
		{
			if (CorProcess is { } process && process.TryIsRunning(out var isRunning) is Cor.S_OK && isRunning)
			{
				var hResult = process.TryStop(0);
				if (hResult is not (Cor.S_OK or Cor.CORDBG_E_PROCESS_TERMINATED)) _logger?.Invoke($"Error stopping process during disconnect: {hResult}");
			}
			Dispose();
		}
	}

	public async Task<ExceptionInfo> ExceptionInfo(ThreadId threadId)
	{
		_logger?.Invoke($"ExceptionInfo for thread {threadId.Value}");
		var thread = CorProcess!.GetThread(threadId.Value);
		if (thread.TryGetCurrentException(out var currentException) is not Cor.S_OK)
		{
			_logger?.Invoke("No current exception");
			throw new InvalidOperationException("No current exception on thread");
		}

		var frameStackDepth = new FrameStackDepth(0);
		var details = await GetExceptionDetails(currentException, 0);

		var description = $"Exception thrown: '{details.FullTypeName}' in {details.Source}.dll: '{details.Message}'";
		if (details.InnerException is [var firstInner])
		{
			var innermost = firstInner;
			while (innermost.InnerException is [var next]) innermost = next;
			description += $"\n Inner exceptions found, see $exception in variables window for more details.\n Innermost exception \t {innermost.FullTypeName} : {innermost.Message}";
		}

		var exceptionInfo = new ExceptionInfo
		{
			ExceptionId = $"CLR/{details.FullTypeName}",
			Description = description,
			BreakMode = _exceptionBreakModes.GetValueOrDefault(threadId, SharpDbgExceptionBreakMode.Unknown),
			Code = 0,
			Details = details
		};
		return exceptionInfo;

		async Task<ExceptionInfo.ExceptionDetails> GetExceptionDetails(ICorDebugValue exception, int depth)
		{
			var (friendlyTypeName, _, _, _) = await GetValueForCorDebugValueAsync(exception, threadId, frameStackDepth, false);

			var hResult = await GetExceptionPropertyValue(exception, "HResult");
			var source = await GetExceptionPropertyValue(exception, "Source");
			var message = await GetExceptionPropertyValue(exception, "Message");
			var stackTrace = await GetExceptionPropertyValue(exception, "StackTrace");

			var typeNameSpan = friendlyTypeName.AsSpan();
			var lastDot = typeNameSpan.LastIndexOf('.');
			var exceptionTypeNameNoNamespace = lastDot is not -1
				? typeNameSpan[(lastDot + 1)..].ToString()
				: friendlyTypeName;

			// Each level nests the next, as ExceptionDetails is recursive. The depth limit guards against a chain made cyclic via reflection
			List<ExceptionInfo.ExceptionDetails> innerException = [];
			if (depth < MaxInnerExceptionDepth)
			{
				var innerValue = await GetExceptionProperty(exception, "InnerException");
				try
				{
					if (innerValue is not ICorDebugReferenceValue { IsNull: true }) innerException.Add(await GetExceptionDetails(innerValue, depth + 1));
				}
				finally
				{
					if (innerValue is ICorDebugHandleValue handle) handle.TryDispose();
				}
			}

			return new ExceptionInfo.ExceptionDetails
			{
				Message = message,
				TypeName = exceptionTypeNameNoNamespace,
				FullTypeName = friendlyTypeName,
				EvaluateName = "$exception",
				StackTrace = stackTrace,
				InnerException = innerException,
				FormattedDescription = $"**{friendlyTypeName}:** '{message}'",
				HResult = int.Parse(hResult),
				Source = source
			};
		}

		async Task<ICorDebugValue> GetExceptionProperty(ICorDebugValue exception, string propertyName)
		{
			return await exception.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, (ICorDebugILFrame)thread.ActiveFrame, propertyName)
				?? throw new InvalidOperationException($"Exception property '{propertyName}' returned no value");
		}

		async Task<string> GetExceptionPropertyValue(ICorDebugValue exception, string propertyName)
		{
			var propertyValue = await GetExceptionProperty(exception, propertyName);
			try
			{
				var (_, value, _, _) = await GetValueForCorDebugValueAsync(propertyValue, threadId, frameStackDepth, false);
				return value;
			}
			finally
			{
				if (propertyValue is ICorDebugHandleValue handle) handle.TryDispose();
			}
		}
	}

	private const int MaxInnerExceptionDepth = 20;

	public void SetExceptionBreakpoints(IReadOnlyList<SharpDbgExceptionBreakpointRequest> breakpoints)
	{
		_exceptionBreakpoints = breakpoints.ToArray();
	}
}
