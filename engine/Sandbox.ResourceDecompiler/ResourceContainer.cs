using System.Buffers.Binary;
using System.Text;

namespace Sandbox.ResourceDecompiler;

/// <summary>
/// Reads Source 2 resource blocks without loading streaming texture/audio data into memory.
/// </summary>
internal sealed class ResourceContainer : IDisposable
{
	readonly FileStream stream;
	readonly Dictionary<string, List<(long Offset, int Length)>> blocks = new( StringComparer.Ordinal );
	public ushort Version { get; private set; }
	public uint NonStreamingSize { get; private set; }
	public long StreamingSize => stream.Length - NonStreamingSize;
	public IEnumerable<string> BlockNames => blocks.Keys;

	public ResourceContainer( string path )
	{
		stream = File.OpenRead( path );
		try
		{
			Span<byte> header = stackalloc byte[16];
			stream.ReadExactly( header );
			var size = BinaryPrimitives.ReadUInt32LittleEndian( header );
			var version = BinaryPrimitives.ReadUInt16LittleEndian( header[4..] );
			Version = BinaryPrimitives.ReadUInt16LittleEndian( header[6..] );
			NonStreamingSize = size;
			if ( version != 12 || size < header.Length || size > stream.Length )
				throw new InvalidDataException( "Invalid or unsupported Source 2 resource header." );

			var table = 8L + BinaryPrimitives.ReadInt32LittleEndian( header[8..] );
			var count = BinaryPrimitives.ReadUInt32LittleEndian( header[12..] );
			var tableEnd = table + count * 12L;
			if ( table < 16 || count == 0 || tableEnd > size )
				throw new InvalidDataException( "Invalid resource block table." );

			Span<byte> entry = stackalloc byte[12];
			for ( var i = 0L; i < count; i++ )
			{
				var position = table + i * entry.Length;
				stream.Position = position;
				stream.ReadExactly( entry );
				var name = Encoding.ASCII.GetString( entry[..4] );
				var offset = position + 4 + BinaryPrimitives.ReadInt32LittleEndian( entry[4..] );
				var length = BinaryPrimitives.ReadUInt32LittleEndian( entry[8..] );
				if ( offset < tableEnd || offset + length > size || length > int.MaxValue )
					throw new InvalidDataException( $"Invalid {name} resource block range." );

				if ( !blocks.TryGetValue( name, out var entries ) )
					blocks.Add( name, entries = [] );
				entries.Add( (offset, (int)length) );
			}
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	public bool HasBlock( string name ) => blocks.ContainsKey( name );

	public bool HasJsonData()
	{
		if ( !blocks.ContainsKey( "DATA" ) )
			return false;

		var block = GetSingleBlock( "DATA" );
		stream.Position = block.Offset;
		for ( var i = 0; i < block.Length; i++ )
		{
			var value = stream.ReadByte();
			if ( value is 9 or 10 or 13 or 32 )
				continue;

			return value == '{';
		}

		return false;
	}

	public byte[] ReadBlock( string name )
	{
		var block = GetSingleBlock( name );
		var data = new byte[block.Length];
		stream.Position = block.Offset;
		stream.ReadExactly( data );
		return data;
	}

	public IReadOnlyList<byte[]> ReadBlocks( string name )
	{
		if ( !blocks.TryGetValue( name, out var entries ) )
			return [];
		var result = new List<byte[]>( entries.Count );
		foreach ( var (offset, length) in entries )
		{
			var data = new byte[length];
			stream.Position = offset;
			stream.ReadExactly( data );
			result.Add( data );
		}
		return result;
	}

	(long Offset, int Length) GetSingleBlock( string name )
	{
		if ( !blocks.TryGetValue( name, out var entries ) )
			throw new InvalidDataException( $"Missing {name} resource block." );
		if ( entries.Count != 1 )
			throw new InvalidDataException( $"Expected exactly one {name} resource block." );
		return entries[0];
	}

	public byte[] ReadStreamingData( uint length )
	{
		if ( length > int.MaxValue || length > stream.Length - NonStreamingSize )
			throw new InvalidDataException( "Invalid streaming data size." );

		stream.Position = NonStreamingSize;
		var data = new byte[(int)length];
		stream.ReadExactly( data );
		return data;
	}

	public static ReadOnlyMemory<byte> TrimTerminator( byte[] data )
	{
		return data.Length > 0 && data[^1] == 0 ? data.AsMemory( 0, data.Length - 1 ) : data;
	}

	public void Dispose() => stream.Dispose();
}
