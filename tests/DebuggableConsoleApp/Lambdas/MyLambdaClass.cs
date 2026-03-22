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
}
