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

namespace SharpDbg.Infrastructure.Debugger;

public record SharpDbgBreakpointRequest(int Line, string? Condition = null, string? HitCondition = null, int? Column = null);
public record SharpDbgFunctionBreakpointRequest(string Name, string? Condition = null, string? HitCondition = null);
public record SharpDbgExceptionBreakpointRequest(SharpDbgExceptionBreakpointFilter Filter, string? Condition = null);
public enum SharpDbgExceptionBreakpointFilter { All, UserUnhandled }

public partial class ManagedDebugger
{
	// Store launch info for deferred attach in ConfigurationDone
	private LaunchInfo? _pendingLaunchInfo;
	private RemoteAttachInfo? _pendingRemoteAttachInfo;

	/// <summary>
	/// Stores the launch request info for use in handling ConfigurationDone
	/// </summary>
	public void Launch(LaunchInfo launchInfo, bool justMyCode)
	{
		EnsureNoProcessBeingDebugged();
		_logger?.Invoke($"Launching program: {launchInfo.Program} {string.Join(' ', launchInfo.Arguments)}");
		_justMyCode = justMyCode;
		_pendingLaunchInfo = launchInfo;
	}

	/// <summary>
	/// Actually perform the launch using DbgShim APIs
	/// </summary>
	private async Task PerformLaunch()
	{
		if (_pendingLaunchInfo is null)
		{
			_logger?.Invoke("No pending launch to perform");
			return;
		}

		var launchInfo = _pendingLaunchInfo;
		_pendingLaunchInfo = null;

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
		_debuggeeProcess = process;

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

		_corDebug = await ClrDebugExtensions.Automatic(processId, true);
		_corDebug.Initialize();
		_corDebug.SetManagedHandler(_callbacks);

		_process = _corDebug.DebugActiveProcess(processId, false);
		_isAttached = true;
		ConfigureExceptionCallbacks();

		_logger?.Invoke($"Successfully attached to process: {processId}");
		OnProcessStarted?.Invoke(processId, launchInfo.Program);
		SendAllBreakpointEvents();
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
		if (bp.CorBreakpoint is not null)
		{
			try
			{
				bp.CorBreakpoint.Activate(false);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Error deactivating breakpoint: {ex.Message}");
			}
		}
		return _breakpointManager.RemoveBreakpoint(id);
	}

	/// <summary>
	/// Store process ID for later attach (actual attach happens in ConfigurationDone)
	/// </summary>
	public void Attach(int processId, bool justMyCode)
	{
		EnsureNoProcessBeingDebugged();
		_logger?.Invoke($"Storing attach target: {processId}");
		_justMyCode = justMyCode;
		_pendingAttachProcessId = processId;
	}

	public void AttachRemote(RemoteAttachInfo remoteAttachInfo, bool justMyCode)
	{
		EnsureNoProcessBeingDebugged();
		_justMyCode = justMyCode;
		_pendingRemoteAttachInfo = remoteAttachInfo;
		_isRemoteAttach = true;
	}

	/// <summary>
	/// Called when DAP configuration is complete - performs deferred launch or attach
	/// </summary>
	public async Task ConfigurationDone()
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke("ConfigurationDone");

		if (_pendingLaunchInfo is { LaunchRequestConsoleType: LaunchRequestConsoleType.ExternalTerminal or LaunchRequestConsoleType.IntegratedTerminal })
		{
			var launchedProcessId = await Task.Run(() => SendRunInTerminalRequest.Invoke(_pendingLaunchInfo)); // get off the dispatcher thread
			_pendingLaunchInfo = null;
			PerformAttach(launchedProcessId);
			await DiagnosticClientHelper.DiagnosticClientResumeRuntime(launchedProcessId);
		}
		else if (_pendingLaunchInfo is not null) // If we have a pending launch, perform it
		{
			await PerformLaunch();
		}
		else if (_pendingRemoteAttachInfo is not null)
		{
			PerformRemoteAttach(_pendingRemoteAttachInfo);
			_pendingRemoteAttachInfo = null;
		}
		// Otherwise check for pending attach
		else if (_pendingAttachProcessId.HasValue)
		{
			PerformAttach(_pendingAttachProcessId.Value);
			_pendingAttachProcessId = null;
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
		Guard.Against.Null(_process);
		_variableManager.ClearAndDisposeHandleValues();
		_frameReferenceManager.Clear();

		var result = _process.TryContinue(false);
		if (result is Cor.CORDBG_E_SUPERFLOUS_CONTINUE) return;
		Marshal.ThrowExceptionForHR(result);
	}

