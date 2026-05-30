namespace Vintagestory.Server;

internal static class StratumInfo
{
	public const string Id = "stratum";
	public const string Name = "Stratum";
	public const string Version = "0.1.0-dev";
	public const string BaseGameVersion = "1.22.3";
	public const string ProtocolMode = "vanilla-compatible";

	public static string FullName => $"{Name} {Version}";
}
