using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Dedicated fast-tick system for chunk pregeneration only
/// Runs at 50 ms 
/// All other Stratum subsystems remain on the 1000 ms ServerSystemStratum
/// </summary>
internal class ServerSystemStratumPregen : ServerSystem
{
	public ServerSystemStratumPregen(ServerMain server)
		: base(server)
	{
	}

	public override int GetUpdateInterval()
	{
		return 50; // 20 ticks/sec 
	}

	public override void OnServerTick(float dt)
	{
		if (server.RunPhase == EnumServerRunPhase.RunGame)
		{
			StratumRuntime.Pregen.Tick(server);
		}
	}
}