	private void ContinueWithVariableClear()
	{
		Guard.Against.Null(_process);
		_variableManager.ClearAndDisposeHandleValues();
		_frameReferenceManager.Clear();

		_process.Continue(false);
	}

	/// <summary>
	/// Pause execution
	/// </summary>
	public void Pause()
	{
		_logger?.Invoke("Pause");
		Guard.Against.Null(_process);
		if (_process.IsRunning is false) throw new InvalidOperationException("The process is not running, so it cannot be paused - it has either already stopped or has not finished starting");
		_process.Stop(0);
		_asyncStepper?.Disable();
	}

	/// <summary>
	/// Step to the next line
	/// </summary>
	public async Task StepNext(int threadId)
	{
		_logger?.Invoke($"StepNext on thread {threadId}");
		if (_threads.TryGetValue(threadId, out var thread))
		{
			var frame = thread.ActiveFrame;
			if (frame is not ICorDebugILFrame ilFrame) throw new InvalidOperationException("Active frame is not an IL frame");
			if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");

			// Try async stepping first
			if (_asyncStepper is not null)
			{
				var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepOver);
				if (handledByAsyncStepper)
				{
					if (useSimpleStepper is false)
					{
						ContinueWithVariableClear();
						return;
					}
				}
			}

			var stepper = SetupStepper(thread, AsyncStepper.StepType.StepOver);
			ContinueWithVariableClear();
		}
	}

	/// <summary>
	/// Step into
	/// </summary>
	public async Task StepIn(int threadId)
	{
		_logger?.Invoke($"StepIn on thread {threadId}");
		if (_threads.TryGetValue(threadId, out var thread))
		{
			var frame = thread.ActiveFrame;
			if (frame is not null)
			{
				// Try async stepping first
				if (_asyncStepper is not null)
				{
					var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepIn);
					if (handledByAsyncStepper)
					{
						if (useSimpleStepper is false)
						{
							ContinueWithVariableClear();
							return;
						}
					}
				}

				var stepper = SetupStepper(thread, AsyncStepper.StepType.StepIn);
				ContinueWithVariableClear();
			}
		}
	}

	/// <summary>
	/// Step out
	/// </summary>
	public async Task StepOut(int threadId)
	{
		_logger?.Invoke($"StepOut on thread {threadId}");
		if (_threads.TryGetValue(threadId, out var thread))
		{
			var frame = thread.ActiveFrame;
			if (frame is not null)
			{
				// Try async stepping first
				if (_asyncStepper is not null)
				{
					var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepOut);
					if (handledByAsyncStepper)
					{
						if (useSimpleStepper is false)
						{
							ContinueWithVariableClear();
							return;
						}
					}
				}

				var stepper = SetupStepper(thread, AsyncStepper.StepType.StepOut);
				if (stepper is not null)
				{
					ContinueWithVariableClear();
				}
			}
		}
	}

	/// <summary>
	/// Set breakpoints for a source file with optional conditions
	/// </summary>
	public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, SharpDbgBreakpointRequest[] breakpoints)
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke($"SetBreakpoints: {filePath}, breakpoints: {string.Join(",", breakpoints.Select(b => $"L{b.Line} {(b.Column is not null ? $"C{b.Column}" : null)} {(b.Condition is not null ? $"[{b.Condition}]" : null)}"))}");

		// Deactivate and clear existing breakpoints for this file
		var existingBreakpoints = _breakpointManager.GetBreakpointsForFile(filePath);
		foreach (var bp in existingBreakpoints)
		{
			if (bp.CorBreakpoint is not null)
			{
				try
				{
					bp.CorBreakpoint.Activate(false);
				}
				catch (Exception ex)
				{
					_logger?.Invoke($"Error deactivating breakpoint: {ex.Message}");
				}
			}
		}
		_breakpointManager.ClearBreakpointsForFile(filePath);

		// Create new breakpoints
		var result = new List<BreakpointManager.BreakpointInfo>();
		foreach (var request in breakpoints)
		{
			var bp = _breakpointManager.CreateBreakpoint(filePath, request.Line, request.Column, request.Condition, request.HitCondition);
			if (request.Condition is not null && SyntaxFactory.ParseExpression(request.Condition).DescendantNodesAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax))
			{
				bp.Message = "Lambda expressions are not supported in breakpoint conditions.";
				result.Add(bp);
				continue;
			}

			// Try to bind the breakpoint if we have a process
			if (_process is not null)
			{
				TryBindBreakpoint(bp);
			}
			else
			{
				// No process yet, mark as pending
				bp.Message = "Breakpoint has not been processed by the debugger.";
			}

			result.Add(bp);
		}

		return result;
	}

	public List<BreakpointManager.BreakpointInfo> SetFunctionBreakpoints(SharpDbgFunctionBreakpointRequest[] breakpoints)
	{
		foreach (var bp in _breakpointManager.GetFunctionBreakpoints())
		{
			foreach (var binding in bp.FunctionBindings)
			{
				try { binding.CorBreakpoint.Activate(false); }
				catch (Exception ex) { _logger?.Invoke($"Error deactivating function breakpoint: {ex.Message}"); }
			}
		}
		_breakpointManager.ClearFunctionBreakpoints();

		var result = new List<BreakpointManager.BreakpointInfo>();
		foreach (var request in breakpoints)
		{
			var bp = _breakpointManager.CreateFunctionBreakpoint(request.Name, request.Condition, request.HitCondition);
			try
			{
				_ = FunctionBreakpointPattern.Parse(request.Name);
				foreach (var module in _modules.Values)
				{
					TryBindFunctionBreakpoint(bp, module);
				}
				if (!bp.Verified) bp.Message = _process is null
					? "Breakpoint has not been processed by the debugger."
					: $"No functions matching '{request.Name}' were found.";
			}
			catch (ArgumentException ex)
			{
				bp.Message = ex.Message;
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
		if (_process is null) return result;

		try
		{
			var threads = _process.EnumerateThreads();
			foreach (var thread in threads)
			{
				result.Add((thread.Id, thread.GetThreadName()));
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
	public List<StackFrameInfo> GetStackTrace(int threadId, int startFrame = 0, int? levels = null)
	{
		var result = new List<StackFrameInfo>();

		if (!_threads.TryGetValue(threadId, out var thread))
		{
			return result;
		}

		try
		{
			var frames = EnumerateFramesForThread(thread);
			var filterFrames = frames.AsValueEnumerable().Skip(startFrame).Take(levels ?? int.MaxValue);

			foreach (var (index, frame) in filterFrames.Index())
			{
				var frameId = _frameReferenceManager.GetOrCreateFrameId(new ThreadId(threadId), new FrameStackDepth(startFrame + index));
				result.Add(CreateStackFrameInfo(frameId, frame, false));
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
			EndLine = 0,
			Column = 0,
			EndColumn = 0,
			Source = null,
			IsResolved = true,
			DecompiledSourceInfo = null
		};

		if (frame is ICorDebugILFrame ilFrame)
		{
			var function = ilFrame.Function;
			var module = _modules[function.Module.BaseAddress];
			// returns null if the function is not a state machine method
			var kickoffMethodToken = module.MetadataReader.GetStateMachineKickoffMethodToken(function.Token);
			stackFrameInfo.Name = GetFunctionFormattedName(ilFrame, kickoffMethodToken);
			var sourceInfo = GetSourceInfoAtFrame(ilFrame, decompileIfNeeded);
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

	/// <summary>
	/// Get scopes for a stack frame
	/// </summary>
	public List<ScopeInfo> GetScopes(int frameId)
	{
		var result = new List<ScopeInfo>();

		var frameInfo = _frameReferenceManager.GetFrameInfoById(frameId);
		if (frameInfo is not var (threadId, frameStackDepth)) return result;
		var frame = GetFrameForThreadIdAndStackDepth(threadId, frameStackDepth);
		if (frame is not ICorDebugILFrame ilFrame) return result;

		var localVariables = ilFrame.LocalVariables;
		var arguments = ilFrame.Arguments;
		var thread = _threads.GetValueOrDefault(threadId.Value);
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
			else if (variablesReference.ReferenceKind is StoredReferenceKind.StackVariable)
			{
				if (variablesReference.DebuggerProxyInstance is not null)
				{
					// get the public members of the debugger proxy instance instead
					var objectValue = variablesReference.DebuggerProxyInstance.UnwrapDebugValueToObject();
					await AddMembersAndStaticPseudoVariable(variablesReference.DebuggerProxyInstance, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, false);
					var rawValueVariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.RawView, variablesReference.ObjectValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, null));
					var rawValuePseudoVariable = new VariableInfo
					{
						Name = "Raw View",
						Value = "",
						Type = "",
						PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
						VariablesReference = rawValueVariablesReference
					};
					result.Add(rawValuePseudoVariable);
					return result;
				}
				var unwrappedDebugValue = variablesReference.ObjectValue!.UnwrapDebugValue();

				if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
				{
					await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, referenceValue: variablesReference.ObjectValue);
				}
				else if (unwrappedDebugValue is ICorDebugObjectValue objectValue)
				{
					if (IsEnumerable(objectValue.ExactType))
					{
						AddEnumerablePseudoVariables(variablesReference, result);
					}
					else
					{
						await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
					}
				}
				else
				{
					throw new ArgumentOutOfRangeException(nameof(unwrappedDebugValue));
				}
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

		var frameInfo = _frameReferenceManager.GetFrameInfoById(frameId.Value);
		if (frameInfo is not var (threadId, frameStackDepth)) throw new InvalidOperationException("Frame ID does not exist");
		var thread = _threads.GetValueOrDefault(threadId.Value);
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
		if (_process is not null)
		{
			try
			{
				// CORDBG_E_PROCESS_NOT_SYNCHRONIZED is thrown if attempting to terminate a running process. We need to stop it first.
				if (_process.TryIsRunning(out var isRunning) is Cor.S_OK && isRunning)
				{
					var stopResult = _process.TryStop(0);
					if (stopResult is not (Cor.S_OK or Cor.CORDBG_E_PROCESS_TERMINATED)) _logger?.Invoke($"Error stopping process before terminating it: {stopResult}");
				}
				_process.Terminate(0);
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
			if (_process is not null && _isAttached && _process?.TryIsRunning(out var isRunning) is Cor.S_OK && isRunning)
			{
				var hResult = _process.TryStop(0);
				if (hResult is not (Cor.S_OK or Cor.CORDBG_E_PROCESS_TERMINATED)) _logger?.Invoke($"Error stopping process during disconnect: {hResult}");
			}
			Dispose();
		}
	}

	public async Task<ExceptionInfo> ExceptionInfo(ThreadId threadId)
	{
		_logger?.Invoke($"ExceptionInfo for thread {threadId.Value}");
		var thread = _process!.GetThread(threadId.Value);
		if (thread.TryGetCurrentException(out var currentException) is not Cor.S_OK)
		{
			_logger?.Invoke("No current exception");
			throw new InvalidOperationException("No current exception on thread");
		}

		var frameStackDepth = new FrameStackDepth(0);
		var (friendlyTypeName, _, _, _) = await GetValueForCorDebugValueAsync(currentException, threadId, frameStackDepth, false);

		var hResult = await GetExceptionPropertyValue("HResult");
		var source = await GetExceptionPropertyValue("Source");
		var message = await GetExceptionPropertyValue("Message");
		var stackTrace = await GetExceptionPropertyValue("StackTrace");

		var typeNameSpan = friendlyTypeName.AsSpan();
		var lastDot = typeNameSpan.LastIndexOf('.');
		var exceptionTypeNameNoNamespace = lastDot is not -1
			? typeNameSpan[(lastDot + 1)..].ToString()
			: friendlyTypeName;

		var exceptionInfo = new ExceptionInfo
		{
			ExceptionId = $"CLR/{friendlyTypeName}",
			Description = $"Exception thrown: '{friendlyTypeName}' in {source}.dll: '{message}'",
			BreakMode = _exceptionBreakModes.GetValueOrDefault(threadId, SharpDbgExceptionBreakMode.Unknown),
			Code = 0,
			Details = new ExceptionInfo.ExceptionDetails
			{
				Message = message,
				TypeName = exceptionTypeNameNoNamespace,
				FullTypeName = friendlyTypeName,
				EvaluateName = "$exception",
				StackTrace = stackTrace,
				InnerException = [],
				FormattedDescription = $"**{friendlyTypeName}:** '{message}'",
				HResult = int.Parse(hResult),
				Source = source
			}
		};
		return exceptionInfo;

		async Task<string> GetExceptionPropertyValue(string propertyName)
		{
			var propertyValue = await currentException.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, (ICorDebugILFrame)thread.ActiveFrame, propertyName)
				?? throw new InvalidOperationException($"Exception property '{propertyName}' returned no value");
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

	public void SetExceptionBreakpoints(IReadOnlyList<SharpDbgExceptionBreakpointRequest> breakpoints)
	{
		_exceptionBreakpoints = breakpoints.ToArray();
	}
}
