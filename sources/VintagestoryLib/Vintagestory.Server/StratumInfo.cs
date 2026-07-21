using System.Reflection;

namespace Vintagestory.Server;

internal static class StratumInfo
{
	public const string Id = "stratum";
	public const string Name = "Stratum";

	// Base Vintage Story release this build is patched against. Bump together with
	// forks.json and the decompile target.
	public const string BaseGameVersion = "1.22.4";

	// Stratum revision on top of BaseGameVersion. Increment for every public release;
	// reset to 1 when BaseGameVersion changes.
	public const string StratumRevision = "1";

	// Optional prerelease label appended after the revision ("rc.1", "dev", "").
	public const string PreRelease = "indev.1";

	public const string ProtocolMode = "vanilla-compatible";

	private static readonly string _version = ResolveVersion();

	public static string Version => _version;

	public static string FullName => $"{Name} {Version}";

	private static string ResolveVersion()
	{
		// Publish overrides the version via -p:InformationalVersion=<tag>. Strip the
		// '+commit' SourceLink suffix MSBuild appends so display stays clean.
		string info = typeof(StratumInfo).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		if (!string.IsNullOrWhiteSpace(info))
		{
			int plus = info.IndexOf('+');
			string trimmed = plus >= 0 ? info.Substring(0, plus) : info;
			if (trimmed.Contains("-stratum."))
			{
				return trimmed;
			}
		}
		return string.IsNullOrEmpty(PreRelease)
			? $"{BaseGameVersion}-stratum.{StratumRevision}"
			: $"{BaseGameVersion}-stratum.{StratumRevision}-{PreRelease}";
	}
}
