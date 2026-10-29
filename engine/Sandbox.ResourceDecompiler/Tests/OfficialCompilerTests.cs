using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sandbox.ResourceDecompiler.Tests;

[TestClass]
[TestCategory( "OfficialCompiler" )]
public sealed class OfficialCompilerTests
{
	string root = null!;
	string engine = null!;
	public TestContext TestContext { get; set; } = null!;

	[TestInitialize]
	public void Initialize()
	{
		engine = Environment.GetEnvironmentVariable( "SBOX_RESOURCE_COMPILER_ROOT" )!;
		if ( string.IsNullOrWhiteSpace( engine ) )
			Assert.Inconclusive( "Set SBOX_RESOURCE_COMPILER_ROOT to the s&box game directory to run official compiler round trips." );
		Assert.IsTrue( File.Exists( Path.Combine( engine, "bin", "win64", "resourcecompiler.exe" ) ) );
		root = Path.Combine( Path.GetTempPath(), $"IndependentResourceRoundTrip-{Guid.NewGuid():N}", "Assets" );
		Directory.CreateDirectory( root );
	}

	[TestCleanup]
	public void Cleanup()
	{
		if ( root is not null )
		{
			Directory.Delete( root, recursive: true );
			Directory.Delete( Path.GetDirectoryName( root )! );
		}
	}

