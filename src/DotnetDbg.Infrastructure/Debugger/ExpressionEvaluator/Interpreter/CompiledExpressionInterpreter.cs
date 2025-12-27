using System.Text;
using ClrDebug;
using DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter(RuntimeAssemblyPrimitiveTypeClasses runtimeAssemblyPrimitiveTypeClasses, CorDebugManagedCallback debuggerManagedCallback, ManagedDebugger debugger)
{
	private EvalData _evalData;
	private readonly ManagedDebugger _debugger = debugger;
	private readonly CorDebugManagedCallback _debuggerManagedCallback = debuggerManagedCallback;
	private readonly RuntimeAssemblyPrimitiveTypeClasses _runtimeAssemblyPrimitiveTypeClasses = runtimeAssemblyPrimitiveTypeClasses;

	public async Task<EvaluationResult> Interpret(CompiledExpression compiledExpression, CompiledExpressionEvaluationContext context)
	{
		var result = await InterpretInternal(compiledExpression, context);
		return result;
	}

	private async Task<EvaluationResult> InterpretInternal(CompiledExpression compiledExpression, CompiledExpressionEvaluationContext context)
	{
		// TODO: CompiledExpressionEvaluationContext should probably be passed to e.g. ExecuteCommand instead of storing these as fields
		_evalData = context.EvalData;
		var evalStack = new LinkedList<EvalStackEntry>();
		var output = new StringBuilder();

		try
		{
			foreach (var instruction in compiledExpression.Instructions)
			{
				await ExecuteCommand(instruction, evalStack);
			}

			if (evalStack.Count != 1)
			{
				throw new InvalidOperationException("Expression evaluation did not produce a single result");
			}

			var resultValue = await GetFrontStackEntryValue(evalStack, true);
			var setterData = evalStack.First.Value.SetterData;

			return new EvaluationResult
			{
				Value = resultValue,
				Editable = evalStack.First.Value.Editable && (setterData == null || setterData.SetterFunction != null),
				SetterData = setterData
			};
		}
		catch (Exception ex)
		{
			output.AppendLine($"error: {ex.Message}");
			return new EvaluationResult
			{
				Error = output.ToString()
			};
		}
	}

	private async Task ExecuteCommand(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		switch (command.OpCode)
		{
			case eOpCode.IdentifierName: await IdentifierName((command as OneOperandCommand)!, evalStack); break;
			case eOpCode.GenericName: await GenericName((command as TwoOperandCommand)!, evalStack); break;
			case eOpCode.InvocationExpression: await InvocationExpression((command as OneOperandCommand)!, evalStack); break;
			case eOpCode.ElementAccessExpression: await ElementAccessExpression((command as OneOperandCommand)!, evalStack); break;
			case eOpCode.NumericLiteralExpression: await NumericLiteralExpression((command as TwoOperandCommand)!, evalStack); break;
			case eOpCode.StringLiteralExpression or eOpCode.InterpolatedStringText: await StringLiteralExpression((command as OneOperandCommand)!, evalStack); break;
			case eOpCode.InterpolatedStringExpression: await InterpolatedStringExpression((command as OneOperandCommand)!, evalStack); break;
			case eOpCode.CharacterLiteralExpression: await CharacterLiteralExpression((command as TwoOperandCommand)!, evalStack); break;
			case eOpCode.PredefinedType: await PredefinedType((command as OneOperandCommand)!, evalStack); break;
			case eOpCode.SimpleMemberAccessExpression: await SimpleMemberAccessExpression(command, evalStack); break;
			case eOpCode.QualifiedName: await QualifiedName(command, evalStack); break;
			case eOpCode.MemberBindingExpression: await MemberBindingExpression(command, evalStack); break;
			case eOpCode.AddExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.AddExpression, evalStack); break;
			case eOpCode.SubtractExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.SubtractExpression, evalStack); break;
			case eOpCode.MultiplyExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.MultiplyExpression, evalStack); break;
			case eOpCode.DivideExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.DivideExpression, evalStack); break;
			case eOpCode.ModuloExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.ModuloExpression, evalStack); break;
			case eOpCode.LeftShiftExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.LeftShiftExpression, evalStack); break;
			case eOpCode.RightShiftExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.RightShiftExpression, evalStack); break;
			case eOpCode.BitwiseAndExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.BitwiseAndExpression, evalStack); break;
			case eOpCode.BitwiseOrExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.BitwiseOrExpression, evalStack); break;
			case eOpCode.ExclusiveOrExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.ExclusiveOrExpression, evalStack); break;
			case eOpCode.LogicalAndExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.LogicalAndExpression, evalStack); break;
			case eOpCode.LogicalOrExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.LogicalOrExpression, evalStack); break;
			case eOpCode.EqualsExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.EqualsExpression, evalStack); break;
			case eOpCode.NotEqualsExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.NotEqualsExpression, evalStack); break;
			case eOpCode.LessThanExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.LessThanExpression, evalStack); break;
			case eOpCode.GreaterThanExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.GreaterThanExpression, evalStack); break;
			case eOpCode.LessThanOrEqualExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.LessThanOrEqualExpression, evalStack); break;
			case eOpCode.GreaterThanOrEqualExpression: evalStack.First.Value.CorDebugValue = await CalculateTwoOperands(OperationType.GreaterThanOrEqualExpression, evalStack); break;
			case eOpCode.UnaryPlusExpression: evalStack.First.Value.CorDebugValue = await CalculateOneOperand(OperationType.UnaryPlusExpression, evalStack); break;
			case eOpCode.UnaryMinusExpression: evalStack.First.Value.CorDebugValue = await CalculateOneOperand(OperationType.UnaryMinusExpression, evalStack); break;
			case eOpCode.LogicalNotExpression: evalStack.First.Value.CorDebugValue = await CalculateOneOperand(OperationType.LogicalNotExpression, evalStack); break;
			case eOpCode.BitwiseNotExpression: evalStack.First.Value.CorDebugValue = await CalculateOneOperand(OperationType.BitwiseNotExpression, evalStack); break;
			case eOpCode.TrueLiteralExpression: evalStack.AddFirst(new EvalStackEntry { Literal = true, CorDebugValue = await CreateBooleanValue(true) }); break;
			case eOpCode.FalseLiteralExpression: evalStack.AddFirst(new EvalStackEntry { Literal = true, CorDebugValue = await CreateBooleanValue(false) }); break;
			case eOpCode.NullLiteralExpression: evalStack.AddFirst(new EvalStackEntry { Literal = true, CorDebugValue = await CreateNullValue() }); break;
			case eOpCode.SizeOfExpression: await SizeOfExpression(evalStack); break;
			case eOpCode.CoalesceExpression: await CoalesceExpression(evalStack); break;
			case eOpCode.ThisExpression: evalStack.AddFirst(new EvalStackEntry { Identifiers = ["this"], Editable = true }); break;
			case eOpCode.ElementBindingExpression: await ElementAccessExpression((command as OneOperandCommand)!, evalStack); break;
			default: throw new NotImplementedException($"OpCode {command.OpCode} is not implemented");
		}
	}

	internal static string ReplaceInternalNames(string expression, bool restore)
	{
		var result = expression;
		var internalNamesMap = new Dictionary<string, string>
		{
			{ "$exception", "__INTERNAL_NCDB_EXCEPTION_VARIABLE" }
		};

		foreach (var entry in internalNamesMap)
		{
			if (restore)
				result = result.Replace(entry.Value, entry.Key);
			else
				result = result.Replace(entry.Key, entry.Value);
		}

		return result;
	}
}

public class EvaluationResult
{
	public CorDebugValue? Value { get; set; }
	public bool Editable { get; set; }
	public SetterData? SetterData { get; set; }
	public string? Error { get; set; }
}
