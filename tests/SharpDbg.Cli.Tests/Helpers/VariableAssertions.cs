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

			options.Using<string>(context =>
			{
				if (referenceComparison is VariableReferenceComparison.Exact)
				{
					context.Subject.Should().Be(context.Expectation);
				}
				else if (context.Subject is null || context.Expectation is null)
				{
					context.Subject.Should().Be(context.Expectation);
				}
				else
				{
					// e.g. expectation `f0e1d2c3-b4a5-9687-7869-5a4b3c2d1e0f` and subject `{f0e1d2c3-b4a5-9687-7869-5a4b3c2d1e0f}` allowed in permissive mode
					var span = context.Subject.AsSpan();
					var subjectTrimmedSpan =  span.Length >= 2 && span[0] is '{' && span[^1] is '}'
						? span[1..^1]
						: span;

					var expectationSpan = context.Expectation.AsSpan();
					var expectationTrimmedSpan = expectationSpan.Length >= 2 && expectationSpan[0] is '{' && expectationSpan[^1] is '}'
						? expectationSpan[1..^1]
						: expectationSpan;
					subjectTrimmedSpan.Equals(expectationTrimmedSpan, StringComparison.Ordinal).Should().BeTrue($"Expected: `{expectationSpan}`, actual `{span}`");
				}
			})
			.When(info => info.Path.EndsWith("." + nameof(Variable.Value), StringComparison.Ordinal));

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
