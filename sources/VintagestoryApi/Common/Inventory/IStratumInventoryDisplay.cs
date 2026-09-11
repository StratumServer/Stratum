using Vintagestory.API.Datastructures;

namespace Vintagestory.API.Common;

/// <summary>Controls which inventory slots other players can see.</summary>
public interface IStratumInventoryDisplay
{
	bool StratumNeedsInventoryForBlockInfo { get; }
	void StratumFilterInventoryForDisplay(ITreeAttribute tree);
}
