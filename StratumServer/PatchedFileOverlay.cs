using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace StratumServer;

// Writes Stratum's patched files over the official server install after VanillaBootstrap has downloaded and extracted the base game. 
// This should be fully safe on Windows and Linux 
internal static class PatchedFileOverlay
{
	private const string RootPrefix = "Stratum.PatchedRoot.";
	private const string ModsPrefix = "Stratum.PatchedMods.";
	private const string DataPrefix = "Stratum.PatchedData.";

	internal static int Apply(string installDir, string overlayStamp)
	{
		Assembly self = typeof(PatchedFileOverlay).Assembly;
		string[] names = GetOverlayResourceNames(self);
		if (names.Length == 0)
		{
			return 0;
		}

		string markerPath = Path.Combine(installDir, ".stratum-patched-files");
		string manifestPath = Path.Combine(installDir, ".stratum-patched-files-manifest");
		string expectedManifest = BuildManifest(self, overlayStamp, names);
		if (File.Exists(markerPath)
			&& File.ReadAllText(markerPath).Trim() == overlayStamp
			&& File.Exists(manifestPath)
			&& string.Equals(File.ReadAllText(manifestPath), expectedManifest, StringComparison.Ordinal))
		{
			return 0;
		}

		int written = 0;
		foreach (string name in names)
		{
			(string targetDir, string fileName, bool overwrite) = ResolveTarget(installDir, name);
			Directory.CreateDirectory(targetDir);
			string destPath = Path.Combine(targetDir, fileName);
			if (!overwrite && File.Exists(destPath))
			{
				continue;
			}

			using Stream resource = self.GetManifestResourceStream(name)
				?? throw new InvalidOperationException("Embedded patched file missing: " + name);
			using FileStream dest = File.Create(destPath);
			resource.CopyTo(dest);
			written++;
		}

		File.WriteAllText(markerPath, overlayStamp);
		File.WriteAllText(manifestPath, expectedManifest);
		return written;
	}

	private static string[] GetOverlayResourceNames(Assembly assembly)
	{
		return assembly.GetManifestResourceNames()
			.Where(name =>
				name.StartsWith(RootPrefix, StringComparison.Ordinal) ||
				name.StartsWith(ModsPrefix, StringComparison.Ordinal) ||
				name.StartsWith(DataPrefix, StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();
	}

	private static (string TargetDir, string FileName, bool Overwrite) ResolveTarget(string installDir, string resourceName)
	{
		if (resourceName.StartsWith(ModsPrefix, StringComparison.Ordinal))
		{
			return (Path.Combine(installDir, "Mods"), resourceName.Substring(ModsPrefix.Length), true);
		}

		if (resourceName.StartsWith(DataPrefix, StringComparison.Ordinal))
		{
			return (Path.Combine(installDir, "Data"), resourceName.Substring(DataPrefix.Length), false);
		}

		if (resourceName.StartsWith(RootPrefix, StringComparison.Ordinal))
		{
			return (installDir, resourceName.Substring(RootPrefix.Length), true);
		}

		throw new InvalidOperationException("Unknown patched file resource: " + resourceName);
	}

	private static string BuildManifest(Assembly assembly, string overlayStamp, string[] names)
	{
		StringBuilder manifest = new StringBuilder();
		manifest.Append("stamp=").Append(overlayStamp).Append('\n');
		foreach (string name in names)
		{
			using Stream resource = assembly.GetManifestResourceStream(name)
				?? throw new InvalidOperationException("Embedded patched file missing: " + name);
			manifest.Append(name).Append('|').Append(resource.Length).Append('|').Append(HashResource(resource)).Append('\n');
		}
		return manifest.ToString();
	}

	private static string HashResource(Stream stream)
	{
		using SHA256 sha = SHA256.Create();
		byte[] hash = sha.ComputeHash(stream);
		return Convert.ToHexString(hash);
	}
}
