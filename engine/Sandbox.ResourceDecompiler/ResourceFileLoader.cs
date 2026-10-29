using System.Buffers.Binary;
using System.Text;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;

namespace Sandbox.ResourceDecompiler;

internal sealed class ResourceFileLoader( string contentRoot, TextWriter log ) : IFileLoader, IDisposable
{
	readonly List<Resource> resources = [];
	public bool MissingDependencies { get; private set; }
	public bool RegenerateNormals { get; private set; }

	public Resource? LoadFile( string file )
	{
		var path = Resolve( file );
		if ( path is null )
		{
			MissingDependencies = true;
			log.WriteLine( $"[MISSING DEPENDENCY] {file}" );
			return null;
		}

		var resource = ReadResource( path );
		resource.FileName = file;
		resources.Add( resource );
		RegenerateNormals |= NativeExport.PrepareModel( resource );
		return resource;
	}

	public Resource? LoadFileCompiled( string file ) => LoadFile( file + "_c" );

	public ShaderCollection? LoadShader( string shaderName )
	{
		log.WriteLine( $"[SHADER MAPPING] Valve shader metadata unavailable for '{shaderName}'; using standard texture mappings." );
		return null;
	}

	public Stream? GetFileStream( string file )
	{
		var path = Resolve( file );
		if ( path is not null )
			return File.OpenRead( path );

		MissingDependencies = true;
		log.WriteLine( $"[MISSING DEPENDENCY] {file}" );
		return null;
	}

	string? Resolve( string file )
	{
		var path = Path.GetFullPath( Path.Combine( contentRoot, file.Replace( '/', Path.DirectorySeparatorChar ) ) );
		var relative = Path.GetRelativePath( contentRoot, path );
		if ( Path.IsPathRooted( relative ) || relative == ".." || relative.StartsWith( $"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal ) )
			throw new InvalidDataException( $"Dependency path escapes the content directory: {file}" );

		return File.Exists( path ) ? path : null;
	}

	public static Resource ReadResource( string path )
	{
		using var container = new ResourceContainer( path );
		var data = File.ReadAllBytes( path );
		var table = 8 + BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( 8 ) );
		var count = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( 12 ) );
		var extensions = new List<(int Index, uint Id, uint Offset, byte[] Data)>();
		for ( var i = 0; i < count; i++ )
		{
			var source = table + i * 12;
			var name = Encoding.ASCII.GetString( data, source, 4 );
			var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( source + 8 ) ));
			if ( length == 0 || name is "GUID" or "LIPS" or "SUBT" or "DBLO" )
			{
				var offset = checked((uint)(source + 4L + BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( source + 4 ) )));
				extensions.Add( (i, BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( source ) ), offset, data.AsSpan( (int)offset, length ).ToArray()) );
				// VRF skips zero-sized entries. Preserve every payload address and the streaming boundary.
				BinaryPrimitives.WriteUInt32LittleEndian( data.AsSpan( source + 8 ), 0 );
			}
		}

		var stream = new MemoryStream( data, writable: false );
		var resource = new Resource { FileName = path };
		try
		{
			resource.Read( stream );
			// CTRL mesh/animation references use original block indices, including extension blocks.
			foreach ( var extension in extensions )
				resource.Blocks.Insert( extension.Index, new ExtensionBlock( extension.Id, extension.Data )
				{
					Resource = resource,
					Offset = extension.Offset,
					Size = (uint)extension.Data.Length
				} );
			return resource;
		}
		catch
		{
			resource.Dispose();
			stream.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		foreach ( var resource in resources )
			resource.Dispose();
	}

	sealed class ExtensionBlock( uint id, byte[] data ) : Block
	{
		public override BlockType Type => (BlockType)id;
		public override void Read( BinaryReader reader ) => throw new NotSupportedException( "Extension block is already loaded." );
		public override void WriteText( IndentedTextWriter writer ) => writer.Write( Convert.ToHexString( data ) );
		public override void Serialize( Stream stream ) => stream.Write( data );
	}
}
