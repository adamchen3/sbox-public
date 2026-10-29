namespace Sandbox.ResourceDecompiler;

public sealed class ResourceScanner( TextWriter output, TextWriter error, IEnumerable<string>? enabledFormats = null )
{
	readonly HashSet<string> enabledFormats = new( enabledFormats ?? DecompilerFormats.Enabled, StringComparer.OrdinalIgnoreCase );

	public int Run( string directory )
	{
		var root = Path.GetFullPath( directory );
		var pending = new Stack<string>();
		pending.Push( root );
		var recovered = 0;
		var skipped = 0;
		var disabled = 0;
		var partial = 0;
		var failed = 0;

		while ( pending.TryPop( out var current ) )
		{
			string[] entries;
			try
			{
				entries = Directory.GetFileSystemEntries( current );
			}
			catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
			{
				error.WriteLine( $"[FAILED] {current}: {exception.Message}" );
				failed++;
				continue;
			}

			foreach ( var path in entries.Order( StringComparer.OrdinalIgnoreCase ) )
			{
				try
				{
					var attributes = File.GetAttributes( path );
					if ( (attributes & FileAttributes.ReparsePoint) != 0 )
					{
						output.WriteLine( $"[SKIP LINK] {path}" );
						continue;
					}

					if ( (attributes & FileAttributes.Directory) != 0 )
					{
						pending.Push( path );
						continue;
					}

					var extension = Path.GetExtension( path );
					if ( !extension.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) )
						continue;

					if ( !enabledFormats.Contains( extension ) )
					{
						output.WriteLine( $"[SKIP FORMAT] {path}: format is not enabled." );
						disabled++;
						continue;
					}

					if ( File.Exists( path[..^2] ) )
					{
						output.WriteLine( $"[SKIP] {path}: source exists." );
						skipped++;
						continue;
					}

					var warning = ResourceExporter.Export( path, root, output );
					if ( warning is null )
					{
						output.WriteLine( $"[RECOVERED] {path[..^2]}" );
						recovered++;
					}
					else
					{
						error.WriteLine( $"[PARTIAL] {path}: {warning}" );
						partial++;
					}
				}
				catch ( Exception exception )
				{
					// A corrupt resource or an exporter failure must not stop recovery of other files.
					error.WriteLine( $"[FAILED] {path}: {exception.GetType().Name}: {exception.Message}" );
					failed++;
				}
			}
		}

		output.WriteLine( $"Recovered: {recovered}; skipped existing: {skipped}; skipped format: {disabled}; partial: {partial}; failed/unsupported: {failed}." );
		return failed == 0 && partial == 0 ? 0 : 1;
	}
}
