using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
using SharpDbg.Infrastructure.Debugger.PresentationHintModels;
using SharpDbg.Infrastructure.Debugger.ResponseModels;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Request to set a breakpoint with optional condition and hit condition
/// </summary>
public record BreakpointRequest(int Line, string? Condition = null, string? HitCondition = null);

public partial class ManagedDebugger
{
	// Store launch info for deferred attach in ConfigurationDone
	private string? _pendingLaunchProgram;
	private string[]? _pendingLaunchArgs;
	private string? _pendingLaunchWorkingDirectory;
	private bool _pendingLaunchStopAtEntry;

	/// <summary>
	/// Launch a process to debug using DbgShim's CreateProcessForLaunch.
	/// This properly launches the process suspended and waits for CLR startup.
	/// </summary>
	public void Launch(string program, string[] args, string? workingDirectory, Dictionary<string, string>? env, bool stopAtEntry)
	{
		_logger?.Invoke($"Launching program: {program} {string.Join(' ', args ?? Array.Empty<string>())}");

		// Store launch parameters for deferred execution in ConfigurationDone
		_pendingLaunchProgram = program;
		_pendingLaunchArgs = args;
		_pendingLaunchWorkingDirectory = workingDirectory;
		_pendingLaunchStopAtEntry = stopAtEntry;
	}

	/// <summary>
	/// Actually perform the launch using DbgShim APIs
	/// </summary>
	private void PerformLaunch()
	{
		if (_pendingLaunchProgram == null)
		{
			_logger?.Invoke("No pending launch to perform");
			return;
		}

		var program = _pendingLaunchProgram;
		var args = _pendingLaunchArgs ?? Array.Empty<string>();
		var workingDirectory = _pendingLaunchWorkingDirectory;
		var stopAtEntry = _pendingLaunchStopAtEntry;

		// Clear pending launch
		_pendingLaunchProgram = null;
		_pendingLaunchArgs = null;
		_pendingLaunchWorkingDirectory = null;

		// Build command line: "program" "arg1" "arg2" ...
		var commandLine = new StringBuilder();
		commandLine.Append('"').Append(program).Append('"');
		foreach (var arg in args)
		{
			commandLine.Append(' ').Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
		}

		_logger?.Invoke($"Creating process for launch: {commandLine}");

		// Initialize DbgShim
		var dbgShimPath = DbgShimResolver.Resolve();
		var dbgshim = new DbgShim(NativeLibrary.Load(dbgShimPath));

		// Create process suspended
		var result = dbgshim.CreateProcessForLaunch(
			commandLine.ToString(),
			bSuspendProcess: true,
			lpEnvironment: IntPtr.Zero, // TODO: support environment variables
			lpCurrentDirectory: workingDirectory);

		var processId = result.ProcessId;
		var resumeHandle = result.ResumeHandle;

		_logger?.Invoke($"Process created suspended with PID: {processId}");

		// Register for runtime startup callback
		IntPtr unregisterToken = IntPtr.Zero;
		var wait = new AutoResetEvent(false);
		CorDebug? cordebug = null;
		HRESULT callbackHr = HRESULT.E_FAIL;

		try
		{
			unregisterToken = dbgshim.RegisterForRuntimeStartup(processId, (pCordb, parameter, hr) =>
			{
				cordebug = pCordb;
				callbackHr = hr;
				wait.Set();
			});

			// Resume the process so CLR can start
			_logger?.Invoke("Resuming process...");
			dbgshim.ResumeProcess(resumeHandle);
			dbgshim.CloseResumeHandle(resumeHandle);

			// Wait for CLR startup (with timeout)
			if (!wait.WaitOne(TimeSpan.FromSeconds(30)))
			{
				throw new InvalidOperationException("Timeout waiting for CLR to start in target process");
			}
		}
		finally
		{
			if (unregisterToken != IntPtr.Zero)
			{
				dbgshim.UnregisterForRuntimeStartup(unregisterToken);
			}
		}

		if (cordebug == null)
		{
			throw new DebugException(callbackHr);
		}

		_logger?.Invoke($"CLR started, initializing debugger for PID: {processId}");

		// Initialize debugging session
		_corDebug = cordebug;
		_corDebug.Initialize();
		_corDebug.SetManagedHandler(_callbacks);

		// Attach to the process (it's already waiting for us due to RegisterForRuntimeStartup)
		_process = _corDebug.DebugActiveProcess(processId, false);
		_isAttached = true;
		IsRunning = true;

		_logger?.Invoke($"Successfully attached to process: {processId}");
	}

