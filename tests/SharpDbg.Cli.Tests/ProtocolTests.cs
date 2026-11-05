using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.InMemory;

namespace SharpDbg.Cli.Tests;

public class ProtocolTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public void UnsupportedRequests_ReturnErrorsWithoutStoppingDispatcher()
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();
		using var _ = adapter;
		using var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		host.Run();

		var unimplementedResult = host.SendRequest(new SetVariableArguments { VariablesReference = 0, Name = "value", Value = "1" });
		unimplementedResult.Value.Should().BeOfType<DapError>().Which.Message.Should().Be("Request 'setVariable' is not supported.");

		var unknownResult = host.SendRequest(new UnknownArguments());
		unknownResult.Value.Should().BeOfType<DapError>().Which.Message.Should().Be("Request 'unknownRequest' is not supported.");

		var initializeResponse = host.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		initializeResponse.Should().NotBeNull();
	}

	private sealed record UnknownArguments : IDapRequest<UnknownResponse>
	{
		public string Command => "unknownRequest";
	}
	private sealed record UnknownResponse;
}
