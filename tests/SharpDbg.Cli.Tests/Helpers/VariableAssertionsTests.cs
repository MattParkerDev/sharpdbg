using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace SharpDbg.Cli.Tests.Helpers;

public class VariableAssertionsTests
{
	[Theory]
	[InlineData(VariableReferenceComparison.Exact, 2, 2, true)]
	[InlineData(VariableReferenceComparison.Exact, 2, 9, false)]
	[InlineData(VariableReferenceComparison.Permissive, 2, 9, true)]
	[InlineData(VariableReferenceComparison.Permissive, 2, 0, false)]
	[InlineData(VariableReferenceComparison.Permissive, 0, 9, false)]
	[InlineData(VariableReferenceComparison.Permissive, 0, 0, true)]
	[InlineData(VariableReferenceComparison.Permissive, 2, -1, false)]
	[InlineData(VariableReferenceComparison.Permissive, 0, -1, false)]
	public void ComparesReferences(VariableReferenceComparison comparison, int expectedReference, int actualReference, bool shouldPass)
	{
		List<Variable> expected = [new() { Name = "value", VariablesReference = expectedReference }];
		List<Variable> actual = [new() { Name = "value", VariablesReference = actualReference }];

		Action assert = () => actual.ShouldBeEquivalentToDebuggerVariables(expected, comparison);
		if (shouldPass)
			assert.Should().NotThrow();
		else
			assert.Should().Throw<Xunit.Sdk.XunitException>();
	}

	[Fact]
	public void MatchesReorderedVariablesAndAllowsSharedHandles()
	{
		List<Variable> expected =
		[
			new() { Name = "first", Value = "one", VariablesReference = 2 },
			new() { Name = "second", Value = "two", VariablesReference = 3 }
		];
		List<Variable> actual =
		[
			new() { Name = "second", Value = "two", VariablesReference = 9 },
			new() { Name = "first", Value = "one", VariablesReference = 9 }
		];

		actual.ShouldBeEquivalentToDebuggerVariables(expected, VariableReferenceComparison.Permissive);

		actual[0].Value = "wrong";
		Action assert = () => actual.ShouldBeEquivalentToDebuggerVariables(expected, VariableReferenceComparison.Permissive);
		assert.Should().Throw<Xunit.Sdk.XunitException>();
	}
}
