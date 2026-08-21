using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpDbg.Application.Protocol;

public class ResolveStackFrameRequest : DebugRequestWithResponse<ResolveStackFrameArguments, ResolveStackFrameResponse>
{
	public const string RequestType = "resolveStackFrame";

	[JsonIgnore]
	public int StackFrameId
	{
		get => Args.StackFrameId;
		set => Args.StackFrameId = value;
	}

	public ResolveStackFrameRequest() : base(RequestType) { }

	public ResolveStackFrameRequest(int stackFrameId) : base(RequestType)
	{
		Args.StackFrameId = stackFrameId;
	}
}

public class ResolveStackFrameArguments : DebugRequestArguments
{
	[JsonProperty("stackFrameId")]
	public int StackFrameId { get; set; }
}

public class ResolveStackFrameResponse : ResponseBody
{
	[JsonProperty("stackFrame")]
	public StackFrame StackFrame { get; set; } = null!;
}

public static class StackFrameExtensions
{
	extension(StackFrame stackFrame)
	{
		public bool? IsResolved
		{
			get => stackFrame.AdditionalProperties["isResolved"]?.Value<bool>();
			set => stackFrame.AdditionalProperties["isResolved"] = value;
		}
	}
}
