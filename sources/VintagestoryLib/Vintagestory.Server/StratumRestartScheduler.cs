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

	private static StratumRestartConfig Cfg => StratumRuntime.Config?.Performance?.Restart;
	private static StratumThemeConfig Theme => StratumRuntime.Config?.Appearance?.Theme;

	public bool IsRestartScheduled => restartInProgress;

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

		if (clearLeadSeconds > 0 && clearLeadSeconds < totalSeconds)
		{
			int clearDelayMs = (totalSeconds - clearLeadSeconds) * 1000;
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

	public int ClearGroundItems()
	{
		Entity[] entities = server.LoadedEntities
			.Where(e => e.Value is EntityItem item && (item.OnGround || item.FeetInLiquid))
			.Select(e => e.Value)
			.ToArray();

		foreach (Entity entity in entities)
		{
			entity.Die(EnumDespawnReason.Expire);
		}

		return entities.Length;
	}

	private void BroadcastCountdown(int secondsRemaining)
	{
		string message = string.Format(Cfg?.CountdownMessage ?? "Server restarting in {0}.", FormatTime(secondsRemaining));
		server.SendMessageToGeneral(ColorizeVtml(message, Theme?.WarnColor), EnumChatType.Notification);
		StratumRuntime.LogInfo($"Restart countdown: {secondsRemaining}s remaining");
	}

	private void BroadcastClearWarning(int secondsUntilClear)
	{
		string message = string.Format(Cfg?.ClearItemsWarningMessage ?? "PICK UP ALL GROUND ITEMS! They will be cleared in {0} seconds.", secondsUntilClear);
		server.SendMessageToGeneral(ColorizeVtml(message, Theme?.WarnColor), EnumChatType.Notification);
	}

	private void ExecuteStop()
	{
		restartInProgress = false;
		pendingCallbackIds.Clear();

		if (Cfg?.ClearGroundItemsBeforeStop == true)
		{
			int count = ClearGroundItems();
			StratumRuntime.LogInfo($"Pre-restart cleanup removed {count} ground items");
		}

		string nowMessage = Cfg?.RestartingNowMessage ?? "Server restarting now.";
		server.SendMessageToGeneral(ColorizeVtml(nowMessage, Theme?.WarnColor), EnumChatType.Notification);

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

	private static string ColorizeVtml(string message, string color)
	{
		if (string.IsNullOrWhiteSpace(color))
		{
			return message;
		}
		return $"<font color='{color}'>{message}</font>";
	}
}
