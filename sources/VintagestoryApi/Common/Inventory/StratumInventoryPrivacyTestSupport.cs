using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Common;

// Stratum: IPlayer declares IsInInteractionRangeOf(BlockPos, float) as an internal interface
// member, so only a type inside this assembly can implement IPlayer at all; the
// InventoryPrivacySmoke test project references this assembly but cannot satisfy that member
// from outside it (confirmed: the compiler rejects an external class that implements IPlayer
// even when it never calls that overload). These two classes exist solely so that test project
// can exercise StratumInventoryPrivacy.CanAccess and CanView against a real IPlayer instead of
// re-deriving their logic. Nothing in runtime code constructs them. Every member the two
// methods under test do not touch throws on purpose, so an accidental new dependency on this
// double shows up as a test failure instead of a silently wrong default.
public sealed class StratumTestPlayer : IPlayer
{
	private readonly string playerUID;
	private readonly EntityPlayer entity;
	private readonly IPlayerInventoryManager inventoryManager;

	public bool StratumInRange = true;

	public StratumTestPlayer(string playerUID, EntityPlayer entity, IPlayerInventoryManager inventoryManager)
	{
		this.playerUID = playerUID;
		this.entity = entity;
		this.inventoryManager = inventoryManager;
	}

	public string PlayerUID => playerUID;
	public EntityPlayer Entity => entity;
	public IPlayerInventoryManager InventoryManager => inventoryManager;
	public bool IsInInteractionRangeOf(Entity entity, float slack = .25f) => StratumInRange;
	// Stratum: internal on the interface (see the file header); only a type inside this
	// assembly can even write this signature. Nothing under test calls the BlockPos overload.
	bool IPlayer.IsInInteractionRangeOf(BlockPos blockPos, float slack) => throw new NotSupportedException();

	public IPlayerRole Role { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public PlayerGroupMembership[] Groups => throw new NotSupportedException();
	public PlayerGroupMembership[] GetGroups() => throw new NotSupportedException();
	public PlayerGroupMembership GetGroup(int groupId) => throw new NotSupportedException();
	public List<Entitlement> Entitlements => throw new NotSupportedException();
	public BlockSelection CurrentBlockSelection => throw new NotSupportedException();
	public EntitySelection CurrentEntitySelection => throw new NotSupportedException();
	public string PlayerName => throw new NotSupportedException();
	public int ClientId => throw new NotSupportedException();
	public IWorldPlayerData WorldData => throw new NotSupportedException();
	public string[] Privileges => throw new NotSupportedException();
	public bool ImmersiveFpMode => throw new NotSupportedException();
	public bool HasPrivilege(string privilegeCode) => throw new NotSupportedException();
}

// Stratum: see StratumTestPlayer. Only GetInventory(string) is implemented; that is the one
// member StratumInventoryPrivacy.CanAccess and CanView call on IPlayer.InventoryManager.
public sealed class StratumTestInventoryManager : IPlayerInventoryManager
{
	public IInventory StratumRegisteredInventory;

	public IInventory GetInventory(string inventoryId) => StratumRegisteredInventory;

	public EnumTool? ActiveTool => throw new NotSupportedException();
	public EnumTool? OffhandTool => throw new NotSupportedException();
	public int ActiveHotbarSlotNumber { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public ItemSlot ActiveHotbarSlot => throw new NotSupportedException();
	public ItemSlot OffhandHotbarSlot => throw new NotSupportedException();
	public Dictionary<string, IInventory> Inventories => throw new NotSupportedException();
	public IEnumerable<InventoryBase> InventoriesOrdered => throw new NotSupportedException();
	public List<IInventory> OpenedInventories => throw new NotSupportedException();
	public ItemSlot MouseItemSlot => throw new NotSupportedException();
	public ItemSlot CurrentHoveredSlot { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public bool DropMouseSlotItems(bool dropAll) => throw new NotSupportedException();
	public bool DropItem(ItemSlot slot, bool fullStack) => throw new NotSupportedException();
	public void NotifySlot(IPlayer player, ItemSlot slot) => throw new NotSupportedException();
	public string GetInventoryName(string inventoryClassName) => throw new NotSupportedException();
	public IInventory GetOwnInventory(string inventoryClassName) => throw new NotSupportedException();
	public bool GetInventory(string invID, [MaybeNullWhen(false)] out InventoryBase invFound) => throw new NotSupportedException();
	public ItemStack GetHotbarItemstack(int slotId) => throw new NotSupportedException();
	public IInventory GetHotbarInventory() => throw new NotSupportedException();
	public ItemSlot GetBestSuitedSlot(ItemSlot sourceSlot, bool onlyPlayerInventory, ItemStackMoveOperation op = null, List<ItemSlot> skipSlots = null) => throw new NotSupportedException();
	public ItemSlot GetBestSuitedSlot(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots) => throw new NotSupportedException();
	public object[] TryTransferAway(ItemSlot sourceSlot, ref ItemStackMoveOperation op, bool onlyPlayerInventory, bool slotNotifyEffect = false) => throw new NotSupportedException();
	public object[] TryTransferAway(ItemSlot sourceSlot, ref ItemStackMoveOperation op, bool onlyPlayerInventory, StringBuilder shiftClickDebugText, bool slotNotifyEffect = false) => throw new NotSupportedException();
	public object TryTransferTo(ItemSlot sourceSlot, ItemSlot targetSlot, ref ItemStackMoveOperation op) => throw new NotSupportedException();
	public bool TryGiveItemstack(ItemStack itemstack, bool slotNotifyEffect = false) => throw new NotSupportedException();
	public object OpenInventory(IInventory inventory) => throw new NotSupportedException();
	public object CloseInventory(IInventory inventory) => throw new NotSupportedException();
	public void CloseInventoryAndSync(IInventory inventory) => throw new NotSupportedException();
	// Stratum: System.Func, spelled out. Vintagestory.API.Common.Delegates declares its own
	// single-arg Func<T1, TResult> delegate, which would otherwise shadow System.Func here
	// since this file is in the same namespace (the interface source has the same
	// qualification for the same reason).
	public bool Find(System.Func<ItemSlot, bool> matcher) => throw new NotSupportedException();
	public bool HasInventory(IInventory inventory) => throw new NotSupportedException();
	public void DiscardAll() => throw new NotSupportedException();
	public void OnDeath() => throw new NotSupportedException();
	public void DropAllInventoryItems(IInventory inv) => throw new NotSupportedException();
	public void BroadcastHotbarSlot() => throw new NotSupportedException();
}
