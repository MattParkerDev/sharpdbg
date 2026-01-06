using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
using SharpDbg.Infrastructure.Debugger.PresentationHintModels;
using SharpDbg.Infrastructure.Debugger.ResponseModels;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>
	/// Launch a process to debug
	/// </summary>
	public void Launch(string program, string[] args, string? workingDirectory, Dictionary<string, string>? env, bool stopAtEntry)
	{
		throw new NotImplementedException("Launch is not implemented, use Attach instead. Use DOTNET_DefaultDiagnosticPortSuspend=1 env var to have the process wait for debugger attach, the resume it yourself after attaching with `new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime()`");
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
	/// Called when DAP configuration is complete - performs deferred attach
	/// </summary>
	public void ConfigurationDone()
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke("ConfigurationDone");

		if (_pendingAttachProcessId.HasValue)
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
	/// Set breakpoints for a source file
	/// </summary>
	public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, int[] lines)
	{
		//System.Diagnostics.Debugger.Launch();
		_logger?.Invoke($"SetBreakpoints: {filePath}, lines: {string.Join(",", lines)}");

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
		foreach (var line in lines)
		{
			var bp = _breakpointManager.CreateBreakpoint(filePath, line);

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
