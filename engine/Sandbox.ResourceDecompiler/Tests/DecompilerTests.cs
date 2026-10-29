using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sandbox.ResourceDecompiler.Tests;

[TestClass]
public sealed class DecompilerTests
{
	string root = null!;

	[TestInitialize]
	public void Initialize()
	{
		root = Path.Combine( Path.GetTempPath(), $"ResourceDecompilerTests-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( root );
	}

	[TestCleanup]
	public void Cleanup() => Directory.Delete( root, recursive: true );

	[DataTestMethod]
	[DataRow( "scene" )]
	[DataRow( "prefab" )]
	[DataRow( "sound" )]
	[DataRow( "tmat" )]
	[DataRow( "surface" )]
	[DataRow( "sprite" )]
	[DataRow( "custom" )]
	public void RecoversJsonAndBlobBesideCompiledResource( string extension )
	{
		var directory = Path.Combine( root, "nested", "deeper" );
		Directory.CreateDirectory( directory );
		var path = Path.Combine( directory, $"test.{extension}_c" );
		var json = "{\"GameObjects\":[],\"SceneProperties\":{},\"__guid\":\"a1b2c3d4-0001-4000-8000-000000000001\"}";
		var original = Container( 3, ("GUID", new byte[16]), ("DATA", Encoding.UTF8.GetBytes( json + '\0' )), ("DBLO", [1, 2, 3]) );
		File.WriteAllBytes( path, original );
		Assert.AreEqual( 0, Scan( DecompilerFormats.Enabled.Append( $".{extension}_c" ) ).Code );
		Assert.AreEqual( json, File.ReadAllText( path[..^2] ) );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3 }, File.ReadAllBytes( path[..^2] + "_d" ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ) );
	}

	[TestMethod]
	public void ExistingSourceIsSkippedEvenIfCompiledResourceIsInvalid()
	{
		var path = Path.Combine( root, "file.SCENE_C" );
		File.WriteAllText( path, "broken compiled data" );
		File.WriteAllText( path[..^2], "original source" );
		var result = Scan();
		Assert.AreEqual( 0, result.Code );
		Assert.IsTrue( result.Output.Contains( "skipped existing: 1", StringComparison.Ordinal ) );
		Assert.AreEqual( "original source", File.ReadAllText( path[..^2] ) );
	}

	[TestMethod]
	public void CorruptResourceDoesNotStopOtherFiles()
	{
		File.WriteAllText( Path.Combine( root, "broken.scene_c" ), "invalid" );
		File.WriteAllBytes( Path.Combine( root, "valid.sound_c" ), Container( 0, ("DATA", "{}"u8.ToArray()) ) );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "[FAILED]", StringComparison.Ordinal ) );
		Assert.AreEqual( "{}", File.ReadAllText( Path.Combine( root, "valid.sound" ) ) );
		Assert.IsFalse( File.Exists( Path.Combine( root, "broken.scene" ) ) );
	}

	[TestMethod]
	public void PreservesExistingBlobAndRefusesMismatchedData()
	{
		var path = Path.Combine( root, "test.scene_c" );
		File.WriteAllBytes( path, Container( 3, ("DATA", "{}"u8.ToArray()), ("DBLO", [1, 2, 3]) ) );
		File.WriteAllBytes( path[..^2] + "_d", [9] );
		Assert.AreEqual( 1, Scan().Code );
		Assert.IsFalse( File.Exists( path[..^2] ) );
		CollectionAssert.AreEqual( new byte[] { 9 }, File.ReadAllBytes( path[..^2] + "_d" ) );
	}

	[TestMethod]
	public void InvalidJsonOrMissingBlobDoesNotCreateSource()
	{
		File.WriteAllBytes( Path.Combine( root, "broken.scene_c" ), Container( 3, ("DATA", "{\"GameObjects\":"u8.ToArray()) ) );
		File.WriteAllBytes( Path.Combine( root, "missing.scene_c" ), Container( 3, ("DATA", "{\"Storage\":{\"$blob\":\"id\"}}"u8.ToArray()) ) );
		Assert.AreEqual( 1, Scan().Code );
		Assert.IsFalse( File.Exists( Path.Combine( root, "broken.scene" ) ) );
		Assert.IsFalse( File.Exists( Path.Combine( root, "missing.scene" ) ) );
	}

	[TestMethod]
	public void InvalidBlockRangeIsRejected()
	{
		var bytes = Container( 0, ("DATA", "{}"u8.ToArray()) );
		BinaryPrimitives.WriteInt32LittleEndian( bytes.AsSpan( 20 ), int.MaxValue );
		File.WriteAllBytes( Path.Combine( root, "bad.scene_c" ), bytes );
		Assert.AreEqual( 1, Scan().Code );
		Assert.IsFalse( File.Exists( Path.Combine( root, "bad.scene" ) ) );
	}

	[TestMethod]
	public void RepeatedNativeBlocksAreAllowedButDuplicateDataIsNot()
	{
		var path = Path.Combine( root, "file.scene_c" );
		File.WriteAllBytes( path, Container( 0, ("MDAT", [1]), ("MDAT", [2]), ("DATA", "{}"u8.ToArray()) ) );
		Assert.AreEqual( 0, Scan().Code );
		File.WriteAllBytes( Path.Combine( root, "duplicate.scene_c" ), Container( 0, ("DATA", "{}"u8.ToArray()), ("DATA", "{}"u8.ToArray()) ) );
		Assert.AreEqual( 1, Scan().Code );
		Assert.IsFalse( File.Exists( Path.Combine( root, "duplicate.scene" ) ) );
	}

	[TestMethod]
	public void ShaderExportsEmbeddedProgramsWithoutInventingSource()
	{
		var path = Path.Combine( root, "test.shader_c" );
		File.WriteAllBytes( path, Container( 1, ("DATA", []), ("HLSL", "{\"Programs\":{\"VFX_PROGRAM_PS\":\"float4 MainPs() { return 1; }\"}}"u8.ToArray()) ) );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "[PARTIAL]", StringComparison.Ordinal ) );
		Assert.AreEqual( "float4 MainPs() { return 1; }", File.ReadAllText( path[..^2] + ".VFX_PROGRAM_PS.hlsl" ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
	}

	[TestMethod]
	public void ShaderWithoutSourceIsReportedUnsupported()
	{
		var path = Path.Combine( root, "test.shader_c" );
		File.WriteAllBytes( path, Container( 1, ("DATA", []) ) );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "no embedded HLSL", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
	}

	[TestMethod]
	public void RejectsOutputPathTraversal()
	{
		var output = new OutputFiles( root, TextWriter.Null );
		Assert.ThrowsException<InvalidDataException>( () => output.Add( Path.Combine( root, "..", "escape.scene" ), [1] ) );
	}

	[TestMethod]
	public void OutputFailureRollsBackNewFiles()
	{
		var primary = Path.Combine( root, "primary.scene" );
		Directory.CreateDirectory( primary );
		var dependency = Path.Combine( root, "dependency.bin" );
		var output = new OutputFiles( root, TextWriter.Null );
		output.Add( primary, [1] );
		output.Add( dependency, [2] );
		Assert.ThrowsException<IOException>( () => output.Commit( primary ) );
		Assert.IsFalse( File.Exists( dependency ) );
		Assert.AreEqual( 0, Directory.GetFiles( root, "*.decompile-tmp" ).Length );
	}

	[TestMethod]
	public void UnsupportedSnapshotDataDoesNotProducePlaceholder()
	{
		var path = Path.Combine( root, "test.vsnap_c" );
		File.WriteAllBytes( path, Container( 1, ("GUID", new byte[16]), ("DATA", [1, 2, 3, 4]) ) );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "KV3 version 2", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
		Assert.IsTrue( result.Error.Contains( "[PARTIAL]", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] + ".runtime.json" ) );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes( Path.Combine( path[..^2] + ".recovered.blocks", "DATA.0.bin" ) ) );
	}

	[TestMethod]
	public void UnsupportedMaterialKv3VersionDoesNotPublishSourceOrMetadata()
	{
		var path = Path.Combine( root, "test.vmat_c" );
		var data = new byte[64];
		BinaryPrimitives.WriteUInt32LittleEndian( data, 0x4b563305 );
		File.WriteAllBytes( path, Container( 1, ("GUID", new byte[16]), ("DATA", data) ) );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "KV3 version 2", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
		Assert.IsFalse( File.Exists( path[..^2] + ".meta" ) );
		Assert.IsFalse( File.Exists( path[..^2] + ".runtime.json" ) );
	}

	[DataTestMethod]
	[DataRow( "vpcf" )]
	[DataRow( "vpost" )]
	[DataRow( "vsnap" )]
	[DataRow( "vmdl" )]
	public void RuntimeOnlyFormatsSalvageDataWithoutInventingAuthoringSource( string extension )
	{
		var path = Path.Combine( root, $"test.{extension}_c" );
		var data = BinaryKv3Tests.Fixture( 1 );
		var original = Container( 1, ("DATA", data), ("ANIM", [1, 2]), ("ANIM", [3, 4]), ("PHYS", [5, 6]) )
			.Concat( new byte[] { 7, 8, 9 } ).ToArray();
		File.WriteAllBytes( path, original );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "[PARTIAL]", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
		Assert.IsFalse( File.Exists( path[..^2] + ".meta" ) );
		Assert.IsTrue( File.ReadAllText( path[..^2] + ".runtime.json" ).Contains( "value.vtex", StringComparison.Ordinal ) );
		var blocks = path[..^2] + ".recovered.blocks";
		CollectionAssert.AreEqual( data, File.ReadAllBytes( Path.Combine( blocks, "DATA.0.bin" ) ) );
		CollectionAssert.AreEqual( new byte[] { 1, 2 }, File.ReadAllBytes( Path.Combine( blocks, "ANIM.0.bin" ) ) );
		CollectionAssert.AreEqual( new byte[] { 3, 4 }, File.ReadAllBytes( Path.Combine( blocks, "ANIM.1.bin" ) ) );
		CollectionAssert.AreEqual( new byte[] { 5, 6 }, File.ReadAllBytes( Path.Combine( blocks, "PHYS.0.bin" ) ) );
		CollectionAssert.AreEqual( new byte[] { 7, 8, 9 }, File.ReadAllBytes( Path.Combine( blocks, "stream.bin" ) ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ) );
		Assert.AreEqual( 1, Scan().Code );
		Assert.AreEqual( 0, Directory.GetFiles( root, "*.decompile-tmp" ).Length );
	}

	[TestMethod]
	public void ConflictingRuntimeReferenceDoesNotPublishJsonOrOtherBlocks()
	{
		var path = Path.Combine( root, "test.vpcf_c" );
		File.WriteAllBytes( path, Container( 1, ("DATA", BinaryKv3Tests.Fixture( 1 )), ("ANIM", [1, 2]) ) );
		var blocks = path[..^2] + ".recovered.blocks";
		Directory.CreateDirectory( blocks );
		var existing = Path.Combine( blocks, "ANIM.0.bin" );
		File.WriteAllBytes( existing, [5, 6] );
		var result = Scan();
		Assert.AreEqual( 1, result.Code );
		Assert.IsTrue( result.Error.Contains( "[FAILED]", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] + ".runtime.json" ) );
		Assert.IsFalse( File.Exists( Path.Combine( blocks, "DATA.0.bin" ) ) );
		CollectionAssert.AreEqual( new byte[] { 5, 6 }, File.ReadAllBytes( existing ) );
	}

	[TestMethod]
	public void Version5PcmCreatesWaveWithCorrectSamplesAndMetadata()
	{
		var header = new byte[48];
		BinaryPrimitives.WriteUInt16LittleEndian( header, 48000 );
		header[3] = 1;
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 8 ), 2 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 28 ), 4 );
		var path = Path.Combine( root, "test.vsnd_c" );
		File.WriteAllBytes( path, Container( 5, ("DATA", header), ("SUBT", "[]"u8.ToArray()) ).Concat( new byte[] { 1, 2, 3, 4 } ).ToArray() );
		Assert.AreEqual( 1, Scan().Code );
		var wave = File.ReadAllBytes( Path.ChangeExtension( path, "wav" ) );
		CollectionAssert.AreEqual( "RIFF"u8.ToArray(), wave[..4] );
		Assert.AreEqual( 48, wave.Length );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3, 4 }, wave[44..] );
		Assert.IsTrue( File.ReadAllText( Path.ChangeExtension( path, "wav.meta" ) ).Contains( "\"subtitles\":[]", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( path[..^2] ) );
	}

	[TestMethod]
	public void Version5Pcm8PadsOddSizedWaveData()
	{
		var header = new byte[48];
		BinaryPrimitives.WriteUInt16LittleEndian( header, 22050 );
		header[2] = 1;
		header[3] = 1;
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 8 ), 3 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 28 ), 3 );
		var path = Path.Combine( root, "test.vsnd_c" );
		File.WriteAllBytes( path, Container( 5, ("DATA", header) ).Concat( new byte[] { 1, 2, 3 } ).ToArray() );
		Assert.AreEqual( 1, Scan().Code );
		var wave = File.ReadAllBytes( Path.ChangeExtension( path, "wav" ) );
		Assert.AreEqual( 48, wave.Length );
		Assert.AreEqual( 40u, BinaryPrimitives.ReadUInt32LittleEndian( wave.AsSpan( 4 ) ) );
		Assert.AreEqual( 3u, BinaryPrimitives.ReadUInt32LittleEndian( wave.AsSpan( 40 ) ) );
		CollectionAssert.AreEqual( new byte[] { 1, 2, 3, 0 }, wave[44..] );
	}

	[TestMethod]
	public void ConflictingExistingDependencyPreventsPublishingSource()
	{
		var primary = Path.Combine( root, "primary.vmat" );
		var dependency = Path.Combine( root, "texture.png" );
		File.WriteAllBytes( dependency, [9] );
		var output = new OutputFiles( root, TextWriter.Null );
		output.Add( primary, [1] );
		output.Add( dependency, [2] );
		Assert.ThrowsException<IOException>( () => output.Commit( primary ) );
		Assert.IsFalse( File.Exists( primary ) );
		CollectionAssert.AreEqual( new byte[] { 9 }, File.ReadAllBytes( dependency ) );
		Assert.AreEqual( 0, Directory.GetFiles( root, "*.decompile-tmp" ).Length );
	}

	[TestMethod]
	public void Version5OpusPreservesPacketsPreSkipAndFinalGranule()
	{
		var path = WriteOpus();
		Assert.AreEqual( 1, Scan().Code );
		var audio = File.ReadAllBytes( Path.ChangeExtension( path, "opus" ) );
		var position = 0;
		var pages = 0;
		while ( position < audio.Length )
		{
			var page = audio.AsSpan( position );
			Assert.IsTrue( page.StartsWith( "OggS"u8 ) );
			Assert.AreEqual( (uint)pages, BinaryPrimitives.ReadUInt32LittleEndian( page[18..] ) );
			Assert.AreNotEqual( 0u, BinaryPrimitives.ReadUInt32LittleEndian( page[22..] ) );
			var segments = page[26];
			var length = 0;
			for ( var index = 0; index < segments; index++ )
				length += page[27 + index];
			var packet = page.Slice( 27 + segments, length );
			if ( pages == 0 )
			{
				Assert.IsTrue( packet.StartsWith( "OpusHead"u8 ) );
				Assert.AreEqual( (ushort)312, BinaryPrimitives.ReadUInt16LittleEndian( packet[10..] ) );
			}
			else if ( pages >= 2 )
			{
				CollectionAssert.AreEqual( new byte[] { 0xf8, 0xff, 0xfe }, packet.ToArray() );
				Assert.AreEqual( pages == 2 ? 960ul : 1554ul, BinaryPrimitives.ReadUInt64LittleEndian( page[6..] ) );
				Assert.AreEqual( pages == 3 ? (byte)4 : (byte)0, page[5] );
			}

			position += 27 + segments + length;
			pages++;
		}

		Assert.AreEqual( 4, pages );
		Assert.AreEqual( audio.Length, position );
		Assert.IsFalse( File.Exists( path[..^2] ) );
	}

	[TestMethod]
	public void InvalidOpusSeekTableDoesNotPublishAudio()
	{
		var path = WriteOpus( invalid: true );
		Assert.AreEqual( 1, Scan().Code );
		Assert.IsFalse( File.Exists( Path.ChangeExtension( path, "opus" ) ) );
	}

	string WriteOpus( bool invalid = false )
	{
		var header = new byte[80];
		BinaryPrimitives.WriteUInt16LittleEndian( header, 48000 );
		header[2] = 4;
		header[3] = 1;
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 8 ), 1242 );
		BinaryPrimitives.WriteInt32LittleEndian( header.AsSpan( 20 ), 28 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 24 ), 8 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 28 ), 6 );
		BinaryPrimitives.WriteInt32LittleEndian( header.AsSpan( 32 ), 24 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 36 ), 6 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 48 ), 312 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 64 ), 3 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 68 ), 960 );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 72 ), invalid ? 7u : 6u );
		BinaryPrimitives.WriteUInt32LittleEndian( header.AsSpan( 76 ), 1920 );
		var path = Path.Combine( root, "test.vsnd_c" );
		File.WriteAllBytes( path, Container( 5, ("DATA", header) ).Concat( new byte[] { 0xf8, 0xff, 0xfe, 0xf8, 0xff, 0xfe } ).ToArray() );
		return path;
	}

	[TestMethod]
	public void DisabledAndUnlistedFormatsAreSkippedBeforeParsing()
	{
		File.WriteAllText( Path.Combine( root, "disabled.vmat_c" ), "invalid compiled resource" );
		File.WriteAllBytes( Path.Combine( root, "unknown.custom_c" ), Container( 0, ("DATA", "{}"u8.ToArray()) ) );
		File.WriteAllBytes( Path.Combine( root, "enabled.SCENE_C" ), Container( 0, ("DATA", "{}"u8.ToArray()) ) );
		var result = Scan( [".scene_c"] );
		Assert.AreEqual( 0, result.Code );
		Assert.AreEqual( "", result.Error );
		Assert.IsTrue( result.Output.Contains( "skipped format: 2", StringComparison.Ordinal ) );
		Assert.IsTrue( result.Output.Contains( "[SKIP FORMAT]", StringComparison.Ordinal ) );
		Assert.IsTrue( File.Exists( Path.Combine( root, "enabled.SCENE" ) ) );
		Assert.IsFalse( File.Exists( Path.Combine( root, "disabled.vmat" ) ) );
		Assert.IsFalse( File.Exists( Path.Combine( root, "unknown.custom" ) ) );
	}

	[TestMethod]
	public void EmptyFormatListDisablesAllRecovery()
	{
		File.WriteAllBytes( Path.Combine( root, "test.scene_c" ), Container( 0, ("DATA", "{}"u8.ToArray()) ) );
		var result = Scan( [] );
		Assert.AreEqual( 0, result.Code );
		Assert.IsTrue( result.Output.Contains( "skipped format: 1", StringComparison.Ordinal ) );
		Assert.IsFalse( File.Exists( Path.Combine( root, "test.scene" ) ) );
	}

	(int Code, string Output, string Error) Scan( IEnumerable<string>? enabledFormats = null )
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var code = new ResourceScanner( output, error, enabledFormats ).Run( root );
		return (code, output.ToString(), error.ToString());
	}

	internal static byte[] Container( ushort version, params (string Name, byte[] Data)[] blocks )
	{
		var bytes = new byte[16 + blocks.Length * 12 + blocks.Sum( block => block.Data.Length )];
		BinaryPrimitives.WriteUInt32LittleEndian( bytes, (uint)bytes.Length );
		BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 4 ), 12 );
		BinaryPrimitives.WriteUInt16LittleEndian( bytes.AsSpan( 6 ), version );
		BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 8 ), 8 );
		BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( 12 ), (uint)blocks.Length );
		var position = 16 + blocks.Length * 12;
		for ( var i = 0; i < blocks.Length; i++ )
		{
			var entry = 16 + i * 12;
			Encoding.ASCII.GetBytes( blocks[i].Name ).CopyTo( bytes, entry );
			BinaryPrimitives.WriteInt32LittleEndian( bytes.AsSpan( entry + 4 ), position - entry - 4 );
			BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( entry + 8 ), (uint)blocks[i].Data.Length );
			blocks[i].Data.CopyTo( bytes, position );
			position += blocks[i].Data.Length;
		}

		return bytes;
	}
}
