using System.Buffers;
using System.Text;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private const int MaxDebuggerDisplayDepth = 10;
	private const string DebuggerDisplayMaxDepthMessage = "<Evaluation of DebuggerDisplay attribute reached max recursion count>";
	private static readonly SearchValues<char> FormatSpecifierChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

	private readonly Dictionary<string, DebuggerDisplaySegment[]> _parsedDebuggerDisplays = new();

	private readonly record struct DebuggerDisplaySegment(Range Literal, string? Expression, bool NoQuotes);

	private async Task<(string Value, bool ResultIsError)> FormatDebuggerDisplayAsync(string debuggerDisplay, ICorDebugValue value, ThreadId threadId, FrameStackDepth frameStackDepth, int debuggerDisplayDepth)
	{
		if (debuggerDisplayDepth >= MaxDebuggerDisplayDepth) return (DebuggerDisplayMaxDepthMessage, false);

		if (_parsedDebuggerDisplays.TryGetValue(debuggerDisplay, out var segments) is false)
		{
			segments = ParseDebuggerDisplay(debuggerDisplay);
			_parsedDebuggerDisplays[debuggerDisplay] = segments;
		}
		var thread = _process!.GetThread(threadId.Value);
		var builder = new StringBuilder();
		var resultIsError = false;
		foreach (var segment in segments)
		{
			if (segment.Expression is null)
			{
				builder.Append(debuggerDisplay.AsSpan(segment.Literal));
				continue;
			}
			var evalContext = new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth, value);
			using var result = await _expressionEvaluator!.Evaluate(segment.Expression, evalContext);
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

	private static DebuggerDisplaySegment[] ParseDebuggerDisplay(string debuggerDisplay)
	{
		var text = debuggerDisplay.AsSpan();
		List<DebuggerDisplaySegment> segments = [];
		var literalStart = 0;
		var index = 0;
		while (index < text.Length)
		{
			if (text[index] is not '{')
			{
				index++;
				continue;
			}
			var end = FindExpressionEnd(text, index + 1);
			if (end is -1) break; // Unterminated, so the rest is literal text
			if (index > literalStart) segments.Add(new(literalStart..index, null, false));
			var expression = text[(index + 1)..end];
			var (expressionLength, noQuotes) = SplitFormatSpecifiers(expression);
			segments.Add(new(default, expression[..expressionLength].ToString(), noQuotes));
			index = end + 1;
			literalStart = index;
		}
		if (literalStart < text.Length) segments.Add(new(literalStart..text.Length, null, false));
		return segments.ToArray();
	}

	private static int FindExpressionEnd(ReadOnlySpan<char> text, int start)
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

	private static int SkipLiteral(ReadOnlySpan<char> text, int quoteIndex)
	{
		var quote = text[quoteIndex];
		for (var i = quoteIndex + 1; i < text.Length; i++)
		{
			if (text[i] is '\\') i++;
			else if (text[i] == quote) return i;
		}
		return -1;
	}

	private static (int ExpressionLength, bool NoQuotes) SplitFormatSpecifiers(ReadOnlySpan<char> expression)
	{
		var depth = 0;
		for (var i = 0; i < expression.Length; i++)
		{
			switch (expression[i])
			{
				case '"' or '\'':
					var literalEnd = SkipLiteral(expression, i);
					if (literalEnd is -1) return (expression.Length, false);
					i = literalEnd;
					break;
				case '{' or '(' or '[':
					depth++;
					break;
				case '}' or ')' or ']':
					depth--;
					break;
				case ',' when depth is 0 && TryReadFormatSpecifiers(expression[(i + 1)..], out var noQuotes):
					return (i, noQuotes);
			}
		}
		return (expression.Length, false);
	}

	private static bool TryReadFormatSpecifiers(ReadOnlySpan<char> specifiers, out bool noQuotes)
	{
		noQuotes = false;
		foreach (var range in specifiers.Split(','))
		{
			var specifier = specifiers[range].Trim();
			if (specifier.IsEmpty || specifier.ContainsAnyExcept(FormatSpecifierChars)) return false;
			noQuotes |= specifier is "nq";
		}
		return true;
	}
}
