using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;

namespace Vintagestory.Server;

internal static class StratumInventorySync
{
	private static readonly byte treeAttributeId = (byte)new TreeAttribute().GetAttributeId();
	private static readonly byte stackAttributeId = (byte)new ItemstackAttribute().GetAttributeId();
	private static readonly byte treeArrayAttributeId = (byte)new TreeArrayAttribute().GetAttributeId();
	private static readonly byte[] stratumBackpackKeyBytes = System.Text.Encoding.ASCII.GetBytes("backpack");

	internal static Packet_InventoryContents GetPublicInventory(InventoryBase inventory, IServerPlayer owner)
	{
		if (!StratumInventoryPrivacy.InventoryGuardsEnabled)
		{
			return (inventory.InvNetworkUtil as InventoryNetworkUtil).ToPacket(owner);
		}

		var slots = new Packet_ItemStack[inventory.CountForNetworkPacket];
		for (int i = 0; i < slots.Length; i++)
		{
			bool visible = inventory.ClassName != "hotbar" || i == owner.InventoryManager.ActiveHotbarSlotNumber || inventory[i] == owner.Entity.LeftHandItemSlot;
			ItemStack stack = visible ? inventory[i]?.Itemstack : null;
			slots[i] = stack == null ? new Packet_ItemStack { ItemClass = -1 } : StackConverter.ToPacket(StratumInventoryPrivacy.GetPublicStack(stack));
		}

		var packet = new Packet_InventoryContents {
			ClientId = owner.ClientId,
			InventoryId = inventory.InventoryID,
			InventoryClass = inventory.ClassName
		};
		packet.SetItemstacks(slots);
		return packet;
	}

	internal static ITreeAttribute GetBlockEntityAttributes(BlockEntity blockEntity, IPlayer viewer = null)
	{
		var tree = new TreeAttribute();
		blockEntity.ToTreeAttributes(tree);
		if (CanViewBlockEntity(blockEntity, viewer)) return tree;

		(blockEntity as IStratumInventoryDisplay)?.StratumFilterInventoryForDisplay(tree);
		return StratumInventoryPrivacy.GetPublicAttributes(tree);
	}

	internal static bool CanInspect(BlockEntity blockEntity, IPlayer viewer)
	{
		return blockEntity is IStratumInventoryDisplay display && display.StratumNeedsInventoryForBlockInfo
			&& viewer?.CurrentBlockSelection?.Position?.Equals(blockEntity.Pos) == true
			&& blockEntity is IBlockEntityContainer container
			&& StratumInventoryPrivacy.CanAccess(container.Inventory as InventoryBase, viewer);
	}

	internal static bool CanViewBlockEntity(BlockEntity blockEntity, IPlayer viewer)
	{
		return (blockEntity is IBlockEntityContainer container && StratumInventoryPrivacy.CanView(container.Inventory as InventoryBase, viewer))
			|| CanInspect(blockEntity, viewer);
	}

	internal static void SendOpenedInventory(InventoryBase inventory, IPlayer player)
	{
		if (!StratumInventoryPrivacy.InventoryGuardsEnabled || inventory.Api.Side != EnumAppSide.Server || !StratumInventoryPrivacy.CanView(inventory, player)) return;
		if (inventory.Pos == null)
		{
			if (player is IServerPlayer serverPlayer && inventory.InvNetworkUtil is InventoryNetworkUtil network)
			{
				var packet = new Packet_Server {
					Id = 30,
					InventoryContents = network.ToPacket(player)
				};
				((ICoreServerAPI)inventory.Api).Network.SendArbitraryPacket(packet, serverPlayer);
			}
			return;
		}
		var server = (ServerMain)inventory.Api.World;
		BlockEntity blockEntity = server.BlockAccessor.GetBlockEntity(inventory.Pos);
		if (blockEntity is IBlockEntityContainer container && ReferenceEquals(container.Inventory, inventory))
		{
			server.SendBlockEntity((IServerPlayer)player, blockEntity);
		}
	}

	internal static Packet_BlockEntity GetFullBlockEntityPacket(BlockEntity blockEntity, Packet_BlockEntity publicPacket)
	{
		var tree = new TreeAttribute();
		blockEntity.ToTreeAttributes(tree);
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		tree.ToBytes(writer);
		return new Packet_BlockEntity {
			Classname = publicPacket.Classname,
			Data = stream.ToArray(),
			PosX = publicPacket.PosX,
			PosY = publicPacket.PosY,
			PosZ = publicPacket.PosZ
		};
	}

