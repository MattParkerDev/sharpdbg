using System.Diagnostics;
using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private void HandleProcessCreated(object? sender, CreateProcessCorDebugManagedCallbackEventArgs createProcessCorDebugManagedCallbackEventArgs)
	{
		_logger?.Invoke("Process created event");
		_rawProcess = createProcessCorDebugManagedCallbackEventArgs.Process;

		ContinueProcess();
	}

	private void HandleProcessExited(object? sender, ExitProcessCorDebugManagedCallbackEventArgs exitProcessCorDebugManagedCallbackEventArgs)
	{
		_logger?.Invoke($"Process exited");
		IsRunning = false;
		OnExited?.Invoke();
		OnTerminated?.Invoke();
	}

	private void HandleThreadCreated(object? sender, CreateThreadCorDebugManagedCallbackEventArgs createThreadCorDebugManagedCallbackEventArgs)
	{
		var corThread = createThreadCorDebugManagedCallbackEventArgs.Thread;
		_threads[corThread.Id] = corThread;
		OnThreadStarted?.Invoke(corThread.Id, $"Thread {corThread.Id}");
		ContinueProcess();
	}

	private void HandleThreadExited(object? sender, ExitThreadCorDebugManagedCallbackEventArgs exitThreadCorDebugManagedCallbackEventArgs)
	{
		var corThread = exitThreadCorDebugManagedCallbackEventArgs.Thread;
		_threads.Remove(corThread.Id);
		OnThreadExited?.Invoke(corThread.Id, $"Thread {corThread.Id}");
		ContinueProcess();
	}

	private void HandleModuleLoaded(object? sender, LoadModuleCorDebugManagedCallbackEventArgs loadModuleCorDebugManagedCallbackEventArgs)
	{
		var corModule = loadModuleCorDebugManagedCallbackEventArgs.Module;
		var modulePath = corModule.Name;
		var moduleName = Path.GetFileName(modulePath);
		var baseAddress = (long) corModule.BaseAddress;

		_logger?.Invoke($"Module loaded: {modulePath} at 0x{baseAddress:X}");

		// Try to load symbols for this module
		SymbolReader? symbolReader = null;
		try
		{
			symbolReader = SymbolReader.TryLoad(modulePath);
			if (symbolReader != null)
			{
				_logger?.Invoke($"  Symbols loaded for {moduleName}");
			}
			else
			{
				_logger?.Invoke($"  No symbols found for {moduleName}");
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"  Error loading symbols for {moduleName}: {ex.Message}");
		}

		// EnC is enabled for assemblies/projects that are authored by the user, so we can use it as a heuristic to determine if this is user code or system code.
		var isUserCode = corModule.JITCompilerFlags is CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION or CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC;

		var moduleInfo = new ModuleInfo(corModule, modulePath, symbolReader, isUserCode);
		_modules[baseAddress] = moduleInfo;

		if (moduleName is "System.Private.CoreLib.dll")
		{
			// we need to map value classes to primitive types to allow evaluation to invoke methods on them
			MapRuntimePrimitiveTypesToCorDebugClass(corModule);
			// We can now initialize the expression interpreter, and assume that modules will be loaded before any stop event is allowed to be returned
			var runtimeAssemblyPrimitiveTypeClasses = new RuntimeAssemblyPrimitiveTypeClasses(CorElementToValueClassMap, CorVoidClass, CorDecimalClass);
			_expressionInterpreter = new CompiledExpressionInterpreter(runtimeAssemblyPrimitiveTypeClasses, _callbacks, this);
		}

		// Fire the module loaded event
		OnModuleLoaded?.Invoke(modulePath, Path.GetFileName(modulePath), modulePath);

		// Try to bind any pending breakpoints now that we have a new module with symbols
		if (symbolReader != null)
		{
			TryBindPendingBreakpoints();
		}

		ContinueProcess();
	}

	private async void HandleBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs breakpointCorDebugManagedCallbackEventArgs)
	{
		try
		{
			//System.Diagnostics.Debugger.Launch();
			var breakpoint = breakpointCorDebugManagedCallbackEventArgs.Breakpoint;
			ArgumentNullException.ThrowIfNull(breakpoint);

			if (_stepper is not null)
			{
				_stepper.Deactivate();
				_stepper = null;
			}

			if (breakpoint is not CorDebugFunctionBreakpoint functionBreakpoint)
			{
				_logger?.Invoke("Unknown breakpoint type hit");
				ContinueProcess(); // may be incorrect
				return;
			}

			var corThread = breakpointCorDebugManagedCallbackEventArgs.Thread;

			// Check if async stepper handles this breakpoint
			if (_asyncStepper != null)
			{
				var (asyncHandled, shouldStop) = await _asyncStepper.TryHandleBreakpoint(corThread, functionBreakpoint);
				if (asyncHandled)
				{
					if (shouldStop is false)
					{
						Continue();
						return;
					}

					IsRunning = false;
					if (_stepper is not null)
					{
						_stepper.Deactivate();
						_stepper = null;
					}

					var sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame);
					if (sourceInfo is null)
					{
						SetupStepper(corThread, AsyncStepper.StepType.StepOut);
						Continue();
						return;
					}
				}
			}

			var managedBreakpoint = _breakpointManager.FindByCorBreakpoint(functionBreakpoint.Raw);
			ArgumentNullException.ThrowIfNull(managedBreakpoint);

			managedBreakpoint.HitCount++;

			if (managedBreakpoint.HitCondition is not null && EvaluateHitCondition(managedBreakpoint.HitCount, managedBreakpoint.HitCondition) is false)
			{
				_logger?.Invoke($"Hit count condition not met: count={managedBreakpoint.HitCount}, condition={managedBreakpoint.HitCondition}");
				Continue();
				return;
			}

			if (managedBreakpoint.Condition is not null && await EvaluateBreakpointCondition(corThread, managedBreakpoint.Condition) is false)
			{
				_logger?.Invoke($"Conditional breakpoint condition not met: {managedBreakpoint.Condition}");
				Continue();
				return;
			}

			IsRunning = false;
			OnStopped2?.Invoke(corThread.Id, managedBreakpoint.FilePath, managedBreakpoint.Line, 0, "breakpoint", null);
		}
		catch (Exception e)
		{
			throw; // TODO handle exception
		}
	}

	private void HandleStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs stepCompleteEventArgs)
	{
		var corThread = stepCompleteEventArgs.Thread;
		IsRunning = false;
		var ilFrame = (CorDebugILFrame) corThread.ActiveFrame;
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
			Continue();
			return;
		}
		var symbolReader = module.SymbolReader ?? throw new UnreachableException("Source info was found, but no symbol reader is available for the module - this should never happen");

		var (currentIlOffset, nextUserCodeIlOffset) = symbolReader.GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ilFrame);
		if (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_CALL && currentIlOffset < nextUserCodeIlOffset)
		{
			SetupStepper(corThread, AsyncStepper.StepType.StepOver);
			Continue();
			return;
		}

		if (nextUserCodeIlOffset is null)
		{
			// Check attributes
			var metadataImport = ilFrame.Function.Module.GetMetaDataInterface().MetaDataImport;
			var mdMethodDef = ilFrame.Function.Token;
			var methodIsNotDebuggable =
				metadataImport.HasAnyAttribute(mdMethodDef, JmcConstants.JmcMethodAttributeNames);
			if (methodIsNotDebuggable)
			{
				SetupStepper(corThread, AsyncStepper.StepType.StepIn);
				Continue();
				return;
			}
		}

		var (sourceFilePath, line, column, decompiledSourceInfo) = sourceInfo.Value;
		OnStopped2?.Invoke(corThread.Id, sourceFilePath, line, column, "step", decompiledSourceInfo);
	}

	private void HandleBreak(object? sender,
		BreakCorDebugManagedCallbackEventArgs breakCorDebugManagedCallbackEventArgs)
	{
		var corThread = breakCorDebugManagedCallbackEventArgs.Thread;
		IsRunning = false;
		_asyncStepper?.Disable();
		if (_stepper is not null)
		{
			_stepper.Deactivate();
			_stepper = null;
		}

		OnStopped?.Invoke(corThread.Id, "pause");
	}

	private void HandleException(object? sender, ExceptionCorDebugManagedCallbackEventArgs exceptionCorDebugManagedCallbackEventArgs)
	{
		var corThread = exceptionCorDebugManagedCallbackEventArgs.Thread;
		IsRunning = false;
		_asyncStepper?.Disable();
		if (_stepper is not null)
		{
			_stepper.Deactivate();
			_stepper = null;
		}

		OnStopped?.Invoke(corThread.Id, "exception");
	}
}
