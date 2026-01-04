using Ardalis.GuardClauses;
using ClrDebug;
using NeoSmart.AsyncLock;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Handles stepping through async/await methods by managing breakpoints at yield/resume offsets
/// </summary>
public class AsyncStepper
{
	private readonly CorDebugManagedCallback _managedCallback;
	private readonly ManagedDebugger _debugger;
    private enum AsyncStepStatus
    {
        YieldBreakpoint,
        ResumeBreakpoint
    }

    public enum StepType
    {
        StepIn,
        StepOver,
        StepOut
    }

    private class AsyncBreakpoint
    {
        public CorDebugFunctionBreakpoint? Breakpoint;
        public CORDB_ADDRESS ModuleAddress;
        public mdMethodDef MethodToken;
        public uint ILOffset;

        public void Deactivate()
        {
            try
            {
                Breakpoint?.Activate(false);
            }
            catch
            {
                // Ignore deactivation errors
            }
        }

        public void Dispose()
        {
            Deactivate();
        }
    }

    private class AsyncStep
    {
        public int ThreadId;
        public StepType InitialStepType;
        public uint ResumeOffset;
        public AsyncStepStatus Status;
        public AsyncBreakpoint? Breakpoint;
        public CorDebugHandleValue? AsyncIdHandle; // Strong handle to builder's ObjectIdForDebugger

        public void Dispose()
        {
            Breakpoint?.Dispose();
            try
            {
                AsyncIdHandle?.Dereference();
            }
            catch
            {
                // Ignore handle cleanup errors
            }
        }
    }

    private readonly Dictionary<long, ModuleInfo> _modules;
    private AsyncStep? _currentAsyncStep;
    private AsyncBreakpoint? _notifyDebuggerBreakpoint;
    private readonly AsyncLock _lock2 = new AsyncLock();

    public AsyncStepper(Dictionary<long, ModuleInfo> modules, CorDebugManagedCallback managedCallback, ManagedDebugger debugger)
    {
	    _modules = modules;
	    _managedCallback = managedCallback;
	    _debugger = debugger;
    }

