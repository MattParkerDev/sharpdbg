using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace SharpDbg.Cli.Tests.Helpers;

public enum VariableReferenceComparison
{
	Exact,
	Permissive
}

public static class VariableAssertions
{
	public static void ShouldBeEquivalentToDebuggerVariables(this List<Variable> variables, List<Variable> expectedVariables, VariableReferenceComparison? referenceComparison = null)
	{
		referenceComparison ??= TestHelper.ReferenceComparison;
		if (!Enum.IsDefined(referenceComparison.Value)) throw new ArgumentOutOfRangeException(nameof(referenceComparison));

		variables.Should().BeEquivalentTo(expectedVariables, options =>
		{
			if (referenceComparison is VariableReferenceComparison.Permissive)
				options = options.Excluding(v => v.MemoryReference).Excluding(v => v.PresentationHint);

			return options.Using<int>(context =>
			{
				if (referenceComparison is VariableReferenceComparison.Exact || context.Expectation is 0)
					context.Subject.Should().Be(context.Expectation);
				else
				{
					context.Expectation.Should().BeGreaterThan(0, "expected variable references must be non-negative");
					context.Subject.Should().BeGreaterThan(0, "the variable should be expandable");
				}
			})
			.When(info => info.Path.EndsWith("." + nameof(Variable.VariablesReference), StringComparison.Ordinal));
		});
	}
}
