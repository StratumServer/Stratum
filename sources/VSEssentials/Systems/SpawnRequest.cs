using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

// One queued fall request. Used by both the entity queue and the instant queue.
internal struct SpawnRequest
{
    public Block Block;                 // falling/landing variant (may differ from the source, e.g. rock -> crackedrock)
    public Block SourceBlock;           // original block physically at InitialPos when the request was made
    public BlockEntity BlockEntity;

    // Snapshot of the block entity attributes taken in RequestSpawn. The live block entity can
    // change or die while the request waits, and OnFallOnto/FromTreeAttributes need the original state.
    public TreeAttribute BlockEntityTree;

    public BlockPos InitialPos;
    public AssetLocation FallSound;
    public float ImpactDamageMul;
    public bool CanFallSideways;
    public float DustIntensity;
    public bool DoRemoveBlock;
    public Vec3d PositionOffset;

    // How many times the request was put back because the position was still occupied.
    // Only used by the entity queue.
    public int RetryCount;
}
