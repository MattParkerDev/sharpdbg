namespace DebuggableConsoleApp.Lambdas;

public class MyLambdaClass
{
	private int _capturedIntField = 4;
	private int _uncapturedIntField = 5;

	public int Test()
	{
		var uncapturedString = "uncaptured";
		var capturedString = "captured";
		int local = 10;

		Func<int, int> outerLambda = x =>
		{
			int outerLocalFromCapturedField = _capturedIntField;
			int outerLocalFromLocal = x + local;

			Func<int, int> innerLambda = y =>
			{
				int result = y * y;

				string innerLocalFromRootLocalString = capturedString;
				int innerLocalFromOuterLocalInt1 = outerLocalFromCapturedField;
				int innerLocalFromRootLocalInt = local;
				int innerLocalFromOuterLocalInt2 = outerLocalFromLocal;

				return result + innerLocalFromOuterLocalInt1 + innerLocalFromRootLocalInt + innerLocalFromOuterLocalInt2; // set breakpoint here
			};

			return innerLambda(x);
		};

		int value = 5;
		int resultValue = outerLambda(value);

		return resultValue;
	}

	public int VariableShadowingMethod()
	{
		var value = 100;
		Func<int, int> outerLambda = x =>
		{
			var outerLocal = x + value;
			Func<int, int> innerLambda = value =>
			{
				return value + outerLocal;
			};
			return innerLambda(200) + outerLocal;
		};
		return outerLambda(3);
	}

	public static int CapturedParameterMethod(int captured)
	{
		var f = () => captured + 1;
		captured = 7; // only the closure's copy of a hoisted parameter is updated
		return f(); // set breakpoint here
	}

	public static int NestedCapturedParameterMethod(int value)
	{
		Func<int, int> outerLambda = x =>
		{
			var outerLocal = x + value;
			Func<int> innerLambda = () => outerLocal + x + value;
			return innerLambda(); // set breakpoint here
		};
		return outerLambda(10);
	}
}
