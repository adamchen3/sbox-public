///
/// 首先第一问题是：会出现engine2.dll加载失败的问题。
/// 如果直接写一个普通程序，不引用Sandbox.Engine，那么就能正常加载，一旦引用了，就会失败。
/// AI给出的说法是，Sandbox.Engine引用了Sandbox.System，而Sandbox.System又引用了Sentry包，
/// 这个包会执行自己的Source Generator，会生成一个初始化方法，其作用是加载Sentry.dll，在Main方法前执行。
/// 此时，加载的Sentry.dll位于exe同目录中的dll。
/// 后面进行手动加载时，指定了engine2.dll的路径，而engine2.dll的依赖链是：engine2.dll → tier0.dll → sentry.dll。
/// 目前猜测，game/bin/win64目录下的sentry.dll才是这个依赖链需要的dll。
/// 由于加载engine2.dll时，前面已经加载了一个同名的Sentry.dll，所以会想去复用，但是又不是想要的那个版本，所以导致加载失败了。
/// 所以，需要确保加载正确的sentry.dll，一种方法就是，不要让Sentry在Main方法前自动加载，
/// 可以使用<SentryDisableSourceGenerator>true</SentryDisableSourceGenerator>来禁止Source Generator的执行，
/// 这是AI参考了ShaderCompiler.csproj的做法给出的解决方法。
/// 有个疑问就是，engine2.dll的加载是给了明确的路径的，但是tier0.dll和sentry.dll的加载路径是如何确定的呢？
/// 优先在engine2.dll所在目录下加载其依赖的dll。
/// 

using NativeEngine;
using Sandbox.Engine;

public static class SdlTest
{
    public static void Run()
    {
        var gameFolder = FindGameFolder();
        var nativeDllPath = Path.Combine( gameFolder, NetCore.NativeDllPath );
        NetCore.NativeDllPath = nativeDllPath;
        const string baseName = "engine2";

        var nativeDllName = Sandbox.Interop.GetNativeLibraryName( baseName );
        Console.WriteLine( $"Native DLL name: {nativeDllName}" );

        NetCore.InitializeInterop( gameFolder );

        using var window = new SdlWindow( Sdl.CreateWindow(
            "SDL Test Window",
            800,
            600,
            Sdl.WindowFlags.Hidden | Sdl.WindowFlags.Resizable | Sdl.WindowFlags.Vulkan ) );

        window.Show();

        while ( true )
        {
            while ( Sdl.PollEvent( out var sdlEvent ) )
            {
                if ( sdlEvent.Type is Sdl.EventType.Quit or Sdl.EventType.WindowCloseRequested )
                    return;
            }

            Thread.Sleep( 16 );
        }
    }

    static string FindGameFolder()
    {
        var searchPaths = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach ( var searchPath in searchPaths )
        {
            for ( var directory = new DirectoryInfo( searchPath ); directory is not null; directory = directory.Parent )
            {
                foreach ( var candidate in new[] { directory.FullName, Path.Combine( directory.FullName, "game" ) } )
                {
                    var engineDll = Path.Combine( candidate, NetCore.NativeDllPath, "engine2.dll" );
                    if ( File.Exists( engineDll ) )
                        return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException( "Couldn't find game/bin/win64/engine2.dll. Run the demo from the repository or game folder." );
    }
}