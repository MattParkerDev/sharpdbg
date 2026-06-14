namespace DebuggableConsoleApp;

public static class MultilineSwitchInMethodCall
{
	public static void Test()
	{
		var result = DoOperation();

		HandleOperationResult(result switch
		{
			Success => "Operation was successful",
			Failure => "Operation failed",
			_ => throw new ArgumentOutOfRangeException()
		});

		return;

		Result DoOperation()
		{
			var success = true;
			return success ? new Success() : new Failure();
		}

		void HandleOperationResult(string r)
		{

		}
	}
	public abstract class Result;
	public class Success : Result;
	public class Failure : Result;
}
