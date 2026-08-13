using System.Diagnostics;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private void HandleProcessCreated(object? sender, CreateProcessCorDebugManagedCallbackEventArgs createProcessCorDebugManagedCallbackEventArgs)
	{
		_logger?.Invoke("Process created event");
		if (_process is null && _isRemoteAttach)
		{
			_process = createProcessCorDebugManagedCallbackEventArgs.Process;
			_isAttached = true;
			_logger?.Invoke($"Remote debuggee established connection to debugger, PID: {_process.Id}");
		}
		Continue();
	}

	private void HandleProcessExited(object? sender, ExitProcessCorDebugManagedCallbackEventArgs exitProcessCorDebugManagedCallbackEventArgs)
	{
		_logger?.Invoke($"Process exited");
		OnExited?.Invoke();
		OnTerminated?.Invoke();
	}

	private void HandleThreadCreated(object? sender, CreateThreadCorDebugManagedCallbackEventArgs createThreadCorDebugManagedCallbackEventArgs)
	{
		var corThread = createThreadCorDebugManagedCallbackEventArgs.Thread;
		_threads[corThread.Id] = corThread;
		OnThreadStarted?.Invoke(corThread.Id);
		Continue();
	}

	private void HandleThreadExited(object? sender, ExitThreadCorDebugManagedCallbackEventArgs exitThreadCorDebugManagedCallbackEventArgs)
	{
		var corThread = exitThreadCorDebugManagedCallbackEventArgs.Thread;
		_threads.Remove(corThread.Id);
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
				var (bytes, _) = _process.ReadMemory(baseAddress2, size);
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
			TryBindPendingBreakpoints();
			foreach (var breakpoint in _breakpointManager.GetFunctionBreakpoints())
			{
				var becameVerified = TryBindFunctionBreakpoint(breakpoint, moduleInfo);
				if (becameVerified) OnBreakpointChanged?.Invoke(breakpoint);
			}
		}

		Continue();
	}

	private async Task HandleBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs breakpointCorDebugManagedCallbackEventArgs)
	{
		var breakpoint = breakpointCorDebugManagedCallbackEventArgs.Breakpoint;
		ArgumentNullException.ThrowIfNull(breakpoint);

		if (EvalStatus.IsRunning)
		{
			Continue();
			return;
		}

		if (_stepper is not null)
		{
			// We have hit a breakpoint. If _stepper is not null, it means we have hit a breakpoint during an in progress step.
			// _stepper.IsActive tells us if the step is complete or not, IsActive true: incomplete, false: complete
			// If it is false, ie the step is complete, remembering that we are handling the breakpoint event currently,
			// it means a StepComplete event is queued, and will be received on the next Continue.
			// If we have a StepComplete event queued, we want to suppress this breakpoint event and Continue, as this means the breakpoint and the step destination are at the same location.
			if (_stepper.IsActive is false)
			{
				ContinueWithVariableClear();
				return;
			}
			// Inversely, if the stepper is still Active, ie incomplete, it means the breakpoint occurred before the step destination, and therefore should override/disable/abandon the step, and we should stop at the breakpoint.
			// Example: stepping over a method, with a breakpoint inside the method.
			_stepper.Deactivate();
			_stepper = null;
		}

		if (breakpoint is not ICorDebugFunctionBreakpoint functionBreakpoint)
		{
			_logger?.Invoke("Unknown breakpoint type hit");
			Continue(); // may be incorrect
			return;
		}

		var corThread = breakpointCorDebugManagedCallbackEventArgs.Thread;

		// Check if async stepper handles this breakpoint
		if (_asyncStepper is not null)
		{
			var (asyncHandled, shouldStop) = await _asyncStepper.TryHandleBreakpoint(corThread, functionBreakpoint);
			if (asyncHandled)
			{
				if (shouldStop is false)
				{
					ContinueWithVariableClear();
					return;
				}

				if (_stepper is not null)
				{
					_stepper.Deactivate();
					_stepper = null;
				}

				var sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame);
				if (sourceInfo is null)
				{
					SetupStepper(corThread, AsyncStepper.StepType.StepOut);
					ContinueWithVariableClear();
					return;
				}
			}
		}

		var managedBreakpoint = _breakpointManager.FindByCorBreakpoint(functionBreakpoint);
		if (managedBreakpoint is null)
		{
			_logger?.Invoke("Hit a breakpoint that has since been replaced - continuing");
			ContinueWithVariableClear();
			return;
		}

		managedBreakpoint.HitCount++;

		if (managedBreakpoint.HitCondition is not null && EvaluateHitCondition(managedBreakpoint.HitCount, managedBreakpoint.HitCondition) is false)
		{
			_logger?.Invoke($"Hit count condition not met: count={managedBreakpoint.HitCount}, condition={managedBreakpoint.HitCondition}");
			ContinueWithVariableClear();
			return;
		}

		if (managedBreakpoint.Condition is not null && await EvaluateBreakpointCondition(corThread, managedBreakpoint.Condition) is false)
		{
			_logger?.Invoke($"Conditional breakpoint condition not met: {managedBreakpoint.Condition}");
			ContinueWithVariableClear();
			return;
		}

		if (managedBreakpoint.IsFunctionBreakpoint)
		{
			var sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame);
			// There exists a 'function breakpoint' type, but netcoredbg et al do not use it, so lets just use 'breakpoint'
			if (sourceInfo is null) OnStopped?.Invoke(corThread.Id, "breakpoint");
			else OnStopped2?.Invoke(corThread.Id, sourceInfo.Value.FilePath, sourceInfo.Value.StartLine, sourceInfo.Value.StartColumn, "breakpoint", [managedBreakpoint.Id], sourceInfo.Value.DecompiledSourceInfo);
			return;
		}

		if (managedBreakpoint.ResolvedBreakpointFromPdb is not {} resolvedBreakpoint) throw new UnreachableException("Breakpoint was not resolved from PDB - this should never happen, as source breakpoints are only bound to resolved source locations");
		OnStopped2?.Invoke(corThread.Id, managedBreakpoint.FilePath, resolvedBreakpoint.StartLine, resolvedBreakpoint.StartColumn, "breakpoint", [managedBreakpoint.Id], null);
	}

	private void HandleStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs stepCompleteEventArgs)
	{
		var corThread = stepCompleteEventArgs.Thread;
		var ilFrame = (ICorDebugILFrame)corThread.ActiveFrame;
		// If we have an active async stepper, it means we would have a breakpoint set up for either yield or resume for the next await statement
		// We would then have done a regular step over/in/out to get to that breakpoint
		// Since the step has completed, it means we did not hit the breakpoint, so we can clear the active async step
		_asyncStepper?.ClearActiveAsyncStep();
		var stepper = _stepper ?? throw new InvalidOperationException("No stepper found for step complete");
		stepper.Deactivate(); // I really don't know if its necessary to deactivate the steppers once done
		_stepper = null;
		var module = _modules[ilFrame.Function.Module.BaseAddress];
		var sourceInfo = GetSourceInfoAtFrame(ilFrame);
		if (sourceInfo is null)
		{
			// sourceInfo will be null if we could not find a PDB for the module
			// Bottom line - if we have no PDB, we have no source info, and there is no possible way for the user to map the stop location to a source file/line
			// Either justMyCode is enabled, or this is a genuinely unmapped method, ie compiler generated with DebuggerStepThrough etc
			// also, landing in an async state machine will not have source info, allowing us to keep stepping to the MoveNext
			// TODO: This should probably be more sophisticated - mark the CorDebugFunction as non user code - `JMCStatus = false`, enable JMC for the stepper and then step over, in case the non user code calls user code, e.g. LINQ methods
			SetupStepper(corThread, AsyncStepper.StepType.StepIn);
			ContinueWithVariableClear();
			return;
		}
		var metadataReader = module.MetadataReader;

		var (currentIlOffset, nextUserCodeIlOffset) = metadataReader.GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ilFrame);
		if (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_CALL && currentIlOffset < nextUserCodeIlOffset)
		{
			SetupStepper(corThread, AsyncStepper.StepType.StepOver);
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
				SetupStepper(corThread, AsyncStepper.StepType.StepIn);
				ContinueWithVariableClear();
				return;
			}
		}

		var (sourceFilePath, line, column, decompiledSourceInfo) = sourceInfo.Value;
		//_logger?.Invoke($"StepComplete: method 0x{ilFrame.Function.Token} IL offset {ilFrame.IP.pnOffset}, reason: {stepCompleteEventArgs.Reason}");
		OnStopped2?.Invoke(corThread.Id, sourceFilePath, line, column, "step", null, decompiledSourceInfo);
	}

	private void HandleBreak(object? sender, BreakCorDebugManagedCallbackEventArgs breakEventArgs)
	{
		var corThread = breakEventArgs.Thread;
		_asyncStepper?.Disable();
		if (_stepper is not null)
		{
			_stepper.Deactivate();
			_stepper = null;
		}

		OnStopped?.Invoke(corThread.Id, "pause");
	}

	private void HandleException(object? sender, ExceptionCorDebugManagedCallbackEventArgs exceptionEventArgs)
	{
		if (EvalStatus.IsRunning)
		{
			Continue();
			return;
		}
		var corThread = exceptionEventArgs.Thread;
		_asyncStepper?.Disable();
		if (_stepper is not null)
		{
			_stepper.Deactivate();
			_stepper = null;
		}

		// TODO: Get from BreakpointFilters, determine if user caught the exception, and conditionally continue
		var breakOnAllExceptions = true; // Does not break if JMC is enabled and the exception is thrown and caught in library code
		var breakOnUserUnhandledExceptions = true; // configures the debugger to stop when an exception is caught in non-user code after having been thrown in user code or traveled through user code
		var exceptionIsCaughtByUser = false;

		var shouldContinue = false;
		if (shouldContinue)
		{
			ContinueWithVariableClear();
			return;
		}
		OnStopped?.Invoke(corThread.Id, "exception");
	}
}
