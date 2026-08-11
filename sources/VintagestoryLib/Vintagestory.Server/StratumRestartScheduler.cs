using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Vintagestory.Server;

internal sealed class StratumRestartScheduler
{
	private readonly ServerMain server;
	private readonly List<long> pendingCallbackIds = new List<long>();
	private bool restartInProgress;
	private long? clearItemsCallbackId;

	private static StratumRestartConfig Cfg => StratumRuntime.Config?.Performance?.Restart;
	private static StratumThemeConfig Theme => StratumRuntime.Config?.Appearance?.Theme;

	public bool IsRestartScheduled => restartInProgress;
	public bool IsClearItemsScheduled => clearItemsCallbackId.HasValue;

	public StratumRestartScheduler(ServerMain server)
	{
		this.server = server;
	}

	public void Schedule(int totalSeconds)
	{
		Cancel();
		restartInProgress = true;

		int[] announcements = Cfg?.CountdownAnnouncementsSeconds ?? [300, 120, 60, 45, 30, 15, 5];
		int clearLeadSeconds = Cfg?.ClearGroundItemsBeforeStop == true ? (Cfg?.ClearGroundItemsLeadSeconds ?? 30) : 0;

		foreach (int secondsBefore in announcements)
		{
			if (secondsBefore >= totalSeconds)
			{
				continue;
			}

			int delayMs = (totalSeconds - secondsBefore) * 1000;
			long id = server.RegisterCallback((_) => BroadcastCountdown(secondsBefore), delayMs);
			pendingCallbackIds.Add(id);
		}

		BroadcastCountdown(totalSeconds);

		if (clearLeadSeconds > 0)
		{
			// Clamp to 0: if the configured lead time doesn't fit before totalSeconds,
			// warn immediately rather than skipping the warning and clearing silently.
			int clearDelayMs = Math.Max(0, (totalSeconds - clearLeadSeconds) * 1000);
			long id = server.RegisterCallback((_) => BroadcastClearWarning(clearLeadSeconds), clearDelayMs);
			pendingCallbackIds.Add(id);
		}

		long stopId = server.RegisterCallback((_) => ExecuteStop(), totalSeconds * 1000);
		pendingCallbackIds.Add(stopId);
	}

	public void Cancel()
	{
		if (!restartInProgress)
		{
			return;
		}

		foreach (long id in pendingCallbackIds)
		{
			server.UnregisterCallback(id);
		}
		pendingCallbackIds.Clear();
		restartInProgress = false;
	}

	/// <summary>
	/// Sweeps every ground item regardless of age (minimum age 0): unlike the
	/// periodic StratumItemCleanup pass, this only runs after its own warning
	/// (from /clearitems or the restart countdown), so nothing abandoned-vs-fresh
	/// distinction applies, everything on the ground is fair game.
	/// </summary>
	public int ClearGroundItems()
	{
		return StratumItemCleanup.RemoveGroundEntities(server, 0);
	}

	/// <summary>
	/// Warns global chat, then clears ground items after leadSeconds. Tracked so a
	/// pending /clearitems warning can be cancelled instead of always firing.
	/// </summary>
	public bool ScheduleClearItemsWarning(int leadSeconds)
	{
		if (clearItemsCallbackId.HasValue)
		{
			return false;
		}

		BroadcastClearWarning(leadSeconds);

		clearItemsCallbackId = server.RegisterCallback((_) =>
		{
			clearItemsCallbackId = null;
			int count = ClearGroundItems();
			server.SendMessageToGeneral(
				StratumChatFormatter.ColorizeVtml($"Cleaned up {count} ground items.", Theme?.AccentColor),
				EnumChatType.Notification);
		}, leadSeconds * 1000);

		return true;
	}

	public bool CancelClearItemsWarning()
	{
		if (!clearItemsCallbackId.HasValue)
		{
			return false;
		}

		server.UnregisterCallback(clearItemsCallbackId.Value);
		clearItemsCallbackId = null;
		return true;
	}

	private void BroadcastCountdown(int secondsRemaining)
	{
		string message = string.Format(Cfg?.CountdownMessage ?? "Server restarting in {0}.", FormatTime(secondsRemaining));
		server.SendMessageToGeneral(StratumChatFormatter.ColorizeVtml(message, Theme?.WarnColor), EnumChatType.Notification);
		StratumRuntime.LogInfo($"Restart countdown: {secondsRemaining}s remaining");
	}

	private void BroadcastClearWarning(int secondsUntilClear)
	{
		string message = string.Format(Cfg?.ClearItemsWarningMessage ?? "PICK UP ALL GROUND ITEMS! They will be cleared in {0} seconds.", secondsUntilClear);
		server.SendMessageToGeneral(StratumChatFormatter.ColorizeVtml(message, Theme?.WarnColor), EnumChatType.Notification);
	}

	private void ExecuteStop()
	{
		restartInProgress = false;
		pendingCallbackIds.Clear();

		if (Cfg?.ClearGroundItemsBeforeStop == true)
		{
			// Never let a cleanup failure prevent the restart itself from happening.
			try
			{
				int count = ClearGroundItems();
				StratumRuntime.LogInfo($"Pre-restart cleanup removed {count} ground items");
			}
			catch (Exception ex)
			{
				StratumRuntime.LogError("Error clearing ground items before restart: " + ex);
			}
		}

		string nowMessage = Cfg?.RestartingNowMessage ?? "Server restarting now.";
		server.SendMessageToGeneral(StratumChatFormatter.ColorizeVtml(nowMessage, Theme?.WarnColor), EnumChatType.Notification);

		int exitCode = Cfg?.ExitCode ?? 0;
		server.ExitCode = exitCode;
		server.Stop("Scheduled restart", EnumExitMode.SoftExit);
	}

	private static string FormatTime(int totalSeconds)
	{
		if (totalSeconds >= 60)
		{
			int minutes = totalSeconds / 60;
			int seconds = totalSeconds % 60;
			if (seconds == 0)
			{
				return minutes == 1 ? "1 minute" : $"{minutes} minutes";
			}
			return $"{minutes}m {seconds}s";
		}
		return totalSeconds == 1 ? "1 second" : $"{totalSeconds} seconds";
	}
}
