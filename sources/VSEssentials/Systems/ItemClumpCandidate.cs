using Vintagestory.API.Common;

namespace Vintagestory.GameContent;

// One queued item that waits to be merged with its neighbours.
internal sealed class ItemClumpCandidate
{
    public EntityItem Entity;
    public long EntityId;
    public long DueMs;
    public int Attempts;
    public bool Cancelled;

    // Position at the previous check. An item that hangs without support and does not move
    // is outside the simulation range, so physics does not tick it.
    public bool HasLastPos;
    public double LastX, LastY, LastZ;
}