	internal static void RestoreChunkViews(ServerMain server, IServerPlayer player, FastList<ServerChunkWithCoord> chunks)
	{
		if (!StratumInventoryPrivacy.InventoryGuardsEnabled) return;
		if (chunks.Count == 0) return;
		foreach (InventoryBase inventory in player.InventoryManager.Inventories.Values)
		{
			if (inventory.Pos == null || !StratumInventoryPrivacy.CanView(inventory, player)) continue;
			BlockEntity blockEntity = getSentBlockEntity(inventory.Pos, chunks);
			if (blockEntity is IBlockEntityContainer container && ReferenceEquals(container.Inventory, inventory))
			{
				server.SendBlockEntity(player, blockEntity);
			}
		}

		BlockPos selected = player.CurrentBlockSelection?.Position;
		if (selected == null) return;
		BlockEntity inspected = getSentBlockEntity(selected, chunks);
		if (CanInspect(inspected, player) && inspected is IBlockEntityContainer inspectedContainer
			&& !StratumInventoryPrivacy.CanView(inspectedContainer.Inventory as InventoryBase, player))
		{
			server.SendBlockEntity(player, inspected);
		}
	}

	private static BlockEntity getSentBlockEntity(BlockPos pos, FastList<ServerChunkWithCoord> chunks)
	{
		var chunkPos = new ChunkPos(pos);
		for (int i = 0; i < chunks.Count; i++)
		{
			ServerChunkWithCoord chunk = chunks[i];
			if (!chunk.pos.Equals(chunkPos)) continue;
			chunk.chunk.BlockEntities.TryGetValue(pos, out BlockEntity blockEntity);
			return blockEntity;
		}
		return null;
	}

	internal static void NotifySlotViewers(IPlayer actor, InventoryBase inventory, int slotId)
	{
		foreach (string uid in inventory.openedByPlayerGUIds)
		{
			if (uid == actor.PlayerUID) continue;
			if (inventory.Api.World.PlayerByUid(uid) is not IServerPlayer viewer || !StratumInventoryPrivacy.CanView(inventory, viewer)) continue;
			var packet = (inventory.InvNetworkUtil as InventoryNetworkUtil)?.getSlotUpdatePacket(viewer, slotId);
			if (packet != null) ((ICoreServerAPI)inventory.Api).Network.SendArbitraryPacket(packet, viewer);
		}
	}

	internal static void FilterPublicEntityUpdates(Entity entity, string[] paths, byte[][] data)
	{
		if (!StratumInventoryPrivacy.InventoryGuardsEnabled) return;

		for (int i = 0; i < paths.Length; i++)
		{
			string path = paths[i];
			if (path == null || data[i] == null || data[i].Length == 0) continue;
			if (path == "backpack" || path.StartsWith("backpack/", StringComparison.Ordinal) || path.EndsWith("/backpack", StringComparison.Ordinal) || path.Contains("/backpack/", StringComparison.Ordinal))
			{
				data[i] = null;
				continue;
			}
			// TreeAttribute.ToBytes writes each key with BinaryWriter.Write(string), so a
			// path carrying a bag has the literal "backpack" key in its bytes. A miss here
			// is every player and animal health and hunger tick, so skip before deserialising.
			if (data[i].AsSpan().IndexOf(stratumBackpackKeyBytes) < 0) continue;
			// Use the packet data because the live attributes may have changed.
			byte id = data[i][0];
			IAttribute attribute;
			if (id == treeAttributeId) attribute = new TreeAttribute();
			else if (id == stackAttributeId) attribute = new ItemstackAttribute();
			else if (id == treeArrayAttributeId) attribute = new TreeArrayAttribute();
			else continue;
			using var input = new BinaryReader(new MemoryStream(data[i]));
			input.ReadByte();
			attribute.FromBytes(input);
			IAttribute filtered = StratumInventoryPrivacy.GetPublicAttribute(attribute);
			if (ReferenceEquals(attribute, filtered)) continue;
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			writer.Write((byte)filtered.GetAttributeId());
			filtered.ToBytes(writer);
			data[i] = stream.ToArray();
		}
	}

	internal static void CloseInventoryForPlayer(InventoryBase inventory, IServerPlayer player)
	{
		if (inventory == null || player == null) return;
		player.InventoryManager.CloseInventory(inventory);
		if (inventory.Pos != null && inventory.Api is ICoreServerAPI sapi)
		{
			// Stratum: some block entities (ground-stored bags) dispatch by slot; carry the
			// same offset the dialog opened with so the close lands on the right sub-dialog.
			sapi.Network.SendBlockEntityPacket(player, inventory.Pos,
				(int)EnumBlockEntityPacketId.Close + inventory.StratumBlockEntityPacketIdOffset);
		}
	}
}
