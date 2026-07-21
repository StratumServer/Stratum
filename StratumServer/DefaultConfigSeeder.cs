using System;
using System.IO;
using System.Reflection;

namespace StratumServer;

internal static class DefaultConfigSeeder
{
	private const string ResourceName = "Stratum.Config.stratum.default.json";

	internal static void Seed(string installDir)
	{
		string dataDir = Path.Combine(installDir, "Data");
		string configPath = Path.Combine(dataDir, "stratum.json");
		if (File.Exists(configPath))
		{
			return;
		}

		Assembly self = typeof(DefaultConfigSeeder).Assembly;
		using Stream resource = self.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException("Embedded default Stratum config missing.");
		Directory.CreateDirectory(dataDir);
		using FileStream dest = File.Create(configPath);
		resource.CopyTo(dest);
	}
}
