namespace RuntimeAsyncConsoleApp;

public class AsyncMethodEvalClass
{
	public int IntField = 10;
	public async Task Test()
	{
		var localInt = 4;

		; // bp before suspension
		await Task.Delay(5);
		; // bp after suspension

	}
}
