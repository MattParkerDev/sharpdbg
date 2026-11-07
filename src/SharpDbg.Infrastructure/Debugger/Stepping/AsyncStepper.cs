using Ardalis.GuardClauses;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// Creates the step states that carry a step across an await, or out of an async method. The debugger holds the state and moves it on
/// </summary>
public class AsyncStepper
{
	private readonly ManagedDebugger _debugger;
	private readonly Dictionary<CORDB_ADDRESS, ModuleInfo> _modules;

	public AsyncStepper(Dictionary<CORDB_ADDRESS, ModuleInfo> modules, ManagedDebugger debugger)
	{
		_modules = modules;
		_debugger = debugger;
	}

	/// <summary>
	/// The state to start a step in an async method with
	/// </summary>
	/// <returns>
	/// Null when a simple step does the right thing: the method is not async, or there is no await left to step over.
	/// <see cref="StepState.Idle"/> when the step runs free, out of an async void method that has resumed after an await
	/// </returns>
	public async Task<StepState?> TryCreateAsyncStep(ICorDebugThread thread, StepType stepType)
	{
		var frame = thread.ActiveFrame;
		if (frame is null) return null;

		var function = frame.Function;
		var moduleAddress = function.Module.BaseAddress;
		if (_modules.TryGetValue(moduleAddress, out var moduleInfo) is false || moduleInfo.MetadataReader.HasSymbols is false) return null;
		var asyncInfo = moduleInfo.MetadataReader.GetAsyncMethodSteppingInfo(function.Token);
		if (asyncInfo is null) return null;

		var ilFrame = frame as ICorDebugILFrame;

		// At the end of an async method, stepping over or in steps out
		if (stepType is not StepType.StepOut && ilFrame is not null)
		{
			var ipResult = ilFrame.IP;
			var mappingResult = ipResult.pMappingResult;
			if (mappingResult != CorDebugMappingResult.MAPPING_PROLOG &&
				mappingResult != CorDebugMappingResult.MAPPING_EPILOG &&
				ipResult.pnOffset >= asyncInfo.LastUserCodeIlOffset)
			{
				stepType = StepType.StepOut;
			}
		}

		if (stepType is StepType.StepOut) return await TryCreateAsyncStepOut(thread, ilFrame, moduleAddress, asyncInfo.KickoffMethodToken);

		if (ilFrame is null) return null;
		var awaitInfo = FindNextAwaitInfo(asyncInfo, (uint)ilFrame.IP.pnOffset);
		if (awaitInfo is null) return null;

		var yieldBreakpoint = function.ILCode.CreateBreakpoint((int)awaitInfo.YieldOffset);
		yieldBreakpoint.Activate(true);
		var stepper = _debugger.CreateStepper(thread, stepType);
		return new StepState.AwaitingYield(stepper, yieldBreakpoint, thread.Id, ilFrame.StackRange, stepType, awaitInfo.ResumeOffset);
	}

	private async Task<StepState?> TryCreateAsyncStepOut(ICorDebugThread thread, ICorDebugILFrame? ilFrame, CORDB_ADDRESS moduleAddress, int kickoffMethodToken)
	{
		var builder = ilFrame is not null ? GetAsyncBuilder(ilFrame) : null;
		if (builder is null) return null;

		var builderType = ManagedDebugger.GetCorDebugTypeFriendlyName(builder.ExactType);
		if (builderType == "System.Runtime.CompilerServices.AsyncVoidMethodBuilder")
		{
			// async void method: no task to wait for, so the step-out returns to the method's synchronous caller if there still is
			// one. Once the method has resumed after an await, the thread pool called it, and there is no task for anyone to await,
			// so there is nothing to return to: the step runs free, as in vsdbg and ReSharper, rather than stopping in thread pool
			// code or in whichever unrelated continuation that thread runs next
			if (kickoffMethodToken is 0) return null;
			return _debugger.TryCreateStepOutBeforeFirstYield(thread, moduleAddress, kickoffMethodToken) ?? StepState.Idle.Instance;
		}

		// Before its first yield the method is an ordinary call: return to the caller, which may not await the task yet
		if (_debugger.TryCreateStepOutBeforeFirstYield(thread, moduleAddress, kickoffMethodToken) is { } stepOutToCaller) return stepOutToCaller;

		// Resumed after an await: no caller to return to, so wait for the task and stop where its awaiter resumes
		if (await SetNotificationForWaitCompletion(builder, ilFrame!, thread) && TryCreateNotifyDebuggerOfWaitCompletionBreakpoint() is { } notifyBreakpoint)
		{
			return new StepState.AwaitingTaskCompletion(notifyBreakpoint);
		}
		return null;
	}

