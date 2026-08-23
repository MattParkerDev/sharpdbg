namespace SharpDbg.Infrastructure.Debugger;

public partial class FrameReferenceManager
{
	private readonly Dictionary<int, (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame)?> _syntheticAsyncFrameReferences = [];
	private readonly Dictionary<(ThreadId threadId, FrameStackDepth physicalFrameStackDepth, int syntheticFrameIndex), int> _syntheticAsyncFrameReferencesByPhysicalFrame = [];

	internal int GetOrCreateSyntheticAsyncFrameId(ThreadId threadId, FrameStackDepth physicalFrameStackDepth, int syntheticFrameIndex, SyntheticAsyncCallerFrame frame)
	{
		lock (_lock)
		{
			var key = (threadId, physicalFrameStackDepth, syntheticFrameIndex);
			if (_syntheticAsyncFrameReferencesByPhysicalFrame.TryGetValue(key, out var existing)) return existing;
			var frameId = _nextFrameId++;
			_syntheticAsyncFrameReferences[frameId] = (threadId, physicalFrameStackDepth, frame);
			_syntheticAsyncFrameReferencesByPhysicalFrame[key] = frameId;
			return frameId;
		}
	}

	internal (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame)? GetSyntheticAsyncFrameById(int frameId)
	{
		lock (_lock) return _syntheticAsyncFrameReferences.GetValueOrDefault(frameId);
	}
}
