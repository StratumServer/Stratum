using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace StratumServer;

// Downloads the matching official server archive on first run or version bump,
// verifies it against Anego's manifest, and lays down the base server files.
internal static class VanillaBootstrap
{
	private const string VersionManifestUrl = "https://api.vintagestory.at/stable-unstable.json";

	internal static void EnsureVanillaAssets(string baseGameVersion, bool refresh)
	{
		string installDir = AppContext.BaseDirectory;
		string markerPath = Path.Combine(installDir, ".stratum-base");
		string expectedMarker = baseGameVersion;
		bool markerExists = File.Exists(markerPath);
		string currentMarker = markerExists ? File.ReadAllText(markerPath).Trim() : string.Empty;
		bool markerMatches = markerExists && currentMarker == expectedMarker;

		if (!refresh && markerMatches)
		{
			return;
		}

		bool existingInstallWithoutMarker = !markerExists && LooksLikeExistingInstall(installDir);
		bool overwriteExisting = refresh || !markerMatches || existingInstallWithoutMarker;

		if (overwriteExisting)
		{
			CleanStaleAssets(installDir);
		}

		ArchiveInfo archive = GetArchiveForPlatform(baseGameVersion);
		string cacheDir = Path.Combine(installDir, ".vanilla-cache");
		Directory.CreateDirectory(cacheDir);
		string archivePath = Path.Combine(cacheDir, archive.FileName);

		if (File.Exists(archivePath) && !VerifyMd5(archivePath, archive.Md5))
		{
			Console.WriteLine($"Stratum: cached {archive.FileName} failed checksum; downloading a fresh copy");
			File.Delete(archivePath);
		}

		if (!File.Exists(archivePath))
		{
			Console.WriteLine($"Stratum: downloading vanilla base game from {archive.Url}");
			DownloadFile(archive.Url, archivePath);
			if (!VerifyMd5(archivePath, archive.Md5))
			{
				File.Delete(archivePath);
				throw new InvalidOperationException("Downloaded vanilla archive failed MD5 verification: " + archive.FileName);
			}
		}
		else
		{
			Console.WriteLine($"Stratum: using cached {archivePath}");
		}

		string extractDir = Path.Combine(cacheDir, "extract");
		if (Directory.Exists(extractDir))
		{
			Directory.Delete(extractDir, recursive: true);
		}
		Directory.CreateDirectory(extractDir);

		Console.WriteLine($"Stratum: unpacking {archive.FileName}");
		if (archive.IsZip)
		{
			ZipFile.ExtractToDirectory(archivePath, extractDir);
		}
		else
		{
			ExtractTarGz(archivePath, extractDir);
		}

		string sourceRoot = FindContentRoot(extractDir);
		int copied = OverlayVanillaFiles(sourceRoot, installDir, overwriteExisting);
		if (overwriteExisting)
		{
			Console.WriteLine($"Stratum: installed {copied} vanilla file(s) (existing files were refreshed)");
		}
		else
		{
			Console.WriteLine($"Stratum: installed {copied} vanilla file(s) (existing files were preserved)");
		}

		File.WriteAllText(markerPath, expectedMarker);
	}

	private static ArchiveInfo GetArchiveForPlatform(string version)
	{
		string platformKey;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			platformKey = "windowsserver";
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			platformKey = "linuxserver";
		}
		else
		{
			throw new PlatformNotSupportedException("Stratum automatic server bootstrap currently supports Windows and Linux.");
		}

		using HttpClient client = new();
		client.Timeout = TimeSpan.FromSeconds(30);
		string json = client.GetStringAsync(VersionManifestUrl).GetAwaiter().GetResult();
		using JsonDocument document = JsonDocument.Parse(json);
		if (!document.RootElement.TryGetProperty(version, out JsonElement versionElement))
		{
			throw new InvalidOperationException("Vintage Story version not found in Anego manifest: " + version);
		}
		if (!versionElement.TryGetProperty(platformKey, out JsonElement platformElement))
		{
			throw new InvalidOperationException("Vintage Story server archive not found in Anego manifest for " + platformKey + " " + version);
		}

