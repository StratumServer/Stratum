using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vintagestory.Server;

internal sealed class StratumPacketLimiter
{
	private readonly object gate = new object();
	private readonly Dictionary<int, ClientPacketBudget> clients = new Dictionary<int, ClientPacketBudget>();
	private long totalAccepted;
	private long totalDropped;
	private long totalBytes;
	private long totalViolations;

	public (long Accepted, long Dropped, long Violations, long Bytes) Snapshot()
	{
		lock (gate) return (totalAccepted, totalDropped, totalViolations, totalBytes);
	}

	public bool ShouldDrop(ConnectedClient client, Packet_Client packet, int byteLength, out string disconnectReason)
	{
		disconnectReason = null;
		StratumConfig stratumConfig = StratumRuntime.Config;
		stratumConfig.EnsurePopulated();
		StratumPacketLimitsConfig config = stratumConfig.PacketLimits;
		if (!stratumConfig.Hardening.PacketMonitoring)
		{
			return false;
		}

		DateTime now = DateTime.UtcNow;
		string category = Classify(packet);
		int maxPackets = GetMaxPackets(config, category);
		bool invalidPacketId = packet.Id <= 0 || packet.Id >= 255;
		bool customPacketTooLarge = IsCustomPacketTooLarge(packet, config, out int customPacketBytes);
		string violation = null;

		lock (gate)
		{
			if (!clients.TryGetValue(client.Id, out ClientPacketBudget budget))
			{
				budget = new ClientPacketBudget(client.Id);
				clients[client.Id] = budget;
			}

			budget.PlayerName = client.PlayerName;
			budget.AdvanceWindow(now, Math.Max(1, config.WindowSeconds));
			budget.RegisterPacket(category);
			totalBytes += byteLength;

			if (config.Enabled && invalidPacketId)
			{
				violation = "invalid packet id";
			}
			else if (config.Enabled && customPacketTooLarge)
			{
				violation = $"custom packet size {customPacketBytes} > {config.CustomPacketMaxBytes}";
			}
			else if (config.Enabled && budget.CurrentCategoryCount(category) > maxPackets)
			{
				violation = $"{category} packet rate {budget.CurrentCategoryCount(category)}/{Math.Max(1, config.WindowSeconds)}s > {maxPackets}";
			}

			if (violation == null)
			{
				totalAccepted++;
				return false;
			}

			totalViolations++;
			budget.RegisterViolation();
			bool shouldKick = ShouldKick(config, invalidPacketId, customPacketTooLarge, budget.WindowDropped);
			string action = shouldKick ? "kick" : (config.DropViolations || invalidPacketId ? "drop" : "monitor");

			if (config.LogViolations && budget.ShouldLogViolation(now))
			{
				StratumRuntime.LogAudit($"packet-limit action={action} client={client.Id} player={budget.PlayerName} packet={packet.Id} category={category} reason={violation}", true);
			}

			if (shouldKick)
			{
				disconnectReason = string.IsNullOrWhiteSpace(config.KickMessage) ? "Disconnected by Stratum packet protection" : config.KickMessage;
				totalDropped++;
				return true;
			}

			if (!config.DropViolations && !invalidPacketId)
			{
				totalAccepted++;
				return false;
			}

			totalDropped++;
			return true;
		}
	}

	public string BuildReport()
	{
		StratumPacketLimitsConfig config = StratumRuntime.Config.PacketLimits;
		StringBuilder output = new StringBuilder();

		lock (gate)
		{
			output.AppendLine($"Packet monitoring: {(StratumRuntime.Config.Hardening.PacketMonitoring ? "on" : "off")}");
			output.AppendLine($"Packet limits: enabled={(config.Enabled ? "on" : "off")}, drop={(config.DropViolations ? "on" : "off")}, kick={(config.KickViolations ? "on" : "off")}, kickAfter={config.KickAfterViolations}, window={Math.Max(1, config.WindowSeconds)}s");
			output.AppendLine($"Totals: accepted={totalAccepted}, dropped={totalDropped}, violations={totalViolations}, bytes={totalBytes}");
			output.AppendLine($"Limits: default={config.DefaultMaxPackets}, movement={config.MovementMaxPackets}, inventory={config.InventoryMaxPackets}, block={config.BlockInteractionMaxPackets}, entity={config.EntityInteractionMaxPackets}, hand={config.HandInteractionMaxPackets}, custom={config.CustomPacketMaxPackets}, customBytes={config.CustomPacketMaxBytes}");

			ClientPacketBudget[] activeClients = clients.Values
				.Where(client => client.WindowPackets > 0 || client.TotalDropped > 0)
				.OrderByDescending(client => client.WindowDropped)
				.ThenByDescending(client => client.WindowPackets)
				.Take(8)
				.ToArray();

			if (activeClients.Length == 0)
			{
				output.Append("Clients: no packet activity recorded yet");
				return output.ToString();
			}

			output.AppendLine("Clients:");
			foreach (ClientPacketBudget activeClient in activeClients)
			{
				output.Append("- ").Append(activeClient.PlayerName).Append(" (#").Append(activeClient.ClientId).Append("): window=").Append(activeClient.WindowPackets)
					.Append(", dropped=").Append(activeClient.WindowDropped).Append(", totalDropped=").Append(activeClient.TotalDropped)
					.Append(", top=").Append(activeClient.FormatTopCategories()).AppendLine();
			}
		}

		return output.ToString().TrimEnd();
	}

