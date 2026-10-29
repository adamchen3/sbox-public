using System.Text;
using System.Text.Json;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

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

		if ( Path.GetExtension( path ).Equals( ".vsnd_c", StringComparison.OrdinalIgnoreCase ) && container.Version == 5 )
		{
			var (extension, data) = SoundExporter.ExtractVersion5( container );
			return WriteSound( container, source, output, extension, data );
		}

		var contentRoot = FindContentRoot( path, root );
		using var loader = new ResourceFileLoader( contentRoot, log );
		using var resource = ResourceFileLoader.ReadResource( path );
		resource.FileName = Path.GetRelativePath( contentRoot, path ).Replace( '\\', '/' );

		if ( resource.ResourceType == ResourceType.Sound )
			return ExportSound( resource, container, source, output );

		var regenerateNormals = NativeExport.PrepareModel( resource );
		var materialExporter = new MaterialExporter();
		using var content = resource.ResourceType switch
		{
			ResourceType.Texture => NativeExport.Texture( resource ),
			ResourceType.Material => materialExporter.Extract( resource, loader ),
			ResourceType.Model or ResourceType.Particle
				or ResourceType.AnimationGraph or ResourceType.PostProcessing or ResourceType.ParticleSnapshot
				=> FileExtract.Extract( resource, loader ),
			_ => throw new NotSupportedException( $"No editable-source exporter for {Path.GetExtension( path )} ({resource.ResourceType})." )
		};

		AddContent( output, content, source, contentRoot );
		output.Commit( source );
		var warnings = new List<string>();
		if ( loader.MissingDependencies )
			warnings.Add( "Some referenced resources are missing; see dependency messages." );
		if ( regenerateNormals || loader.RegenerateNormals )
			warnings.Add( "Mesh geometry was recovered without unsupported packed tangent frames; normals/tangents must be regenerated in ModelDoc." );
		if ( materialExporter.UsedFallback )
			warnings.Add( "Some shader texture input mappings were inferred; check them in the Material Editor." );
		return warnings.Count > 0 ? string.Join( " ", warnings ) : null;
	}

	static void AddContent( OutputFiles output, ContentFile content, string destination, string contentRoot )
	{
		if ( content.Data is not null )
			output.Add( destination, content.Data );

		foreach ( var file in content.SubFiles )
		{
			var bytes = file.Extract?.Invoke() ?? throw new InvalidDataException( $"No extractor for '{file.FileName}'." );
			output.Add( Path.Combine( Path.GetDirectoryName( destination )!, NormalizePath( file.FileName ) ), bytes );
		}

		foreach ( var file in content.AdditionalFiles )
		{
			if ( string.IsNullOrWhiteSpace( file.FileName ) )
				throw new InvalidDataException( "An extracted dependency has no file name." );
			AddContent( output, file, Path.Combine( contentRoot, NormalizePath( file.FileName ) ), contentRoot );
		}
	}

	static string NormalizePath( string path ) => path.Replace( '/', Path.DirectorySeparatorChar ).Replace( '\\', Path.DirectorySeparatorChar );

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

	static string ExportSound( Resource resource, ResourceContainer container, string source, OutputFiles output )
	{
		if ( resource.DataBlock is not Sound sound )
			throw new NotSupportedException( "This sound container has no supported audio data." );

		var extension = FileExtract.GetExtension( resource );
		if ( extension is not ("wav" or "mp3") )
			throw new NotSupportedException( $"Audio export for {sound.SoundType} is not supported." );

		using var audio = sound.GetSoundStream();
		return WriteSound( container, source, output, extension, audio.ToArray() );
	}

	static string WriteSound( ResourceContainer container, string source, OutputFiles output, string extension, byte[] data )
	{
		var audioPath = Path.ChangeExtension( source, extension );
		output.Add( audioPath, data );

		var metadata = new Dictionary<string, JsonElement>();
		foreach ( var (block, key) in new[] { ("LIPS", "visemes"), ("SUBT", "subtitles") } )
		{
			if ( !container.HasBlock( block ) )
				continue;
			using var document = JsonDocument.Parse( ResourceContainer.TrimTerminator( container.ReadBlock( block ) ), JsonOptions );
			metadata.Add( key, document.RootElement.Clone() );
		}

		if ( metadata.Count > 0 )
			output.Add( audioPath + ".meta", JsonSerializer.SerializeToUtf8Bytes( metadata ) );

		output.Commit( null );
		return $"Exported {audioPath}. s&box compiles audio files to .vsnd_c; there is no equivalent editable .vsnd audio source.";
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
