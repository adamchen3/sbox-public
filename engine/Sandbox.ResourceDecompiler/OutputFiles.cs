namespace Sandbox.ResourceDecompiler;

/// <summary>
/// Stages every dependency before publishing the primary source file. Never replaces existing files.
/// </summary>
internal sealed class OutputFiles( string root, TextWriter log )
{
	readonly string root = Path.GetFullPath( root );
	readonly Dictionary<string, byte[]> files = new( StringComparer.OrdinalIgnoreCase );

	public void Add( string path, byte[] data )
	{
		path = Path.GetFullPath( path );
		var relative = Path.GetRelativePath( root, path );
		if ( Path.IsPathRooted( relative ) || relative == ".." || relative.StartsWith( $"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal ) )
			throw new InvalidDataException( $"An extracted file would escape the input directory: {path}" );

		RejectLinks( path );
		if ( files.TryGetValue( path, out var previous ) )
		{
			if ( !previous.AsSpan().SequenceEqual( data ) )
				throw new InvalidDataException( $"Conflicting extracted data for '{path}'." );
			return;
		}

		files.Add( path, data );
	}

	void RejectLinks( string path )
	{
		if ( File.Exists( path ) && (File.GetAttributes( path ) & FileAttributes.ReparsePoint) != 0 )
			throw new IOException( $"Refusing to write through a file link: {path}" );

		for ( var directory = Path.GetDirectoryName( path ); directory is not null; directory = Path.GetDirectoryName( directory ) )
		{
			if ( Directory.Exists( directory ) && (File.GetAttributes( directory ) & FileAttributes.ReparsePoint) != 0 )
				throw new IOException( $"Refusing to write through a directory link: {directory}" );

			if ( string.Equals( directory, root, StringComparison.OrdinalIgnoreCase ) )
				break;
		}
	}

	public void Commit( string? primaryPath )
	{
		if ( primaryPath is not null && !files.ContainsKey( primaryPath ) )
			throw new InvalidDataException( "The exporter did not produce a primary source file." );

		var staged = new List<(string Temporary, string Destination)>();
		var created = new List<string>();
		try
		{
			foreach ( var (path, data) in files.OrderBy( pair => string.Equals( pair.Key, primaryPath, StringComparison.OrdinalIgnoreCase ) ) )
			{
				if ( File.Exists( path ) )
				{
					if ( string.Equals( path, primaryPath, StringComparison.OrdinalIgnoreCase ) )
						throw new IOException( $"The source file already exists: {path}" );

					if ( !File.ReadAllBytes( path ).AsSpan().SequenceEqual( data ) )
						throw new IOException( $"Existing extracted dependency differs from the recovered data: {path}" );

					log.WriteLine( $"[KEEP] {path}" );
					continue;
				}

				Directory.CreateDirectory( Path.GetDirectoryName( path )! );
				var temporary = path + $".{Guid.NewGuid():N}.decompile-tmp";
				staged.Add( (temporary, path) );
				using var stream = new FileStream( temporary, FileMode.CreateNew, FileAccess.Write );
				stream.Write( data );
			}

			foreach ( var (temporary, destination) in staged )
			{
				File.Move( temporary, destination );
				created.Add( destination );
			}
		}
		catch
		{
			foreach ( var path in created )
				File.Delete( path );
			throw;
		}
		finally
		{
			foreach ( var (temporary, _) in staged )
				File.Delete( temporary );
		}
	}
}