	public void ForgetClient(int clientId)
	{
		lock (gate)
		{
			clients.Remove(clientId);
		}
	}

	private static string Classify(Packet_Client packet)
	{
		switch (packet.Id)
		{
			case Packet_ClientIdEnum.MoveKeyChange:
			case Packet_ClientIdEnum.RequestPositionTCP:
				return "movement";
			case Packet_ClientIdEnum.ActivateInventorySlot:
			case Packet_ClientIdEnum.MoveItemstack:
			case Packet_ClientIdEnum.FlipItemstacks:
			case Packet_ClientIdEnum.CreateItemstack:
			case Packet_ClientIdEnum.SelectedHotbarSlot:
			case Packet_ClientIdEnum.InvOpenClose:
			case Packet_ClientIdEnum.SetToolMode:
				return "inventory";
			case Packet_ClientIdEnum.BlockPlaceOrBreak:
			case Packet_ClientIdEnum.BlockEntityPacket:
			case Packet_ClientIdEnum.BlockDamage:
				return "block";
			case Packet_ClientIdEnum.EntityInteraction:
			case Packet_ClientIdEnum.EntityPacket:
				return "entity";
			case Packet_ClientIdEnum.HandInteraction:
				return "hand";
			case Packet_ClientIdEnum.CustomPacket:
				return "custom";
			default:
				return "default";
		}
	}

	private static int GetMaxPackets(StratumPacketLimitsConfig config, string category)
	{
		switch (category)
		{
			case "movement":
				return config.MovementMaxPackets;
			case "inventory":
				return config.InventoryMaxPackets;
			case "block":
				return config.BlockInteractionMaxPackets;
			case "entity":
				return config.EntityInteractionMaxPackets;
			case "hand":
				return config.HandInteractionMaxPackets;
			case "custom":
				return config.CustomPacketMaxPackets;
			default:
				return config.DefaultMaxPackets;
		}
	}

	private static bool IsCustomPacketTooLarge(Packet_Client packet, StratumPacketLimitsConfig config, out int payloadBytes)
	{
		payloadBytes = packet.CustomPacket?.Data?.Length ?? 0;
		return packet.Id == Packet_ClientIdEnum.CustomPacket && payloadBytes > config.CustomPacketMaxBytes;
	}

	private static bool ShouldKick(StratumPacketLimitsConfig config, bool invalidPacketId, bool customPacketTooLarge, int windowViolations)
	{
		if (!config.KickViolations)
		{
			return false;
		}

		if (invalidPacketId && config.KickInvalidPackets)
		{
			return true;
		}

		if (customPacketTooLarge && config.KickOversizedCustomPackets)
		{
			return true;
		}

		return config.KickAfterViolations > 0 && windowViolations >= config.KickAfterViolations;
	}

	private sealed class ClientPacketBudget
	{
		private readonly Dictionary<string, int> currentCategories = new Dictionary<string, int>();
		private DateTime windowStartedUtc = DateTime.UtcNow;
		private DateTime lastViolationLogUtc = DateTime.MinValue;

		public ClientPacketBudget(int clientId)
		{
			ClientId = clientId;
		}

		public int ClientId { get; }

		public string PlayerName { get; set; } = "Unknown";

		public int WindowPackets { get; private set; }

		public int WindowDropped { get; private set; }

		public int TotalDropped { get; private set; }

		public void AdvanceWindow(DateTime now, int windowSeconds)
		{
			if ((now - windowStartedUtc).TotalSeconds < windowSeconds)
			{
				return;
			}

			windowStartedUtc = now;
			WindowPackets = 0;
			WindowDropped = 0;
			currentCategories.Clear();
		}

		public void RegisterPacket(string category)
		{
			WindowPackets++;
			currentCategories[category] = CurrentCategoryCount(category) + 1;
		}

		public int CurrentCategoryCount(string category)
		{
			return currentCategories.TryGetValue(category, out int count) ? count : 0;
		}

		public void RegisterViolation()
		{
			WindowDropped++;
			TotalDropped++;
		}

		public bool ShouldLogViolation(DateTime now)
		{
			if ((now - lastViolationLogUtc).TotalSeconds < 10)
			{
				return false;
			}

			lastViolationLogUtc = now;
			return true;
		}

		public string FormatTopCategories()
		{
			if (currentCategories.Count == 0)
			{
				return "none";
			}

			return string.Join(", ", currentCategories.OrderByDescending(val => val.Value).Take(3).Select(val => $"{val.Key}:{val.Value}"));
		}
	}
}