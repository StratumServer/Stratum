using System;
using System.Security.Cryptography;

namespace Vintagestory.Server;

internal sealed class StratumServerListIdentity : IDisposable
{
	private readonly object syncRoot = new object();
	private readonly ECDsa signingKey;

	internal byte[] PublicKey { get; }

	internal StratumServerListIdentity()
	{
		signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		PublicKey = signingKey.ExportSubjectPublicKeyInfo();
	}

	internal ulong CreateNonce()
	{
		return BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)), 0);
	}

	internal string Sign(byte[] payload)
	{
		byte[] signature;
		lock (syncRoot)
		{
			signature = signingKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		}

		if (signature.Length != 64)
		{
			throw new CryptographicException("Server list registration produced an invalid signature length");
		}

		return Convert.ToHexString(signature);
	}

	public void Dispose()
	{
		lock (syncRoot)
		{
			signingKey.Dispose();
		}
	}
}
