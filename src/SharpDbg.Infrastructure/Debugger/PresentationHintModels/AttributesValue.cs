namespace SharpDbg.Infrastructure.Debugger.PresentationHintModels;

//[Flags]
public enum AttributesValue : uint
{
	None = 0,
	//Static = 1,
	//Constant = 2,
	//ReadOnly = 4,
	//RawString = 8,
	//HasObjectId = 16,
	//CanHaveObjectId = 32,
	//HasSideEffects = 64,
	//HasDataBreakpoint = 16384,
	FailedEvaluation = 128,
	//CanFavorite = 256,
	//IsFavorite = 512,
	//HasFavorites = 1024,
	//ExpansionHasSideEffects = 2048,
	//IsBoolean = 4096,
	//IsTrue = 8192,
	//IsObjectReplaceable = 32768,
}
