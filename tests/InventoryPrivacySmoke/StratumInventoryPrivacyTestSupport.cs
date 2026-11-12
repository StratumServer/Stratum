using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Common;

// These doubles stay in the smoke-test assembly so they cannot become part of the
// VintagestoryAPI.dll or a mod's public API. The API assembly grants this test assembly
// access to IPlayer's internal BlockPos range member; every unused member throws so a
// production dependency accidentally added to the code under test fails loudly.
public sealed class StratumTestPlayer : IPlayer
{
	private readonly string playerUID;
	private readonly EntityPlayer entity;
	private readonly IPlayerInventoryManager inventoryManager;

	public bool StratumInRange = true;
	public Entity StratumRangeEntity = null!;

	public StratumTestPlayer(string playerUID, EntityPlayer entity, IPlayerInventoryManager inventoryManager)
	{
		this.playerUID = playerUID;
		this.entity = entity;
		this.inventoryManager = inventoryManager;
	}

	public string PlayerUID => playerUID;
	public EntityPlayer Entity => entity;
	public IPlayerInventoryManager InventoryManager => inventoryManager;
	public bool IsInInteractionRangeOf(Entity rangeEntity, float slack = .25f) => StratumInRange && ReferenceEquals(rangeEntity, StratumRangeEntity);
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

// Only GetInventory(string) is implemented because it is the member exercised by
// StratumInventoryPrivacy.CanAccess and CanView.
public sealed class StratumTestInventoryManager : IPlayerInventoryManager
{
	public IInventory StratumRegisteredInventory = null!;

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
	public ItemSlot GetBestSuitedSlot(ItemSlot sourceSlot, bool onlyPlayerInventory, ItemStackMoveOperation op = null!, List<ItemSlot> skipSlots = null!) => throw new NotSupportedException();
	public ItemSlot GetBestSuitedSlot(ItemSlot sourceSlot, ItemStackMoveOperation op, List<ItemSlot> skipSlots) => throw new NotSupportedException();
	public object[] TryTransferAway(ItemSlot sourceSlot, ref ItemStackMoveOperation op, bool onlyPlayerInventory, bool slotNotifyEffect = false) => throw new NotSupportedException();
	public object[] TryTransferAway(ItemSlot sourceSlot, ref ItemStackMoveOperation op, bool onlyPlayerInventory, StringBuilder shiftClickDebugText, bool slotNotifyEffect = false) => throw new NotSupportedException();
	public object TryTransferTo(ItemSlot sourceSlot, ItemSlot targetSlot, ref ItemStackMoveOperation op) => throw new NotSupportedException();
	public bool TryGiveItemstack(ItemStack itemstack, bool slotNotifyEffect = false) => throw new NotSupportedException();
	public object OpenInventory(IInventory inventory) => throw new NotSupportedException();
	public object CloseInventory(IInventory inventory) => throw new NotSupportedException();
	public void CloseInventoryAndSync(IInventory inventory) => throw new NotSupportedException();
	public bool Find(System.Func<ItemSlot, bool> matcher) => throw new NotSupportedException();
	public bool HasInventory(IInventory inventory) => throw new NotSupportedException();
	public void DiscardAll() => throw new NotSupportedException();
	public void OnDeath() => throw new NotSupportedException();
	public void DropAllInventoryItems(IInventory inv) => throw new NotSupportedException();
	public void BroadcastHotbarSlot() => throw new NotSupportedException();
}
