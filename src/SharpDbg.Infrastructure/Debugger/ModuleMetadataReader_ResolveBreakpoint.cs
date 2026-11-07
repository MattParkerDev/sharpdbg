using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SharpDbg.Infrastructure.Debugger;

// 🤖
public partial class ModuleMetadataReader
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

	/// The document a breakpoint path names, see <see cref="SourceDocumentMatch"/>. Null when no document has the file's name,
	/// or a relative path's folders do not all match
	public SourceDocumentMatch? FindDocument(string sourceFilePath)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var requestedComponents = SourceDocumentMatch.GetPathComponents(sourceFilePath);
		if (requestedComponents.Count is 0) return null;
		var isRelative = SourceDocumentMatch.IsRelativePath(sourceFilePath);

		SourceDocumentMatch? best = null;
		foreach (var handle in reader.Documents)
		{
			var documentPath = reader.GetString(reader.GetDocument(handle).Name);
			var documentComponents = SourceDocumentMatch.GetPathComponents(documentPath);
			var matchingComponents = SourceDocumentMatch.CountMatchingTrailingComponents(requestedComponents, documentComponents);
			if (matchingComponents is 0) continue; // not even the file name
			var isExact = isRelative is false && matchingComponents == requestedComponents.Count && matchingComponents == documentComponents.Count;
			// A relative path names its folders, all of which must match: '../wrong_folder/Program.cs' is not 'folder/Program.cs'
			if (isRelative && matchingComponents < requestedComponents.Count) continue;
			var match = new SourceDocumentMatch(handle, isExact, matchingComponents, documentPath.Length);
			if (best is null || match.IsBetterThan(best.Value)) best = match;
		}
		return best;
	}

	public ResolvedBreakpoint? ResolveBreakpoint(SourceDocumentMatch document, int line, int? column = null)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var documentHandle = document.Handle;

		var candidates = reader.MethodDebugInformation
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
		var outer = sorted[^2];
		var nested = sorted[^1];

		// Case 1: lambda range fully inside outer's first SP → BP is on the call-site line
		if (nested.FirstSp.Start() > outer.FirstSp.Start() &&
			nested.LastSp.End() < outer.FirstSp.End())
			return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);

		// Case 2: outer's first SP ends after nested's -> BP is closer to the lambda body
		if (outer.FirstSp.End() > nested.FirstSp.End())
			return NewResolvedBreakpoint(nested.Token, nested.CoveringSp!.Value, nested.DocPath);

		return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);
	}

	private MethodCandidate? CollectCandidate(MethodDebugInformationHandle handle, DocumentHandle docHandle, int line, int? column)
	{
		var reader = _pdbMetadataReader ?? throw new InvalidOperationException("No symbols are loaded.");
		var info = reader.GetMethodDebugInformation(handle);
		if (info.SequencePointsBlob.IsNil) return null;

		SequencePoint? firstSP = null;
		SequencePoint? lastSP = null;
		SequencePoint? coveringSP = null;
		string? docPath = null;

		foreach (var sp in info.GetSequencePoints())
		{
			if (sp.IsHidden) continue;
			var spDoc = sp.Document.IsNil ? info.Document : sp.Document;
			if (spDoc != docHandle || sp.IsBeforeRequestedPosition(line, column)) continue;

			docPath ??= reader.GetString(reader.GetDocument(spDoc).Name);

			if (firstSP is null || sp.End() < firstSP.Value.End()) firstSP = sp;
			if (lastSP is null || sp.End() > lastSP.Value.End()) lastSP = sp;

			if (sp.CoversRequestedPosition(line, column) && sp.ShouldReplaceCoveringSequencePoint(coveringSP, column))
				coveringSP = sp;
		}

		if (firstSP is null) return null;
		return new MethodCandidate(MetadataTokens.GetToken(handle.ToDefinitionHandle()), firstSP.Value, lastSP!.Value, coveringSP, docPath!);
	}
	private static ResolvedBreakpoint NewResolvedBreakpoint(int token, SequencePoint sp, string docPath) =>
		new(token, sp.Offset, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn, docPath);
}

/// How well a PDB document matches the path a breakpoint was set with. The exact path wins; otherwise the document whose
/// path ends in the most of the requested path's components, e.g. a PDB built elsewhere ('/_/src/Program.cs') still matches
/// 'C:/repo/src/Program.cs', and over '/_/src/folder/Program.cs'. Equally good matches go to the shortest path.
public readonly record struct SourceDocumentMatch(DocumentHandle Handle, bool IsExact, int MatchingComponents, int PathLength)
{
	private static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

	public bool IsBetterThan(SourceDocumentMatch other)
	{
		if (IsExact != other.IsExact) return IsExact;
		if (MatchingComponents != other.MatchingComponents) return MatchingComponents > other.MatchingComponents;
		return PathLength < other.PathLength;
	}

	/// The path's folder and file names, with '.' and '..' applied. A leading '..' has no folder to remove and is dropped.
	internal static List<string> GetPathComponents(string path)
	{
		var result = new List<string>();
		foreach (var component in path.Split('/', '\\'))
		{
			if (component is "" or ".") continue;
			if (component is "..")
			{
				if (result.Count > 0) result.RemoveAt(result.Count - 1);
				continue;
			}
			result.Add(component);
		}
		return result;
	}

	internal static bool IsRelativePath(string path)
	{
		if (path.StartsWith('/') || path.StartsWith('\\')) return false;
		return path is not [_, ':', ..]; // e.g. 'C:'
	}

	internal static int CountMatchingTrailingComponents(List<string> left, List<string> right)
	{
		var count = 0;
		while (count < left.Count && count < right.Count && string.Equals(left[^(count + 1)], right[^(count + 1)], PathComparison))
		{
			count++;
		}
		return count;
	}
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
file static class SequencePointExtensions
{
	public static bool IsBeforeRequestedPosition(this SequencePoint sp, int requestedLine, int? requestedColumn)
	{
		return requestedColumn is null
			? sp.EndLine < requestedLine
			: sp.End() < new LineCol(requestedLine, requestedColumn.Value);
	}

	public static bool CoversRequestedPosition(this SequencePoint sp, int requestedLine, int? requestedColumn)
	{
		return requestedColumn is null
			? sp.StartLine <= requestedLine
			: new LineCol(requestedLine, requestedColumn.Value) is var requestedPosition && sp.Start() <= requestedPosition && requestedPosition <= sp.End();
	}

	public static bool ShouldReplaceCoveringSequencePoint(this SequencePoint sp, SequencePoint? coveringSp, int? requestedColumn)
	{
		if (coveringSp is null) return true;

		return requestedColumn is null
			? sp.StartLine > coveringSp.Value.StartLine ||
			  (sp.StartLine == coveringSp.Value.StartLine && sp.StartColumn < coveringSp.Value.StartColumn)
			: sp.Start() > coveringSp.Value.Start();
	}
}
