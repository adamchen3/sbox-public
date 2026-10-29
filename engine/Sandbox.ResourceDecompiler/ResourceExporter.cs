using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Sandbox.ResourceDecompiler;

internal static class ResourceExporter
{
	static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 512 };

	public static string? Export( string path, string root, TextWriter log )
	{
		var source = path[..^2];
		var output = new OutputFiles( root, log );
		using var container = new ResourceContainer( path );

		if ( container.HasJsonData() )
		{
			var json = ResourceContainer.TrimTerminator( container.ReadBlock( "DATA" ) );
			using var document = JsonDocument.Parse( json, JsonOptions );
			if ( document.RootElement.ValueKind != JsonValueKind.Object )
				throw new InvalidDataException( "Expected a GameResource JSON object." );
			if ( !container.HasBlock( "DBLO" ) && HasBlobReference( document.RootElement ) )
				throw new InvalidDataException( "Resource JSON references blob data, but its DBLO block is missing." );

			output.Add( source, json.ToArray() );
			if ( container.HasBlock( "DBLO" ) )
			{
				var blob = container.ReadBlock( "DBLO" );
				var blobPath = source + "_d";
				if ( File.Exists( blobPath ) && !File.ReadAllBytes( blobPath ).AsSpan().SequenceEqual( blob ) )
					throw new IOException( $"Existing blob data differs from the compiled resource: {blobPath}" );
				output.Add( blobPath, blob );
			}
			output.Commit( source );
			return null;
		}

		if ( Path.GetExtension( path ).Equals( ".shader_c", StringComparison.OrdinalIgnoreCase ) )
			return ExportShader( container, source, output );

		if ( Path.GetExtension( path ).Equals( ".vsnd_c", StringComparison.OrdinalIgnoreCase ) && container.Version is 4 or 5 )
		{
			var (extension, data) = SoundExporter.Extract( container );
			return WriteSound( container, source, output, extension, data );
		}

		if ( Path.GetExtension( path ).Equals( ".vtex_c", StringComparison.OrdinalIgnoreCase ) )
		{
			var warning = TextureExporter.Export( container, source, FindContentRoot( path, root ), output );
			var metadata = ReadGuid( container );
			if ( metadata.Count > 0 )
				output.Add( source + ".meta", JsonSerializer.SerializeToUtf8Bytes( metadata ) );
			output.Commit( source );
			return warning;
		}

		if ( Path.GetExtension( path ).Equals( ".vmat_c", StringComparison.OrdinalIgnoreCase ) )
		{
			var warning = MaterialExporter.Export( container, source, output );
			var metadata = ReadGuid( container );
			if ( metadata.Count > 0 )
				output.Add( source + ".meta", JsonSerializer.SerializeToUtf8Bytes( metadata ) );
			output.Commit( source );
			return warning;
		}

		if ( Path.GetExtension( path ).Equals( ".vanmgrph_c", StringComparison.OrdinalIgnoreCase ) )
		{
			output.Add( source, Kv3Source.AnimationGraph( container.ReadBlock( "DATA" ) ) );
			var metadata = ReadGuid( container );
			if ( metadata.Count > 0 )
				output.Add( source + ".meta", JsonSerializer.SerializeToUtf8Bytes( metadata ) );
			output.Commit( source );
			return null;
		}

		if ( Path.GetExtension( path ).Equals( ".vmdl_c", StringComparison.OrdinalIgnoreCase ) )
		{
			var (primary, warning) = ModelExporter.Export( container, source, FindContentRoot( path, root ), output );
			var metadata = ReadGuid( container );
			if ( primary is not null && metadata.Count > 0 )
				output.Add( source + ".meta", JsonSerializer.SerializeToUtf8Bytes( metadata ) );
			output.Commit( primary );
			return warning;
		}

		if ( new[] { ".vpcf_c", ".vpost_c", ".vsnap_c" }.Contains( Path.GetExtension( path ), StringComparer.OrdinalIgnoreCase ) )
			return RuntimeDataExporter.Export( container, source, output );

		throw new NotSupportedException( $"No independently verified source reconstruction for {Path.GetExtension( path )} (resource version {container.Version}). VRF fallback is disabled; no placeholder source was written." );
	}

	static bool HasBlobReference( JsonElement element )
	{
		return element.ValueKind switch
		{
			JsonValueKind.Object => element.TryGetProperty( "$blob", out _ ) || element.EnumerateObject().Any( property => HasBlobReference( property.Value ) ),
			JsonValueKind.Array => element.EnumerateArray().Any( HasBlobReference ),
			_ => false
		};
	}

	static string FindContentRoot( string path, string fallback )
	{
		for ( var directory = Directory.GetParent( path ); directory is not null; directory = directory.Parent )
		{
			if ( directory.Name.Equals( "Assets", StringComparison.OrdinalIgnoreCase ) )
				return directory.FullName;
		}

		return fallback;
	}

	static string WriteSound( ResourceContainer container, string source, OutputFiles output, string extension, byte[] data )
	{
		var audioPath = Path.ChangeExtension( source, extension );
		output.Add( audioPath, data );

		var metadata = ReadGuid( container );
		var header = container.ReadBlock( "DATA" );
		var rate = BinaryPrimitives.ReadUInt16LittleEndian( header );
		var loopStart = BinaryPrimitives.ReadInt32LittleEndian( header.AsSpan( 4 ) );
		var loopEnd = BinaryPrimitives.ReadInt32LittleEndian( header.AsSpan( 44 ) );
		var samples = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 8 ) );
		if ( loopEnd < 0 || loopEnd > samples || (loopEnd > 0 && (loopStart < 0 || loopStart >= loopEnd)) )
			throw new InvalidDataException( "Invalid compiled audio loop boundaries." );
		metadata.Add( "loop", JsonSerializer.SerializeToElement( loopEnd > 0 ) );
		if ( loopEnd > 0 )
		{
			metadata.Add( "start", JsonSerializer.SerializeToElement( (double)loopStart / rate ) );
			metadata.Add( "end", JsonSerializer.SerializeToElement( (double)loopEnd / rate ) );
		}
		if ( header[2] is 0 or 1 )
			metadata.Add( "compress", JsonSerializer.SerializeToElement( false ) );
		foreach ( var (block, key) in new[] { ("LIPS", "visemes"), ("SUBT", "subtitles") } )
		{
			if ( !container.HasBlock( block ) )
				continue;
			using var document = JsonDocument.Parse( ResourceContainer.TrimTerminator( container.ReadBlock( block ) ), JsonOptions );
			metadata.Add( key, document.RootElement.Clone() );
		}

		output.Add( audioPath + ".meta", JsonSerializer.SerializeToUtf8Bytes( metadata ) );

		output.Commit( null );
		return $"Exported {audioPath}. s&box compiles audio files to .vsnd_c; there is no equivalent editable .vsnd audio source.";
	}

	static Dictionary<string, JsonElement> ReadGuid( ResourceContainer container )
	{
		var metadata = new Dictionary<string, JsonElement>();
		if ( !container.HasBlock( "GUID" ) )
			return metadata;
		var bytes = container.ReadBlock( "GUID" );
		if ( bytes.Length != 16 )
			throw new InvalidDataException( "Invalid resource GUID block." );
		metadata.Add( "guid", JsonSerializer.SerializeToElement( new Guid( bytes ).ToString() ) );
		return metadata;
	}

	static string ExportShader( ResourceContainer container, string source, OutputFiles output )
	{
		if ( !container.HasBlock( "HLSL" ) )
			throw new NotSupportedException( "Shader has no embedded HLSL source. Compiled GPU bytecode cannot restore the original .shader." );

		var data = ResourceContainer.TrimTerminator( container.ReadBlock( "HLSL" ) );
		using var document = JsonDocument.Parse( data, JsonOptions );
		if ( !document.RootElement.TryGetProperty( "Programs", out var programs ) || programs.ValueKind != JsonValueKind.Object )
			throw new InvalidDataException( "Invalid embedded shader program source." );

		var programCount = 0;
		foreach ( var program in programs.EnumerateObject() )
		{
			if ( !program.Name.StartsWith( "VFX_PROGRAM_", StringComparison.Ordinal )
				|| program.Name.Any( character => !char.IsAsciiLetterOrDigit( character ) && character != '_' )
				|| program.Value.ValueKind != JsonValueKind.String )
				throw new InvalidDataException( $"Invalid shader program: {program.Name}" );

			var text = program.Value.GetString()!;
			output.Add( source + $".{program.Name}.hlsl", Encoding.UTF8.GetBytes( text ) );
			programCount++;
		}

		if ( programCount == 0 )
			throw new InvalidDataException( "Embedded shader source contains no programs." );

		output.Add( source + ".hlsl.json", data.ToArray() );
		output.Commit( null );
		return "Exported embedded per-program HLSL and .hlsl.json. HEADER/MODES/FEATURES and unmasked source are not retained; no fake .shader was created.";
	}
}
