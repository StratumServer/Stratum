using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.Util;

namespace Vintagestory.Server;

internal static class StratumLivesStore
{
	private const string BonusLivesKey = "stratum.player-lives.v1";

	public static int GetBonus(ServerPlayerData playerData)
	{
		if (playerData?.CustomPlayerData == null || !playerData.CustomPlayerData.TryGetValue(BonusLivesKey, out string json) || string.IsNullOrWhiteSpace(json))
		{
			return 0;
		}

		try
		{
			return Math.Max(0, JsonConvert.DeserializeObject<int>(json));
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read bonus lives for " + playerData.LastKnownPlayername + ": " + exception.Message);
			return 0;
		}
	}

	public static int SetBonus(ServerMain server, ServerPlayerData playerData, int bonusLives)
	{
		if (playerData == null)
		{
			return 0;
		}

		int normalized = Math.Max(0, bonusLives);
		playerData.CustomPlayerData ??= new Dictionary<string, string>();
		if (normalized == 0)
		{
			if (playerData.CustomPlayerData.Remove(BonusLivesKey))
			{
				server.PlayerDataManager.playerDataDirty = true;
			}
			return 0;
		}

		string json = JsonConvert.SerializeObject(normalized);
		if (!playerData.CustomPlayerData.TryGetValue(BonusLivesKey, out string existing) || existing != json)
		{
			playerData.CustomPlayerData[BonusLivesKey] = json;
			server.PlayerDataManager.playerDataDirty = true;
		}
		return normalized;
	}

	public static int AddBonus(ServerMain server, ServerPlayerData playerData, int amount)
	{
		long total = (long)GetBonus(playerData) + amount;
		if (total <= 0)
		{
			return SetBonus(server, playerData, 0);
		}

		return SetBonus(server, playerData, total > int.MaxValue ? int.MaxValue : (int)total);
	}

	public static int GetBaseLives(ServerMain server)
	{
		string configured = server.SaveGameData?.WorldConfiguration.GetString("playerlives", "-1");
		int lives = configured?.ToInt(-1) ?? -1;
		return lives < 0 ? -1 : lives;
	}

	public static ServerWorldPlayerData GetWorldData(ServerMain server, ServerPlayerData playerData)
	{
		if (server?.PlayerDataManager?.WorldDataByUID == null || playerData == null)
		{
			return null;
		}

		server.PlayerDataManager.WorldDataByUID.TryGetValue(playerData.PlayerUID, out ServerWorldPlayerData worldData);
		return worldData;
	}

	public static int GetLivesLeft(ServerMain server, ServerPlayerData playerData, ServerWorldPlayerData worldData)
	{
		int baseLives = GetBaseLives(server);
		if (baseLives < 0)
		{
			return -1;
		}

		long deaths = Math.Max(0, worldData?.Deaths ?? 0);
		long remaining = (long)baseLives + GetBonus(playerData) - deaths;
		if (remaining <= 0)
		{
			return 0;
		}

		return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
	}

	public static bool CanRespawn(ServerMain server, ConnectedClient client)
	{
		if (client?.ServerData == null || client.WorldData == null)
		{
			return false;
		}

		return GetLivesLeft(server, client.ServerData, client.WorldData) != 0;
	}

	public static bool ShouldAutoRespawn(ServerMain server, ConnectedClient client)
	{
		if (client?.ServerData == null || client.WorldData == null || GetBonus(client.ServerData) == 0)
		{
			return false;
		}

		int baseLives = GetBaseLives(server);
		return baseLives >= 0 && client.WorldData.Deaths >= baseLives && GetLivesLeft(server, client.ServerData, client.WorldData) > 0;
	}
}