		string fileName = RequiredString(platformElement, "filename");
		string md5 = RequiredString(platformElement, "md5");
		JsonElement urls = platformElement.GetProperty("urls");
		string url = RequiredString(urls, "cdn");
		bool isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
		bool isTarGz = fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
		if (!isZip && !isTarGz)
		{
			throw new InvalidOperationException("Unsupported Vintage Story server archive type: " + fileName);
		}

		return new ArchiveInfo(fileName, url, md5, isZip);
	}

	private static void DownloadFile(string url, string destination)
	{
		string tempDestination = destination + ".download";
		if (File.Exists(tempDestination))
		{
			File.Delete(tempDestination);
		}

		using HttpClient client = new();
		client.Timeout = TimeSpan.FromMinutes(10);
		using HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
		response.EnsureSuccessStatusCode();
		using (FileStream output = File.Create(tempDestination))
		{
			response.Content.CopyToAsync(output).GetAwaiter().GetResult();
		}

		if (File.Exists(destination))
		{
			File.Delete(destination);
		}
		File.Move(tempDestination, destination);
	}

	private static void ExtractTarGz(string archivePath, string destination)
	{
		using FileStream input = File.OpenRead(archivePath);
		using GZipStream gz = new(input, CompressionMode.Decompress);
		System.Formats.Tar.TarFile.ExtractToDirectory(gz, destination, overwriteFiles: true);
	}

	private static string FindContentRoot(string extractDir)
	{
		// The Linux tarball wraps content in a top-level "server/" folder; the Windows zip doesn't.
		string nested = Path.Combine(extractDir, "server");
		if (Directory.Exists(nested))
		{
			return nested;
		}

		string[] entries = Directory.GetFileSystemEntries(extractDir);
		if (entries.Length == 1 && Directory.Exists(entries[0]))
		{
			return entries[0];
		}

		return extractDir;
	}

	private static int OverlayVanillaFiles(string sourceRoot, string installDir, bool overwriteExisting)
	{
		int copied = 0;
		foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			string rel = Path.GetRelativePath(sourceRoot, sourcePath);
			string destPath = Path.Combine(installDir, rel);
			bool exists = File.Exists(destPath);
			if (exists && !overwriteExisting)
			{
				continue;
			}

			string destDir = Path.GetDirectoryName(destPath);
			if (destDir != null)
			{
				Directory.CreateDirectory(destDir);
			}
			File.Copy(sourcePath, destPath, overwrite: overwriteExisting);
			copied++;
		}
		return copied;
	}

	private static bool LooksLikeExistingInstall(string installDir)
	{
		return File.Exists(Path.Combine(installDir, "VintagestoryLib.dll"))
			|| Directory.Exists(Path.Combine(installDir, "assets"));
	}

	private static void CleanStaleAssets(string installDir)
	{
		string assetsDir = Path.Combine(installDir, "assets");
		if (!Directory.Exists(assetsDir))
		{
			return;
		}

		Console.WriteLine("Stratum: clearing stale vanilla assets before refresh");
		Directory.Delete(assetsDir, recursive: true);
	}

	private static string RequiredString(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			throw new InvalidOperationException("Anego manifest entry is missing property: " + propertyName);
		}

		string value = property.GetString();
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidOperationException("Anego manifest entry has empty property: " + propertyName);
		}

		return value;
	}

	private static bool VerifyMd5(string path, string expectedMd5)
	{
		using FileStream stream = File.OpenRead(path);
		using MD5 md5 = MD5.Create();
		string actual = Convert.ToHexString(md5.ComputeHash(stream));
		return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase);
	}

	private readonly struct ArchiveInfo
	{
		public ArchiveInfo(string fileName, string url, string md5, bool isZip)
		{
			FileName = fileName;
			Url = url;
			Md5 = md5;
			IsZip = isZip;
		}

		public string FileName { get; }
		public string Url { get; }
		public string Md5 { get; }
		public bool IsZip { get; }
	}
}
