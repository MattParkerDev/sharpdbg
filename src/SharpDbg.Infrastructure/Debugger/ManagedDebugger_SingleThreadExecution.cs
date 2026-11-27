using Ardalis.GuardClauses;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>The thread a single-thread continue or step runs, so a thread created while it runs stays suspended until the next stop.</summary>
	private int? _singleThreadExecutionThreadId;

	/// <summary>
	/// Restrict the next continue to <paramref name="threadId"/> when <paramref name="singleThread"/> is set, or lift that
	/// restriction. ICorDebug resumes every thread whose debug state is THREAD_RUN, so the other threads are suspended here
	/// before the continue and restored when the next stop is reported.
	/// </summary>
	/// <returns>Whether every thread is resumed, i.e. the requested thread was unknown or single-thread execution was not requested</returns>
	private bool SetSingleThreadExecution(int threadId, bool singleThread)
	{
		Guard.Against.Null(CorProcess);
		if (!singleThread)
		{
			_singleThreadExecutionThreadId = null;
			SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN);
			return true;
		}

		if (TryGetThread(threadId) is null)
		{
			_logger?.Invoke($"Thread {threadId} does not exist, continuing all threads");
			_singleThreadExecutionThreadId = null;
			SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN);
			return true;
		}

		_singleThreadExecutionThreadId = threadId;
		foreach (var (id, threadInfo) in _threads)
		{
			SetThreadDebugState(threadInfo.Thread, id == threadId ? CorDebugThreadState.THREAD_RUN : CorDebugThreadState.THREAD_SUSPEND);
		}
		return false;
	}

	/// <summary>Lift single-thread execution when a stop is reported, so the next request resumes the whole process again.</summary>
	private void EndSingleThreadExecution()
	{
		if (_singleThreadExecutionThreadId is null) return;
		_singleThreadExecutionThreadId = null;
		SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN);
	}

	/// <summary>Keep a thread created during single-thread execution suspended, as it was not the thread the client asked to run.</summary>
	private void SuspendThreadForSingleThreadExecution(ICorDebugThread thread)
	{
		if (_singleThreadExecutionThreadId is { } target && thread.Id != target)
			SetThreadDebugState(thread, CorDebugThreadState.THREAD_SUSPEND);
	}

	private void SetAllThreadsDebugState(CorDebugThreadState state)
	{
		foreach (var threadInfo in _threads.Values)
			SetThreadDebugState(threadInfo.Thread, state);
	}

	private void SetThreadDebugState(ICorDebugThread thread, CorDebugThreadState state)
	{
		try
		{
			var result = thread.TrySetDebugState(state);
			if (result is not Cor.S_OK)
				_logger?.Invoke($"Unable to set thread {thread.Id} debug state to {state}: {result}");
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Unable to set thread {thread.Id} debug state to {state}: {ex.Message}");
		}
	}
}
