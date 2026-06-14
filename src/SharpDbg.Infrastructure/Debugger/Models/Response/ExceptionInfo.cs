namespace SharpDbg.Infrastructure.Debugger.Models.Response;

public class ExceptionInfo
{
	public string ExceptionId { get; set; } = null!;
	public string Description { get; set; } = null!;
	public SharpDbgExceptionBreakMode BreakMode { get; set; }
	public int Code { get; set; }
	public ExceptionDetails Details { get; set; } = null!;

	public class ExceptionDetails
	{
		public string Message { get; set; } = null!;
		public string TypeName { get; set; } = null!;
		public string FullTypeName { get; set; } = null!;
		public string EvaluateName { get; set; } = null!;
		public string StackTrace { get; set; } = null!;
		public List<ExceptionDetails> InnerException { get; set; } = null!;
		public string FormattedDescription { get; set; } = null!;
		public int HResult { get; set; }
		public string Source { get; set; } = null!;
	}
}

public enum SharpDbgExceptionBreakMode
{
	Never = 0,
	Always = 1,
	Unhandled = 2,
	UserUnhandled = 3,
	Unknown = 2147483647
}
