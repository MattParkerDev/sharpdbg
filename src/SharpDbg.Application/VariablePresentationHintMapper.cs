using SharpDbg.Infrastructure.Debugger.PresentationHintModels;
using Debugger_VariablePresentationHint = SharpDbg.Infrastructure.Debugger.PresentationHintModels.VariablePresentationHint;
using VariablePresentationHint = SharpDbg.DapProtocol.VariablePresentationHint;

namespace SharpDbg.Application;

public static class VariablePresentationHintMapper
{
	// Not part of the DAP specification - a Visual Studio extension to the 'attributes' values
	private static readonly VariablePresentationHint.AttributesValue FailedEvaluation = new("failedEvaluation");

	public static VariablePresentationHint ToDto(this Debugger_VariablePresentationHint hint)
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

	private static IReadOnlyList<VariablePresentationHint.AttributesValue> ToDto(this AttributesValue kind)
	{
		return kind switch
		{
			AttributesValue.FailedEvaluation => [FailedEvaluation],
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}
}