	/// <summary>
	/// The stepping activation has yielded at the await: wait for it to resume after the await
	/// </summary>
	public async Task<StepState.AwaitingResume> CreateAwaitingResume(ICorDebugILFrame frame, StepState.AwaitingYield awaitingYield)
	{
		// Read before the eval below, which leaves the frame a zombie
		var ilCode = frame.Function.ILCode;
		// The async state machine's ID tells the stepping activation apart when it resumes on another thread
		var asyncId = await GetAsyncIdReference(frame);
		Guard.Against.Null(asyncId);

		var resumeBreakpoint = ilCode.CreateBreakpoint((int)awaitingYield.ResumeOffset);
		resumeBreakpoint.Activate(true);
		return new StepState.AwaitingResume(resumeBreakpoint, awaitingYield.ThreadId, awaitingYield.StepType, asyncId);
	}

	/// <summary>
	/// Whether the thread that hit the resume breakpoint is resuming the stepping activation, rather than another invocation of the method
	/// </summary>
	public async Task<bool> IsResumingSteppingActivation(ICorDebugThread thread, StepState.AwaitingResume awaitingResume)
	{
		if (thread.Id == awaitingResume.ThreadId) return true;

		var currentAsyncId = await GetAsyncIdReference((ICorDebugILFrame)thread.ActiveFrame);
		if (currentAsyncId is null) return false;
		var currentAddress = currentAsyncId.Dereference().Address;
		var steppingAddress = awaitingResume.AsyncId.Dereference().Address;
		return currentAddress == steppingAddress || currentAddress == 0 || steppingAddress == 0;
	}

