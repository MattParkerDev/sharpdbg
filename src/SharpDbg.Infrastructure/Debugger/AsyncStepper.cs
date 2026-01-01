using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Handles stepping through async/await methods by managing breakpoints at yield/resume offsets
/// </summary>
public class AsyncStepper
{
	private readonly CorDebugManagedCallback _managedCallback;
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
        public long ModuleAddress;
        public int MethodToken;
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
    private readonly object _lock = new();

    public AsyncStepper(Dictionary<long, ModuleInfo> modules, CorDebugManagedCallback managedCallback)
    {
	    _modules = modules;
	    _managedCallback = managedCallback;
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

            lock (_lock)
            {
                // Clean up any existing async step
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;

                if (stepType == StepType.StepOut)
                {
                    // For step-out, we'll use SetNotificationForWaitCompletion
                    // This will be handled in a separate method
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

                shouldUseSimpleStepper = false;
                return true;
            }
        }
        catch (Exception)
        {
            // If anything goes wrong, fall back to simple stepper
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
    public bool TryHandleBreakpoint(CorDebugThread thread, CorDebugFunctionBreakpoint breakpoint, out bool shouldStop)
    {
        shouldStop = false;

        lock (_lock)
        {
            // Check if it's our NotifyDebuggerOfWaitCompletion breakpoint
            if (_notifyDebuggerBreakpoint != null &&
                MatchesBreakpoint(breakpoint, _notifyDebuggerBreakpoint, thread))
            {
                // NotifyDebuggerOfWaitCompletion was hit - this is for step-out
                _notifyDebuggerBreakpoint?.Dispose();
                _notifyDebuggerBreakpoint = null;

                // Continue with normal step-out
                shouldStop = true;
                return true;
            }

            // Check if we have an active async step
            if (_currentAsyncStep == null)
                return false;

            // Check if breakpoint matches our async step
            if (!MatchesBreakpoint(breakpoint, _currentAsyncStep.Breakpoint!, thread))
            {
                // Different breakpoint hit - cancel async stepping
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return false;
            }

            // Check if IP matches expected offset
            var frame = thread.ActiveFrame as CorDebugILFrame;
            if (frame == null)
            {
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return false;
            }

            var ipResult = frame.IP;
            if (ipResult.pnOffset != _currentAsyncStep.Breakpoint!.ILOffset)
            {
                // Wrong offset - cancel async stepping
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return false;
            }

            if (_currentAsyncStep.Status == AsyncStepStatus.YieldBreakpoint)
            {
                // Yield breakpoint hit - switch to resume breakpoint
                return HandleYieldBreakpoint(thread, frame, out shouldStop);
            }
            else if (_currentAsyncStep.Status == AsyncStepStatus.ResumeBreakpoint)
            {
                // Resume breakpoint hit - check if we should stop
                return HandleResumeBreakpoint(thread, out shouldStop);
            }
        }

        return false;
    }

    private bool HandleYieldBreakpoint(CorDebugThread thread, CorDebugILFrame frame, out bool shouldStop)
    {
        shouldStop = false;

        try
        {
            // Get async state machine ID for parallel execution tracking
            var asyncId = GetAsyncIdReference(thread, frame);
            if (asyncId != null)
            {
                // Create strong handle to prevent invalidation
                if (asyncId is not CorDebugHandleValue handleValue) throw new InvalidOperationException("Async ID is not a handle value");
                //var strongHandle = process.CreateHandle(asyncId, CorDebugHandleType.HANDLE_STRONG);
                _currentAsyncStep!.AsyncIdHandle = handleValue;
            }

            // Create resume breakpoint
            var function = frame.Function;
            var ilCode = function.ILCode;
            var resumeBreakpoint = ilCode.CreateBreakpoint((int)_currentAsyncStep!.ResumeOffset);
            resumeBreakpoint.Activate(true);

            // Deactivate yield breakpoint
            _currentAsyncStep!.Breakpoint?.Deactivate();

            // Update state
            _currentAsyncStep!.Breakpoint = new AsyncBreakpoint
            {
                Breakpoint = resumeBreakpoint,
                ModuleAddress = (long)function.Module.BaseAddress,
                MethodToken = function.Token,
                ILOffset = _currentAsyncStep!.ResumeOffset
            };
            _currentAsyncStep!.Status = AsyncStepStatus.ResumeBreakpoint;

            // Continue execution
            return true;
        }
        catch (Exception)
        {
            // If anything fails, cancel async stepping
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            shouldStop = true;
            return true;
        }
    }

    private bool HandleResumeBreakpoint(CorDebugThread thread, out bool shouldStop)
    {
        shouldStop = false;

        try
        {
            // Check if this is the same thread
            if (_currentAsyncStep!.ThreadId == thread.Id)
            {
                // Same thread - stop
                shouldStop = true;
                _currentAsyncStep?.Dispose();
                _currentAsyncStep = null;
                return true;
            }

            // Different thread - check async ID
            if (_currentAsyncStep!.AsyncIdHandle != null)
            {
                var currentAsyncId = GetAsyncIdReference(thread, thread.ActiveFrame as CorDebugILFrame);
                if (currentAsyncId != null)
                {
                    var currentAddress = currentAsyncId.Address;
                    var dereferencedHandle = _currentAsyncStep!.AsyncIdHandle!.Dereference();
                    var storedAddress = dereferencedHandle.Address;

                    if (currentAddress == storedAddress)
                    {
                        // Same async instance - stop
                        shouldStop = true;
                        _currentAsyncStep?.Dispose();
                        _currentAsyncStep = null;
                        return true;
                    }
                    else
                    {
                        // Different async instance - continue
                        return true;
                    }
                }
            }

            // Can't determine - stop to be safe
            shouldStop = true;
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            return true;
        }
        catch (Exception)
        {
            // If anything fails, stop to be safe
            shouldStop = true;
            _currentAsyncStep?.Dispose();
            _currentAsyncStep = null;
            return true;
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

    private CorDebugValue? GetAsyncIdReference(CorDebugThread thread, CorDebugILFrame? frame)
    {
        try
        {
            if (frame == null)
                return null;

            var builder = GetAsyncBuilder(frame);
            if (builder == null)
                return null;

            var objectId = GetObjectIdForDebugger(builder, frame, thread);
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

    private CorDebugValue? GetObjectIdForDebugger(CorDebugValue builder, CorDebugILFrame frame, CorDebugThread thread)
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
            var result = eval.CallParameterizedFunctionAsync(
	            _managedCallback,
                getMethod,
                builder.ExactType.TypeParameters.Length,
                builder.ExactType.TypeParameters.Select(t => t.Raw).ToArray(),
                1,
                [builder.Raw]
            ).GetAwaiter().GetResult();

            return result;
        }
        catch (Exception)
        {
            return null;
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
    /// Disable all async stepping and cleanup
    /// </summary>
    public void Disable()
    {
        lock (_lock)
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