	public bool RemoveBreakpoint(int id)
	{
		_logger?.Invoke($"RemoveBreakpoint: {id}");
		var bp = _breakpointManager.GetBreakpoint(id);
		if (bp == null) return false;
		if (bp.CorBreakpoint != null)
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
	public void Attach(int processId)
	{
		_logger?.Invoke($"Storing attach target: {processId}");
		_pendingAttachProcessId = processId;
	}

	/// <summary>
	/// Called when DAP configuration is complete - performs deferred launch or attach
	/// </summary>
	public void ConfigurationDone()
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke("ConfigurationDone");

		// If we have a pending launch, perform it
		if (_pendingLaunchProgram != null)
		{
			PerformLaunch();
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
	public void Continue()
	{
		_logger?.Invoke("Continue");
		if (_rawProcess != null)
		{
			IsRunning = true;
			_variableManager.ClearAndDisposeHandleValues();
			_rawProcess.Continue(false);
		}
	}

	/// <summary>
	/// Pause execution
	/// </summary>
	public void Pause()
	{
		_logger?.Invoke("Pause");
		if (_rawProcess != null && IsRunning)
		{
			_rawProcess.Stop(0);
			IsRunning = false;
			_asyncStepper?.Disable();
		}
	}

	/// <summary>
	/// Step to the next line
	/// </summary>
	public async void StepNext(int threadId)
	{
		_logger?.Invoke($"StepNext on thread {threadId}");
		if (_threads.TryGetValue(threadId, out var thread))
		{
			var frame = thread.ActiveFrame;
			if (frame is not CorDebugILFrame ilFrame) throw new InvalidOperationException("Active frame is not an IL frame");
			if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");

			// Try async stepping first
			if (_asyncStepper is not null)
			{
				var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepOver);
				if (handledByAsyncStepper)
				{
					if (useSimpleStepper is false)
					{
						Continue();
						return;
					}
				}
			}

			var stepper = SetupStepper(thread, AsyncStepper.StepType.StepOver);
			IsRunning = true;
			_variableManager.ClearAndDisposeHandleValues();
			_rawProcess?.Continue(false);
		}
	}

	/// <summary>
	/// Step into
	/// </summary>
	public async void StepIn(int threadId)
	{
		_logger?.Invoke($"StepIn on thread {threadId}");
		if (_threads.TryGetValue(threadId, out var thread))
		{
			var frame = thread.ActiveFrame;
			if (frame != null)
			{
				// Try async stepping first
				if (_asyncStepper is not null)
				{
					var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepIn);
					if (handledByAsyncStepper)
					{
						if (useSimpleStepper is false)
						{
							Continue();
							return;
						}
					}
				}

				var stepper = SetupStepper(thread, AsyncStepper.StepType.StepIn);
				IsRunning = true;
				_variableManager.ClearAndDisposeHandleValues();
				_rawProcess?.Continue(false);
			}
		}
	}

