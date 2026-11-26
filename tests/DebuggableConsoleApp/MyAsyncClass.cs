using DebuggableConsoleApp.Namespace1;

namespace DebuggableConsoleApp;

public class MyAsyncClass
{
	public async Task<int> MyMethodAsync(int myParam)
	{
		var list = new List<int> { 1, 2, 3 }; // hoisted, and needs a func-eval ([DebuggerDisplay]) to display
		var intVar = 10;
		intVar = 10;
		var result = await AnotherClass.AnotherMethodAsync();
		var result2 = await AnotherClass.AnotherMethodAsync();
		var result3 = await AnotherClass.AnotherMethodAsync();
		AnotherClass.AsyncVoidMethod();
		return result;
	}

	public async Task<int> MyAsyncMethodWithNoAwaits()
	{
		var intVar = 10;
		var result = AnotherClass.AnotherMethodAsync().GetAwaiter().GetResult();
		var myString = "Hello";
		var result2 = AnotherClass.AnotherMethodAsync().Result;
		var result3 = AnotherClass.AnotherMethodAsync().GetAwaiter().GetResult();
		return result;
	}

	private int _fieldInAsyncClass = 42;
}