    /// <summary>
    /// Call SetNotificationForWaitCompletion on the async builder
    /// </summary>
    private bool SetNotificationForWaitCompletion(CorDebugValue builder, CorDebugILFrame? frame, CorDebugThread thread)
    {
        try
        {
            var objectValue = builder.UnwrapDebugValueToObject();
            var @class = objectValue.Class;
            var module = @class.Module;
            var metadataImport = module.GetMetaDataInterface().MetaDataImport;

            // Find SetNotificationForWaitCompletion method
            var methodDef = metadataImport.FindMethod(@class.Token, "SetNotificationForWaitCompletion", 0, 0);
            if (methodDef.IsNil)
                return false;

            var function = module.GetFunctionFromToken(methodDef);
            var eval = thread.CreateEval();

            var boolValue = eval.NewBooleanValue(true);

            // Call builder.SetNotificationForWaitCompletion(true)
            var typeParameterArgs = objectValue.ExactType.TypeParameters.Select(t => t.Raw).ToArray();
            var result = eval.CallParameterizedFunctionAsync(
                _managedCallback,
                function,
                typeParameterArgs.Length,
                typeParameterArgs,
                2,
                [builder.Raw, boolValue.Raw]
            ).GetAwaiter().GetResult();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Setup breakpoint in Task.NotifyDebuggerOfWaitCompletion method
    /// </summary>
    private bool SetupNotifyDebuggerOfWaitCompletionBreakpoint(CorDebugThread thread)
    {
        try
        {
            const string assemblyName = "System.Private.CoreLib.dll";
            const string className = "System.Threading.Tasks.Task";
            const string methodName = "NotifyDebuggerOfWaitCompletion";

            // Find the module
            CorDebugModule? targetModule = null;
            foreach (var module in _modules.Values)
            {
                if (module.Module.Name.EndsWith(assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    targetModule = module.Module;
                    break;
                }
            }

            if (targetModule == null)
                return false;

            // TODO: This doesn't need to be looked up every time
            var metadataImport = targetModule.GetMetaDataInterface().MetaDataImport;
            var classDef = metadataImport.FindTypeDefByNameOrNull(className, mdToken.Nil);
            if (classDef is null) return false;

            var methodDef = metadataImport.FindMethod(classDef.Value, methodName, 0, 0);
            if (methodDef.IsNil) return false;

            var function = targetModule.GetFunctionFromToken(methodDef);
            var ilCode = function.ILCode;
            var breakpoint = ilCode.CreateBreakpoint(0);
            breakpoint.Activate(true);

            using (_lock2.Lock())
            {
                _notifyDebuggerBreakpoint = new AsyncBreakpoint
                {
                    Breakpoint = breakpoint,
                    ModuleAddress = targetModule.BaseAddress,
                    MethodToken = methodDef,
                    ILOffset = 0
                };
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Try to set up async stepping. Returns true if async stepping was initiated.
    /// </summary>
    /// <param name="thread">Thread to step on</param>
    /// <param name="stepType">Type of step</param>
    /// <param name="shouldUseSimpleStepper">Output: whether to use simple stepper</param>
    /// <returns>True if async stepping was initiated, false otherwise</returns>
    public bool TrySetupAsyncStep(CorDebugThread thread, StepType stepType, out bool shouldUseSimpleStepper)
    {
        shouldUseSimpleStepper = true;

        try
        {
            var frame = thread.ActiveFrame;
            if (frame == null) return false;

            var function = frame.Function;
            var moduleAddress = (long)function.Module.BaseAddress;
            var methodToken = function.Token;
            var ilCode = function.ILCode;
            var methodVersion = ilCode.VersionNumber;

            // Check if module has symbols
            if (!_modules.TryGetValue(moduleAddress, out var moduleInfo) || moduleInfo.SymbolReader == null)
                return false;

            // Check if method has async stepping info
            var asyncInfo = moduleInfo.SymbolReader.GetAsyncMethodSteppingInfo(methodToken);
            if (asyncInfo == null)
                return false;

            // Check if we're at the end of an async method and need step-out behavior
            if (stepType != StepType.StepOut)
            {
                var ilFrame = frame as CorDebugILFrame;
                if (ilFrame != null)
                {
                    var ipResult = ilFrame.IP;
                    var currentOffset = ipResult.pnOffset;
                    var mappingResult = ipResult.pMappingResult;

                    if (mappingResult != CorDebugMappingResult.MAPPING_PROLOG &&
                        mappingResult != CorDebugMappingResult.MAPPING_EPILOG &&
                        currentOffset >= asyncInfo.LastUserCodeIlOffset)
                    {
                        // At end of async method with await blocks - switch to step-out behavior
                        stepType = StepType.StepOut;
                    }
                }
            }

            using (_lock2.Lock())
            {
                // Clean up any existing async step
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;

                if (stepType == StepType.StepOut)
                {
                    // For step-out in async method with await, check if we need NotifyDebuggerOfWaitCompletion
                    var builder = GetAsyncBuilder(frame as CorDebugILFrame);
                    if (builder != null)
                    {
                        // Check if builder is AsyncVoidMethodBuilder
                        var builderType = ManagedDebugger.GetCorDebugTypeFriendlyName(builder.ExactType);
                        if (builderType == "System.Runtime.CompilerServices.AsyncVoidMethodBuilder")
                        {
                            // async void method - use normal step-out
                            return false;
                        }

                        // Not async void - use NotifyDebuggerOfWaitCompletion magic
                        var success = SetNotificationForWaitCompletion(builder, frame as CorDebugILFrame, thread);
                        if (success)
                        {
                            // Setup breakpoint in Task.NotifyDebuggerOfWaitCompletion
                            var notifyBpSuccess = SetupNotifyDebuggerOfWaitCompletionBreakpoint(thread);
                            if (notifyBpSuccess)
                            {
                                // Async step-out handled - no need for stepper
                                shouldUseSimpleStepper = false;
                                return true;
                            }
                        }
                    }

                    // Fall back to normal step-out
                    return false;
                }

                // Find next await block after current offset
                var ilFrame = frame as CorDebugILFrame;
                if (ilFrame == null)
                    return false;

                var ipResult = ilFrame.IP;
                var currentOffset = ipResult.pnOffset;

                var awaitInfo = FindNextAwaitInfo(asyncInfo, (uint)currentOffset);
                if (awaitInfo == null)
                {
                    // No more await blocks - use simple stepper
                    return false;
                }

                // Create yield breakpoint
                var yieldBreakpoint = ilCode.CreateBreakpoint((int)awaitInfo.YieldOffset);
                yieldBreakpoint.Activate(true);

                _currentAsyncStep = new AsyncStep
                {
                    ThreadId = thread.Id,
                    InitialStepType = stepType,
                    ResumeOffset = awaitInfo.ResumeOffset,
                    Status = AsyncStepStatus.YieldBreakpoint,
                    Breakpoint = new AsyncBreakpoint
                    {
                        Breakpoint = yieldBreakpoint,
                        ModuleAddress = moduleAddress,
                        MethodToken = methodToken,
                        ILOffset = awaitInfo.YieldOffset
                    }
                };

                // Don't set shouldUseSimpleStepper to false - the simple stepper should be created
                // to handle stepping until the yield breakpoint is reached
                return true;
            }
        }
        catch (Exception)
        {
            // If anything goes wrong, fall back to simple stepper
            throw;
            return false;
        }
    }

    /// <summary>
    /// Try to handle a breakpoint hit as part of async stepping.
    /// </summary>
    /// <param name="thread">Thread that hit the breakpoint</param>
    /// <param name="breakpoint">Breakpoint that was hit</param>
    /// <param name="shouldStop">Output: whether execution should stop</param>
    /// <returns>True if breakpoint was handled by async stepper</returns>
    public async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> TryHandleBreakpoint(CorDebugThread thread, CorDebugFunctionBreakpoint breakpoint)
    {
        using (await _lock2.LockAsync())
        {
            // Check if it's our NotifyDebuggerOfWaitCompletion breakpoint
            if (_notifyDebuggerBreakpoint != null &&
                MatchesBreakpoint(breakpoint, _notifyDebuggerBreakpoint, thread))
            {
                // NotifyDebuggerOfWaitCompletion was hit - this is for step-out
                _notifyDebuggerBreakpoint?.Dispose();
                _notifyDebuggerBreakpoint = null;

                // Continue with normal step-out
                return (true, true);
            }

            // Check if we have an active async step
            if (_currentAsyncStep == null)
                return (false, null);

            // Check if breakpoint matches our async step
            if (!MatchesBreakpoint(breakpoint, _currentAsyncStep.Breakpoint!, thread))
            {
                // Different breakpoint hit - cancel async stepping
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return (false, null);
            }

            // Check if IP matches expected offset
            var frame = thread.ActiveFrame as CorDebugILFrame;
            if (frame == null)
            {
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return (false, null);
            }

            var ipResult = frame.IP;
            if (ipResult.pnOffset != _currentAsyncStep.Breakpoint!.ILOffset)
            {
                // Wrong offset - cancel async stepping
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return (false, null);
            }

            if (_currentAsyncStep.Status == AsyncStepStatus.YieldBreakpoint)
            {
	            if (_currentAsyncStep.ThreadId != thread.Id)
	            {
		            return (false, null);
	            }
                // Yield breakpoint hit - switch to resume breakpoint
                return await HandleYieldBreakpoint(thread, frame);
            }
            else if (_currentAsyncStep.Status == AsyncStepStatus.ResumeBreakpoint)
            {
                // Resume breakpoint hit - check if we should stop
                return await HandleResumeBreakpoint(thread);
            }
        }

        return (false, null);
    }

    private async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> HandleYieldBreakpoint(CorDebugThread thread, CorDebugILFrame frame)
    {
        var shouldStop = false;

        try
        {
            // Disable all simple steppers when we hit the yield breakpoint
            DisableAllSimpleSteppers(thread.Process);

            // Get async state machine ID for parallel execution tracking
            var function = frame.Function;
            var ilCode = function.ILCode;
            var asyncIdHandleValue = await GetAsyncIdReference(thread, frame);
            Guard.Against.Null(asyncIdHandleValue);
            _currentAsyncStep!.AsyncIdHandle = asyncIdHandleValue;

            // Create resume breakpoint
            var resumeBreakpoint = ilCode.CreateBreakpoint((int)_currentAsyncStep!.ResumeOffset);
            resumeBreakpoint.Activate(true);

            // Deactivate yield breakpoint
            _currentAsyncStep!.Breakpoint?.Deactivate();

            // Update state
            _currentAsyncStep!.Breakpoint = new AsyncBreakpoint
            {
                Breakpoint = resumeBreakpoint,
                ModuleAddress = function.Module.BaseAddress,
                MethodToken = function.Token,
                ILOffset = _currentAsyncStep!.ResumeOffset
            };
            _currentAsyncStep!.Status = AsyncStepStatus.ResumeBreakpoint;

            // Continue execution
            return (true, shouldStop);
        }
        catch (Exception)
        {
            // If anything fails, cancel async stepping
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            shouldStop = true;
            throw;
            return (true, shouldStop);
        }
    }

    private async Task<(bool HandledByAsyncStepper, bool? ShouldStop)> HandleResumeBreakpoint(CorDebugThread thread)
    {
        var shouldStop = false;

        try
        {
            // Check if this is the same thread
            if (_currentAsyncStep!.ThreadId == thread.Id)
            {
                // Same thread - set up stepper and clear async step
                _debugger.SetupStepper(thread, _currentAsyncStep.InitialStepType);
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return (true, shouldStop);
            }

            // Different thread - check async ID
            if (_currentAsyncStep!.AsyncIdHandle != null)
            {
                var currentAsyncId = await GetAsyncIdReference(thread, thread.ActiveFrame as CorDebugILFrame);
                if (currentAsyncId != null)
                {
                    var currentAddress = currentAsyncId.Dereference().Address;
                    var dereferencedHandle = _currentAsyncStep!.AsyncIdHandle!.Dereference();
                    var storedAddress = dereferencedHandle.Address;

                    if (currentAddress == storedAddress || currentAddress == 0 || storedAddress == 0)
                    {
                        // Same async instance - set up stepper and clear async step
                        var stepper = _debugger.SetupStepper(thread, _currentAsyncStep.InitialStepType);
                        _currentAsyncStep?.Dispose();
                        _currentAsyncStep = null;
                        //return (true, shouldStop);
                    }
                    else
                    {
                        // Different async instance - continue
                        //return (true, shouldStop);
                    }
                }
            }

            // Can't determine - set up stepper and clear async step
            //_debugger.SetupStepper(thread, _currentAsyncStep.InitialStepType);
            //_currentAsyncStep?.Dispose();
            //_currentAsyncStep = null;
            return (true, shouldStop);
        }
        catch (Exception)
        {
            // If anything fails, set up stepper and clear async step
            throw;
            _debugger.SetupStepper(thread, _currentAsyncStep.InitialStepType);
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            return (true, shouldStop);
        }
    }

    private SymbolReader.AsyncAwaitInfo? FindNextAwaitInfo(SymbolReader.AsyncMethodSteppingInfo asyncInfo, uint currentOffset)
    {
        foreach (var awaitInfo in asyncInfo.AwaitInfos)
        {
            if (currentOffset <= awaitInfo.YieldOffset)
            {
                return awaitInfo;
            }
            // Stop search if we're inside an await block
            else if (currentOffset < awaitInfo.ResumeOffset)
            {
                break;
            }
        }

        return null;
    }

    private async Task<CorDebugHandleValue?> GetAsyncIdReference(CorDebugThread thread, CorDebugILFrame? frame)
    {
        try
        {
            if (frame == null)
                return null;

            var builder = GetAsyncBuilder(frame);
            if (builder == null)
                return null;

            var objectId = await GetObjectIdForDebugger(builder, frame, thread);
            return objectId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private CorDebugValue? GetAsyncBuilder(CorDebugILFrame frame)
    {
        try
        {
            var function = frame.Function;
            var module = function.Module;
            var methodToken = function.Token;
            var metadataImport = module.GetMetaDataInterface().MetaDataImport;

            var methodProps = metadataImport.GetMethodProps(methodToken);
            var isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;

            if (isStatic)
                return null;

            // Get 'this' parameter
            var arguments = frame.Arguments;
            if (arguments.Length == 0)
                return null;

            var thisValue = arguments[0];
            var thisRefValue = thisValue as CorDebugReferenceValue;
            if (thisRefValue == null || thisRefValue.IsNull)
                return null;

            var thisValueUnwrapped = thisRefValue.Dereference();
            var thisObjectValue = thisValueUnwrapped as CorDebugObjectValue;
            if (thisObjectValue == null)
                return null;

            var thisClass = thisObjectValue.Class;
            var fieldDef = metadataImport.EnumFieldsWithName(thisClass.Token, "<>t__builder").SingleOrDefault();
            if (fieldDef.IsNil)
                return null;

            var fieldValue = thisObjectValue.GetFieldValue(thisClass.Raw, fieldDef);
            var fieldValueUnwrapped = fieldValue.UnwrapDebugValue();
            return fieldValueUnwrapped;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<CorDebugHandleValue?> GetObjectIdForDebugger(CorDebugValue builder, CorDebugILFrame frame, CorDebugThread thread)
    {
        try
        {
            var objectValue = builder.UnwrapDebugValueToObject();
            var @class = objectValue.Class;
            var module = @class.Module;
            var metadataImport = module.GetMetaDataInterface().MetaDataImport;

            var propertyDef = metadataImport.GetPropertyWithName(@class.Token, "ObjectIdForDebugger");
            if (propertyDef == null || propertyDef.Value.IsNil)
                return null;

            var propertyProps = metadataImport.GetPropertyProps(propertyDef.Value);
            var getMethodDef = propertyProps.pmdGetter;
            if (getMethodDef.IsNil)
                return null;

            var getMethod = module.GetFunctionFromToken(getMethodDef);
            var eval = frame.Chain.Thread.CreateEval();

            // Call ObjectIdForDebugger getter
            var result = await eval.CallParameterizedFunctionAsync(
	            _managedCallback,
	            getMethod,
	            builder.ExactType.TypeParameters.Length,
	            builder.ExactType.TypeParameters.Select(t => t.Raw).ToArray(),
	            1,
	            [builder.Raw]
            );

			if (result is not CorDebugHandleValue handleValue) throw new InvalidOperationException("ObjectIdForDebugger is not a handle value");
            return handleValue;
        }
        catch (Exception)
        {
	        throw;
	        //return null;
        }
    }

    private bool MatchesBreakpoint(CorDebugFunctionBreakpoint breakpoint, AsyncBreakpoint asyncBp, CorDebugThread thread)
    {
        try
        {
            var frame = thread.ActiveFrame;
            if (frame == null)
                return false;

            var function = frame.Function;
            var moduleAddress = (long)function.Module.BaseAddress;
            var methodToken = function.Token;

            return moduleAddress == asyncBp.ModuleAddress &&
                   methodToken == asyncBp.MethodToken &&
                   breakpoint.Raw == asyncBp.Breakpoint?.Raw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Disable all simple steppers across all app domains
    /// </summary>
    private void DisableAllSimpleSteppers(CorDebugProcess process)
    {
        try
        {
            var appDomains = process.EnumerateAppDomains();
            foreach (var appDomain in appDomains)
            {
                var steppers = appDomain.EnumerateSteppers();
                foreach (var stepper in steppers)
                {
                    stepper.Deactivate();
                }
            }
        }
        catch (Exception)
        {
            // Ignore errors disabling steppers
        }
    }

    /// <summary>
    /// Clear active async step if step completed without hitting async breakpoint
    /// </summary>
    public void ClearActiveAsyncStep()
    {
        using (_lock2.Lock())
        {
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
        }
    }

    /// <summary>
    /// Disable all async stepping and cleanup
    /// </summary>
    public void Disable()
    {
        using (_lock2.Lock())
        {
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            _notifyDebuggerBreakpoint?.Dispose();
            _notifyDebuggerBreakpoint = null;
        }
    }

    public void Dispose()
    {
        Disable();
    }
}