	/// <summary>
	/// Ask the method's task to notify the debugger when its awaiter observes completion (Task.NotifyDebuggerOfWaitCompletion)
	/// </summary>
	/// <remarks>
	/// Only the non-generic AsyncTaskMethodBuilder has its own SetNotificationForWaitCompletion; AsyncTaskMethodBuilder&lt;T&gt; and the
	/// ValueTask builders do not. Every one of them exposes its task through ObjectIdForDebugger (for a ValueTask builder, the
	/// state machine box, which is a Task), so the notification is set on that task directly.
	/// </remarks>
	private async Task<bool> SetNotificationForWaitCompletion(ICorDebugValue builder, ICorDebugILFrame frame, ICorDebugThread thread)
	{
		try
		{
			var task = await GetObjectIdForDebugger(builder, frame);
			if (task is null) return false;
			var taskObject = task.UnwrapDebugValueToObject();

			var eval = thread.CreateEval();
			var boolValue = eval.NewBooleanValue(true);

			// Declared on the non-generic System.Threading.Tasks.Task, found through the box's base types
			var function = _debugger.FindMethodOnType(taskObject.ExactType, "SetNotificationForWaitCompletion", [boolValue], false, false);
			Guard.Against.Null(function);

			// result should be null, as SetNotificationForWaitCompletion returns void
			var result = await eval.CallParameterizedFunctionAsync(
				_debugger.ProcessRuntimeEventsUntilEvalEvent,
				_debugger.EvalStatus,
				function,
				0,
				[],
				2,
				[task, boolValue]
			);
			if (result is not null) throw new InvalidOperationException("SetNotificationForWaitCompletion returned a value when void was expected");
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
	/// <summary>
	/// Create a breakpoint at the start of Task.NotifyDebuggerOfWaitCompletion
	/// </summary>
	private ICorDebugFunctionBreakpoint? TryCreateNotifyDebuggerOfWaitCompletionBreakpoint()
	{
		try
		{
			const string assemblyName = "System.Private.CoreLib.dll";
			const string className = "System.Threading.Tasks.Task";
			const string methodName = "NotifyDebuggerOfWaitCompletion";

			var targetModule = _modules.Values.Select(module => module.Module).FirstOrDefault(module => module.Name.EndsWith(assemblyName, StringComparison.OrdinalIgnoreCase));
			if (targetModule is null) return null;

			// TODO: This doesn't need to be looked up every time
			var metadataImport = targetModule.GetMetaDataInterface<IMetaDataImport>();
			var classDef = metadataImport.FindTypeDefByNameOrNull(className, mdToken.Nil);
			if (classDef is null) return null;

			var methodDef = metadataImport.FindMethod(classDef.Value, methodName, 0, 0);
			if (methodDef.IsNil) return null;

			var breakpoint = targetModule.GetFunctionFromToken(methodDef).ILCode.CreateBreakpoint(0);
			breakpoint.Activate(true);
			return breakpoint;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private ModuleMetadataReader.AsyncAwaitInfo? FindNextAwaitInfo(ModuleMetadataReader.AsyncMethodSteppingInfo asyncInfo, uint currentOffset)
	{
		foreach (var awaitInfo in asyncInfo.AwaitInfos)
		{
			if (currentOffset <= awaitInfo.YieldOffset)
			{
				return awaitInfo;
			}
			// Stop search if we're inside an await block
			if (currentOffset < awaitInfo.ResumeOffset)
			{
				break;
			}
		}
		return null;
	}

	private async Task<ICorDebugHandleValue?> GetAsyncIdReference(ICorDebugILFrame frame)
	{
		Guard.Against.Null(frame);
		var builder = GetAsyncBuilder(frame);
		if (builder is null) return null;

		var objectId = await GetObjectIdForDebugger(builder, frame);
		return objectId;
	}

	private ICorDebugValue? GetAsyncBuilder(ICorDebugILFrame frame)
	{
		try
		{
			var function = frame.Function;
			var module = function.Module;
			var methodToken = function.Token;
			var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

			var methodProps = metadataImport.GetMethodProps(methodToken);
			var isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;

			if (isStatic)
				return null;

			// Get 'this' parameter
			var arguments = frame.Arguments;
			if (arguments.Length == 0)
				return null;

			var thisValue = arguments[0];
			var thisRefValue = thisValue as ICorDebugReferenceValue;
			if (thisRefValue is null || thisRefValue.IsNull)
				return null;

			var thisValueUnwrapped = thisRefValue.Dereference();
			var thisObjectValue = thisValueUnwrapped as ICorDebugObjectValue;
			if (thisObjectValue is null)
				return null;

			var thisClass = thisObjectValue.Class;
			var fieldDef = metadataImport.EnumFieldsWithName(thisClass.Token, "<>t__builder").SingleOrDefault();
			if (fieldDef.IsNil)
				return null;

			var fieldValue = thisObjectValue.GetFieldValue(thisClass, fieldDef);
			var fieldValueUnwrapped = fieldValue.UnwrapDebugValue();
			return fieldValueUnwrapped;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private async Task<ICorDebugHandleValue?> GetObjectIdForDebugger(ICorDebugValue builder, ICorDebugILFrame frame)
	{

		var objectValue = builder.UnwrapDebugValueToObject();
		var @class = objectValue.Class;
		var module = @class.Module;
		var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

		var propertyDef = metadataImport.GetPropertyWithName(@class.Token, "ObjectIdForDebugger");
		if (propertyDef is null || propertyDef.Value.IsNil)
			return null;

		var propertyProps = metadataImport.GetPropertyProps(propertyDef.Value);
		var getMethodDef = propertyProps.pmdGetter;
		if (getMethodDef.IsNil)
			return null;

		var getMethod = module.GetFunctionFromToken(getMethodDef);
		var eval = frame.Chain.Thread.CreateEval();

		// Call ObjectIdForDebugger getter
		var result = await eval.CallParameterizedFunctionAsync(
			_debugger.ProcessRuntimeEventsUntilEvalEvent,
			_debugger.EvalStatus,
			getMethod,
			builder.ExactType.TypeParameters.Length,
			builder.ExactType.TypeParameters,
			1,
			[builder]
		);

		if (result is not ICorDebugHandleValue handleValue) throw new InvalidOperationException("ObjectIdForDebugger is not a handle value");
		return handleValue;
	}}
