using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vintagestory.Server;

internal sealed class StratumPacketLimiter
{
	private readonly object gate = new object();
	private readonly Dictionary<int, ClientPacketBudget> clients = new Dictionary<int, ClientPacketBudget>();
	private readonly Dictionary<int, PacketStat> packetStats = new Dictionary<int, PacketStat>();
	private readonly Dictionary<CustomPacketKey, PacketStat> customPacketStats = new Dictionary<CustomPacketKey, PacketStat>();
	private long totalAccepted;
	private long totalDropped;
	private long totalBytes;
	private long totalViolations;
	private long totalMonitoredViolations;

	public (long Accepted, long Dropped, long Violations, long Bytes) Snapshot()
	{
		lock (gate) return (totalAccepted, totalDropped, totalViolations, totalBytes);
	}

	public bool ShouldDrop(ConnectedClient client, Packet_Client packet, int byteLength, NetworkAPI networkApi, out string disconnectReason)
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
		PacketInfo info = DescribePacket(packet, networkApi);
		int maxPackets = GetMaxPackets(config, info.Category);
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
			budget.RegisterPacket(info, byteLength);
			RegisterGlobalPacket(info, byteLength);
			totalBytes += byteLength;

			if (config.Enabled && invalidPacketId)
			{
				violation = "invalid packet id";
			}
			else if (config.Enabled && customPacketTooLarge)
			{
				violation = $"custom packet size {customPacketBytes} > {config.CustomPacketMaxBytes}";
			}
			else if (config.Enabled && budget.CurrentCategoryCount(info.Category) > maxPackets)
			{
				violation = $"{info.Category} packet rate {budget.CurrentCategoryCount(info.Category)}/{Math.Max(1, config.WindowSeconds)}s > {maxPackets}";
			}

			if (violation == null)
			{
				totalAccepted++;
				return false;
			}

			totalViolations++;
			bool forcedHardDrop = invalidPacketId || customPacketTooLarge;
			bool monitorOnly = !forcedHardDrop && config.MonitorOnlySensitivePackets && IsSensitiveForRateLimit(info);
			int projectedDroppedViolations = monitorOnly ? budget.WindowDropped : budget.WindowDropped + 1;
			bool shouldKick = !monitorOnly && ShouldKick(config, invalidPacketId, customPacketTooLarge, projectedDroppedViolations);
			bool shouldDrop = forcedHardDrop || (!monitorOnly && config.DropViolations);
			string action = shouldKick ? "kick" : (shouldDrop ? "drop" : "monitor");
			budget.RegisterViolation(shouldDrop || shouldKick);

			if (!shouldDrop && !shouldKick)
			{
				totalMonitoredViolations++;
			}

			if (config.LogViolations && budget.ShouldLogViolation(now))
			{
				StratumRuntime.LogAudit($"packet-limit action={action} client={client.Id} player={budget.PlayerName} packet={info.PacketLabel} category={info.Category} detail={info.DetailLabel} bytes={Math.Max(0, byteLength)} reason={violation}", true);
			}

			if (shouldKick)
			{
				disconnectReason = string.IsNullOrWhiteSpace(config.KickMessage) ? "Disconnected by Stratum packet protection" : config.KickMessage;
				totalDropped++;
				return true;
			}

			if (!shouldDrop)
			{
				totalAccepted++;
				return false;
			}

