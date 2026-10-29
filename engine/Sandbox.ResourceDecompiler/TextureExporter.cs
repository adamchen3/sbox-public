using System.Buffers.Binary;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace Sandbox.ResourceDecompiler;

/// <summary>
/// Inverse of VTexWriter's header and smallest-mip-first streaming layout.
/// Native textures additionally carry field-relative extra-data and per-mip compression tables.
/// </summary>
internal static class TextureExporter
{
	const int MaximumMipBytes = 256 * 1024 * 1024;

	public static string Export( ResourceContainer container, string source, string contentRoot, OutputFiles output )
	{
		var data = container.ReadBlock( "DATA" );
		if ( data.Length < 40 || U16( data, 0 ) != 1 )
			throw new NotSupportedException( "Only VTEX internal header version 1 is supported." );
		var flags = U16( data, 2 );
		var width = U16( data, 20 );
		var height = U16( data, 22 );
		var depth = U16( data, 24 );
		var format = data[26];
		var mipCount = data[27];
		var cube = (flags & 0x10) != 0;
		if ( width == 0 || height == 0 || depth != 1 || mipCount == 0 || mipCount > 16 )
			throw new InvalidDataException( "Invalid or unsupported texture dimensions/mip count." );
		if ( (flags & (0x20 | 0x40 | 0x100)) != 0 )
			throw new NotSupportedException( "Volume, array and YCoCg texture reconstruction is not yet verified." );
		if ( cube && width != height )
			throw new InvalidDataException( "Cubemap faces must be square." );
		var diskFormat = format switch
		{
			1 => "DXT1",
			2 => "DXT5",
			3 => "I8",
			4 or 16 or 18 => "RGBA8888",
			15 or 17 => "RGBA8888",
			5 => "R16",
			6 => "RG1616",
			7 => "RGBA16161616",
			8 => "R16F",
			9 => "RG1616F",
			10 => "RGBA16161616F",
			11 => "R32F",
			12 => "RG3232F",
			13 => "RGB323232F",
			14 => "RGBA32323232F",
			19 => "BC6H",
			20 => "BC7",
			21 => "ATI2N",
			22 => "IA88",
			27 => "ATI1N",
			28 => "BGRA8888",
			_ => throw new NotSupportedException( $"VTEX pixel format {format} has no verified independent image export." )
		};
		if ( container.StreamingSize > int.MaxValue )
			throw new NotSupportedException( "Texture streaming payload exceeds the supported size." );
		var stream = container.ReadStreamingData( (uint)container.StreamingSize );
		var extra = ReadExtraData( data );
		var sheetWarning = "";
		if ( extra.TryGetValue( 2, out var sheet ) )
		{
			output.Add( source + ".sheet.bin", data.AsSpan( sheet.Item1, sheet.Item2 ).ToArray() );
			sheetWarning = " Sprite-sheet metadata retained as .sheet.bin; animation sequences are not reauthored. The recovered definition compiles the static atlas only.";
		}
		// Treat recovered samples as stored numeric values. Reapplying the author's unknown
		// gamma transform can change even RGBA8888 pixels on each compile.
		var colorSpace = "linear";
		string baseImage;
		if ( format is 15 or 16 or 17 or 18 )
		{
			if ( cube )
				throw new NotSupportedException( "Embedded-image cubemap layout is not verified." );
			if ( mipCount != 1 )
				throw new InvalidDataException( "Embedded image texture must contain one mip." );
			var png = format is 16 or 18;
			if ( png ? !stream.AsSpan().StartsWith( new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 } )
				: !stream.AsSpan().StartsWith( new byte[] { 255, 216, 255 } ) )
				throw new InvalidDataException( "Texture embedded image signature does not match its format." );
			baseImage = source + (png ? ".recovered.png" : ".recovered.jpg");
			output.Add( baseImage, stream );
		}
		else
		{
			var (method, sizes) = ReadMipSizes( data, extra, mipCount );
			var position = 0;
			var hdr = format is >= 5 and <= 14 or 19;
			var imageExtension = hdr || cube ? ".exr" : ".tga";
			baseImage = source + ".recovered.mip0" + imageExtension;
			for ( var mip = mipCount - 1; mip >= 0; mip-- )
			{
				var w = Math.Max( 1, width >> mip );
				var h = Math.Max( 1, height >> mip );
				var faceBytes = MipSize( w, h, format );
				var expected = checked(faceBytes * (cube ? 6 : 1));
				if ( cube && ((long)w * h * 12 * 16 > MaximumMipBytes || expected > MaximumMipBytes) )
					throw new NotSupportedException( "Decoded cubemap cross exceeds the 256 MiB limit." );
				var stored = sizes is null ? expected : sizes[mip];
				if ( stored <= 0 || stored > expected || stored > stream.Length - position )
					throw new InvalidDataException( $"Invalid stored size for mip {mip}." );
				var packed = stream.AsSpan( position, stored ).ToArray();
				position += stored;
				var pixels = stored == expected ? packed : Decompress( packed, expected, method );
				output.Add( source + $".recovered.mip{mip}" + imageExtension,
					cube ? EncodeCube( pixels, w, faceBytes, format, hdr )
					: hdr ? HdrImage.EncodeExr( w, h, DecodeHdr( pixels, w, h, format ) ) : EncodeTga( w, h, Decode( pixels, w, h, format ) ) );
			}

			if ( position != stream.Length )
				throw new InvalidDataException( "Texture mip sizes do not account for the complete streaming payload." );
		}

