using System;
using System.Threading;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace Vintagestory.Server;

internal sealed class StratumServerListQuery
{
	internal static readonly StratumServerListQuery Instance = new StratumServerListQuery();

	private readonly object syncRoot = new object();
	private readonly Packet_ServerQueryAnswer answer;
	private byte[] publicKey;
	private byte[] preparedPacket;

	internal byte[] PreparedPacket => Volatile.Read(ref preparedPacket);

	internal StratumServerListQuery()
	{
		answer = new Packet_ServerQueryAnswer
		{
			ServerVersion = GameVersion.ShortGameVersion
		};
		preparedPacket = Serialize();
	}

	internal void SetPublicKey(byte[] publicKey)
	{
		lock (syncRoot)
		{
			this.publicKey = publicKey;
			Volatile.Write(ref preparedPacket, Serialize());
		}
	}

	internal static int GetRequestId(byte[] data, int length)
	{
		try
		{
			Packet_Client packet = new Packet_Client();
			Packet_ClientSerializer.DeserializeBuffer(data, length, packet);
			return packet.Id;
		}
		catch
		{
			return -1;
		}
	}

	private byte[] Serialize()
	{
		CitoMemoryStream answerStream = new CitoMemoryStream();
		Packet_ServerQueryAnswerSerializer.Serialize(answerStream, answer);
		if (publicKey != null)
		{
			answerStream.WriteByte(66);
			ProtocolParser.WriteBytes(answerStream, publicKey);
		}

		byte[] answerBytes = new byte[answerStream.Position()];
		Buffer.BlockCopy(answerStream.GetBuffer(), 0, answerBytes, 0, answerBytes.Length);

		CitoMemoryStream stream = new CitoMemoryStream();
		stream.WriteByte(0);
		stream.WriteByte(0);
		stream.WriteByte(0);
		stream.WriteByte(0);
		stream.WriteKey(90, 0);
		ProtocolParser.WriteUInt32(stream, Packet_ServerIdEnum.QueryAnswer);
		stream.WriteKey(28, 2);
		ProtocolParser.WriteBytes(stream, answerBytes);

		int length = stream.Position();
		byte[] result = new byte[length];
		Buffer.BlockCopy(stream.GetBuffer(), 0, result, 0, length);
		NetIncomingMessage.WriteInt(result, length - 4);
		return result;
	}
}
