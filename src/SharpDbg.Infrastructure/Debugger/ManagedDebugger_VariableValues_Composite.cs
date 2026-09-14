using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static bool IsTupleType(string typeName) => typeName is "System.ValueTuple"
	                                                    || typeName.StartsWith("System.Tuple<", StringComparison.Ordinal)
	                                                    || typeName.StartsWith("System.ValueTuple<", StringComparison.Ordinal);

	private async Task<(string Value, bool ResultIsError)> FormatCompositeValueAsync(ICorDebugObjectValue value, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValueFormatKind formatKind)
	{
		var typeName = GetCorDebugTypeFriendlyName(value.ExactType);
		var metadata = value.Class.Module.GetMetaDataInterface<IMetaDataImport>();
		List<string> members = [];
		var resultIsError = false;
		if (formatKind is CorDebugValueFormatKind.Tuple)
		{
			var prefix = typeName.StartsWith("System.Tuple<", StringComparison.Ordinal) ? "m_" : "";
			var arity = value.ExactType.TypeParameters.Length;
			List<ICorDebugValue> tupleMemberValues = [];
			for (var i = 1; i <= Math.Min(arity, 7); i++)
			{
				var token = metadata.FindField(value.Class.Token, $"{prefix}Item{i}", 0, 0);
				tupleMemberValues.Add(value.GetFieldValue(value.Class, token));
			}

			ICorDebugValue? rest = null;
			if (arity == 8)
			{
				var token = metadata.FindField(value.Class.Token, $"{prefix}Rest", 0, 0);
				rest = value.GetFieldValue(value.Class, token);
			}

			// Formatting may evaluate DebuggerDisplay or ToString and neuter the parent value,
			// so retrieve every field before awaiting any member formatting.
			foreach (var memberValue in tupleMemberValues)
			{
				var result = await FormatMember(memberValue);
				members.Add(result.Value);
				resultIsError |= result.ResultIsError;
			}

			if (rest is not null)
			{
				var result = await FormatMember(rest);
				var text = result.Value;
				resultIsError |= result.ResultIsError;
				// Only Rest is flattened; tuples stored in ItemN retain their parentheses.
				if (IsTupleType(GetCorDebugTypeFriendlyName(rest.ExactType)) && text.StartsWith('(') &&
				    text.EndsWith(')'))
				{
					if (text.Length > 2) members.Add(text[1..^1]);
				}
				else members.Add(text);
			}

			return ($"({string.Join(", ", members)})", resultIsError);
		}

		List<(string Name, ICorDebugValue Value)> anonymousMemberValues = [];
		foreach (var token in metadata.EnumFields(value.Class.Token))
		{
			var field = metadata.GetFieldProps(token);
			var name = field.szField;
			if (!name.StartsWith('<') || !name.EndsWith(">i__Field", StringComparison.Ordinal)) continue;
			name = name[1..name.IndexOf('>')];
			anonymousMemberValues.Add((name, value.GetFieldValue(value.Class, token)));
		}

		// As with tuples, capture all fields before formatting can resume the debuggee.
		foreach (var (name, memberValue) in anonymousMemberValues)
		{
			var result = await FormatMember(memberValue);
			members.Add($"{name} = {result.Value}");
			resultIsError |= result.ResultIsError;
		}

		var summary = members.Count == 0 ? "{ }" : $"{{ {string.Join(", ", members)} }}";
		return (summary, resultIsError);

		async Task<(string Value, bool ResultIsError)> FormatMember(ICorDebugValue member)
		{
			var result = await FormatCorDebugValueAsync(ResolveCorDebugValue(member), threadId, stackDepth, true);
			return (result.Value, result.ResultIsError);
		}
	}
}
