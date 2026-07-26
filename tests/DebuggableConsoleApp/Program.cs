
using DebuggableConsoleApp.Lambdas;

namespace DebuggableConsoleApp;

public static class Program
{
	public static void Main(string[] args)
	{
		Console.WriteLine("DebuggableConsoleApp is running");
		Console.WriteLine("Log2");
		var myLambdaClass = new MyLambdaClass();
		var myClass = new MyClass();
		var myAsyncClass = new MyAsyncClass();
		var myAsyncMethodEvalClass = new AsyncMethodEvalClass();
		var myClassNoMembers = new MyClassNoMembers();
		var hitConditionClass = new HitConditionClass();
		var variablesClass = new VariablesClass();
		var columnBreakpointClass = new ColumnBreakpointClass();
		var exceptionToThrow = ExceptionToThrow.None;
		while (true)
		{
			// Keep the application running to allow debugging
			myLambdaClass.Test();
			myClass.MyMethod(13, 6);
			myClassNoMembers.MyMethod(42);
			hitConditionClass.Test();
			variablesClass.Test();
			columnBreakpointClass.Test();
			MultilineSwitchInMethodCall.Test();
			ClassWithBclCall.Test();
			var asyncResult = myAsyncClass.MyMethodAsync(4).GetAwaiter().GetResult();
			myAsyncMethodEvalClass.Test().GetAwaiter().GetResult();
			Exceptions.Test(exceptionToThrow);
			Thread.Sleep(100);
			//await Task.Delay(500);
		}
	}
}
