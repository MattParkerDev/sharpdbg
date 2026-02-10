using System.Runtime.InteropServices;
using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
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
	private static bool IsTruthyValue(CorDebugValue? value)
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
					CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(buffer)), 0) !=
					                     0,
					CorElementType.R8 => BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(buffer)), 0) !=
					                     0,
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
}