			totalDropped++;
			return true;
		}
	}

	public void ObserveUdpPacket(ConnectedClient client, Packet_UdpPacket packet, int byteLength, NetworkAPI networkApi)
	{
		if (client == null || packet == null)
		{
			return;
		}

		StratumConfig stratumConfig = StratumRuntime.Config;
		stratumConfig.EnsurePopulated();
		if (!stratumConfig.Hardening.PacketMonitoring)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		PacketInfo info = DescribeUdpPacket(packet, networkApi);

		lock (gate)
		{
			if (!clients.TryGetValue(client.Id, out ClientPacketBudget budget))
			{
				budget = new ClientPacketBudget(client.Id);
				clients[client.Id] = budget;
			}

			budget.PlayerName = client.PlayerName;
			budget.AdvanceWindow(now, Math.Max(1, stratumConfig.PacketLimits.WindowSeconds));
			budget.RegisterPacket(info, byteLength);
			RegisterGlobalPacket(info, byteLength);
			totalBytes += byteLength;
			totalAccepted++;
		}
	}

	public string BuildReport()
	{
		return BuildReport(null, null);
	}

	public string BuildReport(string mode, string detail)
	{
		StratumPacketLimitsConfig config = StratumRuntime.Config.PacketLimits;
		StringBuilder output = new StringBuilder();

		lock (gate)
		{
			output.AppendLine($"Packet monitoring: {(StratumRuntime.Config.Hardening.PacketMonitoring ? "on" : "off")}");
			output.AppendLine($"Packet limits: enabled={(config.Enabled ? "on" : "off")}, drop={(config.DropViolations ? "on" : "off")}, kick={(config.KickViolations ? "on" : "off")}, sensitiveRateMode={(config.MonitorOnlySensitivePackets ? "monitor" : "drop")}, kickAfter={config.KickAfterViolations}, window={Math.Max(1, config.WindowSeconds)}s");
			output.AppendLine($"Totals: accepted={totalAccepted}, dropped={totalDropped}, violations={totalViolations}, monitored={totalMonitoredViolations}, bytes={totalBytes}");
			output.AppendLine($"Limits: default={config.DefaultMaxPackets}, movement={config.MovementMaxPackets}, inventory={config.InventoryMaxPackets}, block={config.BlockInteractionMaxPackets}, entity={config.EntityInteractionMaxPackets}, hand={config.HandInteractionMaxPackets}, custom={config.CustomPacketMaxPackets}, customBytes={config.CustomPacketMaxBytes}");

			if (string.Equals(mode, "top", StringComparison.OrdinalIgnoreCase))
			{
				AppendTopPackets(output, packetStats.Values, "Top packets");
				return output.ToString().TrimEnd();
			}

			if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
			{
				AppendTopPackets(output, customPacketStats.Values, "Top custom packets");
				return output.ToString().TrimEnd();
			}

			if (string.Equals(mode, "player", StringComparison.OrdinalIgnoreCase))
			{
				AppendPlayerReport(output, detail);
				return output.ToString().TrimEnd();
			}

			if (string.Equals(mode, "watch", StringComparison.OrdinalIgnoreCase))
			{
				AppendWatchReport(output, detail, config);
				return output.ToString().TrimEnd();
			}

			ClientPacketBudget[] activeClients = clients.Values
				.Where(client => client.WindowPackets > 0 || client.TotalDropped > 0 || client.TotalMonitored > 0)
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
					.Append(", dropped=").Append(activeClient.WindowDropped).Append(", monitored=").Append(activeClient.WindowMonitored)
					.Append(", totalDropped=").Append(activeClient.TotalDropped)
					.Append(", top=").Append(activeClient.FormatTopCategories()).AppendLine();
			}

			AppendTopPackets(output, packetStats.Values, "Top packets", 5);
			if (customPacketStats.Count > 0)
			{
				AppendTopPackets(output, customPacketStats.Values, "Top custom packets", 5);
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

	private void RegisterGlobalPacket(PacketInfo info, int byteLength)
	{
		if (!packetStats.TryGetValue(info.PacketId, out PacketStat packetStat))
		{
			packetStat = new PacketStat(info.PacketLabel, info.Category);
			packetStats[info.PacketId] = packetStat;
		}

		packetStat.Register(byteLength);

		if (info.CustomKey.HasValue)
		{
			CustomPacketKey key = info.CustomKey.Value;
			if (!customPacketStats.TryGetValue(key, out PacketStat customStat))
			{
				customStat = new PacketStat(info.DetailLabel, info.Category);
				customPacketStats[key] = customStat;
			}

			customStat.Register(byteLength);
		}
	}

	private static void AppendTopPackets(StringBuilder output, IEnumerable<PacketStat> stats, string title, int take = 12)
	{
		PacketStat[] top = stats
			.Where(stat => stat.Count > 0)
			.OrderByDescending(stat => stat.Count)
			.ThenByDescending(stat => stat.Bytes)
			.Take(take)
			.ToArray();

		output.AppendLine(title + ":");
		if (top.Length == 0)
		{
			output.AppendLine("- none");
			return;
		}

		foreach (PacketStat stat in top)
		{
			output.Append("- ").Append(stat.Label)
				.Append(": count=").Append(stat.Count)
				.Append(", bytes=").Append(stat.Bytes)
				.Append(", category=").Append(stat.Category)
				.AppendLine();
		}
	}

	private void AppendPlayerReport(StringBuilder output, string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
		{
			output.Append("Usage: /stratum packets player &lt;name|clientId&gt;");
			return;
		}

		ClientPacketBudget budget = clients.Values.FirstOrDefault(client =>
			string.Equals(client.PlayerName, detail, StringComparison.OrdinalIgnoreCase)
			|| client.ClientId.ToString(System.Globalization.CultureInfo.InvariantCulture) == detail);

		if (budget == null)
		{
			output.Append("No packet activity recorded for ").Append(detail);
			return;
		}

		output.AppendLine("Player packets: " + budget.PlayerName + " (#" + budget.ClientId + ")");
		output.AppendLine("Window: packets=" + budget.WindowPackets + ", dropped=" + budget.WindowDropped + ", monitored=" + budget.WindowMonitored);
		output.AppendLine("Totals: dropped=" + budget.TotalDropped + ", monitored=" + budget.TotalMonitored + ", bytes=" + budget.TotalBytes);
		AppendTopPackets(output, budget.PacketStats.Values, "Top packets", 10);
		AppendTopPackets(output, budget.CustomPacketStats.Values, "Top custom packets", 10);
		var backPressure = StratumRuntime.PacketBackPressure.SnapshotClient(budget.ClientId);
		output.AppendLine("Back-pressure: queued=" + backPressure.Queued + ", servedThisTick=" + backPressure.ServedThisTick);
	}

	private void AppendWatchReport(StringBuilder output, string detail, StratumPacketLimitsConfig config)
	{
		int seconds = Math.Max(1, config.WindowSeconds);
		if (!string.IsNullOrWhiteSpace(detail) && int.TryParse(detail, out int parsedSeconds))
		{
			seconds = Math.Max(1, parsedSeconds);
		}

		output.AppendLine("Packet watch: current rolling stats, requestedWindow=" + seconds + "s, activeWindow=" + Math.Max(1, config.WindowSeconds) + "s");
		output.AppendLine("This command is non-blocking. Run it again after the requested window to compare counts.");
		AppendTopPackets(output, packetStats.Values, "Top packets", 12);
		if (customPacketStats.Count > 0)
		{
			AppendTopPackets(output, customPacketStats.Values, "Top custom packets", 12);
		}
	}

	private static PacketInfo DescribePacket(Packet_Client packet, NetworkAPI networkApi)
	{
		string category = Classify(packet);
		string packetLabel = GetPacketName(packet.Id);
		if (packet.Id != Packet_ClientIdEnum.CustomPacket || packet.CustomPacket == null)
		{
			return new PacketInfo(packet.Id, category, packetLabel, packetLabel, null);
		}

		Packet_CustomPacket custom = packet.CustomPacket;
		string channelName = null;
		if (networkApi != null && networkApi.StratumTryGetChannelName(custom.ChannelId, udp: false, out string resolvedName))
		{
			channelName = resolvedName;
		}

		string customLabel = string.IsNullOrWhiteSpace(channelName)
			? "custom channel=" + custom.ChannelId + " msg=" + custom.MessageId
			: "custom " + channelName + " channel=" + custom.ChannelId + " msg=" + custom.MessageId;

		return new PacketInfo(packet.Id, category, packetLabel, customLabel, new CustomPacketKey(udp: false, custom.ChannelId, custom.MessageId, channelName));
	}

	private static PacketInfo DescribeUdpPacket(Packet_UdpPacket packet, NetworkAPI networkApi)
	{
		string category = ClassifyUdp(packet);
		string packetLabel = GetUdpPacketName(packet.Id);
		if (packet.Id != Packet_UdpPacketIdEnum.CustomPacket || packet.ChannelPacket == null)
		{
			return new PacketInfo(1000 + packet.Id, category, packetLabel, packetLabel, null);
		}

		Packet_CustomPacket custom = packet.ChannelPacket;
		string channelName = null;
		if (networkApi != null && networkApi.StratumTryGetChannelName(custom.ChannelId, udp: true, out string resolvedName))
		{
			channelName = resolvedName;
		}

		string customLabel = string.IsNullOrWhiteSpace(channelName)
			? "udp custom channel=" + custom.ChannelId + " msg=" + custom.MessageId
			: "udp custom " + channelName + " channel=" + custom.ChannelId + " msg=" + custom.MessageId;

		return new PacketInfo(1000 + packet.Id, category, packetLabel, customLabel, new CustomPacketKey(udp: true, custom.ChannelId, custom.MessageId, channelName));
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

	private static bool IsSensitiveForRateLimit(PacketInfo info)
	{
		return info.Category == "movement"
			|| info.Category == "block"
			|| info.Category == "entity"
			|| info.Category == "hand"
			|| info.Category == "custom";
	}

	private static string ClassifyUdp(Packet_UdpPacket packet)
	{
		switch (packet.Id)
		{
			case Packet_UdpPacketIdEnum.Position:
			case Packet_UdpPacketIdEnum.MountPosition:
				return "movement";
			case Packet_UdpPacketIdEnum.CustomPacket:
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

	private static string GetPacketName(int packetId)
	{
		switch (packetId)
		{
			case Packet_ClientIdEnum.PlayerIdentification:
				return "PlayerIdentification(1)";
			case Packet_ClientIdEnum.PingReply:
				return "PingReply(2)";
			case Packet_ClientIdEnum.BlockPlaceOrBreak:
				return "BlockPlaceOrBreak(3)";
			case Packet_ClientIdEnum.ChatLine:
				return "ChatLine(4)";
			case Packet_ClientIdEnum.ActivateInventorySlot:
				return "ActivateInventorySlot(7)";
			case Packet_ClientIdEnum.MoveItemstack:
				return "MoveItemstack(8)";
			case Packet_ClientIdEnum.FlipItemstacks:
				return "FlipItemstacks(9)";
			case Packet_ClientIdEnum.CreateItemstack:
				return "CreateItemstack(10)";
			case Packet_ClientIdEnum.RequestJoin:
				return "RequestJoin(11)";
			case Packet_ClientIdEnum.SelectedHotbarSlot:
				return "SelectedHotbarSlot(13)";
			case Packet_ClientIdEnum.Leave:
				return "Leave(14)";
			case Packet_ClientIdEnum.EntityInteraction:
				return "EntityInteraction(17)";
			case Packet_ClientIdEnum.RequestModeChange:
				return "RequestModeChange(20)";
			case Packet_ClientIdEnum.MoveKeyChange:
				return "MoveKeyChange(21)";
			case Packet_ClientIdEnum.BlockEntityPacket:
				return "BlockEntityPacket(22)";
			case Packet_ClientIdEnum.CustomPacket:
				return "CustomPacket(23)";
			case Packet_ClientIdEnum.HandInteraction:
				return "HandInteraction(25)";
			case Packet_ClientIdEnum.ClientLoaded:
				return "ClientLoaded(26)";
			case Packet_ClientIdEnum.SetToolMode:
				return "SetToolMode(27)";
			case Packet_ClientIdEnum.BlockDamage:
				return "BlockDamage(28)";
			case Packet_ClientIdEnum.ClientPlaying:
				return "ClientPlaying(29)";
			case Packet_ClientIdEnum.InvOpenClose:
				return "InvOpenClose(30)";
			case Packet_ClientIdEnum.EntityPacket:
				return "EntityPacket(31)";
			case Packet_ClientIdEnum.RequestPositionTCP:
				return "RequestPositionTCP(34)";
			default:
				return "Id" + packetId;
		}
	}

	private static string GetUdpPacketName(int packetId)
	{
		switch (packetId)
		{
			case Packet_UdpPacketIdEnum.Connect:
				return "UdpConnection(1)";
			case Packet_UdpPacketIdEnum.Position:
				return "UdpPlayerPosition(2)";
			case Packet_UdpPacketIdEnum.MountPosition:
				return "UdpMountPosition(3)";
			case Packet_UdpPacketIdEnum.ClientBulkPacket:
				return "UdpEntityBulkPosition(4)";
			case Packet_UdpPacketIdEnum.ClientSinglePacket:
				return "UdpEntityPosition(5)";
			case Packet_UdpPacketIdEnum.CustomPacket:
				return "UdpCustomPacket(6)";
			case Packet_UdpPacketIdEnum.KeepAlive:
				return "UdpPing(7)";
			default:
				return "UdpId" + packetId;
		}
	}

	private readonly struct PacketInfo
	{
		public readonly int PacketId;
		public readonly string Category;
		public readonly string PacketLabel;
		public readonly string DetailLabel;
		public readonly CustomPacketKey? CustomKey;

		public PacketInfo(int packetId, string category, string packetLabel, string detailLabel, CustomPacketKey? customKey)
		{
			PacketId = packetId;
			Category = category;
			PacketLabel = packetLabel;
			DetailLabel = detailLabel;
			CustomKey = customKey;
		}
	}

	private readonly struct CustomPacketKey : IEquatable<CustomPacketKey>
	{
		private readonly bool udp;
		private readonly int channelId;
		private readonly int messageId;
		private readonly string channelName;

		public CustomPacketKey(bool udp, int channelId, int messageId, string channelName)
		{
			this.udp = udp;
			this.channelId = channelId;
			this.messageId = messageId;
			this.channelName = channelName ?? "";
		}

		public bool Equals(CustomPacketKey other)
		{
			return udp == other.udp && channelId == other.channelId && messageId == other.messageId && string.Equals(channelName, other.channelName, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is CustomPacketKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			unchecked
			{
				int hash = channelId;
				hash = (hash * 397) ^ udp.GetHashCode();
				hash = (hash * 397) ^ messageId;
				hash = (hash * 397) ^ channelName.GetHashCode();
				return hash;
			}
		}
	}

	private sealed class PacketStat
	{
		public PacketStat(string label, string category)
		{
			Label = label;
			Category = category;
		}

		public string Label { get; }

		public string Category { get; }

		public long Count { get; private set; }

		public long Bytes { get; private set; }

		public void Register(int byteLength)
		{
			Count++;
			Bytes += Math.Max(0, byteLength);
		}
	}

	private sealed class ClientPacketBudget
	{
		private readonly Dictionary<string, int> currentCategories = new Dictionary<string, int>();
		public readonly Dictionary<int, PacketStat> PacketStats = new Dictionary<int, PacketStat>();
		public readonly Dictionary<CustomPacketKey, PacketStat> CustomPacketStats = new Dictionary<CustomPacketKey, PacketStat>();
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

		public int WindowMonitored { get; private set; }

		public int TotalDropped { get; private set; }

		public int TotalMonitored { get; private set; }

		public long TotalBytes { get; private set; }

		public void AdvanceWindow(DateTime now, int windowSeconds)
		{
			if ((now - windowStartedUtc).TotalSeconds < windowSeconds)
			{
				return;
			}

			windowStartedUtc = now;
			WindowPackets = 0;
			WindowDropped = 0;
			WindowMonitored = 0;
			currentCategories.Clear();
		}

		public void RegisterPacket(PacketInfo info, int byteLength)
		{
			WindowPackets++;
			TotalBytes += Math.Max(0, byteLength);
			currentCategories[info.Category] = CurrentCategoryCount(info.Category) + 1;

			if (!PacketStats.TryGetValue(info.PacketId, out PacketStat packetStat))
			{
				packetStat = new PacketStat(info.PacketLabel, info.Category);
				PacketStats[info.PacketId] = packetStat;
			}

			packetStat.Register(byteLength);

			if (info.CustomKey.HasValue)
			{
				CustomPacketKey key = info.CustomKey.Value;
				if (!CustomPacketStats.TryGetValue(key, out PacketStat customStat))
				{
					customStat = new PacketStat(info.DetailLabel, info.Category);
					CustomPacketStats[key] = customStat;
				}

				customStat.Register(byteLength);
			}
		}

		public int CurrentCategoryCount(string category)
		{
			return currentCategories.TryGetValue(category, out int count) ? count : 0;
		}

		public void RegisterViolation(bool dropped)
		{
			if (!dropped)
			{
				WindowMonitored++;
				TotalMonitored++;
				return;
			}

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