		var imagePath = Path.GetRelativePath( contentRoot, baseImage ).Replace( '\\', '/' );
		output.Add( source, Encoding.UTF8.GetBytes( Definition( imagePath, diskFormat, colorSpace, flags, mipCount > 1, cube ) ) );
		return "Independent texture reconstruction: all stored mips were retained as images; authoring color space/filter settings are not fully stored. Recompilation may regenerate mips and recompress pixels. Review before replacing the original." + sheetWarning;
	}

	static Dictionary<uint, (int Offset, int Size)> ReadExtraData( byte[] data )
	{
		var result = new Dictionary<uint, (int, int)>();
		var count = U32( data, 36 );
		if ( count > 64 )
			throw new InvalidDataException( "Texture extra-data count exceeds the supported limit." );
		var table = 32L + I32( data, 32 );
		Range( data, table, count * 12L );
		for ( var i = 0; i < count; i++ )
		{
			var entry = checked((int)table + i * 12);
			var offset = entry + 4L + I32( data, entry + 4 );
			var size = U32( data, entry + 8 );
			Range( data, offset, size );
			if ( !result.TryAdd( U32( data, entry ), ((int)offset, checked((int)size)) ) )
				throw new InvalidDataException( "Duplicate texture extra-data type." );
		}

		return result;
	}

	static (uint Method, int[]? Sizes) ReadMipSizes( byte[] data, Dictionary<uint, (int Offset, int Size)> extra, int count )
	{
		if ( !extra.TryGetValue( 4, out var block ) )
			return (0, null);
		if ( block.Size < 12 )
			throw new InvalidDataException( "Incomplete texture compression metadata." );
		var method = U32( data, block.Offset );
		if ( method > 2 || U32( data, block.Offset + 8 ) != count )
			throw new NotSupportedException( "Unsupported texture compression method or mip table." );
		var offset = block.Offset + 4L + I32( data, block.Offset + 4 );
		Range( data, offset, count * 4L );
		var sizes = new int[count];
		for ( var i = 0; i < count; i++ )
			sizes[i] = checked((int)U32( data, (int)offset + i * 4 ));
		return (method, sizes);
	}

	static byte[] Decompress( byte[] input, int size, uint method )
	{
		var output = new byte[size];
		var actual = method switch
		{
			1 => LZ4Codec.Decode( input, 0, input.Length, output, 0, output.Length ),
			2 => DecompressZstd( input, output ),
			_ => throw new InvalidDataException( "Compressed mip has no supported compression method." )
		};
		if ( actual != size )
			throw new InvalidDataException( "Decompressed mip size differs from its pixel dimensions." );
		return output;
	}

	static int DecompressZstd( byte[] input, byte[] output )
	{
		using var decoder = new Decompressor();
		return decoder.Unwrap( input, output );
	}

	static int MipSize( int width, int height, byte format )
	{
		var size = format switch
		{
			1 or 27 => ((long)width + 3) / 4 * ((height + 3) / 4) * 8,
			2 or 19 or 20 or 21 => ((long)width + 3) / 4 * ((height + 3) / 4) * 16,
			3 => (long)width * height,
			5 or 8 or 22 => (long)width * height * 2,
			7 or 10 or 12 => (long)width * height * 8,
			13 => (long)width * height * 12,
			14 => (long)width * height * 16,
			_ => (long)width * height * 4
		};
		var decodedBytes = format is >= 5 and <= 14 or 19 ? 16 : 4;
		if ( size > MaximumMipBytes || (long)width * height * decodedBytes > MaximumMipBytes )
			throw new NotSupportedException( "Decoded texture mip exceeds the 256 MiB limit." );
		return (int)size;
	}

	static float[] DecodeHdr( byte[] input, int width, int height, byte format )
	{
		var rgba = new float[checked(width * height * 4)];
		if ( format == 19 )
		{
			var colors = new BcDecoder().DecodeRawHdr( input, width, height, CompressionFormat.Bc6U );
			for ( var i = 0; i < colors.Length; i++ )
			{
				rgba[i * 4] = colors[i].r;
				rgba[i * 4 + 1] = colors[i].g;
				rgba[i * 4 + 2] = colors[i].b;
				rgba[i * 4 + 3] = 1;
			}
			return rgba;
		}
		var channels = format switch { 5 or 8 or 11 => 1, 6 or 9 or 12 => 2, 13 => 3, _ => 4 };
		var sixteenBit = format <= 10;
		for ( var i = 0; i < width * height; i++ )
		{
			rgba[i * 4 + 3] = 1;
			for ( var channel = 0; channel < channels; channel++ )
			{
				var offset = (i * channels + channel) * (sixteenBit ? 2 : 4);
				rgba[i * 4 + channel] = format switch
				{
					<= 7 => U16( input, offset ) / 65535.0f,
					<= 10 => (float)BitConverter.UInt16BitsToHalf( U16( input, offset ) ),
					_ => BinaryPrimitives.ReadSingleLittleEndian( input.AsSpan( offset ) )
				};
			}
		}
		return rgba;
	}

	static byte[] EncodeCube( byte[] pixels, int size, int faceBytes, byte format, bool hdr )
	{
		var cross = new float[checked(size * size * 12 * 4)];
		for ( var face = 0; face < 6; face++ )
		{
			var input = pixels.AsSpan( face * faceBytes, faceBytes ).ToArray();
			var rgba = hdr ? DecodeHdr( input, size, size, format ) : Decode( input, size, size, format ).Select( value => value / 255.0f ).ToArray();
			for ( var y = 0; y < size; y++ )
			{
				for ( var x = 0; x < size; x++ )
				{
					// Inverse of the compiler's horizontal-cross face rotations, verified with coordinate-coded pixels.
					var (cx, cy) = face switch
					{
						0 => (2 * size + y, size + x),
						1 => (size - 1 - y, 2 * size - 1 - x),
						2 => (size + x, 2 * size - 1 - y),
						3 => (4 * size - 1 - x, size + y),
						4 => (size + x, size - 1 - y),
						_ => (2 * size - 1 - x, 2 * size + y)
					};
					rgba.AsSpan( (y * size + x) * 4, 4 ).CopyTo( cross.AsSpan( (cy * size * 4 + cx) * 4 ) );
				}
			}
		}
		return HdrImage.EncodeExr( size * 4, size * 3, cross );
	}

	static byte[] Decode( byte[] input, int width, int height, byte format )
	{
		var rgba = new byte[checked(width * height * 4)];
		if ( format is 1 or 2 or 20 or 21 or 27 )
		{
			var compression = format switch
			{
				1 => CompressionFormat.Bc1,
				2 => CompressionFormat.Bc3,
				20 => CompressionFormat.Bc7,
				21 => CompressionFormat.Bc5,
				_ => CompressionFormat.Bc4
			};
			var colors = new BcDecoder().DecodeRaw( input, width, height, compression );
			for ( var i = 0; i < colors.Length; i++ )
			{
				rgba[i * 4] = colors[i].r;
				rgba[i * 4 + 1] = colors[i].g;
				rgba[i * 4 + 2] = colors[i].b;
				rgba[i * 4 + 3] = colors[i].a;
			}
			return rgba;
		}

		for ( var i = 0; i < width * height; i++ )
		{
			var destination = rgba.AsSpan( i * 4, 4 );
			if ( format is 3 or 22 )
			{
				destination[..3].Fill( input[i * (format == 22 ? 2 : 1)] );
				destination[3] = format == 22 ? input[i * 2 + 1] : (byte)255;
			}
			else
			{
				input.AsSpan( i * 4, 4 ).CopyTo( destination );
				if ( format == 28 )
					(destination[0], destination[2]) = (destination[2], destination[0]);
			}
		}
		return rgba;
	}

	internal static byte[] EncodeTga( int width, int height, byte[] rgba )
	{
		if ( width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue || rgba.Length != (long)width * height * 4 )
			throw new InvalidDataException( "Invalid TGA image dimensions or pixel count." );
		var image = new byte[checked(18 + rgba.Length)];
		image[2] = 2;
		BinaryPrimitives.WriteUInt16LittleEndian( image.AsSpan( 12 ), (ushort)width );
		BinaryPrimitives.WriteUInt16LittleEndian( image.AsSpan( 14 ), (ushort)height );
		image[16] = 32;
		image[17] = 0x28;
		for ( var i = 0; i < rgba.Length; i += 4 )
		{
			image[18 + i] = rgba[i + 2];
			image[19 + i] = rgba[i + 1];
			image[20 + i] = rgba[i];
			image[21 + i] = rgba[i + 3];
		}
		return image;
	}

	internal static string Definition( string image, string format, string colorSpace, ushort flags, bool generateMips = false, bool cube = false ) => $$"""
		<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
		"CDmeVtex"
		{
			"m_inputTextureArray" "element_array"
			[
				"CDmeInputTexture"
				{
					"m_name" "string" "0"
					"m_fileName" "string" "{{Escape( image )}}"
					"m_colorSpace" "string" "{{colorSpace}}"
					"m_typeString" "string" "{{(cube ? "CUBE" : "2D")}}"
				}
			]
			"m_outputTypeString" "string" "{{(cube ? "CUBE" : "2D")}}"
			"m_outputFormat" "string" "{{format}}"
			"m_vClamp" "vector3" "{{((flags & 1) != 0 ? 1 : 0)}} {{((flags & 2) != 0 ? 1 : 0)}} {{((flags & 4) != 0 ? 1 : 0)}}"
			"m_textureOutputChannelArray" "element_array"
			[
				"CDmeTextureOutputChannel"
				{
					"m_inputTextureArray" "string_array" [ "0" ]
					"m_srcChannels" "string" "rgba"
					"m_dstChannels" "string" "rgba"
					"m_mipAlgorithm" "CDmeImageProcessor"
					{
						"m_algorithm" "string" "{{(generateMips ? "Box" : "")}}"
						"m_stringArg" "string" ""
						"m_vFloat4Arg" "vector4" "0 0 0 0"
					}
					"m_outputColorSpace" "string" "{{colorSpace}}"
				}
			]
		}
		""";

	static string Escape( string value ) => value.Replace( "\\", "\\\\", StringComparison.Ordinal ).Replace( "\"", "\\\"", StringComparison.Ordinal );
	static ushort U16( byte[] data, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( offset ) );
	static uint U32( byte[] data, int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( offset ) );
	static int I32( byte[] data, int offset ) => BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( offset ) );
	static void Range( byte[] data, long offset, long size )
	{
		if ( offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset )
			throw new InvalidDataException( "Texture metadata points outside its DATA block." );
	}
}
