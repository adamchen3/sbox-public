using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSharp;

namespace Sandbox.ResourceDecompiler.Tests;

[TestClass]
public sealed class BinaryKv3Tests
{
	[DataTestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 2 )]
	public void ReadsIndependentLanesAndResourceStrings( int compression )
	{
		var result = BinaryKv3.Read( Fixture( compression ) );
		Assert.AreEqual( "value.vtex", result["name"]!.GetValue<string>() );
		Assert.AreEqual( 1.25, result["number"]!.GetValue<double>() );
		Assert.AreEqual( 0, result["array"]![0]!.GetValue<int>() );
		Assert.AreEqual( 1, result["array"]![1]!.GetValue<int>() );
	}

	[TestMethod]
	public void RejectsUnknownVersionWithoutGuessing()
	{
		var bytes = Fixture( 0 );
		bytes[0] = 5;
		Assert.ThrowsException<NotSupportedException>( () => BinaryKv3.Read( bytes ) );
	}

	[TestMethod]
	public void RejectsBadStringIndex()
	{
		var bytes = Fixture( 0 );
		Write( bytes, 64 + 12, 999 );
		Assert.ThrowsException<InvalidDataException>( () => BinaryKv3.Read( bytes ) );
	}

	[TestMethod]
	public void RejectsNegativeCollectionLength()
	{
		var bytes = Fixture( 0 );
		Write( bytes, 64 + 4, -1 );
		Assert.ThrowsException<InvalidDataException>( () => BinaryKv3.Read( bytes ) );
	}

	[TestMethod]
	public void RejectsTruncatedAndUnconsumedPayload()
	{
		Assert.ThrowsException<InvalidDataException>( () => BinaryKv3.Read( Fixture( 0 )[..^1] ) );
		var bytes = Fixture( 0 );
		bytes[^1] = 0;
		Assert.ThrowsException<InvalidDataException>( () => BinaryKv3.Read( bytes ) );
	}

	internal static byte[] Fixture( int compression )
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream );
		foreach ( var integer in new[] { 4, 3, 0, 1, 2, 3, 2 } )
			writer.Write( integer );
		writer.Write( 0 ); // Eight-byte lane alignment.
		writer.Write( 1.25 );
		writer.Write( Encoding.UTF8.GetBytes( "name\0value.vtex\0number\0array\0" ) );
		writer.Write( new byte[] { 9, 0x86, 1, 5, 8, 15, 16 } );
		writer.Write( 0xffeedd00 );
		var raw = stream.ToArray();
		byte[] payload;
		if ( compression == 1 )
		{
			payload = new byte[LZ4Codec.MaximumOutputSize( raw.Length )];
			var length = LZ4Codec.Encode( raw, 0, raw.Length, payload, 0, payload.Length );
			payload = payload[..length];
		}
		else if ( compression == 2 )
		{
			using var compressor = new Compressor();
			payload = compressor.Wrap( raw ).ToArray();
		}
		else payload = raw;
		var result = new byte[64 + payload.Length];
		Write( result, 0, 0x4b563302 );
		Write( result, 20, compression );
		Write( result, 32, 7 );
		Write( result, 36, 1 );
		Write( result, 40, raw.Length - 40 - 4 );
		Write( result, 48, raw.Length );
		Write( result, 52, payload.Length );
		payload.CopyTo( result, 64 );
		return result;
	}

	static void Write( byte[] bytes, int offset, int value ) => BinaryPrimitives.WriteInt32LittleEndian( bytes.AsSpan( offset ), value );
}
