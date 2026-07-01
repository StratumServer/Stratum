using System;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Vintagestory.Server;

/// <summary>
/// Periodic world backup scheduler. Triggers the vanilla SQLite backup API on a configurable
/// interval and prunes old scheduled backups beyond the configured retention count.
/// Runs the actual I/O on the thread pool to avoid blocking the server tick.
/// </summary>
internal sealed class StratumBackupScheduler
{
	private readonly ServerMain server;
	private long lastBackupMs;
	private const string ScheduledPrefix = "stratum-backup-";

	public StratumBackupScheduler(ServerMain server)
	{
		this.server = server;
		lastBackupMs = server.ElapsedMilliseconds;
		server.RegisterGameTickListener(OnTick, 30_000); // check every 30s
	}

	private StratumBackupConfig Cfg => StratumRuntime.Config?.Backup;

	private void OnTick(float dt)
	{
		StratumBackupConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return;

		long elapsedMs = server.ElapsedMilliseconds;
		long intervalMs = (long)cfg.IntervalMinutes * 60_000L;

		if (elapsedMs - lastBackupMs < intervalMs) return;

		lastBackupMs = elapsedMs;
		RunBackup(cfg);
	}

	private void RunBackup(StratumBackupConfig cfg)
	{
		if (server.chunkThread.BackupInProgress)
		{
			if (cfg.LogBackups) StratumRuntime.LogInfo("scheduled backup skipped (backup already in progress)");
			return;
		}

		string worldName = Path.GetFileName(server.Config.WorldConfig.SaveFileLocation).Replace(".vcdbs", "");
		if (worldName.Length == 0) worldName = "world";

		string filename = ScheduledPrefix + worldName + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".vcdbs";

		server.chunkThread.BackupInProgress = true;

		if (cfg.LogBackups) StratumRuntime.LogInfo("scheduled backup started: " + filename);

		TyronThreadPool.QueueTask(delegate
		{
			try
			{
				server.chunkThread.gameDatabase.CreateBackup(filename);
			}
			catch (Exception ex)
			{
				server.chunkThread.BackupInProgress = false;
				StratumRuntime.LogWarning("scheduled backup failed: " + ex.Message);
				return;
			}

			server.EnqueueMainThreadTask(delegate
			{
				server.chunkThread.BackupInProgress = false;
				if (cfg.LogBackups) StratumRuntime.LogInfo("scheduled backup complete: " + filename);
				PruneOldBackups(cfg);
			});
		}, "stratum-scheduled-backup");
	}

	private void PruneOldBackups(StratumBackupConfig cfg)
	{
		try
		{
			string backupDir = GamePaths.Backups;
			if (!Directory.Exists(backupDir)) return;

			FileInfo[] scheduled = new DirectoryInfo(backupDir)
				.GetFiles(ScheduledPrefix + "*.vcdbs")
				.OrderByDescending(f => f.CreationTimeUtc)
				.ToArray();

			if (scheduled.Length <= cfg.RetainCount) return;

			for (int i = cfg.RetainCount; i < scheduled.Length; i++)
			{
				scheduled[i].Delete();
				if (cfg.LogBackups) StratumRuntime.LogInfo("pruned old backup: " + scheduled[i].Name);
			}
		}
		catch (Exception ex)
		{
			StratumRuntime.LogWarning("backup pruning failed: " + ex.Message);
		}
	}
}
