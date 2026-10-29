using System.Buffers.Binary;
using K4os.Compression.LZ4;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSharp;

namespace Sandbox.ResourceDecompiler.Tests;

[TestClass]
public sealed class TextureTests
{
	string root = null!;

	[TestInitialize]
	public void Initialize()
	{
		root = Path.Combine( Path.GetTempPath(), $"IndependentTextureTests-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
	}

	[TestCleanup]
	public void Cleanup() => Directory.Delete( root, recursive: true );

	[TestMethod]
	public void RawRgbaRecoversExactPixelsAndIgnoresUnneededCompileRecords()
	{
		var data = Header( 2, 1, 4, 1 );
		var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
		var path = Write( data, pixels );
		Assert.IsNotNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		CollectionAssert.AreEqual( TextureExporter.EncodeTga( 2, 1, pixels ), File.ReadAllBytes( path[..^2] + ".recovered.mip0.tga" ) );
		var definition = File.ReadAllText( path[..^2] );
		Assert.IsTrue( definition.Contains( "\"RGBA8888\"", StringComparison.Ordinal ) );
		Assert.IsTrue( definition.Contains( "test.vtex.recovered.mip0.tga", StringComparison.Ordinal ) );
	}

	[DataTestMethod]
	[DataRow( 1u )]
	[DataRow( 2u )]
	public void CompressedMipsUseFieldRelativeSizesAndSmallestMipFirst( uint method )
	{
		var large = Enumerable.Repeat( (byte)40, 16 * 16 * 4 ).ToArray();
		var small = Enumerable.Repeat( (byte)80, 8 * 8 * 4 ).ToArray();
		var compressedLarge = Compress( large, method );
		var compressedSmall = Compress( small, method );
		var data = new byte[72];
		Header( 16, 16, 4, 2 ).CopyTo( data, 0 );
		U32( data, 32, 8 ); U32( data, 36, 1 );
		U32( data, 40, 4 ); U32( data, 44, 8 ); U32( data, 48, 12 );
		U32( data, 52, method ); U32( data, 56, 8 ); U32( data, 60, 2 );
		U32( data, 64, (uint)compressedLarge.Length ); U32( data, 68, (uint)compressedSmall.Length );
		var path = Write( data, compressedSmall.Concat( compressedLarge ).ToArray() );
		ResourceExporter.Export( path, root, TextWriter.Null );
		CollectionAssert.AreEqual( TextureExporter.EncodeTga( 16, 16, large ), File.ReadAllBytes( path[..^2] + ".recovered.mip0.tga" ) );
		CollectionAssert.AreEqual( TextureExporter.EncodeTga( 8, 8, small ), File.ReadAllBytes( path[..^2] + ".recovered.mip1.tga" ) );
	}

	[TestMethod]
	public void TruncatedTextureDoesNotPublishAnyOutput()
	{
		var path = Write( Header( 16, 16, 4, 1 ), [1, 2, 3] );
		Assert.ThrowsException<InvalidDataException>( () => ResourceExporter.Export( path, root, TextWriter.Null ) );
		Assert.AreEqual( 1, Directory.GetFiles( root ).Length );
	}

	[TestMethod]
	public void UnsupportedTextureDoesNotPretendToRecoverSource()
	{
		var data = Header( 4, 4, 23, 1 );
		var path = Write( data, new byte[16] );
		Assert.ThrowsException<NotSupportedException>( () => ResourceExporter.Export( path, root, TextWriter.Null ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
	}

	[TestMethod]
	public void SpriteSheetRetainsAtlasPixelsAndMetadataWithExplicitPartialWarning()
	{
		var data = new byte[56];
		Header( 1, 1, 4, 1 ).CopyTo( data, 0 );
		U32( data, 32, 8 ); U32( data, 36, 1 );
		U32( data, 40, 2 ); U32( data, 44, 8 ); U32( data, 48, 4 );
		new byte[] { 1, 2, 3, 4 }.CopyTo( data, 52 );
		var pixels = new byte[] { 10, 20, 30, 40 };
		var path = Write( data, pixels );
		var warning = ResourceExporter.Export( path, root, TextWriter.Null );
		Assert.IsNotNull( warning );
		Assert.IsTrue( warning.Contains( "static atlas only", StringComparison.Ordinal ) );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes( path[..^2] + ".sheet.bin" ) );
		CollectionAssert.AreEqual( TextureExporter.EncodeTga( 1, 1, pixels ), File.ReadAllBytes( path[..^2] + ".recovered.mip0.tga" ) );
		Assert.IsTrue( File.Exists( path[..^2] ) );
	}

	[TestMethod]
	public void HalfFloatTexturePreservesNegativeHdrAndAlphaInExr()
	{
		var values = new float[] { -1.25f, 2.5f, 4, 0.5f, 7, -0.5f, 1, 1 };
		var pixels = new byte[values.Length * 2];
		for ( var i = 0; i < values.Length; i++ )
			BinaryPrimitives.WriteUInt16LittleEndian( pixels.AsSpan( i * 2 ), BitConverter.HalfToUInt16Bits( (Half)values[i] ) );
		var path = Write( Header( 2, 1, 10, 1 ), pixels );
		Assert.IsNotNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		var image = File.ReadAllBytes( path[..^2] + ".recovered.mip0.exr" );
		Assert.AreEqual( 20000630, BinaryPrimitives.ReadInt32LittleEndian( image ) );
		var expected = new float[] { 0.5f, 1, 4, 1, 2.5f, -0.5f, -1.25f, 7 };
		var start = image.Length - expected.Length * sizeof( float );
		for ( var i = 0; i < expected.Length; i++ )
			Assert.AreEqual( expected[i], BinaryPrimitives.ReadSingleLittleEndian( image.AsSpan( start + i * sizeof( float ) ) ) );
	}

	[TestMethod]
	public void InvalidExtraDataPointerIsRejected()
	{
		var data = Header( 4, 4, 4, 1 );
		U32( data, 32, int.MaxValue ); U32( data, 36, 1 );
		var path = Write( data, new byte[64] );
		Assert.ThrowsException<InvalidDataException>( () => ResourceExporter.Export( path, root, TextWriter.Null ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
	}

	string Write( byte[] header, byte[] payload )
	{
		var path = Path.Combine( root, "test.vtex_c" );
		// REDI is a compile record, not required to recover texture pixels.
		var container = DecompilerTests.Container( 1, ("REDI", [2, 0, 0, 0]), ("DATA", header) );
		File.WriteAllBytes( path, container.Concat( payload ).ToArray() );
		return path;
	}

	static byte[] Header( ushort width, ushort height, byte format, byte mips )
	{
		var header = new byte[40];
		BinaryPrimitives.WriteUInt16LittleEndian( header, 1 );
		BinaryPrimitives.WriteUInt16LittleEndian( header.AsSpan( 20 ), width );
		BinaryPrimitives.WriteUInt16LittleEndian( header.AsSpan( 22 ), height );
		BinaryPrimitives.WriteUInt16LittleEndian( header.AsSpan( 24 ), 1 );
		header[26] = format; header[27] = mips;
		return header;
	}

	static void U32( byte[] data, int offset, uint value ) => BinaryPrimitives.WriteUInt32LittleEndian( data.AsSpan( offset ), value );

	static byte[] Compress( byte[] input, uint method )
	{
		if ( method == 2 )
		{
			using var compressor = new Compressor();
			return compressor.Wrap( input ).ToArray();
		}
		var output = new byte[LZ4Codec.MaximumOutputSize( input.Length )];
		var size = LZ4Codec.Encode( input, 0, input.Length, output, 0, output.Length );
		Assert.IsTrue( size > 0 );
		return output[..size];
	}
}
