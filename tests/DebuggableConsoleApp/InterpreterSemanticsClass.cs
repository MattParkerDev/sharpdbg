namespace DebuggableConsoleApp;

// Static state read by EvalTests, kept off MyClass so the variables it lists are unaffected
public static class InterpreterSemantics
{
	public static uint UIntMax = uint.MaxValue;
	public static char Letter = 'x';
	public static string Text = "abc";
	public static SByteEnum SignedKind = SByteEnum.Negative;
	public static UShortEnum UnsignedKind = UShortEnum.Large;
	public static long LongIndex = 1;
	public static int[] Numbers = [3, 1, 2];
	public static bool Flag;
	public static int? Maybe = 3;
	public static object BoxedLong = 5000000000L;
	public static WidthHolder Holder = new();
}

public enum SByteEnum : sbyte { Negative = -1, Positive = 1 }
public enum UShortEnum : ushort { Small = 1, Large = 40000 }

public struct WidthStruct
{
	public double Width { get; set; }
	public WidthStruct(double width) { Width = width; }
}

public class WidthHolder
{
	public double? Nullable { get; set; }
	public WidthStruct Box { get; set; }
}
