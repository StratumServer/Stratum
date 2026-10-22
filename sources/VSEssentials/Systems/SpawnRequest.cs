using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

// One queued fall request.
internal struct SpawnRequest
{
	public Block Block;                 // falling/landing variant (may differ from the source, e.g. rock -> crackedrock)
	public Block SourceBlock;           // original block physically at InitialPos when the request was made
	public BlockEntity BlockEntity;

	// Snapshot of the block entity attributes taken when the request is made. The live block entity can
	// change or die while the request waits, and OnFallOnto/FromTreeAttributes need the original state.
	public TreeAttribute BlockEntityTree;

	public BlockPos InitialPos;
	public bool DoRemoveBlock;
}
