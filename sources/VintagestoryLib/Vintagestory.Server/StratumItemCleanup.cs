using System;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Vintagestory.Server;

internal sealed class StratumItemCleanup
{
	private readonly ServerMain server;
	private static StratumItemCleanupConfig Cfg => StratumRuntime.Config?.Performance?.ItemCleanup;
	private static StratumThemeConfig Theme => StratumRuntime.Config?.Appearance?.Theme;

	private static int CleanupIntervalMs => (Cfg?.IntervalSeconds ?? 60) * 1000;

	public StratumItemCleanup(ServerMain server)
	{
		this.server = server;

		RegisterCallbacks();
	}

	private void RegisterCallbacks()
	{
		var pendingEntities = GetItemEntities();
		if (pendingEntities.Length == 0)
		{
			StratumRuntime.LogInfo($"No items on the ground to clean up. Waiting another {CleanupIntervalMs}ms.");
			server.RegisterCallback((_) => RegisterCallbacks(), CleanupIntervalMs);

			return;
		}

		server.RegisterCallback(DoCleanup, CleanupIntervalMs);

		if (!string.IsNullOrWhiteSpace(Cfg?.CleanupWarningMessage) && Cfg?.CleanupWarningTimeOffsets != null)
		{
			foreach (var offset in Cfg.CleanupWarningTimeOffsets)
			{
				if (offset > 0 && offset < Cfg.IntervalSeconds)
				{
					server.RegisterCallback((_) => DoCleanupWarning(offset), CleanupIntervalMs - (offset * 1000));
				}
			}
		}
	}

	private void DoCleanupWarning(int seconds)
	{
		StratumRuntime.LogInfo($"Cleaning up ground items in {seconds} seconds");
		server.SendMessageToGeneral(StratumChatFormatter.ColorizeVtml(string.Format(Cfg.CleanupWarningMessage, seconds), Theme?.WarnColor), EnumChatType.Notification);
	}

	private void DoCleanup(float dt)
	{
		if (!string.IsNullOrEmpty(Cfg.CleanupStartingMessage))
		{
			server.SendMessageToGeneral(StratumChatFormatter.ColorizeVtml(Cfg.CleanupStartingMessage, Theme?.AccentColor), EnumChatType.Notification);
		}
		StratumRuntime.LogInfo($"Cleaning up ground items...");

        try
        {
            var s = new Stopwatch();
            s.Start();

            var count = RemoveGroundEntities();

            s.Stop();

            if (!string.IsNullOrEmpty(Cfg.CleanupDoneMessage))
            {
                server.SendMessageToGeneral(StratumChatFormatter.ColorizeVtml(string.Format(Cfg.CleanupDoneMessage, count), Theme?.GoodColor), EnumChatType.Notification);
            }
            StratumRuntime.LogInfo($"Cleaned up {count} ground items in {s.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            StratumRuntime.LogError("Error during ground item cleanup: " + ex);
        }
        finally
        {
            RegisterCallbacks();
        }
    }

	private Entity[] GetItemEntities()
	{
		return GetItemEntities(server, Cfg?.MinimumEntityAgeSeconds ?? 1);
	}

	private int RemoveGroundEntities()
	{
		return RemoveGroundEntities(server, Cfg?.MinimumEntityAgeSeconds ?? 1);
	}

	/// <summary>
	/// Shared with StratumRestartScheduler, which sweeps with a minimum age of 0
	/// (restart/clearitems is an explicit admin action meant to clear everything
	/// on the ground after its own warning, not just items abandoned a while ago).
	/// </summary>
	internal static Entity[] GetItemEntities(ServerMain server, int minimumAgeSeconds)
	{
		// If the item is on the ground and has been around for at least the minimum age, remove it
		return server.LoadedEntities.Where(e => e.Value is EntityItem item
			&& (item.OnGround || item.FeetInLiquid)
			&& server.ElapsedMilliseconds - item.itemSpawnedMilliseconds > minimumAgeSeconds * 1000)
			.Select(e => e.Value)
			.ToArray();
	}

	internal static int RemoveGroundEntities(ServerMain server, int minimumAgeSeconds)
	{
		var entities = GetItemEntities(server, minimumAgeSeconds);
		var count = entities.Length;

		foreach (var p in entities)
		{
			p.Die(EnumDespawnReason.Expire);
		}

		return count;
	}
}
