using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// This appears to be 1 based, ie requires no adjustment when returned to the user
	private (string FilePath, int StartLine, int StartColumn)? GetSourceInfoAtFrame(CorDebugFrame frame)
	{
		if (frame is not CorDebugILFrame ilFrame)
			throw new InvalidOperationException("Active frame is not an IL frame");
		var function = ilFrame.Function;
		var module = _modules[function.Module.BaseAddress];
		if (module.SymbolReader is not null)
		{
			var ilOffset = ilFrame.IP.pnOffset;
			var methodToken = function.Token;
			var sourceInfo = module.SymbolReader.GetSourceLocationForOffset(methodToken, ilOffset);
			if (sourceInfo != null)
			{
				return (sourceInfo.Value.sourceFilePath, sourceInfo.Value.startLine, sourceInfo.Value.startColumn);
			}
		}

		return null;
	}
}