	[DataTestMethod]
	[DataRow( "RGBA8888", 4, false )]
	[DataRow( "DXT1", 1, false )]
	[DataRow( "DXT5", 2, false )]
	[DataRow( "BC7", 20, false )]
	[DataRow( "ATI1N", 27, false )]
	[DataRow( "ATI2N", 21, false )]
	[DataRow( "I8", 3, false )]
	[DataRow( "IA88", 22, false )]
	[DataRow( "BGRA8888", 28, false )]
	[DataRow( "BC7", 20, true )]
	public void TextureSourceRecompilesWithSameDimensionsAndFormat( string format, int formatId, bool generateMips )
	{
		var source = Path.Combine( root, "test.vtex" );
		var originalImage = Path.Combine( root, "original.tga" );
		var rgba = new byte[16 * 16 * 4];
		for ( var i = 0; i < rgba.Length; i += 4 )
		{
			rgba[i] = (byte)(i / 4 % 16 * 16);
			rgba[i + 1] = (byte)(i / 4 / 16 * 16);
			rgba[i + 2] = 128;
			rgba[i + 3] = formatId == 2 ? (byte)(i / 4 % 16 * 16) : (byte)255;
		}
		File.WriteAllBytes( originalImage, TextureExporter.EncodeTga( 16, 16, rgba ) );
		File.WriteAllText( source, TextureExporter.Definition( "original.tga", format, formatId is 21 or 27 ? "linear" : "srgb", 0, generateMips ) );
		File.WriteAllText( source + ".meta", "{\"guid\":\"dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7\"}" );
		Compile( source );
		var path = source + "_c";
		var original = File.ReadAllBytes( path );
		var reference = path + ".reference";
		File.WriteAllBytes( reference, original );
		Compile( source );
		CheckBaseline( reference, path );
		original = File.ReadAllBytes( path );
		File.WriteAllBytes( reference, original );
		File.Delete( source );
		File.Delete( source + ".meta" );
		File.Delete( originalImage );
		Assert.IsNotNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ), "Recovery modified the compiled input." );
		Compile( source );
		var rebuilt = File.ReadAllBytes( path );
		using var container = new ResourceContainer( path );
		var data = container.ReadBlock( "DATA" );
		Assert.AreEqual( 16, (int)BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 20 ) ) );
		Assert.AreEqual( 16, (int)BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 22 ) ) );
		Assert.AreEqual( formatId, (int)data[26] );
		using var before = new ResourceContainer( reference );
		Assert.AreEqual( before.ReadBlock( "DATA" )[27], data[27], "Mip count changed." );
		if ( generateMips )
			Assert.IsTrue( data[27] > 1, "Fixture must actually generate a mip chain." );
		CollectionAssert.AreEqual( before.ReadBlock( "GUID" ), container.ReadBlock( "GUID" ), "Recovered resource GUID changed." );
		var image = source + ".recovered.mip0.tga";
		var exported = File.ReadAllBytes( image );
		TestContext.WriteLine( $"Recovered pixel bytes: {Convert.ToHexString( exported.AsSpan( 18, 16 ) )}; DATA: {Convert.ToHexString( before.ReadBlock( "DATA" ).AsSpan( 0, 40 ) )}" );
		if ( formatId == 4 )
		{
			File.Delete( source );
			File.Delete( source + ".meta" );
			File.Delete( image );
			ResourceExporter.Export( path, root, TextWriter.Null );
			CollectionAssert.AreEqual( exported, File.ReadAllBytes( image ), "Raw recovered pixels changed on recompilation." );
		}
		TestContext.WriteLine( $"{format}: original SHA256={Hash( original )}; rebuilt SHA256={Hash( rebuilt )}; byte-identical={original.AsSpan().SequenceEqual( rebuilt )}" );
		var beforeEdit = container.ReadStreamingData( (uint)container.StreamingSize );
		container.Dispose();
		var edited = new byte[rgba.Length];
		for ( var i = 0; i < edited.Length; i += 4 )
		{
			edited[i] = 255;
			edited[i + 3] = 255;
		}
		File.WriteAllBytes( image, TextureExporter.EncodeTga( 16, 16, edited ) );
		Compile( source );
		using var afterEdit = new ResourceContainer( path );
		Assert.IsFalse( beforeEdit.AsSpan().SequenceEqual( afterEdit.ReadStreamingData( (uint)afterEdit.StreamingSize ) ), "Editing recovered source did not affect the compiled texture." );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void AudioRoundTripPreservesPacketsLoopAndGuid( bool compress )
	{
		var source = Path.Combine( root, "test.wav" );
		using ( var stream = File.Create( source ) )
		using ( var writer = new BinaryWriter( stream, Encoding.UTF8 ) )
		{
			writer.Write( "RIFF"u8 );
			writer.Write( 36 + 48000 * 2 );
			writer.Write( "WAVEfmt "u8 );
			writer.Write( 16 ); writer.Write( (ushort)1 ); writer.Write( (ushort)1 );
			writer.Write( 48000 ); writer.Write( 96000 ); writer.Write( (ushort)2 ); writer.Write( (ushort)16 );
			writer.Write( "data"u8 ); writer.Write( 48000 * 2 );
			for ( var i = 0; i < 48000; i++ )
				writer.Write( (short)(Math.Sin( i * 2 * Math.PI * 440 / 48000 ) * 12000) );
		}
		var guid = Guid.Parse( "dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7" );
		File.WriteAllText( source + ".meta", JsonSerializer.Serialize( new { guid, compress, loop = true, start = 0.25, end = 0.5 } ) );
		Compile( source );
		var path = Path.ChangeExtension( source, ".vsnd_c" );
		var original = File.ReadAllBytes( path );
		var reference = path + ".reference";
		File.WriteAllBytes( reference, original );
		Compile( source );
		CheckBaseline( reference, path );
		original = File.ReadAllBytes( path );
		File.WriteAllBytes( reference, original );
		File.Delete( source );
		File.Delete( source + ".meta" );
		byte[] payload;
		byte[] header;
		using ( var container = new ResourceContainer( path ) )
		{
			payload = container.ReadStreamingData( (uint)container.StreamingSize );
			header = container.ReadBlock( "DATA" );
			TestContext.WriteLine( $"Audio resource version={container.Version}, DATA length={header.Length}" );
		}
		Assert.IsNotNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ) );
		var recoveredSource = Path.ChangeExtension( source, compress ? ".opus" : ".wav" );
		Assert.IsTrue( File.Exists( recoveredSource ) );
		Compile( recoveredSource );
		var rebuilt = File.ReadAllBytes( path );
		using var compiled = new ResourceContainer( path );
		CollectionAssert.AreEqual( payload, compiled.ReadStreamingData( (uint)compiled.StreamingSize ), "Audio packet/PCM bytes changed." );
		var rebuiltHeader = compiled.ReadBlock( "DATA" );
		CollectionAssert.AreEqual( header[..16], rebuiltHeader[..16], "Rate, format, channels, loop start, sample count or duration changed." );
		CollectionAssert.AreEqual( header, rebuiltHeader, "Compiled audio DATA or seek table changed." );
		Assert.AreEqual( 24000, BinaryPrimitives.ReadInt32LittleEndian( rebuiltHeader.AsSpan( 44 ) ) );
		using var before = new ResourceContainer( reference );
		CollectionAssert.AreEqual( before.ReadBlock( "GUID" ), compiled.ReadBlock( "GUID" ), "Recovered resource GUID changed." );
		TestContext.WriteLine( $"Audio compress={compress}: original SHA256={Hash( original )}; rebuilt SHA256={Hash( rebuilt )}; byte-identical={original.AsSpan().SequenceEqual( rebuilt )}" );
	}

	[DataTestMethod]
	[DataRow( "R16", 5 )]
	[DataRow( "RG1616", 6 )]
	[DataRow( "RGBA16161616", 7 )]
	[DataRow( "R16F", 8 )]
	[DataRow( "RG1616F", 9 )]
	[DataRow( "RGBA16161616F", 10 )]
	[DataRow( "R32F", 11 )]
	[DataRow( "RG3232F", 12 )]
	[DataRow( "RGB323232F", 13 )]
	[DataRow( "RGBA32323232F", 14 )]
	[DataRow( "BC6H", 19 )]
	public void HighPrecisionTextureRecompilesWithMeasuredSampleFidelity( string format, int formatId )
	{
		var source = Path.Combine( root, "test.vtex" );
		var pixels = new float[16 * 16 * 4];
		for ( var i = 0; i < pixels.Length; i += 4 )
		{
			pixels[i] = i / 4 % 16 * 0.125f + (formatId == 19 ? 0 : -0.5f);
			pixels[i + 1] = i / 4 / 16 * 0.25f;
			pixels[i + 2] = 4.5f;
			pixels[i + 3] = 0.75f;
		}
		var image = Path.Combine( root, "original.exr" );
		File.WriteAllBytes( image, HdrImage.EncodeExr( 16, 16, pixels ) );
		File.WriteAllText( source, TextureExporter.Definition( "original.exr", format, "linear", 0 ) );
		File.WriteAllText( source + ".meta", "{\"guid\":\"dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7\"}" );
		Compile( source );
		var original = File.ReadAllBytes( source + "_c" );
		byte[] guid;
		using ( var container = new ResourceContainer( source + "_c" ) )
		{
			Assert.AreEqual( formatId, (int)container.ReadBlock( "DATA" )[26] );
			guid = container.ReadBlock( "GUID" );
		}
		File.Delete( source );
		File.Delete( source + ".meta" );
		File.Delete( image );
		Assert.IsNotNull( ResourceExporter.Export( source + "_c", root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( source + "_c" ) );
		var extracted = File.ReadAllBytes( source + ".recovered.mip0.exr" );
		Compile( source );
		using ( var rebuilt = new ResourceContainer( source + "_c" ) )
		{
			var data = rebuilt.ReadBlock( "DATA" );
			Assert.AreEqual( formatId, (int)data[26] );
			Assert.AreEqual( 16, (int)BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 20 ) ) );
			Assert.AreEqual( 16, (int)BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 22 ) ) );
			CollectionAssert.AreEqual( guid, rebuilt.ReadBlock( "GUID" ) );
		}
		var next = Path.Combine( root, "second" );
		Directory.CreateDirectory( next );
		var copy = Path.Combine( next, "test.vtex_c" );
		File.Copy( source + "_c", copy );
		ResourceExporter.Export( copy, root, TextWriter.Null );
		var roundTrip = File.ReadAllBytes( copy[..^2] + ".recovered.mip0.exr" );
		if ( formatId != 19 )
			CollectionAssert.AreEqual( extracted, roundTrip, "Stored floating-point samples changed on recompilation." );
		else
		{
			var maximumError = 0.0f;
			var squaredError = 0.0;
			var firstScanline = extracted.Length - 16 * (8 + 16 * 16);
			for ( var y = 0; y < 16; y++ )
			{
				for ( var sample = 0; sample < 16 * 4; sample++ )
				{
					var offset = firstScanline + y * (8 + 16 * 16) + 8 + sample * sizeof( float );
					var error = BinaryPrimitives.ReadSingleLittleEndian( extracted.AsSpan( offset ) ) - BinaryPrimitives.ReadSingleLittleEndian( roundTrip.AsSpan( offset ) );
					maximumError = Math.Max( maximumError, Math.Abs( error ) );
					squaredError += error * error;
				}
			}
			var rootMeanSquaredError = Math.Sqrt( squaredError / (16 * 16 * 4) );
			TestContext.WriteLine( $"BC6H fixture maximum absolute sample error: {maximumError}; RMS: {rootMeanSquaredError}" );
			Assert.IsTrue( maximumError <= 0.25f && rootMeanSquaredError <= 0.1, "BC6H fixture recompression exceeds its 0.25 maximum / 0.1 RMS sample tolerances." );
		}
		byte[] beforeEdit;
		using ( var before = new ResourceContainer( source + "_c" ) )
			beforeEdit = before.ReadStreamingData( (uint)before.StreamingSize );
		File.WriteAllBytes( source + ".recovered.mip0.exr", HdrImage.EncodeExr( 16, 16, new float[pixels.Length] ) );
		Compile( source );
		using var afterEdit = new ResourceContainer( source + "_c" );
		Assert.IsFalse( beforeEdit.AsSpan().SequenceEqual( afterEdit.ReadStreamingData( (uint)afterEdit.StreamingSize ) ), "Editing HDR pixels had no effect." );
	}

	[DataTestMethod]
	[DataRow( "RGBA8888", 4, false )]
	[DataRow( "RGBA32323232F", 14, false )]
	[DataRow( "BC6H", 19, true )]
	public void CubemapCrossRecompilesWithCorrectFacesAndOrientation( string format, int formatId, bool mips )
	{
		const int size = 16;
		var source = Path.Combine( root, "test.vtex" );
		var pixels = new float[size * size * 12 * 4];
		for ( var y = 0; y < size * 3; y++ )
		{
			for ( var x = 0; x < size * 4; x++ )
			{
				var offset = (y * size * 4 + x) * 4;
				pixels[offset] = x / (size * 4.0f - 1);
				pixels[offset + 1] = y / (size * 3.0f - 1);
				pixels[offset + 2] = (x + y) % 11 / 10.0f;
				pixels[offset + 3] = 1;
			}
		}
		var image = Path.Combine( root, "original.exr" );
		File.WriteAllBytes( image, HdrImage.EncodeExr( size * 4, size * 3, pixels ) );
		File.WriteAllText( source, TextureExporter.Definition( "original.exr", format, "linear", 0, mips, cube: true ) );
		File.WriteAllText( source + ".meta", "{\"guid\":\"dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7\"}" );
		Compile( source );
		var original = File.ReadAllBytes( source + "_c" );
		byte[] payload, guid;
		int mipCount;
		using ( var before = new ResourceContainer( source + "_c" ) )
		{
			payload = before.ReadStreamingData( (uint)before.StreamingSize );
			guid = before.ReadBlock( "GUID" );
			var data = before.ReadBlock( "DATA" );
			mipCount = data[27];
			Assert.AreEqual( 0x10, BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 2 ) ) & 0x10 );
			Assert.AreEqual( formatId, (int)data[26] );
			if ( mips )
				Assert.IsTrue( mipCount > 1, "Fixture must include actual cube mips." );
		}
		File.Delete( source );
		File.Delete( source + ".meta" );
		File.Delete( image );
		Assert.IsNotNull( ResourceExporter.Export( source + "_c", root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( source + "_c" ) );
		for ( var mip = 0; mip < mipCount; mip++ )
			Assert.IsTrue( File.Exists( source + $".recovered.mip{mip}.exr" ) );
		Compile( source );
		using ( var rebuilt = new ResourceContainer( source + "_c" ) )
		{
			var data = rebuilt.ReadBlock( "DATA" );
			Assert.AreEqual( size, (int)BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 20 ) ) );
			Assert.AreEqual( size, (int)BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 22 ) ) );
			Assert.AreEqual( mipCount, (int)data[27] );
			Assert.AreEqual( formatId, (int)data[26] );
			Assert.AreEqual( 0x10, BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( 2 ) ) & 0x10 );
			CollectionAssert.AreEqual( guid, rebuilt.ReadBlock( "GUID" ) );
			if ( formatId != 19 )
				CollectionAssert.AreEqual( payload, rebuilt.ReadStreamingData( (uint)rebuilt.StreamingSize ), "Cube faces or their orientations changed." );
			payload = rebuilt.ReadStreamingData( (uint)rebuilt.StreamingSize );
		}
		File.WriteAllBytes( source + ".recovered.mip0.exr", HdrImage.EncodeExr( size * 4, size * 3, new float[pixels.Length] ) );
		Compile( source );
		using var edited = new ResourceContainer( source + "_c" );
		Assert.IsFalse( payload.AsSpan().SequenceEqual( edited.ReadStreamingData( (uint)edited.StreamingSize ) ), "Editing cube pixels did not affect the compiled payload." );
	}

	[DataTestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void MaterialSourceRecompilesWithRuntimeParameters( bool constantColor )
	{
		var source = Path.Combine( root, "test.vmat" );
		File.WriteAllText( source, """
			"Layer0"
			{
				"shader" "shaders/simple.shader"
				"g_flModelTintAmount" "0.375"
				"g_vColorTint" "[0.25 0.5 0.75 1]"
				"g_bFogEnabled" "0"
				"TextureColor" "[0.125 0.25 0.5 1]"
				"Attributes"
				{
					"face" "1"
					"label" "example"
				}
			}
			""" );
		if ( !constantColor )
		{
			File.WriteAllBytes( Path.Combine( root, "original.tga" ), TextureExporter.EncodeTga( 4, 4, Enumerable.Range( 0, 64 ).Select( i => (byte)(i * 3) ).ToArray() ) );
			File.WriteAllText( source, File.ReadAllText( source ).Replace( "[0.125 0.25 0.5 1]", "original.tga", StringComparison.Ordinal ) );
		}
		File.WriteAllText( source + ".meta", "{\"guid\":\"dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7\"}" );
		Compile( source );
		Compile( source );
		var path = source + "_c";
		byte[] original;
		byte[] guid;
		System.Text.Json.Nodes.JsonObject expected;
		using ( var before = new ResourceContainer( path ) )
		{
			original = File.ReadAllBytes( path );
			guid = before.ReadBlock( "GUID" );
			expected = BinaryKv3.Read( before.ReadBlock( "DATA" ) );
		}
		File.Delete( source );
		File.Delete( source + ".meta" );
		if ( !constantColor )
			File.Delete( Path.Combine( root, "original.tga" ) );
		Assert.IsNotNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ) );
		File.Delete( path );
		Compile( source );
		using var after = new ResourceContainer( path );
		var actual = BinaryKv3.Read( after.ReadBlock( "DATA" ) );
		CollectionAssert.AreEqual( guid, after.ReadBlock( "GUID" ), "Recovered material GUID changed." );
		foreach ( var key in new[] { "m_shaderName", "m_intParams", "m_floatParams", "m_vectorParams", "m_textureParams", "m_intAttributes", "m_floatAttributes", "m_vectorAttributes", "m_textureAttributes", "m_stringAttributes" } )
			Assert.IsTrue( System.Text.Json.Nodes.JsonNode.DeepEquals( expected[key], actual[key] ), $"Material runtime field {key} changed.\nExpected: {expected[key]}\nActual: {actual[key]}" );
		Assert.IsTrue( File.Exists( source + ".runtime.json" ) );
		after.Dispose();
		File.WriteAllText( source, File.ReadAllText( source ).Replace( "\"0.375\"", "\"0.625\"", StringComparison.Ordinal ) );
		Compile( source );
		using var edited = new ResourceContainer( path );
		var editedParameters = BinaryKv3.Read( edited.ReadBlock( "DATA" ) )["m_floatParams"]!.AsArray();
		var tint = editedParameters.Single( item => item!["m_name"]!.GetValue<string>() == "g_flModelTintAmount" );
		Assert.AreEqual( 0.625, tint!["m_flValue"]!.GetValue<double>(), "Editing recovered material did not affect compiled parameters." );
	}

	[TestMethod]
	public void AnimationGraphRecompilesWithOriginalAuthoringNodes()
	{
		var source = Path.Combine( root, "test.vanmgrph" );
		File.WriteAllText( source, """
			<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:animgraph2:version{0f7898b8-5471-45c4-9867-cd9c46bcfdb5} -->
			{
				_class = "CAnimationGraph"
				m_nodeManager =
				{
					_class = "CAnimNodeManager"
					m_nodes =
					[
						{
							key = { m_id = 1 }
							value =
							{
								_class = "CRootAnimNode"
								m_sName = "Recovered root"
								m_vecPosition = [ 124.0, -136.0 ]
								m_nNodeID = { m_id = 1 }
								m_sNote = "Don't lose & < + \"quoted\" text or \\ paths.\nPreserve node layout."
								m_inputConnection =
								{
									m_nodeID = { m_id = 4294967295 }
									m_outputID = { m_id = 4294967295 }
								}
							}
						}
					]
				}
			}
			""" );
		File.WriteAllText( source + ".meta", "{\"guid\":\"dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7\"}" );
		Compile( source );
		var path = source + "_c";
		byte[] original, guid;
		System.Text.Json.Nodes.JsonObject expected;
		using ( var before = new ResourceContainer( path ) )
		{
			original = File.ReadAllBytes( path );
			guid = before.ReadBlock( "GUID" );
			expected = BinaryKv3.Read( before.ReadBlock( "DATA" ) );
		}
		File.Delete( source );
		File.Delete( source + ".meta" );
		Assert.IsNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ) );
		Compile( source );
		using var after = new ResourceContainer( path );
		var actual = BinaryKv3.Read( after.ReadBlock( "DATA" ) );
		Assert.IsTrue( System.Text.Json.Nodes.JsonNode.DeepEquals( expected, actual ), $"Animation graph DATA changed.\n{actual}" );
		CollectionAssert.AreEqual( guid, after.ReadBlock( "GUID" ) );
		after.Dispose();
		File.WriteAllText( source, File.ReadAllText( source ).Replace( "Recovered root", "Edited root", StringComparison.Ordinal ) );
		Compile( source );
		using var edited = new ResourceContainer( path );
		var editedData = BinaryKv3.Read( edited.ReadBlock( "DATA" ) );
		Assert.AreEqual( "Edited root", editedData["m_nodeManager"]!["m_nodes"]![0]!["value"]!["m_sName"]!.GetValue<string>(), "Editing the recovered graph did not affect compiled nodes." );
	}

	[TestMethod]
	public void StaticModelRecompilesWithPositionsUvsTrianglesAndMaterial()
	{
		var source = Path.Combine( root, "test.vmdl" );
		File.WriteAllText( Path.Combine( root, "original.obj" ), """
			mtllib original.mtl
			o asymmetric
			v 2 3 5
			v 13 3 5
			v 2 20 5
			v 2 3 28
			vt 0 0
			vt 1 0
			vt 0 1
			vt 1 1
			usemtl surface
			f 1/1 3/3 2/2
			f 1/1 2/2 4/4
			f 1/1 4/4 3/3
			f 2/2 3/3 4/4
			""" );
		File.WriteAllText( Path.Combine( root, "original.mtl" ), "newmtl surface\nKd 1 1 1\n" );
		File.WriteAllText( source, ModelExporter.Definition( "original.obj", "{ from = \"surface.vmat\" to = \"materials/dev/primary_white.vmat\" }" ) );
		File.WriteAllText( source + ".meta", "{\"guid\":\"dcff6d3a-cf6f-41c0-b236-b6f03bd6a7a7\"}" );
		Compile( source );
		var path = source + "_c";
		byte[] original, guid;
		using ( var before = new ResourceContainer( path ) )
		{
			original = File.ReadAllBytes( path );
			guid = before.ReadBlock( "GUID" );
		}
		File.Delete( source );
		File.Delete( source + ".meta" );
		File.Delete( Path.Combine( root, "original.obj" ) );
		File.Delete( Path.Combine( root, "original.mtl" ) );
		Assert.IsNotNull( ResourceExporter.Export( path, root, TextWriter.Null ) );
		CollectionAssert.AreEqual( original, File.ReadAllBytes( path ) );
		var recovered = File.ReadAllLines( source + ".recovered.obj" );
		foreach ( var position in new[] { "v 2 3 5", "v 13 3 5", "v 2 20 5", "v 2 3 28" } )
			Assert.IsTrue( recovered.Contains( position ), $"Lost model coordinate {position}." );
		Assert.AreEqual( 4, recovered.Count( line => line.StartsWith( "f ", StringComparison.Ordinal ) ) );
		Compile( source );
		using ( var after = new ResourceContainer( path ) )
		{
			CollectionAssert.AreEqual( guid, after.ReadBlock( "GUID" ) );
			var meshes = after.ReadBlocks( "MDAT" );
			Assert.AreEqual( 1, meshes.Count );
			var draw = BinaryKv3.Read( meshes[0] )["m_sceneObjects"]![0]!["m_drawCalls"]![0]!;
			Assert.AreEqual( 12, draw["m_nIndexCount"]!.GetValue<int>() );
			Assert.AreEqual( "materials/dev/primary_white.vmat", draw["m_material"]!.GetValue<string>() );
		}
		var next = Path.Combine( root, "second" );
		Directory.CreateDirectory( next );
		var copied = Path.Combine( next, "test.vmdl_c" );
		File.Copy( path, copied );
		ResourceExporter.Export( copied, root, TextWriter.Null );
		var roundTrip = File.ReadAllLines( copied[..^2] + ".recovered.obj" );
		CollectionAssert.AreEqual( TriangleSignatures( recovered ), TriangleSignatures( roundTrip ), "Triangle positions, winding or UVs changed." );
		var obj = source + ".recovered.obj";
		File.WriteAllText( obj, File.ReadAllText( obj ).Replace( "v 13 3 5", "v 23 3 5", StringComparison.Ordinal ) );
		Compile( source );
		var editedDirectory = Path.Combine( root, "edited" );
		Directory.CreateDirectory( editedDirectory );
		var editedModel = Path.Combine( editedDirectory, "test.vmdl_c" );
		File.Copy( path, editedModel );
		ResourceExporter.Export( editedModel, root, TextWriter.Null );
		Assert.IsTrue( File.ReadAllLines( editedModel[..^2] + ".recovered.obj" ).Contains( "v 23 3 5" ), "Editing recovered OBJ positions did not affect compiled geometry." );
	}

	static string[] TriangleSignatures( string[] obj )
	{
		var positions = obj.Where( line => line.StartsWith( "v ", StringComparison.Ordinal ) ).ToArray();
		var uvs = obj.Where( line => line.StartsWith( "vt ", StringComparison.Ordinal ) ).ToArray();
		return obj.Where( line => line.StartsWith( "f ", StringComparison.Ordinal ) ).Select( face =>
		{
			var vertices = face.Split( ' ' )[1..].Select( item =>
			{
				var indices = item.Split( '/' );
				return positions[int.Parse( indices[0] ) - 1] + ";" + uvs[int.Parse( indices[1] ) - 1];
			} ).ToArray();
			return Enumerable.Range( 0, 3 ).Select( offset => string.Join( "|", Enumerable.Range( 0, 3 ).Select( i => vertices[(i + offset) % 3] ) ) ).Min( StringComparer.Ordinal )!;
		} ).Order( StringComparer.Ordinal ).ToArray();
	}

	[TestMethod]
	public void ModelLodsDoNotOverlapInRecoveredDefaultMesh()
	{
		var source = Path.Combine( root, "test.vmdl" );
		foreach ( var (name, x) in new[] { ("high", 0), ("low", 100) } )
		{
			File.WriteAllText( Path.Combine( root, name + ".obj" ),
				$"mtllib original.mtl\no {name}\nv {x} 3 5\nv {x + 10} 3 5\nv {x} 20 5\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl surface\nf 1/1 3/3 2/2\n" );
		}
		File.WriteAllText( Path.Combine( root, "original.mtl" ), "newmtl surface\nKd 1 1 1\n" );
		File.WriteAllText( source, ModelExporter.Definition( "unused.obj", "" ).Split( '\n' )[0] + """

			{
				rootNode = {
					_class = "RootNode"
					children = [
						{ _class = "LODGroupList" children = [
							{ _class = "LODGroup" switch_threshold = 0.0 meshes = ["high"] },
							{ _class = "LODGroup" switch_threshold = 30.0 meshes = ["low"] }
						] },
						{ _class = "MaterialGroupList" children = [
							{ _class = "DefaultMaterialGroup" use_global_default = true global_default_material = "materials/dev/primary_white.vmat" }
						] },
						{ _class = "RenderMeshList" children = [
							{ _class = "RenderMeshFile" name = "high" filename = "high.obj" import_scale = 1.0 align_origin_x_type = "None" align_origin_y_type = "None" align_origin_z_type = "None" },
							{ _class = "RenderMeshFile" name = "low" filename = "low.obj" import_scale = 1.0 align_origin_x_type = "None" align_origin_y_type = "None" align_origin_z_type = "None" }
						] }
					]
				}
			}
			""" );
		Compile( source );
		using ( var original = new ResourceContainer( source + "_c" ) )
		{
			Assert.AreEqual( 2, original.ReadBlocks( "MDAT" ).Count, "Fixture must actually contain separate LOD meshes." );
			var data = BinaryKv3.Read( original.ReadBlock( "DATA" ) );
			Assert.AreEqual( 1, ModelExporter.DefaultMeshes( data, 2 ).Count );
		}
		File.Delete( source );
		Assert.IsNotNull( ResourceExporter.Export( source + "_c", root, TextWriter.Null ) );
		var obj = File.ReadAllLines( source + ".recovered.obj" );
		Assert.AreEqual( 1, obj.Count( line => line.StartsWith( "f ", StringComparison.Ordinal ) ) );
		Assert.IsTrue( obj.Contains( "v 0 3 5" ) );
		Assert.IsFalse( obj.Contains( "v 100 3 5" ) );
		var lower = File.ReadAllLines( source + ".recovered.lod1.obj" );
		Assert.AreEqual( 1, lower.Count( line => line.StartsWith( "f ", StringComparison.Ordinal ) ) );
		Assert.IsTrue( lower.Contains( "v 100 3 5" ) );
		Assert.IsFalse( lower.Contains( "v 0 3 5" ) );
		Assert.IsTrue( File.Exists( source + ".mesh1.runtime.json" ) );
		Compile( source );
		using var rebuilt = new ResourceContainer( source + "_c" );
		Assert.AreEqual( 2, rebuilt.ReadBlocks( "MDAT" ).Count );
		var actual = BinaryKv3.Read( rebuilt.ReadBlock( "DATA" ) );
		var distances = actual["m_lodGroupSwitchDistances"]!.AsArray();
		Assert.AreEqual( 2, distances.Count );
		Assert.AreEqual( 30.0, distances[1]!.GetValue<double>() );
		rebuilt.Dispose();
		var copy = Path.Combine( root, "second" );
		Directory.CreateDirectory( copy );
		var copied = Path.Combine( copy, "test.vmdl_c" );
		File.Copy( source + "_c", copied );
		ResourceExporter.Export( copied, root, TextWriter.Null );
		CollectionAssert.AreEqual( TriangleSignatures( obj ), TriangleSignatures( File.ReadAllLines( copied[..^2] + ".recovered.obj" ) ) );
		CollectionAssert.AreEqual( TriangleSignatures( lower ), TriangleSignatures( File.ReadAllLines( copied[..^2] + ".recovered.lod1.obj" ) ) );
	}

	void Compile( string source )
	{
		var extension = Path.GetExtension( source );
		var compiled = extension is ".wav" or ".opus" or ".ogg" or ".mp3"
			? Path.ChangeExtension( source, ".vsnd_c" )
			: source + "_c";
		File.Delete( compiled );
		var start = new ProcessStartInfo( Path.Combine( engine, "bin", "win64", "resourcecompiler.exe" ) )
		{
			WorkingDirectory = engine,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach ( var argument in new[] { "-f", "-noassert", "-skiprendersystem", "-searchpaths", $"core|{root}", source } )
			start.ArgumentList.Add( argument );
		using var process = Process.Start( start ) ?? throw new IOException( "Failed to start official resource compiler." );
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		if ( !process.WaitForExit( 90000 ) )
		{
			process.Kill( entireProcessTree: true );
			Assert.Fail( "Official resource compiler timed out." );
		}
		var log = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
		Assert.AreEqual( 0, process.ExitCode, log );
		Assert.IsFalse( log.Contains( "Failed to find compiler", StringComparison.OrdinalIgnoreCase ), log );
		Assert.IsTrue( File.Exists( compiled ), $"Official compiler did not create {compiled}.\n{log}" );
	}

	void CheckBaseline( string reference, string compiled )
	{
		using var first = new ResourceContainer( reference );
		using var second = new ResourceContainer( compiled );
		CollectionAssert.AreEqual( first.ReadBlock( "DATA" ), second.ReadBlock( "DATA" ), "Compiler DATA is not deterministic for this fixture." );
		CollectionAssert.AreEqual( first.ReadStreamingData( (uint)first.StreamingSize ), second.ReadStreamingData( (uint)second.StreamingSize ), "Compiler payload is not deterministic for this fixture." );
		TestContext.WriteLine( $"Baseline whole-file identical: {File.ReadAllBytes( reference ).AsSpan().SequenceEqual( File.ReadAllBytes( compiled ) )}; GUID identical: {first.ReadBlock( "GUID" ).AsSpan().SequenceEqual( second.ReadBlock( "GUID" ) )}" );
	}

	static string Hash( byte[] bytes ) => Convert.ToHexString( SHA256.HashData( bytes ) );
}
