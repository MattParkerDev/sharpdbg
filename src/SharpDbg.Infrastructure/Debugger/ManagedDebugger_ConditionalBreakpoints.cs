using System.Runtime.InteropServices;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <returns>Whether the condition is true, or the error if it could not be evaluated to a boolean</returns>
	private async Task<(bool IsTrue, string? Error)> EvaluateBreakpointCondition(ICorDebugThread corThread, string condition)
	{
		try
		{
			var threadId = new ThreadId(corThread.Id);
			var frameStackDepth = new FrameStackDepth(0); // Top frame

			var evalContext = new CompiledExpressionEvaluationContext(corThread, threadId, frameStackDepth);
			using var result = await _expressionEvaluator.Evaluate(condition, evalContext);

			if (result.Error is not null) return (false, result.Error);
			return TryGetBoolean(result.Value) is { } isTrue ? (isTrue, null) : (false, "The breakpoint condition must evaluate to a boolean operation");
		}
		catch (Exception ex)
		{
			return (false, ex.Message);
		}
	}

	/// <summary>
	/// Matches vsdbg: a condition that cannot be evaluated stops at the breakpoint, and says why in the output and on the breakpoint
	/// </summary>
	private void ReportBreakpointConditionError(BreakpointManager.BreakpointInfo breakpoint, string error)
	{
		var message = $"The condition for a breakpoint failed to execute. The condition was '{breakpoint.Condition}'. The error returned was '{error}'.";
		_logger?.Invoke(message);
		var location = breakpoint.IsFunctionBreakpoint ? breakpoint.FunctionName : $"{breakpoint.FilePath}:{breakpoint.Line}";
		OnImportantOutput?.Invoke($"Breakpoint error: {message} - {location}\n");

		// The message only belongs to this hit, so is not kept on the breakpoint
		var previousMessage = breakpoint.Message;
		breakpoint.Message = message;
		OnBreakpointChanged?.Invoke(breakpoint);
		breakpoint.Message = previousMessage;
	}

	private static bool EvaluateHitCondition(int hitCount, string hitCondition)
	{
		// Support common hit count formats:
		// "10" or "==10" - break when hit count equals 10
		// ">=10" - break when hit count is >= 10
		// ">10" - break when hit count is > 10
		// "%10" - break every 10th hit (modulo)

		hitCondition = hitCondition.Trim();
		var hitConditionSpan = hitCondition.AsSpan();
		return hitConditionSpan switch
		{
			['>', '=', ..] => int.TryParse(hitConditionSpan[2..], out var threshold) && hitCount >= threshold,
			['>', ..] => int.TryParse(hitConditionSpan[1..], out var threshold) && hitCount > threshold,
			['<', '=', ..] => int.TryParse(hitConditionSpan[2..], out var threshold) && hitCount <= threshold,
			['<', ..] => int.TryParse(hitConditionSpan[1..], out var threshold) && hitCount < threshold,
			['%', ..] => int.TryParse(hitConditionSpan[1..], out var modulo) && modulo > 0 && hitCount % modulo == 0,
			['=', '=', ..] => int.TryParse(hitConditionSpan[2..], out var target) && hitCount == target,
			_ => int.TryParse(hitConditionSpan, out var target) && hitCount == target // Plain number means break when hit count = number
		};
	}

	private static bool? TryGetBoolean(ICorDebugValue? value)
	{
		if (value?.UnwrapDebugValue() is not ICorDebugGenericValue { Type: CorElementType.BOOLEAN } genericValue) return null;

		var buffer = Marshal.AllocHGlobal(genericValue.Size);
		try
		{
			genericValue.GetValue(buffer);
			return Marshal.ReadByte(buffer) != 0;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}
}
