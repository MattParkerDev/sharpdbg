using System.Buffers;
using System.Runtime.InteropServices;
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
		var thread = GetThread(threadId);
		var builder = new StringBuilder();
		var resultIsError = false;
		foreach (var segment in segments)
		{
			if (segment.Expression is null)
			{
				builder.Append(debuggerDisplay.AsSpan(segment.Literal));
				continue;
			}
			if (IsIdentifier(segment.Expression) && await TryReadInstanceMemberAsync(value, segment.Expression, threadId) is { } memberValue)
			{
				// Fast path for the common '{Count}', as the expression evaluator is far slower than reading the member
				try
				{
					var formattedMember = await FormatCorDebugValueAsync(ResolveCorDebugValue(memberValue), threadId, frameStackDepth, escapeStringValue: segment.NoQuotes is false, debuggerDisplayDepth + 1);
					builder.Append(formattedMember.Value);
					resultIsError |= formattedMember.ResultIsError;
				}
				finally
				{
					if (memberValue is ICorDebugHandleValue handle) handle.TryDispose();
				}
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

	private static bool IsIdentifier(string expression)
	{
		if (expression.Length is 0 || (char.IsAsciiLetter(expression[0]) is false && expression[0] is not '_')) return false;
		foreach (var c in expression)
		{
			if (char.IsAsciiLetterOrDigit(c) is false && c is not '_') return false;
		}
		return true;
	}

	/// Reads the instance field, or instance property, that C# name lookup finds for <paramref name="memberName"/> from inside the type of
	/// <paramref name="value"/>: the field directly, a <see cref="TrivialGetter"/> property's field directly, and any other property by calling its
	/// getter, which is what the expression evaluator would compile it to. Returns null for anything else, e.g. a method, a static member, a getter
	/// that throws, or a field ICorDebug cannot read, which the expression evaluator handles. A reference read, other than a string, is in a strong handle that the caller owns
	private async Task<ICorDebugValue?> TryReadInstanceMemberAsync(ICorDebugValue value, string memberName, ThreadId threadId)
	{
		if (value.UnwrapDebugValue() is not ICorDebugObjectValue objectValue || objectValue is ICorDebugStringValue) return null;
		try
		{
			if (FindDebuggerDisplayMember(objectValue, memberName) is not { } member) return null;
			ICorDebugValue memberValue;
			if (member.Getter is null) memberValue = objectValue.GetFieldValue(member.DeclaringClass, member.Field);
			else if (TryReadTrivialGetter(member.Getter, objectValue, out var getterValue)) memberValue = getterValue;
			// A func eval's result is already in a handle the caller owns
			else return await CallDebuggerDisplayGetterAsync(member.Getter, member.DeclaringClass, value, objectValue.ExactType, threadId);
			return InStrongHandleUnlessString(memberValue);
		}
		catch (Exception ex)
		{
			// e.g. a field added by Edit and Continue, which ICorDebug cannot read, or a getter that throws: the expression evaluator reports the segment's error
			_logger?.Invoke($"Reading '{memberName}' for a DebuggerDisplay failed, falling back to the expression evaluator: {ex.Message}");
			return null;
		}
	}

	private async Task<ICorDebugValue?> CallDebuggerDisplayGetterAsync(ICorDebugFunction getter, ICorDebugClass declaringClass, ICorDebugValue receiver, ICorDebugType receiverType, ThreadId threadId)
	{
		// A getter declared on a generic base takes the base's instantiation, not the receiver's own type arguments
		var declaringType = receiverType;
		while (declaringType is not null && (declaringType.Class.Token != declaringClass.Token || declaringType.Class.Module.BaseAddress != declaringClass.Module.BaseAddress))
		{
			declaringType = declaringType.Base;
		}
		if (declaringType is null) return null;
		var typeArguments = declaringType.TypeParameters;
		return await GetThread(threadId).CreateEval().CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, getter, typeArguments.Length, typeArguments, 1, [receiver], throwOnException: true);
	}

	/// What C# name lookup finds for a member name from inside a type, when it may be read without the expression evaluator:
	/// an instance field (<see cref="Getter"/> is null), or the getter of a parameterless instance property
	private readonly record struct DebuggerDisplayMember(ICorDebugClass DeclaringClass, mdFieldDef Field, ICorDebugFunction? Getter);

	// By type definition, as name lookup does not depend on a generic type's arguments. Cleared when a module loads or unloads, which can change the
	// lookup and invalidate the classes and functions held
	private readonly Dictionary<(CORDB_ADDRESS Module, mdTypeDef Type, string Name), DebuggerDisplayMember?> _debuggerDisplayMembers = new();
	private int _debuggerDisplayMembersModuleVersion = -1;

	private DebuggerDisplayMember? FindDebuggerDisplayMember(ICorDebugObjectValue objectValue, string memberName)
	{
		if (_debuggerDisplayMembersModuleVersion != ModuleSet_Version)
		{
			_debuggerDisplayMembers.Clear();
			_debuggerDisplayMembersModuleVersion = ModuleSet_Version;
		}
		var corClass = objectValue.Class;
		var key = (corClass.Module.BaseAddress, corClass.Token, memberName);
		if (_debuggerDisplayMembers.TryGetValue(key, out var member)) return member;
		member = LookUpDebuggerDisplayMember(objectValue.ExactType, memberName);
		_debuggerDisplayMembers[key] = member;
		return member;
	}

	private static DebuggerDisplayMember? LookUpDebuggerDisplayMember(ICorDebugType exactType, string memberName)
	{
		for (var type = exactType; type is not null; type = type.Base)
		{
			if (type.Type is not (CorElementType.CLASS or CorElementType.VALUETYPE)) return null;
			var corClass = type.Class;
			var metadataImport = corClass.Module.GetMetaDataInterface<IMetaDataImport>();
			// Fields and methods: a method of the name in a derived type hides a field or property of a base, so it ends the lookup too
			foreach (var member in metadataImport.EnumMembersWithName(corClass.Token, memberName))
			{
				if (member.Type is not CorTokenType.mdtFieldDef) return null;
				var field = new mdFieldDef(member.Value);
				return metadataImport.GetFieldProps(field).pdwAttr.IsFdStatic() ? null : new DebuggerDisplayMember(corClass, field, null);
			}
			if (metadataImport.GetPropertyWithName(corClass.Token, memberName) is { IsNil: false } property)
			{
				var getter = metadataImport.GetPropertyProps(property).pmdGetter;
				if (getter == mdMethodDef.Nil) return null;
				// A static property, or an indexer, which needs arguments, is left to the expression evaluator
				var getterProps = metadataImport.GetMethodProps(getter);
				if (getterProps.pdwAttr.IsMdStatic() || Marshal.ReadByte(getterProps.ppvSigBlob, 1) is not 0) return null;
				return new DebuggerDisplayMember(corClass, default, corClass.Module.GetFunctionFromToken(getter));
			}
		}
		return null;
	}

	private static DebuggerDisplaySegment[] ParseDebuggerDisplay(string debuggerDisplay)
	{
		var text = debuggerDisplay.AsSpan();
		List<DebuggerDisplaySegment> segments = [];
		var literalStart = 0;
		var index = 0;
		while (index < text.Length)
		{
			// '\{' and '\}' are literal braces, so the backslash is dropped and the brace starts the next literal
			if (text[index] is '\\' && index + 1 < text.Length && text[index + 1] is '{' or '}')
			{
				if (index > literalStart) segments.Add(new(literalStart..index, null, false));
				literalStart = index + 1;
				index += 2;
				continue;
			}
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
