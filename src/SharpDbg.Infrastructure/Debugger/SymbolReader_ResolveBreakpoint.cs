using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SharpDbg.Infrastructure.Debugger;

// 🤖
public partial class SymbolReader
{
	private sealed record MethodCandidate(
    int Token,
    SequencePoint FirstSp,      // smallest End where EndLine >= line (next-line snapping)
    SequencePoint LastSp,       // largest End where EndLine >= line
    SequencePoint? CoveringSp,  // latest Start where the requested source position is covered
    string DocPath)
	{
	    public bool CoversLine => CoveringSp.HasValue;
	}

	public ResolvedBreakpoint? ResolveBreakpoint(string sourceFilePath, int line, int? column = null)
	{
	    var normalizedPath = NormalizePath(sourceFilePath);

	    var documentHandle = _reader.Documents.FirstOrDefault(h => PathsMatch(normalizedPath, _reader.GetString(_reader.GetDocument(h).Name)));

	    if (documentHandle.IsNil) return null;

	    var candidates = _reader.MethodDebugInformation
	        .Select(h => CollectCandidate(h, documentHandle, line, column))
	        .OfType<MethodCandidate>()
	        .ToList();

	    var covering = candidates.Where(c => c.CoversLine).ToList();

	    if (covering.Count == 0)
	        return candidates
	            .OrderBy(c => c.FirstSp.Start())
	            .Select(c => NewResolvedBreakpoint(c.Token, c.FirstSp, c.DocPath))
	            .FirstOrDefault();

	    if (covering.Count == 1) return NewResolvedBreakpoint(covering[0].Token, covering[0].CoveringSp!.Value, covering[0].DocPath);

	    // Keep only the candidates whose covering SP starts latest — i.e. whose SP most
	    // specifically matches the target line from above or at.  This naturally picks the
	    // innermost lambda when the enclosing method only covers the line via a large
	    // spanning SP (e.g. a delegate-assignment SP that spans the whole lambda body),
	    // without needing explicit case analysis.
	    var maxCoverStart = covering.Max(c => c.CoveringSp!.Value.Start());
	    var primary = covering.Where(c => c.CoveringSp!.Value.Start() == maxCoverStart).ToList();

	    if (primary.Count == 1) return NewResolvedBreakpoint(primary[0].Token, primary[0].CoveringSp!.Value, primary[0].DocPath);

		// https://github.com/Samsung/netcoredbg/blob/8b8b22200fecdb1aec5f47af63215462d8c79a4b/src/managed/SymbolReader.cs#L801-L817

	    // Only reach here when multiple methods have a covering SP starting at the exact
	    // same source position — the same-line lambda case (e.g. items.Select(i => i * 2)).
	    // Apply netcoredbg's two-case containment check for that narrow scenario.
	    var sorted = primary.OrderBy(c => c.FirstSp.Start()).ToList();
	    var outer  = sorted[^2];
	    var nested = sorted[^1];

	    // Case 1: lambda range fully inside outer's first SP → BP is on the call-site line
	    if (nested.FirstSp.Start() > outer.FirstSp.Start() &&
	        nested.LastSp.End()    < outer.FirstSp.End())
	        return NewResolvedBreakpoint(outer.Token,  outer.CoveringSp!.Value,  outer.DocPath);

	    // Case 2: outer's first SP ends after nested's -> BP is closer to the lambda body
	    if (outer.FirstSp.End() > nested.FirstSp.End())
	        return NewResolvedBreakpoint(nested.Token, nested.CoveringSp!.Value, nested.DocPath);

	    return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);
	}

	private MethodCandidate? CollectCandidate(MethodDebugInformationHandle handle, DocumentHandle docHandle, int line, int? column)
	{
	    var info = _reader.GetMethodDebugInformation(handle);
	    if (info.SequencePointsBlob.IsNil) return null;

	    SequencePoint? firstSP    = null;
	    SequencePoint? lastSP     = null;
	    SequencePoint? coveringSP = null;
	    string? docPath = null;
	    LineCol? requestedPosition = column is null ? null : new LineCol(line, column.Value);

	    foreach (var sp in info.GetSequencePoints())
	    {
	        if (sp.IsHidden) continue;
	        var spDoc = sp.Document.IsNil ? info.Document : sp.Document;
	        if (spDoc != docHandle || IsBeforeRequestedPosition(sp, line, requestedPosition)) continue;

	        docPath ??= _reader.GetString(_reader.GetDocument(spDoc).Name);

	        if (firstSP == null || sp.End() < firstSP.Value.End()) firstSP = sp;
	        if (lastSP  == null || sp.End() > lastSP.Value.End())  lastSP  = sp;

	        if (CoversRequestedPosition(sp, line, requestedPosition) && ShouldReplaceCoveringSequencePoint(sp, coveringSP, requestedPosition))
	            coveringSP = sp;
	    }

	    if (firstSP == null) return null;
	    return new MethodCandidate(MetadataTokens.GetToken(handle.ToDefinitionHandle()), firstSP.Value, lastSP!.Value, coveringSP, docPath!);
	}

	private static bool IsBeforeRequestedPosition(SequencePoint sp, int line, LineCol? requestedPosition)
	{
		return requestedPosition is null
			? sp.EndLine < line
			: sp.End() < requestedPosition.Value;
	}

	private static bool CoversRequestedPosition(SequencePoint sp, int line, LineCol? requestedPosition)
	{
		return requestedPosition is null
			? sp.StartLine <= line
			: sp.Start() <= requestedPosition.Value && requestedPosition.Value <= sp.End();
	}

	private static bool ShouldReplaceCoveringSequencePoint(SequencePoint sp, SequencePoint? coveringSP, LineCol? requestedPosition)
	{
		if (coveringSP is null) return true;

		return requestedPosition is null
			? sp.StartLine > coveringSP.Value.StartLine ||
			  (sp.StartLine == coveringSP.Value.StartLine && sp.StartColumn < coveringSP.Value.StartColumn)
			: sp.Start() > coveringSP.Value.Start();
	}

	private static ResolvedBreakpoint NewResolvedBreakpoint(int token, SequencePoint sp, string docPath) =>
		new(token, sp.Offset, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn, docPath);
}

internal readonly record struct LineCol(int Line, int Column) : IComparable<LineCol>
{
	public int CompareTo(LineCol other)
	{
		var c = Line.CompareTo(other.Line);
		return c != 0 ? c : Column.CompareTo(other.Column);
	}
	public static bool operator <(LineCol a, LineCol b) => a.CompareTo(b) < 0;
	public static bool operator >(LineCol a, LineCol b) => a.CompareTo(b) > 0;
	public static bool operator <=(LineCol a, LineCol b) => a.CompareTo(b) <= 0;
	public static bool operator >=(LineCol a, LineCol b) => a.CompareTo(b) >= 0;
}

file static class LineColExtensions
{
	public static LineCol Start(this SequencePoint sp) => new(sp.StartLine, sp.StartColumn);
	public static LineCol End(this SequencePoint sp) => new(sp.EndLine, sp.EndColumn);
}