	/// <summary>
	/// Step out
	/// </summary>
	public async void StepOut(int threadId)
	{
		_logger?.Invoke($"StepOut on thread {threadId}");
		if (_threads.TryGetValue(threadId, out var thread))
		{
			var frame = thread.ActiveFrame;
			if (frame != null)
			{
				// Try async stepping first
				if (_asyncStepper is not null)
				{
					var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepOut);
					if (handledByAsyncStepper)
					{
						if (useSimpleStepper is false)
						{
							Continue();
							return;
						}
					}
				}

				var stepper = SetupStepper(thread, AsyncStepper.StepType.StepOut);
				if (stepper != null)
				{
					IsRunning = true;
					_variableManager.ClearAndDisposeHandleValues();
					_rawProcess?.Continue(false);
				}
			}
		}
	}

	/// <summary>
	/// Set breakpoints for a source file without conditions
	/// </summary>
	public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, int[] lines)
	{
		var requests = lines.Select(line => new BreakpointRequest(line)).ToArray();
		return SetBreakpoints(filePath, requests);
	}

	/// <summary>
	/// Set breakpoints for a source file with optional conditions
	/// </summary>
	public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, BreakpointRequest[] breakpoints)
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke($"SetBreakpoints: {filePath}, breakpoints: {string.Join(",", breakpoints.Select(b => $"L{b.Line}" + (b.Condition != null ? $"[{b.Condition}]" : "")))}");

		// Deactivate and clear existing breakpoints for this file
		var existingBreakpoints = _breakpointManager.GetBreakpointsForFile(filePath);
		foreach (var bp in existingBreakpoints)
		{
			if (bp.CorBreakpoint != null)
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
			var bp = _breakpointManager.CreateBreakpoint(filePath, request.Line, request.Condition, request.HitCondition);

			// Try to bind the breakpoint if we have a process
			if (_process != null)
			{
				TryBindBreakpoint(bp);
			}
			else
			{
				// No process yet, mark as pending
				bp.Message = "The breakpoint is pending and will be resolved when debugging starts.";
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
		if (_process == null) return result;

		try
		{
			var threads = _process.EnumerateThreads();
			foreach (var thread in threads)
			{
				result.Add((thread.Id, $"Thread {thread.Id}"));
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
			var chains = thread.EnumerateChains();
			foreach (var chain in chains)
			{
				var frames = chain.Frames;
				var filterFrames = frames.AsValueEnumerable().Skip(startFrame).Take(levels ?? int.MaxValue);

				foreach (var (index, frame) in filterFrames.Index())
				{
					if (frame is CorDebugILFrame ilFrame)
					{
						var function = ilFrame.Function;

						var frameId = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.Scope, null, new ThreadId(threadId), new FrameStackDepth(index), null));
						var module = _modules[function.Module.BaseAddress];
						var line = 0;
						var column = 0;
						var endLine = 0;
						var endColumn = 0;
						string? sourceFilePath = null;
						if (module.SymbolReader is not null)
						{
							var ilOffset = ilFrame.IP.pnOffset;
							var methodToken = function.Token;
							var sourceInfo = module.SymbolReader.GetSourceLocationForOffset(methodToken, ilOffset);
							if (sourceInfo != null)
							{
								line = sourceInfo.Value.startLine;
								column = sourceInfo.Value.startColumn;
								endLine = sourceInfo.Value.endLine;
								endColumn = sourceInfo.Value.endColumn;
								sourceFilePath = sourceInfo.Value.sourceFilePath;
							}
						}

						result.Add(new StackFrameInfo
						{
							Id = frameId,
							Name = GetFunctionFormattedName(function),
							Line = line,
							EndLine =  endLine,
							Column = column,
							EndColumn =  endColumn,
							Source = sourceFilePath
						});
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error getting stack trace: {ex.Message}");
		}

		return result;
	}

	/// <summary>
	/// Get scopes for a stack frame
	/// </summary>
	public List<ScopeInfo> GetScopes(int frameId)
	{
		var result = new List<ScopeInfo>();

		var variablesReference = _variableManager.GetReference(frameId);
		if (variablesReference is null) return result;
		var frame = GetFrameForThreadIdAndStackDepth(variablesReference.Value.ThreadId, variablesReference.Value.FrameStackDepth);


		var localVariables = frame.LocalVariables;
		var arguments = frame.Arguments;
		if (localVariables.Length is 0 && arguments.Length is 0) return result;

		// can this just be the same reference?
		var localsRef = _variableManager.CreateReference(new  VariablesReference(StoredReferenceKind.Scope, null, variablesReference.Value.ThreadId, variablesReference.Value.FrameStackDepth, null));
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
		var ilFrame = GetFrameForThreadIdAndStackDepth(variablesReference.ThreadId, variablesReference.FrameStackDepth);
		try
		{
			if (variablesReference.ReferenceKind is StoredReferenceKind.Scope)
			{
				var corDebugFunction = ilFrame.Function;
				var module = _modules[corDebugFunction.Module.BaseAddress];
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
					var rawValueVariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.StackVariable, variablesReference.ObjectValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, null));
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

				if (unwrappedDebugValue is CorDebugArrayValue arrayValue)
				{
					await AddArrayElements(arrayValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
				}
				else if (unwrappedDebugValue is CorDebugObjectValue objectValue)
				{
					await AddMembersAndStaticPseudoVariable(variablesReference.ObjectValue!, objectValue.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result);
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
	public async Task<(string result, string? type, int variablesReference)> Evaluate(string expression, int? frameId)
	{
		_logger?.Invoke($"Evaluate: {expression}");
		if (frameId is null or 0) throw new InvalidOperationException("Frame ID is required for evaluation");

		var variablesReference = _variableManager.GetReference(frameId.Value);
		ArgumentNullException.ThrowIfNull(variablesReference);
		if (variablesReference.Value.ReferenceKind is not StoredReferenceKind.Scope) throw new InvalidOperationException("Frame ID does not refer to a stack frame scope");
		var thread = _process!.Threads.Single(s => s.Id == variablesReference.Value.ThreadId.Value);

		var compiledExpression = ExpressionCompiler.Compile(expression, false);
		var evalContext = new CompiledExpressionEvaluationContext(thread, variablesReference.Value.ThreadId, variablesReference.Value.FrameStackDepth);
		ArgumentNullException.ThrowIfNull(_expressionInterpreter);
		var result = await _expressionInterpreter.Interpret(compiledExpression, evalContext);

		if (result.Error is not null)
		{
			_logger?.Invoke($"Evaluation error: {result.Error}");
			return (result.Error, null, 0);
		}
		var (friendlyTypeName, value, debuggerProxyInstance, resultIsError) = await GetValueForCorDebugValueAsync(result.Value!, variablesReference.Value.ThreadId, variablesReference.Value.FrameStackDepth);
		// TODO: create variables reference. Just return a VariableInfo
		return (value, friendlyTypeName, 0);
	}

	/// <summary>
	/// Terminate the debugged process
	/// </summary>
	public void Terminate()
	{
		_logger?.Invoke("Terminate");
		if (_process != null)
		{
			try
			{
				_process.Terminate(0);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Error terminating process: {ex.Message}");
			}
		}
		Cleanup();
	}

	/// <summary>
	/// Disconnect from the debuggee
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
			if (_process != null && _isAttached)
			{
				try
				{
					if (IsRunning)
					{
						// pause first
						_process.Stop(0);
						IsRunning = false;
					}
					foreach (var bp in _breakpointManager.GetAllBreakpoints().Where(b => b.CorBreakpoint != null))
					{
						try
						{
							bp.CorBreakpoint!.Activate(false);
						}
						catch (Exception ex)
						{
							_logger?.Invoke($"Error deactivating breakpoint during detach: {ex.Message}");
						}
					}
					Cleanup();
					_process.Detach();
				}
				catch (Exception ex)
				{
					_logger?.Invoke($"Error detaching: {ex.Message}");
				}
			}
		}
	}
}
