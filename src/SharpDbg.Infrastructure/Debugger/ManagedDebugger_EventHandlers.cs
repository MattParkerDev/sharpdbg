using System.Runtime.InteropServices;
using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
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

		// Store module info
		var moduleInfo = new ModuleInfo(corModule, modulePath, symbolReader);
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

			if (!string.IsNullOrEmpty(managedBreakpoint.HitCondition))
			{
				if (!EvaluateHitCondition(managedBreakpoint.HitCount, managedBreakpoint.HitCondition))
				{
					_logger?.Invoke($"Hit count condition not met: count={managedBreakpoint.HitCount}, condition={managedBreakpoint.HitCondition}");
					Continue();
					return;
				}
			}

			if (!string.IsNullOrEmpty(managedBreakpoint.Condition))
			{
				var conditionResult = await EvaluateBreakpointCondition(corThread, managedBreakpoint.Condition);
				if (!conditionResult)
				{
					_logger?.Invoke($"Conditional breakpoint condition not met: {managedBreakpoint.Condition}");
					Continue();
					return;
				}
			}

			IsRunning = false;
			OnStopped2?.Invoke(corThread.Id, managedBreakpoint.FilePath, managedBreakpoint.Line, "breakpoint");
		}
		catch (Exception e)
		{
			throw; // TODO handle exception
		}
	}

	private async Task<bool> EvaluateBreakpointCondition(CorDebugThread corThread, string condition)
	{
		try
		{
			var threadId = new ThreadId(corThread.Id);
			var frameStackDepth = new FrameStackDepth(0); // Top frame

			var compiledExpression = ExpressionCompiler.Compile(condition, false);
			var evalContext = new CompiledExpressionEvaluationContext(corThread, threadId, frameStackDepth);
			var result = await _expressionInterpreter.Interpret(compiledExpression, evalContext);

			if (result.Error is not null)
			{
				_logger?.Invoke($"Condition evaluation error for '{condition}': {result.Error}");
				return false; // Don't stop on error - condition couldn't be evaluated, so skip the breakpoint
			}

			return IsTruthyValue(result.Value);
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Exception evaluating condition '{condition}': {ex.Message}");
			return false; // Don't stop on exception - condition couldn't be evaluated, so skip the breakpoint
		}
	}

	private static bool EvaluateHitCondition(int hitCount, string hitCondition)
	{
		// Support common hit count formats:
		// "10" or "==10" - break when hit count equals 10
		// ">=10" - break when hit count is >= 10
		// ">10" - break when hit count is > 10
		// "%10" - break every 10th hit (modulo)

		hitCondition = hitCondition.Trim();

		if (hitCondition.StartsWith(">="))
		{
			if (int.TryParse(hitCondition[2..], out var threshold))
				return hitCount >= threshold;
		}
		else if (hitCondition.StartsWith(">"))
		{
			if (int.TryParse(hitCondition[1..], out var threshold))
				return hitCount > threshold;
		}
		else if (hitCondition.StartsWith("<="))
		{
			if (int.TryParse(hitCondition[2..], out var threshold))
				return hitCount <= threshold;
		}
		else if (hitCondition.StartsWith("<"))
		{
			if (int.TryParse(hitCondition[1..], out var threshold))
				return hitCount < threshold;
		}
		else if (hitCondition.StartsWith("%"))
		{
			if (int.TryParse(hitCondition[1..], out var modulo) && modulo > 0)
				return hitCount % modulo == 0;
		}
		else if (hitCondition.StartsWith("=="))
		{
			if (int.TryParse(hitCondition[2..], out var target))
				return hitCount == target;
		}
		else
		{
			// Plain number means "break when hit count equals this"
			if (int.TryParse(hitCondition, out var target))
				return hitCount == target;
		}

		return false;
	}

	/// <summary>
	/// Check if a debug value is truthy (true, non-zero, non-null)
	/// </summary>
	private bool IsTruthyValue(CorDebugValue? value)
	{
		if (value is null) return false;

		var unwrapped = value.UnwrapDebugValue();

		if (unwrapped is CorDebugGenericValue genericValue)
		{
			IntPtr buffer = Marshal.AllocHGlobal(genericValue.Size);
			try
			{
				genericValue.GetValue(buffer);
				return genericValue.Type switch
				{
					CorElementType.Boolean => Marshal.ReadByte(buffer) != 0,
					CorElementType.I1 or CorElementType.U1 => Marshal.ReadByte(buffer) != 0,
					CorElementType.I2 or CorElementType.U2 => Marshal.ReadInt16(buffer) != 0,
					CorElementType.I4 or CorElementType.U4 => Marshal.ReadInt32(buffer) != 0,
					CorElementType.I8 or CorElementType.U8 => Marshal.ReadInt64(buffer) != 0,
					CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(buffer)), 0) != 0,
					CorElementType.R8 => BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(buffer)), 0) != 0,
					_ => true // Unknown types - default to true
				};
			}
			catch
			{
				return false;
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}

		if (unwrapped is CorDebugReferenceValue refValue)
		{
			return !refValue.IsNull;
		}

		return true;
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
		var symbolReader = _modules[ilFrame.Function.Module.BaseAddress].SymbolReader;
		if (symbolReader is null)
		{
			// We don't have symbols, but we're going to step in, in case this code calls user code that would be missed if we stepped out or over
			// Alternative is to use JMC true - we'll never stop in non-user code, so in theory symbolReader would never be null
			SetupStepper(corThread, AsyncStepper.StepType.StepIn);
			Continue();
			return;
		}

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

		var sourceInfo = GetSourceInfoAtFrame(ilFrame);
		if (sourceInfo is null)
		{
			// sourceInfo will be null if we could not find a PDB for the module
			// Bottom line - if we have no PDB, we have no source info, and there is no possible way for the user to map the stop location to a source file/line
			// (Until we implement Source Link and/or Decompilation support)
			// So for now, if this occurs, we are going to do a step out to get us back to a stop location with source info
			// TODO: This should probably be more sophisticated - mark the CorDebugFunction as non user code - `JMCStatus = false`, enable JMC for the stepper and then step over, in case the non user code calls user code, e.g. LINQ methods
			SetupStepper(corThread, AsyncStepper.StepType.StepOver);
			Continue();
			return;
		}

		var (sourceFilePath, line, _) = sourceInfo.Value;
		OnStopped2?.Invoke(corThread.Id, sourceFilePath, line, "step");
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
