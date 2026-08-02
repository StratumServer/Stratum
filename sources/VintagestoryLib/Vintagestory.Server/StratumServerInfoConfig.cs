namespace Vintagestory.Server;

internal class StratumServerInfoConfig
{
	public string Rules { get; set; } = "Rules: be respectful, no griefing, no cheating, no harassment, and do not abuse exploits.";

	public string DiscordUrl { get; set; } = "";

	public string WebsiteUrl { get; set; } = "";

	public string Motd { get; set; } = "Welcome to this Stratum server.";

	public void EnsurePopulated()
	{
		Rules ??= "Rules: be respectful, no griefing, no cheating, no harassment, and do not abuse exploits.";
		DiscordUrl ??= "";
		WebsiteUrl ??= "";
		Motd ??= "Welcome to this Stratum server.";
	}
}
