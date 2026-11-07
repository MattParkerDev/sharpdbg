namespace DebuggableConsoleApp;

public class EvaluationSemanticsClass
{
	public enum Options { A = 1, B = 2, C = 4 }

	public static void Test()
	{
		double nan = double.NaN;
		int count = -7;
		int? maybe = 3;
		long big = 5;
		object boxedInt = 42;
		object boxedFalse = false;
		var map = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
		var matrix = new int[,] { { 1, 2 }, { 3, 4 } };
		var derived = new DerivedFromGeneric();
		;
		var counter = 10;
		Bump(ref counter);
	}

	private static void Bump(ref int value)
	{
		value += 1;
		;
	}

	public class GenericBase<T>
	{
		public T Value { get; set; } = default!;
	}

	public class DerivedFromGeneric : GenericBase<int>
	{
		public DerivedFromGeneric() => Value = 9;
	}
}
