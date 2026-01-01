using DebuggableConsoleApp.Namespace1;

namespace DebuggableConsoleApp;

public class MyAsyncClass
{
	public async Task<int> MyMethodAsync()
	{
		var intVar = 10;
		var result = await AnotherClass.AnotherMethodAsync();
		var result2 = await AnotherClass.AnotherMethodAsync();
		return result;
	}
}
