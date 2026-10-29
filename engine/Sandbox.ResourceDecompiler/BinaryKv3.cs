using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace Sandbox.ResourceDecompiler;

internal sealed class BinaryKv3
{
	const int MaximumBytes = 64 * 1024 * 1024;
	static readonly UTF8Encoding Utf8 = new( false, true );
	readonly byte[] data;
	readonly string[] strings;
	int bytes, integers, doubles, types;
	readonly int bytesEnd, integersEnd, doublesEnd, typesEnd;
	int remaining = 1_000_000;

	BinaryKv3( byte[] header )
	{
		if ( header.Length < 64 || I32( header, 0 ) != 0x4b563302 )
			throw new NotSupportedException( "Only independently verified binary KV3 version 2 is supported." );
		if ( I32( header, 56 ) != 0 || I32( header, 60 ) != 0 )
			throw new NotSupportedException( "Binary KV3 external blob streams are not yet supported." );
		var unpacked = I32( header, 48 );
		var packed = I32( header, 52 );
		if ( unpacked < 4 || unpacked > MaximumBytes || packed < 0 || packed != header.Length - 64 )
			throw new InvalidDataException( "Invalid binary KV3 payload sizes." );
		data = new byte[unpacked];
		var method = I32( header, 20 );
		var written = method switch
		{
			0 when packed == unpacked => Copy( header.AsSpan( 64 ), data ),
			1 => LZ4Codec.Decode( header, 64, packed, data, 0, unpacked ),
			2 => Unwrap( header.AsSpan( 64 ), data ),
			_ => throw new NotSupportedException( $"Unsupported binary KV3 compression {method}." )
		};
		if ( written != unpacked )
			throw new InvalidDataException( "Binary KV3 decompressed size mismatch." );
		bytesEnd = CheckedSize( I32( header, 28 ), 1 );
		integers = checked((bytesEnd + 3) & ~3);
		integersEnd = checked(integers + CheckedSize( I32( header, 32 ), 4 ));
		doubles = checked((integersEnd + 7) & ~7);
		doublesEnd = checked(doubles + CheckedSize( I32( header, 36 ), 8 ));
		typesEnd = checked(doublesEnd + I32( header, 40 ));
		if ( doublesEnd > data.Length - 4 || typesEnd != data.Length - 4 || I32( data, typesEnd ) != unchecked((int)0xffeedd00) )
			throw new InvalidDataException( "Invalid binary KV3 lane sizes or terminator." );
		var count = Int();
		if ( count < 0 || count > 100_000 )
			throw new InvalidDataException( "Invalid binary KV3 string count." );
		strings = new string[count];
		types = doublesEnd;
		for ( var i = 0; i < count; i++ )
		{
			var end = Array.IndexOf( data, (byte)0, types, typesEnd - types );
			if ( end < 0 )
				throw new InvalidDataException( "Unterminated binary KV3 string." );
			strings[i] = Utf8.GetString( data, types, end - types );
			types = end + 1;
		}
	}

	public static JsonObject Read( byte[] data )
	{
		var reader = new BinaryKv3( data );
		var result = reader.Value( reader.Type(), 0 ) as JsonObject
			?? throw new InvalidDataException( "Expected binary KV3 object root." );
		if ( reader.bytes != reader.bytesEnd || reader.integers != reader.integersEnd
			|| reader.doubles != reader.doublesEnd || reader.types != reader.typesEnd )
			throw new InvalidDataException( "Unconsumed binary KV3 values." );
		return result;
	}

	byte Type()
	{
		var type = TakeByte( ref types, typesEnd );
		if ( (type & 0x80) != 0 )
		{
			var flag = TakeByte( ref types, typesEnd );
			if ( flag != 1 )
				throw new NotSupportedException( $"Unsupported binary KV3 value flag {flag}." );
		}
		return (byte)(type & 0x7f);
	}

	JsonNode? Value( byte type, int depth )
	{
		if ( depth > 128 || --remaining < 0 )
			throw new InvalidDataException( "Binary KV3 nesting or value limit exceeded." );
		switch ( type )
		{
			case 1: return null;
			case 2: return JsonValue.Create( TakeByte( ref bytes, bytesEnd ) != 0 );
			case 3: return JsonValue.Create( Long() );
			case 4: return JsonValue.Create( unchecked((ulong)Long()) );
			case 5: return JsonValue.Create( BitConverter.Int64BitsToDouble( Long() ) );
			case 6: return JsonValue.Create( String( Int() ) );
			case 8:
			case 10:
				{
					var count = Count();
					var subtype = type == 10 ? Type() : (byte)0;
					var array = new JsonArray();
					for ( var i = 0; i < count; i++ )
						array.Add( Value( type == 10 ? subtype : Type(), depth + 1 ) );
					return array;
				}
			case 9:
				{
					var count = Count();
					var result = new JsonObject();
					for ( var i = 0; i < count; i++ )
					{
						var name = String( Int() );
						if ( result.ContainsKey( name ) )
							throw new InvalidDataException( "Duplicate binary KV3 object member." );
						result.Add( name, Value( Type(), depth + 1 ) );
					}
					return result;
				}
			case 11: return JsonValue.Create( Int() );
			case 12: return JsonValue.Create( unchecked((uint)Int()) );
			case 13: return JsonValue.Create( true );
			case 14: return JsonValue.Create( false );
			case 15: return JsonValue.Create( 0 );
			case 16: return JsonValue.Create( 1 );
			case 17: return JsonValue.Create( 0.0 );
			case 18: return JsonValue.Create( 1.0 );
			case 19: return JsonValue.Create( BitConverter.Int32BitsToSingle( Int() ) );
			default: throw new NotSupportedException( $"Unsupported binary KV3 type {type}." );
		}
	}

	int Count()
	{
		var value = Int();
		if ( value < 0 || value > remaining )
			throw new InvalidDataException( "Invalid binary KV3 collection count." );
		return value;
	}

	string String( int index ) => index == -1 ? "" : index >= 0 && index < strings.Length
		? strings[index] : throw new InvalidDataException( "Invalid binary KV3 string index." );

	int Int()
	{
		if ( integers > integersEnd - 4 )
			throw new InvalidDataException( "Truncated binary KV3 integer lane." );
		var value = I32( data, integers );
		integers += 4;
		return value;
	}

	long Long()
	{
		if ( doubles > doublesEnd - 8 )
			throw new InvalidDataException( "Truncated binary KV3 eight-byte lane." );
		var value = BinaryPrimitives.ReadInt64LittleEndian( data.AsSpan( doubles ) );
		doubles += 8;
		return value;
	}

	byte TakeByte( ref int position, int end )
	{
		if ( position >= end )
			throw new InvalidDataException( "Truncated binary KV3 byte/type lane." );
		return data[position++];
	}

	static int CheckedSize( int count, int size ) => count >= 0 && count <= MaximumBytes / size
		? count * size : throw new InvalidDataException( "Invalid binary KV3 lane count." );
	static int I32( byte[] data, int offset ) => BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( offset ) );
	static int Copy( ReadOnlySpan<byte> source, byte[] destination ) { source.CopyTo( destination ); return source.Length; }
	static int Unwrap( ReadOnlySpan<byte> source, byte[] destination )
	{
		using var decoder = new Decompressor();
		return decoder.Unwrap( source, destination );
	}
}
