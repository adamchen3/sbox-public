
using Sandbox;
using Sandbox.Engine;

public static class LoaderTest
{
    public static async Task Run()
    {
        const string org = "cwg";
        const string package = "2048";
        var packageIdent = Package.FormatIdent( org, package );

#pragma warning disable CA2000 // Backend retains ownership of its handler
        Backend.Initialize( new ForwardingHandler() );
#pragma warning restore CA2000

        try
        {
            var Pkg = await Package.Fetch( packageIdent, false );

            using var packageLoader = new PackageLoader( "LoaderTest", typeof( IGameInstanceDll ).Assembly );

            using var enroller = packageLoader.CreateEnroller( "LoaderTest" );

            if ( !enroller.LoadPackage( packageIdent ) )
            {
                throw new InvalidOperationException( $"Failed to load package '{packageIdent}'." );
            }

            var assemblyName = $"package.{org}.{package}";
            var loaded = enroller.FindAssembly( Pkg, assemblyName )
                ?? throw new InvalidOperationException( $"Assembly '{assemblyName}' was not loaded." );

            if ( loaded.CompiledAssemblyBytes is null )
            {
                throw new InvalidOperationException( $"Assembly '{assemblyName}' has no compiled DLL bytes." );
            }

            if ( loaded.CodeArchiveBytes is null )
            {
                throw new InvalidOperationException( $"Assembly '{assemblyName}' has no code archive bytes." );
            }

            var dllPath = Path.GetFullPath( $"Package.{org}.{package}.dll" );
            var archivePath = Path.GetFullPath( $"Package.{org}.{package}.cll" );

            await File.WriteAllBytesAsync( dllPath, loaded.CompiledAssemblyBytes );
            await File.WriteAllBytesAsync( archivePath, loaded.CodeArchiveBytes );

            Console.WriteLine( $"Saved compiled assembly to '{dllPath}'." );
            Console.WriteLine( $"Saved code archive to '{archivePath}'." );
        }
        finally
        {
        }
    }

    private sealed class ForwardingHandler : DelegatingHandler
    {
#pragma warning disable CA2000 // The delegating handler owns and disposes its inner handler.
        public ForwardingHandler() : base( new HttpClientHandler() )
        {
        }
#pragma warning restore CA2000
    }
}