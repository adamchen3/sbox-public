using System.Buffers.Binary;
using System.Text;
using MeshOptimizer;

namespace Sandbox.ResourceDecompiler;

internal sealed class ModelBuffers
{
	internal sealed record Attribute( string Name, int Index, int Format, int Offset );
	internal sealed record Buffer( int Count, int Stride, byte[] Data, IReadOnlyList<Attribute> Attributes );
	public IReadOnlyList<Buffer> Vertices { get; }
	public IReadOnlyList<Buffer> Indices { get; }

	public ModelBuffers( byte[] data )
	{
		Check( data, 0, 16 );
		long remaining = 256 * 1024 * 1024;
		Vertices = Read( data, 0, true, ref remaining );
		Indices = Read( data, 8, false, ref remaining );
	}

	static IReadOnlyList<Buffer> Read( byte[] data, int field, bool vertices, ref long remaining )
	{
		var count = I32( data, field + 4 );
		if ( count < 0 || count > 256 )
			throw new InvalidDataException( "Invalid model buffer count." );
		var table = checked(field + I32( data, field ));
		Check( data, table, count * 24 );
		var result = new List<Buffer>( count );
		for ( var i = 0; i < count; i++ )
		{
			var entry = table + i * 24;
			var elements = I32( data, entry );
			var stride = I32( data, entry + 4 );
			if ( elements <= 0 || stride <= 0 || stride > 256 || (long)elements * stride > 256 * 1024 * 1024 )
				throw new InvalidDataException( "Invalid model buffer dimensions." );
			remaining -= (long)elements * stride;
			if ( remaining < 0 )
				throw new NotSupportedException( "Decoded model buffers exceed the 256 MiB limit." );
			if ( !vertices && stride is not (2 or 4) )
				throw new NotSupportedException( $"Unsupported model index stride {stride}." );
			var attributes = new List<Attribute>();
			var attributeCount = I32( data, entry + 12 );
			if ( attributeCount < 0 || attributeCount > 32 )
				throw new InvalidDataException( "Invalid model vertex attribute count." );
			var attributeTable = checked(entry + 8 + I32( data, entry + 8 ));
			Check( data, attributeTable, attributeCount * 56 );
			for ( var a = 0; a < attributeCount; a++ )
			{
				var start = attributeTable + a * 56;
				var nameBytes = data.AsSpan( start, 32 );
				var end = nameBytes.IndexOf( (byte)0 );
				if ( end < 0 )
					throw new InvalidDataException( "Unterminated model vertex semantic." );
				if ( I32( data, start + 44 ) != 0 || I32( data, start + 48 ) != 0 )
					throw new NotSupportedException( "Instanced or multiple-slot model vertex attributes are not supported." );
				attributes.Add( new Attribute( Encoding.ASCII.GetString( nameBytes[..end] ), I32( data, start + 32 ), I32( data, start + 36 ), I32( data, start + 40 ) ) );
			}
			var offset = checked(entry + 16 + I32( data, entry + 16 ));
			var size = I32( data, entry + 20 );
			Check( data, offset, size );
			var packed = data.AsSpan( offset, size ).ToArray();
			var raw = new byte[checked(elements * stride)];
			var compressed = packed.Length > 0 && (vertices ? (packed[0] & 0xf0) == 0xa0 : (packed[0] & 0xf0) == 0xe0);
			if ( packed.Length == raw.Length )
				packed.CopyTo( raw, 0 );
			else if ( compressed )
			{
				if ( vertices ? stride % 4 != 0 : elements % 3 != 0 )
					throw new InvalidDataException( "Invalid meshoptimizer buffer alignment or triangle count." );
				Decode( packed, raw, elements, stride, vertices );
			}
			else
				throw new NotSupportedException( "Unknown model vertex/index buffer encoding." );
			result.Add( new Buffer( elements, stride, raw, attributes ) );
		}
		return result;
	}

	static unsafe void Decode( byte[] packed, byte[] raw, int count, int stride, bool vertices )
	{
		fixed ( byte* source = packed )
		fixed ( byte* destination = raw )
		{
			var status = vertices
				? Meshopt.DecodeVertexBuffer( destination, (nuint)count, (nuint)stride, source, (nuint)packed.Length )
				: Meshopt.DecodeIndexBuffer( destination, (nuint)count, (nuint)stride, source, (nuint)packed.Length );
			if ( status != 0 )
				throw new InvalidDataException( $"Meshoptimizer {(vertices ? "vertex" : "index")} decode failed ({status})." );
		}
	}

	internal static void Check( byte[] data, int offset, int size )
	{
		if ( offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset )
			throw new InvalidDataException( "Model buffer range is outside its block." );
	}

	internal static int I32( byte[] data, int offset ) => BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( offset ) );
}
