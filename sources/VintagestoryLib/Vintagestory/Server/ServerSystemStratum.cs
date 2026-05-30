using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal class ServerSystemStratum : ServerSystem
{
	[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
	private static extern uint StratumTimeBeginPeriod(uint period);

	private static int stratumActiveTimerPeriodMs;

	public ServerSystemStratum(ServerMain server)
		: base(server)
	{
	}

	public override int GetUpdateInterval()
	{
		return 1000;
	}

	public override void OnBeginConfiguration()
	{
		bool loaded = StratumRuntime.LoadOrCreateConfig(server, out string message);
		if (StratumRuntime.Config.Diagnostics.LogStartupSummary)
		{
			LogStartupSummary(loaded, message);
		}

		// Wire BlockEntity init-stagger from config.
		StratumBlockEntityInitConfig beInit = StratumRuntime.Config.Performance.BlockEntityInit;
		BlockEntity.StratumDefaultInitialDelaySpreadMs = (beInit != null && beInit.Enabled) ? beInit.MaxStaggerMs : 0;

		// Raise Windows multimedia timer resolution so Thread.Sleep(N) is accurate to ~1ms instead
		// of the default ~15.6ms scheduler tick. Without this, the server's tick-sleep math
		// (Config.TickTime - elapsed) rounds up to the next 15.6ms boundary, capping the
		// achievable tickrate at ~25 tps on Windows regardless of how little work each tick does.
		// Process-wide; cleaned up automatically at process exit.
		StratumTimerResolutionConfig timerCfg = StratumRuntime.Config.Performance.TimerResolution;
		if (timerCfg != null && timerCfg.Enabled && stratumActiveTimerPeriodMs == 0
			&& RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			uint period = (uint)Math.Max(1, timerCfg.PeriodMs);
			try
			{
				uint result = StratumTimeBeginPeriod(period);
				if (result == 0)
				{
					stratumActiveTimerPeriodMs = (int)period;
					StratumRuntime.LogInfo("timer resolution raised to " + period + "ms (was ~15.6ms default)");
				}
				else
				{
					StratumRuntime.LogWarning("timeBeginPeriod(" + period + ") returned " + result + " — timer resolution unchanged");
				}
			}
			catch (Exception ex)
			{
				StratumRuntime.LogWarning("could not raise timer resolution: " + ex.Message);
			}
		}

		if (StratumRuntime.Config.Performance.Timings.EnabledOnStartup)
		{
			StratumRuntime.Timings.Start();
			StratumRuntime.LogInfo("timings started from config");
		}

		if (StratumRuntime.Config.Diagnostics.RunStartupPreflight)
		{
			StratumPreflightReport report = StratumRuntime.RunPreflight();
			StratumRuntime.LogInfo("preflight " + report.Summary);

			if (!report.Passed || StratumRuntime.Config.Diagnostics.LogPreflightWarnings)
			{
				foreach (string error in report.Errors)
				{
					StratumRuntime.LogWarning("preflight: " + error);
				}

				foreach (string warning in report.Warnings)
				{
					StratumRuntime.LogWarning("preflight: " + warning);
				}
			}
		}
	}

	public override void OnBeginRunGame()
	{
		StratumPlayerPrivacy.Initialize(server);
		StratumMetricsPublisher.Start();
		StratumRuntime.LogInfo("runtime ready. Use /stratum health, /stratum status, and /stratum timings start.");
	}

	public override void OnServerTick(float dt)
	{
		if (server.RunPhase == EnumServerRunPhase.RunGame)
		{
			StratumRuntime.Pregen.Tick(server);
			StratumMetricsPublisher.Publish(server);
		}
	}

	private static void LogStartupSummary(bool loaded, string message)
	{
		StratumConfig config = StratumRuntime.Config;
		StratumRuntime.LogInfo($"{StratumInfo.FullName} for Vintage Story {StratumInfo.BaseGameVersion}");
		StratumRuntime.LogInfo($"config {(loaded ? "ready" : "using defaults")}: {message} ({StratumRuntime.ConfigPath})");
		StratumRuntime.LogInfo($"hardening: packets={OnOff(config.Hardening.PacketMonitoring)} blockBreak={OnOff(config.Hardening.BlockBreakGuards)} inventory={OnOff(config.Hardening.InventoryGuards)} entities={OnOff(config.Hardening.EntityGuards)}");
		if (config.ClientModPolicy.LogPolicyOnStartup)
		{
			StratumRuntime.LogInfo($"client mod policy: enabled={OnOff(config.ClientModPolicy.Enabled)} strictWhitelist={OnOff(config.ClientModPolicy.StrictWhitelist)} allowExtras={config.ClientModPolicy.AllowModIds.Count}");
		}
		if (config.LoadTesting.AllowUnauthenticatedClients)
		{
			StratumRuntime.LogWarning($"load testing auth bypass enabled for names starting '{config.LoadTesting.RequiredPlayerNamePrefix}' and UIDs starting '{config.LoadTesting.RequiredPlayerUidPrefix}'");
		}
		StratumRuntime.LogInfo($"performance: chunkSend={OnOff(config.Performance.ChunkSending.Enabled)} chunkGen={OnOff(config.Performance.ChunkGeneration.Enabled)} pregen={OnOff(config.Performance.Pregen.Enabled)} entityTick={OnOff(config.Performance.EntityTicking.Enabled)} autosave={OnOff(config.Performance.AutoSave.Enabled)} blockTicks={OnOff(config.Performance.BlockTicks.Enabled)} timings={(config.Performance.Timings.EnabledOnStartup ? "startup" : "manual")}");
		StratumRuntime.LogInfo($"commands: /stratum health, investigation={OnOff(config.Commands.Enabled)}, playerQoL={OnOff(config.Commands.Enabled)}");
	}

	private static string OnOff(bool enabled)
	{
		return enabled ? "on" : "off";
	}
}