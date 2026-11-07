using System.Text;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private const int MaxDebuggerDisplayDepth = 10;
	private const string DebuggerDisplayMaxDepthMessage = "<Evaluation of DebuggerDisplay attribute reached max recursion count>";

	private readonly record struct DebuggerDisplaySegment(string Text, bool IsExpression, bool NoQuotes);

	private async Task<(string Value, bool ResultIsError)> FormatDebuggerDisplayAsync(string debuggerDisplay, ICorDebugValue value, ThreadId threadId, FrameStackDepth frameStackDepth, int debuggerDisplayDepth)
	{
		if (debuggerDisplayDepth >= MaxDebuggerDisplayDepth) return (DebuggerDisplayMaxDepthMessage, false);

		var thread = _process!.GetThread(threadId.Value);
		var builder = new StringBuilder();
		var resultIsError = false;
		foreach (var segment in ParseDebuggerDisplay(debuggerDisplay))
		{
			if (segment.IsExpression is false)
			{
				builder.Append(segment.Text);
				continue;
			}
			var evalContext = new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth, value);
			using var result = await _expressionEvaluator!.Evaluate(segment.Text, evalContext);
			if (result.Error is not null)
			{
				_logger?.Invoke($"Evaluation error: {result.Error}");
				builder.Append(result.Error);
				resultIsError = true;
				continue;
			}
			var formatted = await FormatCorDebugValueAsync(ResolveCorDebugValue(result.Value!), threadId, frameStackDepth, escapeStringValue: segment.NoQuotes is false, debuggerDisplayDepth + 1);
			builder.Append(formatted.Value);
			resultIsError |= formatted.ResultIsError;
		}
		return (builder.ToString(), resultIsError);
	}

	private static List<DebuggerDisplaySegment> ParseDebuggerDisplay(string debuggerDisplay)
	{
		List<DebuggerDisplaySegment> segments = [];
		var literalStart = 0;
		var index = 0;
		while (index < debuggerDisplay.Length)
		{
			if (debuggerDisplay[index] is not '{')
			{
				index++;
				continue;
			}
			var end = FindExpressionEnd(debuggerDisplay, index + 1);
			if (end is -1) break; // Unterminated, so the rest is literal text
			if (index > literalStart) segments.Add(new(debuggerDisplay[literalStart..index], false, false));
			var (expression, noQuotes) = SplitFormatSpecifiers(debuggerDisplay[(index + 1)..end]);
			segments.Add(new(expression, true, noQuotes));
			index = end + 1;
			literalStart = index;
		}
		if (literalStart < debuggerDisplay.Length) segments.Add(new(debuggerDisplay[literalStart..], false, false));
		return segments;
	}

	private static int FindExpressionEnd(string text, int start)
	{
		var depth = 0;
		for (var i = start; i < text.Length; i++)
		{
			switch (text[i])
			{
				case '"' or '\'':
					i = SkipLiteral(text, i);
					if (i is -1) return -1;
					break;
				case '{' or '(' or '[':
					depth++;
					break;
				case ')' or ']':
					depth--;
					break;
				case '}' when depth is 0:
					return i;
				case '}':
					depth--;
					break;
			}
		}
		return -1;
	}

	private static int SkipLiteral(string text, int quoteIndex)
	{
		var quote = text[quoteIndex];
		for (var i = quoteIndex + 1; i < text.Length; i++)
		{
			if (text[i] is '\\') i++;
			else if (text[i] == quote) return i;
		}
		return -1;
	}

	private static (string Expression, bool NoQuotes) SplitFormatSpecifiers(string expression)
	{
		var depth = 0;
		List<int> topLevelCommas = [];
		for (var i = 0; i < expression.Length; i++)
		{
			switch (expression[i])
			{
				case '"' or '\'':
					var literalEnd = SkipLiteral(expression, i);
					if (literalEnd is -1) return (expression, false);
					i = literalEnd;
					break;
				case '{' or '(' or '[':
					depth++;
					break;
				case '}' or ')' or ']':
					depth--;
					break;
				case ',' when depth is 0:
					topLevelCommas.Add(i);
					break;
			}
		}
		foreach (var comma in topLevelCommas)
		{
			var specifiers = expression[(comma + 1)..].Split(',', StringSplitOptions.TrimEntries);
			if (specifiers.Any(specifier => specifier.Length is 0 || specifier.All(char.IsAsciiLetterOrDigit) is false)) continue;
			return (expression[..comma], specifiers.Contains("nq"));
		}
		return (expression, false);
	}
}
