using System.Buffers.Binary;
using System.Text;

namespace Sandbox.ResourceDecompiler;

internal static class SoundExporter
{
	/// <summary>
	/// s&box v5 adds a seek-table array at DATA+32. Opus header data contains pre-skip and gain.
	/// </summary>
	public static (string Extension, byte[] Data) ExtractVersion5( ResourceContainer container )
	{
		var header = container.ReadBlock( "DATA" );
		if ( header.Length < 48 )
			throw new InvalidDataException( "Incomplete version 5 sound header." );

		var rate = BinaryPrimitives.ReadUInt16LittleEndian( header );
		var format = header[2];
		var channels = header[3];
		var samples = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 8 ) );
		var audio = container.ReadStreamingData( BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 28 ) ) );
		if ( rate == 0 || channels == 0 )
			throw new InvalidDataException( "Invalid audio sample rate or channel count." );

		if ( format == 5 )
		{
			if ( !audio.AsSpan().StartsWith( "OggS"u8 ) )
				throw new InvalidDataException( "Vorbis streaming data has no Ogg container." );
			return ("ogg", audio);
		}

		if ( format == 4 )
		{
			if ( rate != 48000 || channels > 2 )
				throw new NotSupportedException( "Only 48 kHz mono/stereo Opus sounds can be remuxed." );
			return ("opus", RemuxOpus( header, audio, channels, samples ));
		}

		if ( format == 2 )
			return ("mp3", audio);

		if ( format is not (0 or 1) )
			throw new NotSupportedException( $"Unsupported s&box v5 audio format: {format}." );

		var bits = format == 0 ? 16 : 8;
		if ( (long)samples * channels * (bits / 8) != audio.Length )
			throw new InvalidDataException( "PCM sample count does not match the streaming data size." );

		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, Encoding.UTF8, leaveOpen: true );
		writer.Write( "RIFF"u8 );
		writer.Write( checked(36u + (uint)audio.Length + (uint)(audio.Length & 1)) );
		writer.Write( "WAVEfmt "u8 );
		writer.Write( 16u );
		writer.Write( (ushort)1 );
		writer.Write( (ushort)channels );
		writer.Write( (uint)rate );
		writer.Write( (uint)(rate * channels * bits / 8) );
		writer.Write( (ushort)(channels * bits / 8) );
		writer.Write( (ushort)bits );
		writer.Write( "data"u8 );
		writer.Write( (uint)audio.Length );
		writer.Write( audio );
		if ( (audio.Length & 1) != 0 )
			writer.Write( (byte)0 );
		return ("wav", stream.ToArray());
	}

	static byte[] RemuxOpus( byte[] header, byte[] audio, byte channels, uint samples )
	{
		var extraOffset = 20L + BinaryPrimitives.ReadInt32LittleEndian( header.AsSpan( 20 ) );
		var extraSize = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 24 ) );
		var seekOffset = 32L + BinaryPrimitives.ReadInt32LittleEndian( header.AsSpan( 32 ) );
		var seekCount = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 36 ) );
		if ( extraOffset < 48 || extraSize != 8 || extraOffset + extraSize > header.Length
			|| seekOffset < 48 || seekCount < 4 || seekCount % 2 != 0 || seekOffset + seekCount * 4L > header.Length )
			throw new InvalidDataException( "Invalid Opus header or packet seek table." );

		var preSkip = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( (int)extraOffset ) );
		var gain = BinaryPrimitives.ReadInt32LittleEndian( header.AsSpan( (int)extraOffset + 4 ) );
		if ( preSkip > ushort.MaxValue || gain is < short.MinValue or > short.MaxValue )
			throw new InvalidDataException( "Invalid Opus pre-skip or output gain." );

		using var stream = new MemoryStream();
		using var packetStream = new MemoryStream();
		using var writer = new BinaryWriter( packetStream, Encoding.UTF8, leaveOpen: true );
		writer.Write( "OpusHead"u8 );
		writer.Write( (byte)1 );
		writer.Write( channels );
		writer.Write( (ushort)preSkip );
		writer.Write( 48000u );
		writer.Write( (short)gain );
		writer.Write( (byte)0 );
		WritePage( stream, packetStream.ToArray(), 0, 0, 2 );
		packetStream.SetLength( 0 );
		writer.Write( "OpusTags"u8 );
		writer.Write( 0u );
		writer.Write( 0u );
		WritePage( stream, packetStream.ToArray(), 1, 0, 0 );

		var table = header.AsSpan( (int)seekOffset, (int)seekCount * 4 );
		if ( BinaryPrimitives.ReadUInt32LittleEndian( table ) != 0
			|| BinaryPrimitives.ReadUInt32LittleEndian( table[4..] ) != 0 )
			throw new InvalidDataException( "Opus seek table does not start at the beginning of the audio." );

		uint sequence = 2;
		for ( var index = 2; index < seekCount; index += 2 )
		{
			var start = BinaryPrimitives.ReadUInt32LittleEndian( table[((index - 2) * 4)..] );
			var end = BinaryPrimitives.ReadUInt32LittleEndian( table[(index * 4)..] );
			var granule = BinaryPrimitives.ReadUInt32LittleEndian( table[(index * 4 + 4)..] );
			var previousGranule = BinaryPrimitives.ReadUInt32LittleEndian( table[(index * 4 - 4)..] );
			var last = index == seekCount - 2;
			if ( end <= start || end > audio.Length || granule <= previousGranule
				|| (last && (end != audio.Length || (ulong)samples + preSkip > granule || (ulong)samples + preSkip < previousGranule)) )
				throw new InvalidDataException( "Invalid Opus packet boundary or granule position." );

			WritePage( stream, audio.AsSpan( (int)start, (int)(end - start) ), sequence++,
				last ? (ulong)samples + preSkip : granule, last ? (byte)4 : (byte)0 );
		}

		return stream.ToArray();
	}

	static void WritePage( Stream stream, ReadOnlySpan<byte> packet, uint sequence, ulong granule, byte flags )
	{
		var segmentCount = packet.Length / 255 + 1;
		if ( segmentCount > 255 )
			throw new NotSupportedException( "Opus packet exceeds one Ogg page." );

		var page = new byte[27 + segmentCount + packet.Length];
		"OggS"u8.CopyTo( page );
		page[5] = flags;
		BinaryPrimitives.WriteUInt64LittleEndian( page.AsSpan( 6 ), granule );
		BinaryPrimitives.WriteUInt32LittleEndian( page.AsSpan( 14 ), 1 );
		BinaryPrimitives.WriteUInt32LittleEndian( page.AsSpan( 18 ), sequence );
		page[26] = (byte)segmentCount;
		page.AsSpan( 27, segmentCount - 1 ).Fill( 255 );
		page[26 + segmentCount] = (byte)(packet.Length % 255);
		packet.CopyTo( page.AsSpan( 27 + segmentCount ) );
		uint crc = 0;
		foreach ( var value in page )
		{
			crc ^= (uint)value << 24;
			for ( var bit = 0; bit < 8; bit++ )
				crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
		}

		BinaryPrimitives.WriteUInt32LittleEndian( page.AsSpan( 22 ), crc );
		stream.Write( page );
	}
}
