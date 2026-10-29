namespace Sandbox.ResourceDecompiler;

internal static class Program
{
	public static int Main( string[] args )
	{
		if ( args is ["--help"] or ["-h"] )
		{
			PrintUsage();
			return 0;
		}

		if ( args.Length != 1 || !Directory.Exists( args[0] ) )
		{
			PrintUsage();
			Console.Error.WriteLine( "Provide one existing directory." );
			return 2;
		}

		var scanner = new ResourceScanner( Console.Out, Console.Error );
		return scanner.Run( args[0] );
	}

	static void PrintUsage()
	{
		Console.WriteLine( "Usage: Sandbox.ResourceDecompiler <directory>" );
		Console.WriteLine( "Recursively recovers missing source files beside *_c resources without overwriting existing files." );
		Console.WriteLine( "Enabled formats are configured in DecompilerFormats.cs; unlisted formats are skipped." );
		Console.WriteLine( "Audio is exported with its real extension. Shaders without full source can only be partially recovered." );
	}
}
