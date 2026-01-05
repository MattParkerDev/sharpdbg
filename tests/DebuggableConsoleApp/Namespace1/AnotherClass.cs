namespace DebuggableConsoleApp.Namespace1;

public class AnotherClass
{
	public static int IntStaticProperty { get; set; } = 10;
	public static List<int> MyStaticMethod()
	{
		var test = 4;
		List<int> myList = [1, 4, 8, 11];
		var myResult = myList
			.Where(s => s is 4)
			.ToList();
		return myResult;
	}

	public static async Task<int> AnotherMethodAsync()
	{
		var test = 5;
		await Task.Delay(10);
		return test;
	}

	public static async void AsyncVoidMethod()
	{
		var test = 5;
		await Task.Delay(10);
		;
	}
}
