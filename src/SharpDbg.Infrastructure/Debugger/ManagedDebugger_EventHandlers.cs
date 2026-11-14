using System.Diagnostics;
using System.Runtime.Versioning;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.Infrastructure.Debugger.Breakpoints;
using SharpDbg.Infrastructure.Debugger.Stepping;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private void HandleProcessCreated(object? sender, CreateProcessCorDebugManagedCallbackEventArgs createProcessCorDebugManagedCallbackEventArgs)
	{
		_logger?.Invoke("Process created event");
		if (_session is SessionState.WaitingForRemoteDebuggee { CorDebug: var corDebug })
		{
			var process = createProcessCorDebugManagedCallbackEventArgs.Process;
			_session = new SessionState.Debugging(corDebug, process, DebuggeeProcess: null, IsRemote: true);
			ConfigureExceptionCallbacks();
			_logger?.Invoke($"Remote debuggee established connection to debugger, PID: {process.Id}");
		}
		Continue();
	}

	private void HandleProcessExited(object? sender, ExitProcessCorDebugManagedCallbackEventArgs exitProcessCorDebugManagedCallbackEventArgs)
	{
		_logger?.Invoke($"Process exited");
		int? exitCode = _session is SessionState.Debugging { DebuggeeProcess: { HasExited: true } debuggeeProcess } ? debuggeeProcess.ExitCode : null;
		OnExited?.Invoke(exitCode);
		OnTerminated?.Invoke();
	}

	private void HandleThreadCreated(object? sender, CreateThreadCorDebugManagedCallbackEventArgs createThreadCorDebugManagedCallbackEventArgs)
	{
		var corThread = createThreadCorDebugManagedCallbackEventArgs.Thread;
		if (IsMainThreadCandidate(corThread.Id)) _mainThreadId = corThread.Id;
		_threads[corThread.Id] = new ThreadInfo(corThread);
		OnThreadStarted?.Invoke(corThread.Id);
		Continue();
	}

	// The managed thread id says nothing about which thread is the main one (on .NET Core id 1 is usually the finalizer),
	// and the order threads are announced in is not reliable either: we launch with DOTNET_DefaultDiagnosticPortSuspend,
	// so by the time we attach the runtime has started its own threads, and they are announced in the runtime's own order.
	// So the main thread is told by its OS id: the process id on Linux, the lowest id on macOS (ids grow with creation),
	// and on Windows, where ids say nothing, the oldest thread of the process.
	private bool IsMainThreadCandidate(int threadId)
	{
		if (_mainThreadId is null) return true;
		var processId = CorProcess?.Id;
		if (processId is null || _mainThreadId == processId) return false;
		if (threadId == processId) return true;
		if (_session is SessionState.Debugging { IsRemote: false } && OperatingSystem.IsWindows()) return threadId == GetOldestThreadId(processId.Value);
		return threadId < _mainThreadId;
	}

	[SupportedOSPlatform("windows")]
	private int? GetOldestThreadId(int processId)
	{
		if (_oldestThreadId is not null) return _oldestThreadId;
		try
		{
			using var process = Process.GetProcessById(processId);
			_oldestThreadId = process.Threads.Cast<ProcessThread>().MinBy(t => t.StartTime)?.Id;
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Failed to find the oldest thread of process {processId}: {ex.Message}");
		}
		return _oldestThreadId;
	}

	private void HandleThreadExited(object? sender, ExitThreadCorDebugManagedCallbackEventArgs exitThreadCorDebugManagedCallbackEventArgs)
	{
		var corThread = exitThreadCorDebugManagedCallbackEventArgs.Thread;
		var threadId = new ThreadId(corThread.Id);
		_threads.Remove(corThread.Id);
		_exceptionStates.Remove(threadId);
		_exceptionBreakModes.Remove(threadId);
		OnThreadExited?.Invoke(corThread.Id);
		Continue();
	}

	private void HandleModuleLoaded(object? sender, LoadModuleCorDebugManagedCallbackEventArgs loadModuleCorDebugManagedCallbackEventArgs)
	{
		var corModule = loadModuleCorDebugManagedCallbackEventArgs.Module;
		var modulePath = corModule.Name;
		var moduleName = Path.GetFileName(modulePath);
		var baseAddress = corModule.BaseAddress;

		_logger?.Invoke($"Module loaded: {modulePath} at 0x{(long)baseAddress:X}");

		ModuleMetadataReader? metadataReader = null;
		try
		{
			if (corModule.IsInMemory)
			{
				var size = corModule.Size;
				var baseAddress2 = corModule.BaseAddress;
				var (bytes, _) = CorProcess.ReadMemory(baseAddress2, size);
				metadataReader = ModuleMetadataReader.TryLoadFromBytes(bytes);
			}
			else
			{
				metadataReader = ModuleMetadataReader.TryLoad(modulePath);
			}
			if (metadataReader is null) throw new InvalidOperationException("The module's PE metadata could not be read.");
			_logger?.Invoke(metadataReader.HasSymbols ? $"  Symbols loaded for {moduleName}" : $"  No symbols found for {moduleName}");
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"  Error loading symbols for {moduleName}: {ex.Message}");
		}

		if (metadataReader is null)
		{
			_logger?.Invoke($"  Module metadata unavailable for {moduleName}");
			Continue();
			return;
		}
		if (TryConsumeTransientEvaluationModule(metadataReader.Mvid))
		{
			_logger?.Invoke($"  Ignoring transient evaluation module {metadataReader.Mvid}");
			metadataReader.Dispose();
			Continue();
			return;
		}

		// EnC is enabled for assemblies/projects that are authored by the user, so we can use it as a heuristic to determine if this is user code or system code.
		var isUserCode = corModule.JITCompilerFlags is CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION or CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC;
		if (_justMyCode && isUserCode && metadataReader.HasSymbols)
		{
			// JMC Status starts as false
			corModule.SetJMCStatus(true, 0, []);
		}

		var moduleInfo = new ModuleInfo(corModule, modulePath, metadataReader, isUserCode);
		_modules[baseAddress] = moduleInfo;
		ModuleSet_Version++;
		TryBindEntryBreakpoint(moduleInfo);

		if (moduleName is "System.Private.CoreLib.dll")
		{
			// we need to map value classes to primitive types to allow evaluation to invoke methods on them
			MapRuntimePrimitiveTypesToCorDebugClass(corModule);
			// We can now initialize the expression interpreter, and assume that modules will be loaded before any stop event is allowed to be returned
			var runtimeAssemblyPrimitiveTypeClasses = new RuntimeAssemblyPrimitiveTypeClasses(CorElementToValueClassMap, CorVoidClass, CorDecimalClass);
			_expressionEvaluator = new CilExpressionEvaluator(runtimeAssemblyPrimitiveTypeClasses, this);
		}

		// Fire the module loaded event
		OnModuleLoaded?.Invoke(modulePath, Path.GetFileName(modulePath), modulePath);

		// Try to bind any pending breakpoints now that we have a new module with symbols
		if (metadataReader.HasSymbols)
		{
			TryBindUnboundSourceBreakpoints();
			foreach (var breakpoint in _breakpointManager.GetFunctionBreakpoints())
			{
				var becameVerified = TryBindFunctionBreakpoint(breakpoint, moduleInfo);
				if (becameVerified) OnBreakpointChanged?.Invoke(breakpoint);
			}
		}

		Continue();
	}

	private void HandleModuleUnloaded(object? sender, UnloadModuleCorDebugManagedCallbackEventArgs unloadModuleEvent)
	{
		var corModule = unloadModuleEvent.Module;
		var baseAddress = corModule.BaseAddress;
		if (_modules.Remove(baseAddress, out var moduleInfo) is false)
		{
			Continue();
			return;
		}

		_logger?.Invoke($"Module unloaded: {moduleInfo.ModulePath} from 0x{(long)baseAddress:X}");
		moduleInfo.Dispose();
		ModuleSet_Version++;

		foreach (var breakpoint in _breakpointManager.GetAllBreakpoints())
		{
			switch (breakpoint.Binding)
			{
				case BreakpointBinding.FunctionBound bound:
					var remaining = bound.Bindings.RemoveAll(binding => binding.ModuleBaseAddress == baseAddress);
					if (remaining.Length == bound.Bindings.Length) break;
					if (remaining.Length > 0)
					{
						breakpoint.Binding = new BreakpointBinding.FunctionBound(remaining);
						break;
					}
					breakpoint.Binding = new BreakpointBinding.Unbound(NoMatchingFunctionMessage);
					OnBreakpointChanged?.Invoke(breakpoint);
					break;

				case BreakpointBinding.SourceBound bound when bound.ModuleBaseAddress == baseAddress:
					breakpoint.Binding = new BreakpointBinding.Unbound(NoSymbolsForDocumentMessage);
					TryBindBreakpoint(breakpoint);
					OnBreakpointChanged?.Invoke(breakpoint);
					break;
			}
		}

		OnModuleUnloaded?.Invoke(moduleInfo.ModulePath, moduleInfo.ModuleName, moduleInfo.ModulePath);
		Continue();
	}

	private async Task HandleBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs breakpointCorDebugManagedCallbackEventArgs)
	{
		var breakpoint = breakpointCorDebugManagedCallbackEventArgs.Breakpoint;
		ArgumentNullException.ThrowIfNull(breakpoint);

		if (_run is RunState.Evaluating)
		{
			Continue();
			return;
		}

		// A step that has already finished has a StepComplete queued, which arrives on the next Continue: the breakpoint is at the
		// step's destination, and the step reports the stop. A breakpoint hit before the step finishes ends the step only if it stops,
		// so a breakpoint whose condition is false leaves the step running, e.g. stepping over a method with a breakpoint inside it
		if (_step.CorStepper is { IsActive: false })
		{
			ContinueWithVariableClear();
			return;
		}

		if (breakpoint is not ICorDebugFunctionBreakpoint functionBreakpoint)
		{
			_logger?.Invoke("Unknown breakpoint type hit");
			Continue(); // may be incorrect
			return;
		}

		var corThread = breakpointCorDebugManagedCallbackEventArgs.Thread;
		if (await TryHandleStepBreakpoint(corThread, functionBreakpoint)) return;
		if (EntryCorBreakpoint == functionBreakpoint)
		{
			// Remove the one-shot breakpoint unless a user breakpoint shares the same CLR breakpoint.
			if (_breakpointManager.FindByCorBreakpoint(functionBreakpoint) is null) functionBreakpoint.TryActivate(false);
			_entryBreakpoint = EntryBreakpointState.Consumed.Instance;
			EndStep();
			ReportStopAtActiveFrame(corThread, "entry");
			return;
		}

		// Breakpoints at the same location share the runtime breakpoint: each keeps its own hit count and conditions, and the
		// stop names every one that is hit
		var managedBreakpoints = _breakpointManager.FindAllByCorBreakpoint(functionBreakpoint);
		if (managedBreakpoints.Count is 0)
		{
			_logger?.Invoke("Hit a breakpoint that has since been replaced - continuing");
			ContinueWithVariableClear();
			return;
		}

		var hitBreakpoints = new List<BreakpointInfo>();
		foreach (var managedBreakpoint in managedBreakpoints)
		{
			managedBreakpoint.HitCount++;

			if (managedBreakpoint.HitCondition is not null && EvaluateHitCondition(managedBreakpoint.HitCount, managedBreakpoint.HitCondition) is false)
			{
				_logger?.Invoke($"Hit count condition not met: count={managedBreakpoint.HitCount}, condition={managedBreakpoint.HitCondition}");
				continue;
			}

			if (managedBreakpoint.Condition is not null)
			{
				var (isTrue, error) = await EvaluateBreakpointCondition(corThread, managedBreakpoint.Condition);
				if (error is not null) ReportBreakpointConditionError(managedBreakpoint, error);
				else if (isTrue is false)
				{
					_logger?.Invoke($"Conditional breakpoint condition not met: {managedBreakpoint.Condition}");
					continue;
				}
			}

			hitBreakpoints.Add(managedBreakpoint);
		}

		if (hitBreakpoints.Count is 0)
		{
			ContinueWithVariableClear();
			return;
		}

		EndStep();
		var hitBreakpointIds = hitBreakpoints.Select(bp => bp.Id).ToList();
		var firstHitBreakpoint = hitBreakpoints[0];
		if (firstHitBreakpoint.IsFunctionBreakpoint)
		{
			// There exists a 'function breakpoint' type, but netcoredbg et al do not use it, so lets just use 'breakpoint'
			ReportStopAtActiveFrame(corThread, "breakpoint", hitBreakpointIds);
			return;
		}

		if (firstHitBreakpoint.Binding is not BreakpointBinding.SourceBound { Resolved: var resolvedBreakpoint }) throw new UnreachableException("A source breakpoint was hit while not bound - this should never happen, as only bound breakpoints are found by their runtime breakpoint");
		ReportStop(corThread.Id, "breakpoint", (firstHitBreakpoint.FilePath, resolvedBreakpoint.StartLine, resolvedBreakpoint.StartColumn), hitBreakpointIds);
	}

	private void TryBindEntryBreakpoint(ModuleInfo moduleInfo)
	{
		if (_entryBreakpoint is not EntryBreakpointState.Armed) return;
		try
		{
			if (moduleInfo.MetadataReader.ResolveManagedEntryPoint() is not { } entryPoint) return;
			var function = moduleInfo.Module.GetFunctionFromToken(entryPoint.MethodToken);
			var corBreakpoint = function.ILCode.CreateBreakpoint(entryPoint.ILOffset);
			corBreakpoint.Activate(true);
			_entryBreakpoint = new EntryBreakpointState.Bound(corBreakpoint, moduleInfo.BaseAddress, entryPoint.MethodToken, entryPoint.ILOffset);
			_logger?.Invoke($"Entry breakpoint bound in {moduleInfo.ModuleName} at method 0x{entryPoint.MethodToken:X}, IL offset {entryPoint.ILOffset}");
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding entry breakpoint in {moduleInfo.ModuleName}: {ex.Message}");
		}
	}

	private async Task HandleStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs stepCompleteEventArgs)
	{
		var corThread = stepCompleteEventArgs.Thread;
		var step = _step;
		if (step.CorStepper is null) throw new InvalidOperationException($"No stepper found for step complete, in step state {step.GetType().Name}");
		if (step is StepState.RuntimeAsyncAwait runtimeAsync)
		{
			if (_asyncStepper.WaitAtAwait(corThread, runtimeAsync) is { } waiting)
			{
				runtimeAsync.Stepper?.TryDeactivate();
				_step = waiting;
				ContinueWithVariableClear();
				return;
			}
		}
		var ilFrame = (ICorDebugILFrame)corThread.ActiveFrame;
		// A step waiting for an await's yield breakpoint did not reach it, so the step over the await ends here too
		EndStep();
		if (step is StepState.StepOutToAwaitingUserFrame or StepState.FinishAwait or StepState.AwaitingYield { StopOffset: not null })
		{
			// Leave the rest of the caller's statement available to step into, as after a synchronous call.
			// The code after GetResult is hidden, so report the stop on the await's line
			ReportStopAtActiveFrame(corThread, "step");
			return;
		}
		if (step is StepState.StepOutToKickoffCaller)
		{
			// The callee returned before yielding, but the caller's await must still finish before the step out ends
			if (await TryFinishAwaitingStatement(corThread, stopAfterAwait: true))
			{
				ContinueWithVariableClear();
				return;
			}
			if (_justMyCode && IsUserCodeFrame(ilFrame) is false)
			{
				SetupStepper(corThread, StepType.StepOut);
				ContinueWithVariableClear();
				return;
			}
		}
		if (TrySetupRunToStateMachineStart(corThread, ilFrame) || (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_CALL && TrySetupRunToRuntimeAsyncStart(corThread)))
		{
			ContinueWithVariableClear();
			return;
		}
		var module = _modules[ilFrame.Function.Module.BaseAddress];
		if (module.MetadataReader.IsCurrentSequencePointHidden(ilFrame.Function.Token, ilFrame.IP.pnOffset))
		{
			// Skip hidden sequence points so source lookup does not report
			// the preceding visible line as the step destination.
			// Hidden code can await (e.g. the DisposeAsync of an 'await foreach' that a 'break' jumps to): carry the step across that
			// await, rather than the next one in IL order that the step set out with
			if (await TryFinishAwaitingStatement(corThread))
			{
				ContinueWithVariableClear();
				return;
			}
			SetupStepper(corThread, StepType.StepOver);
			ContinueWithVariableClear();
			return;
		}
		var sourceInfo = GetSourceInfoAtFrame(ilFrame, _justMyCode is false);
		if (sourceInfo is null)
		{
			// sourceInfo will be null if we could not find a PDB for the module
			// Bottom line - if we have no PDB, we have no source info, and there is no possible way for the user to map the stop location to a source file/line
			// Either justMyCode is enabled, or this is a genuinely unmapped method, ie compiler generated with DebuggerStepThrough etc
			// also, landing in an async state machine will not have source info, allowing us to keep stepping to the MoveNext
			// TODO: This should probably be more sophisticated - mark the CorDebugFunction as non user code - `JMCStatus = false`, enable JMC for the stepper and then step over, in case the non user code calls user code, e.g. LINQ methods
			SetupStepper(corThread, StepType.StepIn);
			ContinueWithVariableClear();
			return;
		}
		var metadataReader = module.MetadataReader;

		var (currentIlOffset, nextUserCodeIlOffset) = metadataReader.GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ilFrame);
		if (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_CALL && currentIlOffset < nextUserCodeIlOffset)
		{
			SetupStepper(corThread, StepType.StepOver);
			ContinueWithVariableClear();
			return;
		}

		if (nextUserCodeIlOffset is null)
		{
			// Check attributes
			var metadataImport = ilFrame.Function.Module.GetMetaDataInterface<IMetaDataImport>();
			var mdMethodDef = ilFrame.Function.Token;
			var methodIsNotDebuggable =
				metadataImport.HasAnyAttribute(mdMethodDef, AttributeConstants.JmcMethodAttributeNames);
			if (methodIsNotDebuggable)
			{
				SetupStepper(corThread, StepType.StepIn);
				ContinueWithVariableClear();
				return;
			}
		}

		//_logger?.Invoke($"StepComplete: method 0x{ilFrame.Function.Token} IL offset {ilFrame.IP.pnOffset}, reason: {stepCompleteEventArgs.Reason}");
		ReportStop(corThread.Id, "step", (sourceInfo.Value.FilePath, sourceInfo.Value.StartLine, sourceInfo.Value.StartColumn));
	}

	private void HandleBreak(object? sender, BreakCorDebugManagedCallbackEventArgs breakEventArgs)
	{
		// A Debugger.Break in code run by an evaluation, e.g. a property getter, does not stop: the evaluation carries on
		if (_run is RunState.Evaluating)
		{
			Continue();
			return;
		}
		var corThread = breakEventArgs.Thread;
		EndStep();
		ReportStop(corThread.Id, "pause");
	}

	private void HandleException(object? sender, ExceptionCorDebugManagedCallbackEventArgs exceptionEventArgs)
	{
		// ICorDebugManagedCallback2 supplies the exception stage needed for filtering.
		Continue();
	}

	private void HandleException2(object? sender, Exception2CorDebugManagedCallbackEventArgs exceptionEventArgs)
	{
		if (_run is RunState.Evaluating)
		{
			Continue();
			return;
		}
		var corThread = exceptionEventArgs.Thread;

		var threadId = new ThreadId(corThread.Id);
		var exceptionType = GetCurrentExceptionType(corThread);
		var shouldStop = false;
		var breakMode = SharpDbgExceptionBreakMode.Unknown;

		switch (exceptionEventArgs.DwEventType)
		{
			case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE:
			{
				shouldStop = MatchesExceptionBreakpoint(SharpDbgExceptionBreakpointFilter.All, exceptionType);
				_exceptionStates[threadId] = new ExceptionPropagationState(HasReachedUserCode: IsUserCodeFrame(exceptionEventArgs.Frame), AlwaysStopReported: shouldStop);
				breakMode = SharpDbgExceptionBreakMode.Always;
				break;
			}
			case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE:
			{
				var alreadyReported = _exceptionStates.GetValueOrDefault(threadId, ExceptionPropagationState.NotStarted).AlwaysStopReported;
				shouldStop = alreadyReported is false && MatchesExceptionBreakpoint(SharpDbgExceptionBreakpointFilter.All, exceptionType);
				_exceptionStates[threadId] = new ExceptionPropagationState(HasReachedUserCode: true, AlwaysStopReported: alreadyReported || shouldStop);
				breakMode = SharpDbgExceptionBreakMode.Always;
				break;
			}
			case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
			{
				if (_step is StepState.RuntimeAsyncAwait runtimeAsync && exceptionEventArgs.Frame is { } handler && _asyncStepper.UnwindsStep(corThread, runtimeAsync.Step, handler))
				{
					// The step's await will never go on: the step is a plain one again
					_logger?.Invoke("An exception unwinds the step's frame: the step through its await ends");
					EndRuntimeAsyncWait(runtimeAsync);
				}
				var handlerIsUserCode = IsUserCodeFrame(exceptionEventArgs.Frame);
				var state = _exceptionStates.Remove(threadId, out var removed) ? removed : ExceptionPropagationState.NotStarted;
				shouldStop = _justMyCode && handlerIsUserCode is false && state.HasReachedUserCode && MatchesExceptionBreakpoint(SharpDbgExceptionBreakpointFilter.UserUnhandled, exceptionType);
				breakMode = SharpDbgExceptionBreakMode.UserUnhandled;
				break;
			}
			case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED:
				_exceptionStates.Remove(threadId);
				shouldStop = true;
				breakMode = SharpDbgExceptionBreakMode.Unhandled;
				break;
		}

		if (shouldStop is false)
		{
			ContinueWithVariableClear();
			return;
		}
		EndStep();
		_exceptionBreakModes[threadId] = breakMode;
		ReportStop(corThread.Id, "exception");
	}

	private static string GetCurrentExceptionType(ICorDebugThread thread)
	{
		return thread.TryGetCurrentException(out var exception) is Cor.S_OK
			? GetCorDebugTypeFriendlyName(exception.ExactType)
			: "<unknown exception>";
	}

	private bool IsUserCodeFrame(ICorDebugFrame? frame)
	{
		if (_justMyCode is false) return false;
		try
		{
			return frame is not null && _modules.TryGetValue(frame.Function.Module.BaseAddress, out var module) && module.IsUserCode;
		}
		catch
		{
			return false;
		}
	}

	private bool MatchesExceptionBreakpoint(SharpDbgExceptionBreakpointFilter filter, string exceptionType)
	{
		return _exceptionBreakpoints.Any(breakpoint => breakpoint.Filter == filter && MatchesCondition(breakpoint.Condition, exceptionType));

		static bool MatchesCondition(string? condition, string type)
		{
			if (string.IsNullOrWhiteSpace(condition)) return true;
			var typeAsSpan = type.AsSpan();
			var trimmed = condition.AsSpan().Trim();
			var exclude = trimmed.StartsWith('!');
			if (exclude) trimmed = trimmed[1..];
			var contains = false;
			foreach (var range in trimmed.SplitAny([',', ' ', '\t', '\r', '\n']))
			{
				var entry = trimmed[range].Trim();
				if (entry.IsEmpty) continue;

				if (entry.Equals(typeAsSpan, StringComparison.Ordinal))
				{
					contains = true;
					break;
				}
			}
			return exclude ? !contains : contains;
		}
	}
}
