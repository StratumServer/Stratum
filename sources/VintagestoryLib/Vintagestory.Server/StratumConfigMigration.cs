using System;
using Newtonsoft.Json.Linq;

namespace Vintagestory.Server;

internal static class StratumConfigMigration
{
	public const int CurrentVersion = 3;

	public static int Upgrade(JObject main, JObject performance, bool mainExisted)
	{
		int loadedVersion = GetLoadedVersion(main, performance, mainExisted);
		if (loadedVersion >= CurrentVersion)
		{
			return loadedVersion;
		}

		if (loadedVersion < 2)
		{
			UpgradeToVersion2(main, performance);
		}
		if (loadedVersion < 3)
		{
			UpgradeToVersion3(main, performance);
		}

		main[nameof(StratumConfig.ConfigVersion)] = CurrentVersion;
		return loadedVersion;
	}

	private static int GetLoadedVersion(JObject main, JObject performance, bool mainExisted)
	{
		if (mainExisted)
		{
			return main.Value<int?>(nameof(StratumConfig.ConfigVersion)) ?? 1;
		}

		return performance != null ? 2 : CurrentVersion;
	}

	private static void UpgradeToVersion2(JObject main, JObject performance)
	{
		JObject performanceRoot = performance ?? GetObject(main, nameof(StratumConfig.Performance));
		JObject physics = GetObject(performanceRoot, nameof(StratumPerformanceConfig.Physics));
		if (physics?.Value<int?>(nameof(StratumPhysicsConfig.ParallelThreshold)) == 256)
		{
			physics[nameof(StratumPhysicsConfig.ParallelThreshold)] = 32;
		}
	}

	private static void UpgradeToVersion3(JObject main, JObject performance)
	{
		JObject chat = GetOrCreateObject(main, nameof(StratumConfig.Chat));
		JObject appearance = GetOrCreateObject(main, nameof(StratumConfig.Appearance));
		Move(main, "Theme", appearance, nameof(StratumAppearanceConfig.Theme));
		Move(main, "Nametags", appearance, nameof(StratumAppearanceConfig.Nametags));
		JObject nametags = GetObject(appearance, nameof(StratumAppearanceConfig.Nametags));
		Move(nametags, "ApplyChatPrefix", nametags, nameof(StratumNametagsConfig.ApplyRolePrefix));

		JObject rolePrefixes = GetOrCreateObject(appearance, nameof(StratumAppearanceConfig.RolePrefixes));
		Move(chat, "RolePrefixesEnabled", rolePrefixes, nameof(StratumRolePrefixesConfig.Enabled));
		Move(chat, "PrefixFormat", rolePrefixes, nameof(StratumRolePrefixesConfig.Format));
		Move(chat, "RolePrefixes", rolePrefixes, nameof(StratumRolePrefixesConfig.Roles));

		JObject connectionMessages = GetOrCreateObject(chat, nameof(StratumChatConfig.ConnectionMessages));
		Move(chat, "ShowJoinMessages", connectionMessages, nameof(StratumConnectionMessagesConfig.ShowJoins));
		Move(chat, "ShowLeaveMessages", connectionMessages, nameof(StratumConnectionMessagesConfig.ShowLeaves));
		Move(chat, "ShowDisconnectMessages", connectionMessages, nameof(StratumConnectionMessagesConfig.ShowDisconnects));

		JObject serverInfo = GetOrCreateObject(main, nameof(StratumConfig.ServerInfo));
		Move(chat, "RulesText", serverInfo, nameof(StratumServerInfoConfig.Rules));
		Move(chat, nameof(StratumServerInfoConfig.DiscordUrl), serverInfo, nameof(StratumServerInfoConfig.DiscordUrl));
		Move(chat, nameof(StratumServerInfoConfig.WebsiteUrl), serverInfo, nameof(StratumServerInfoConfig.WebsiteUrl));
		Move(chat, "MotdText", serverInfo, nameof(StratumServerInfoConfig.Motd));

		JObject rateLimit = GetOrCreateObject(chat, nameof(StratumChatConfig.RateLimit));
		JObject embeddedPerformance = GetObject(main, nameof(StratumConfig.Performance));
		JObject legacyPerformanceChat = TakeObject(performance, "Chat") ?? TakeObject(embeddedPerformance, "Chat");
		if (legacyPerformanceChat != null)
		{
			Move(legacyPerformanceChat, "Enabled", rateLimit, nameof(StratumChatRateLimitConfig.Enabled));
			Move(legacyPerformanceChat, "MinIntervalMs", rateLimit, nameof(StratumChatRateLimitConfig.MinimumIntervalMs));
			Move(legacyPerformanceChat, "DropDuplicates", rateLimit, nameof(StratumChatRateLimitConfig.DropDuplicates));
			Move(legacyPerformanceChat, "DuplicateWindowMs", rateLimit, nameof(StratumChatRateLimitConfig.DuplicateWindowMs));
			Move(legacyPerformanceChat, "ExemptCommands", rateLimit, nameof(StratumChatRateLimitConfig.ExemptCommands));
		}
		RemoveLegacyRateLimit(chat);
	}

	private static void RemoveLegacyRateLimit(JObject chat)
	{
		chat.Property("MinIntervalMs", StringComparison.OrdinalIgnoreCase)?.Remove();
		chat.Property("DropDuplicates", StringComparison.OrdinalIgnoreCase)?.Remove();
		chat.Property("DuplicateWindowMs", StringComparison.OrdinalIgnoreCase)?.Remove();
		chat.Property("ExemptCommands", StringComparison.OrdinalIgnoreCase)?.Remove();
	}

	private static JObject GetOrCreateObject(JObject owner, string name)
	{
		JObject value = GetObject(owner, name);
		if (value != null)
		{
			return value;
		}

		value = new JObject();
		owner[name] = value;
		return value;
	}

	private static JObject GetObject(JObject owner, string name)
	{
		return owner?.GetValue(name, StringComparison.OrdinalIgnoreCase) as JObject;
	}

	private static JObject TakeObject(JObject owner, string name)
	{
		JProperty property = owner?.Property(name, StringComparison.OrdinalIgnoreCase);
		if (property?.Value is not JObject value)
		{
			return null;
		}

		property.Remove();
		return value;
	}

	private static void Move(JObject source, string sourceName, JObject destination, string destinationName)
	{
		JProperty property = source?.Property(sourceName, StringComparison.OrdinalIgnoreCase);
		if (property == null)
		{
			return;
		}

		if (destination.Property(destinationName, StringComparison.OrdinalIgnoreCase) == null)
		{
			destination[destinationName] = property.Value.DeepClone();
		}
		property.Remove();
	}
}
