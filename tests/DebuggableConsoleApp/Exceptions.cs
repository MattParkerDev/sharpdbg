namespace DebuggableConsoleApp;

public static class Exceptions
{
	public static void Test(bool shouldThrow)
	{
		var test = shouldThrow;
		try
		{
			if (test)
			{
				throw new InvalidOperationException("Test exception");
			}
		}
		catch (Exception e)
		{
			;
		}
	}
}
