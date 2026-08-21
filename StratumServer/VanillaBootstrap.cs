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

	// Anego publishes the aarch64 natives as a standalone overlay; the main manifest is x64-only.
	// Pinned in forks.json (arm64NativeOverlays), embedded into the assembly - resolving it needs
	// no GitHub API call at runtime.
	private const string Arm64CatalogResourceName = "Stratum.forks.json";
	private static readonly string[] RequiredArm64NativeLibraries =
	{
		"libe_sqlite3.so",
		"libSkiaSharp.so",
		"libzstd.so"
	};

	internal static void EnsureVanillaAssets(string baseGameVersion, bool refresh)
	{
		string installDir = AppContext.BaseDirectory;
		string markerPath = Path.Combine(installDir, ".stratum-base");
		string expectedMarker = baseGameVersion;
		bool markerExists = File.Exists(markerPath);
		string currentMarker = markerExists ? File.ReadAllText(markerPath).Trim() : string.Empty;
		bool markerMatches = markerExists && currentMarker == expectedMarker;
		bool isLinuxArm64 = IsLinuxArm64();
		Arm64Asset arm64Asset = default;
		if (isLinuxArm64)
		{
			// Resolve from the embedded catalog before the base-marker fast path. An install
			// created on x64 still needs the arm64 overlay when it moves to an arm64 host.
			arm64Asset = ResolveArm64Asset(baseGameVersion);
		}

		if (!refresh && markerMatches)
		{
			if (!isLinuxArm64)
			{
				return;
			}

			string existingCacheDir = Path.Combine(installDir, ".vanilla-cache");
			Directory.CreateDirectory(existingCacheDir);
			ApplyArm64NativeOverlay(installDir, existingCacheDir, baseGameVersion, arm64Asset, refresh);
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

		// Anego ships no linux-arm64 server archive, so the files just laid down are x86-64.
		// Swap the handful of architecture-specific natives before PatchedFileOverlay runs.
		if (isLinuxArm64)
		{
			ApplyArm64NativeOverlay(installDir, cacheDir, baseGameVersion, arm64Asset, refresh);
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

	// Replaces the x86-64 natives from the official archive with the aarch64 builds Anego publishes
	// as a separate overlay release. Only *.so files are taken: everything else in that overlay is
	// either portable IL or VintagestoryServer host files, and the overlay lags the base game by a
	// patch release or two, so copying its managed/host files would risk a version mismatch against
	// Stratum's patched assemblies. The natives are self-contained third-party libraries
	// (SkiaSharp, e_sqlite3, zstd) whose ABI does not track Vintage Story patch releases.
	private static void ApplyArm64NativeOverlay(string installDir, string cacheDir, string baseGameVersion, Arm64Asset asset, bool refresh)
	{
		string markerPath = Path.Combine(installDir, ".stratum-arm64-natives");
		string expectedMarker = BuildArm64Marker(baseGameVersion, asset);
		if (!refresh && File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == expectedMarker)
		{
			try
			{
				ValidateArm64NativeLibraries(installDir);
				Console.WriteLine("Stratum: arm64 natives already in place for " + baseGameVersion + "; leaving them untouched");
				return;
			}
			catch (InvalidOperationException exception)
			{
				Console.WriteLine("Stratum: arm64 native layout is stale; reapplying the overlay: " + exception.Message);
			}
		}

		Console.WriteLine($"Stratum: linux-arm64 host detected; fetching native overlay {asset.Name}");

		string archivePath = Path.Combine(cacheDir, asset.Name);
		if (File.Exists(archivePath) && !VerifyArm64Digest(archivePath, asset.Digest))
		{
			Console.WriteLine($"Stratum: cached {asset.Name} failed checksum; downloading a fresh copy");
			File.Delete(archivePath);
		}

		if (!File.Exists(archivePath))
		{
			DownloadFile(asset.Url, archivePath);
			if (!VerifyArm64Digest(archivePath, asset.Digest))
			{
				File.Delete(archivePath);
				throw new InvalidOperationException("Downloaded arm64 native overlay failed SHA256 verification: " + asset.Name);
			}
		}
		else
		{
			Console.WriteLine($"Stratum: using cached {archivePath}");
		}

		string extractDir = Path.Combine(cacheDir, "extract-arm64");
		if (Directory.Exists(extractDir))
		{
			Directory.Delete(extractDir, recursive: true);
		}
		Directory.CreateDirectory(extractDir);

		Console.WriteLine($"Stratum: unpacking {asset.Name}");
		ExtractTarGz(archivePath, extractDir);

		string sourceRoot = FindContentRoot(extractDir);
		int copied = 0;
		foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			if (!IsNativeLibrary(sourcePath))
			{
				continue;
			}

			string rel = Path.GetRelativePath(sourceRoot, sourcePath);
			string destPath = Path.Combine(installDir, rel);
			string destDir = Path.GetDirectoryName(destPath);
			if (destDir != null)
			{
				Directory.CreateDirectory(destDir);
			}
			File.Copy(sourcePath, destPath, overwrite: true);
			copied++;
		}

		Directory.Delete(extractDir, recursive: true);

		if (copied == 0)
		{
			throw new InvalidOperationException("arm64 native overlay " + asset.Name + " contained no shared libraries; refusing to run against x86-64 natives.");
		}

		RemoveUnusedArm64NativeLibraries(installDir);
		ValidateArm64NativeLibraries(installDir);
		Console.WriteLine($"Stratum: replaced {copied} native librar(ies) with aarch64 builds from {asset.Name}");
		File.WriteAllText(markerPath, expectedMarker);
	}

	private static bool IsLinuxArm64()
	{
		return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
			&& RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
	}

	private static string BuildArm64Marker(string baseGameVersion, Arm64Asset asset)
	{
		return "version=" + baseGameVersion + "\nasset=" + asset.Name + "\ndigest=" + asset.Digest;
	}

	private static void RemoveUnusedArm64NativeLibraries(string installDir)
	{
		// The official x64 archive includes OpenAL for the client, but the dedicated server
		// never loads it and the archive has no arm64 replacement. Do not leave an x64 ELF
		// in an arm64 install where a mod or future code could load it accidentally.
		string openAlPath = Path.Combine(installDir, "Lib", "libopenal.so.1");
		if (File.Exists(openAlPath))
		{
			File.Delete(openAlPath);
			Console.WriteLine("Stratum: removed unused x86-64 libopenal.so.1 from arm64 install");
		}
	}

	private static void ValidateArm64NativeLibraries(string installDir)
	{
		string libDir = Path.Combine(installDir, "Lib");
		if (!Directory.Exists(libDir))
		{
			throw new InvalidOperationException("arm64 native directory is missing: Lib");
		}

		foreach (string name in RequiredArm64NativeLibraries)
		{
			string path = Path.Combine(libDir, name);
			if (!File.Exists(path))
			{
				throw new InvalidOperationException("required arm64 native library is missing: Lib/" + name);
			}
		}

		foreach (string path in Directory.EnumerateFiles(libDir, "*", SearchOption.TopDirectoryOnly))
		{
			if (!IsNativeLibrary(path))
			{
				continue;
			}

			if (!IsArm64Elf(path))
			{
				throw new InvalidOperationException("native library is not an AArch64 ELF: " + Path.GetRelativePath(installDir, path));
			}
		}
	}

	private static bool IsArm64Elf(string path)
	{
		byte[] header = new byte[20];
		using FileStream stream = File.OpenRead(path);
		stream.ReadExactly(header);
		if (header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
		{
			return false;
		}

		// Linux arm64 uses ELF64 little-endian and e_machine EM_AARCH64 (183).
		return header[4] == 2 && header[5] == 1 && header[18] == 183 && header[19] == 0;
	}

	private static bool IsNativeLibrary(string path)
	{
		string fileName = Path.GetFileName(path);
		return fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
			|| fileName.Contains(".so.", StringComparison.OrdinalIgnoreCase);
	}

	// Overlay releases are cut per minor version, so a 1.22.6 base resolves the pinned 1.22 entry.
	// Pinned in forks.json rather than resolved from the GitHub API at runtime: it keeps every
	// arm64 asset URL and digest reviewable in source control, and needs no network round-trip
	// (or GITHUB_TOKEN, to dodge the anonymous rate limit) just to boot.
	private static Arm64Asset ResolveArm64Asset(string baseGameVersion)
	{
		string majorMinor = MajorMinor(baseGameVersion);

		using Stream resource = typeof(VanillaBootstrap).Assembly.GetManifestResourceStream(Arm64CatalogResourceName)
			?? throw new InvalidOperationException("Embedded resource missing: " + Arm64CatalogResourceName);
		using JsonDocument document = JsonDocument.Parse(resource);

		if (document.RootElement.TryGetProperty("arm64NativeOverlays", out JsonElement overlays))
		{
			foreach (JsonElement entry in overlays.EnumerateArray())
			{
				if (!string.Equals(RequiredString(entry, "version"), majorMinor, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string name = RequiredString(entry, "name");
				string url = RequiredString(entry, "url");
				string sha256 = RequiredString(entry, "sha256");
				return new Arm64Asset(name, url, "sha256:" + sha256);
			}
		}

		throw new InvalidOperationException(
			"No pinned arm64 native overlay for Vintage Story " + majorMinor + ".x in forks.json. " +
			"Add one under arm64NativeOverlays from https://github.com/anegostudios/VintagestoryServerArm64/releases.");
	}

	private static string MajorMinor(string version)
	{
		string[] parts = version.Split('.');
		return parts.Length >= 2 ? parts[0] + "." + parts[1] : version;
	}

	private static bool VerifyArm64Digest(string path, string digest)
	{
		// digest is sourced from our own pinned forks.json, not a remote response, so a missing or
		// malformed value is a config mistake - fail closed rather than silently trusting the file.
		const string Sha256Prefix = "sha256:";
		if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"arm64 native overlay entry in forks.json has no usable sha256 digest: '{digest}'");
		}

		using FileStream stream = File.OpenRead(path);
		using SHA256 sha = SHA256.Create();
		string actual = Convert.ToHexString(sha.ComputeHash(stream));
		return string.Equals(actual, digest.Substring(Sha256Prefix.Length), StringComparison.OrdinalIgnoreCase);
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
			throw new InvalidOperationException("JSON entry is missing property: " + propertyName);
		}

		string value = property.GetString();
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidOperationException("JSON entry has empty property: " + propertyName);
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

	private readonly struct Arm64Asset
	{
		public Arm64Asset(string name, string url, string digest)
		{
			Name = name;
			Url = url;
			Digest = digest;
		}

		public string Name { get; }
		public string Url { get; }
		public string Digest { get; }
	}
}
