using DotnetDbg.Infrastructure.Debugger;
using VariablePresentationHint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.VariablePresentationHint;

namespace DotnetDbg.Application;

public static class VariablePresentationHintMapper
{
	public static VariablePresentationHint ToDto(this Infrastructure.Debugger.VariablePresentationHint hint)
	{
		return new VariablePresentationHint
		{
			Kind = hint.Kind?.ToDto(),
			Attributes = hint.Attributes?.ToDto(),
			Visibility = null
		};
	}

	private static VariablePresentationHint.KindValue ToDto(this PresentationHintKind kind)
	{
		return kind switch
		{
			PresentationHintKind.Property => VariablePresentationHint.KindValue.Property,
			PresentationHintKind.Method => VariablePresentationHint.KindValue.Method,
			PresentationHintKind.Event => VariablePresentationHint.KindValue.Event,
			PresentationHintKind.Class => VariablePresentationHint.KindValue.Class,
			PresentationHintKind.Data => VariablePresentationHint.KindValue.Data,
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	private static VariablePresentationHint.AttributesValue ToDto(this AttributesValue kind)
	{
		return kind switch
		{
			AttributesValue.FailedEvaluation => VariablePresentationHint.AttributesValue.FailedEvaluation,
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}
}
