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

	// Snapshot used by OnFallOnto/FromTreeAttributes. Containers refresh from their live state when
	// processed; other block entities keep the request-time state, including if the source entity dies.
	public TreeAttribute BlockEntityTree;

	public BlockPos InitialPos;
	public bool DoRemoveBlock;
}
