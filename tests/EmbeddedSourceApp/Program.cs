namespace EmbeddedSourceApp;

public static class Program
{
	public static void Main()
	{
		while (true)
		{
			Work();
			Thread.Sleep(100);
		}
	}

	static void Work()
	{
		var embeddedMarker = 42;
		;
	}
}
