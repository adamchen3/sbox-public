/// NOTE:
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
/// 如果使用SdlWindow类去创建实例，其构建方法会判断是否在主线程内（引擎级别的标识）运行，如果不是，则会抛异常。
/// 一种解决方法是，参考Sandbox.ApppSystem和Sandbox.Test那样，在Sandbox.System中配置一下运行Sandbox.Demo访问它的internal API，然后调用ThreadSafe.MarkMainThread方法。
/// 另外一种方法，那就直接使用Sdl类提供的原生SDL接口来创建和管理窗口。
/// 

using System.Globalization;
using Editor;
using NativeEngine;
using Sandbox;
using Sandbox.Engine;
using Sandbox.UI;

public class SdlTest3
{

    private SdlWindow _window;

    private CMaterialSystem2AppSystemDict _appSystem;

    public void Run()
    {
        Initialize();

        var windowHandle = Sdl.CreateWindow(
            "SDL Test Window",
            800,
            600,
            Sdl.WindowFlags.Hidden | Sdl.WindowFlags.Resizable | Sdl.WindowFlags.Vulkan );

        _window = new SdlWindow( windowHandle );
        _window.CreateSwapChain( "Test", RenderMultisampleType.RENDER_MULTISAMPLE_NONE, false );

        // TODO: Draw a triangle

        _window.Show();

        while ( RunFrame() ) ;

        NativeEngine.EngineGlobal.SourceEngineShutdown( _appSystem, false );

        if ( _appSystem.IsValid )
        {
            _appSystem.Destroy();
            _appSystem = default;
        }

        Sandbox.Application.Shutdown();
    }

    protected virtual bool RunFrame()
    {
        while ( Sdl.PollEvent( out var sdlEvent ) )
        {
            if ( sdlEvent.Type is Sdl.EventType.Quit or Sdl.EventType.WindowCloseRequested )
                return false;
        }
        NativeEngine.EngineGlobal.SourceEnginePanelAppFrame();
        return !Sandbox.Application.WantsExit;
    }

    protected void Initialize()
    {
        InstallRenderers();

        var gameFolder = FindGameFolder();
        var nativeDllPath = Path.Combine( gameFolder, NetCore.NativeDllPath );
        NetCore.NativeDllPath = nativeDllPath;
        NetCore.InitializeInterop( gameFolder );

        if ( OperatingSystem.IsWindows() )
        {
            var path = System.Environment.GetEnvironmentVariable( "PATH" );
            path = $"{nativeDllPath};{path}";
            System.Environment.SetEnvironmentVariable( "PATH", path );
        }

        InitManagedMinimal();

        // _window = new PanelWindow( "Panel Gallery", new Vector2( 1280, 860 ), null, true );

        // _window.Show();

        _appSystem = CMaterialSystem2AppSystemDict.Create( new MaterialSystem2AppSystemDictCreateInfo() );
        _appSystem.SetModGameSubdir( "core" );
        _appSystem.SetInToolsMode();
        _appSystem.SetSteamAppId( (uint)Sandbox.Application.AppId );

        if ( !NativeEngine.EngineGlobal.SourceEnginePreInit( "-nosteam -novr -panelapp", _appSystem ) )
        {
            throw new Exception( "SourceEnginePreInit failed" );
        }

        WindowInput.Initialize();

        Graphics.Initialize();

        if ( !NativeEngine.EngineGlobal.SourceEnginePanelAppInit( _appSystem, false ) )
        {
            throw new Exception( "SourceEnginePanelAppInit failed" );
        }

        FontManager.Instance.LoadAll( EngineFileSystem.CoreContent );

        Material.Preload();
        WarmRenderLayers();

        // NativeEngine.EngineGlobal.Plat_SetCurrentFrame( 0 );
    }

    protected static void InstallRenderers()
    {
        Sandbox.SceneRenderer.EngineHooks.Install();
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

    void InitManagedMinimal()
    {
        Sandbox.Application.Initialize( dedicated: false, headless: false, toolsMode: true, testMode: false, isRetail: NativeEngine.EngineGlobal.IsRetail() );

        DLLImportResolver.SetupResolvers();
        Sandbox.Tasks.SyncContext.Init();
        ThreadSafe.MarkMainThread();

        // Same as Bootstrap.PreInit - the engine formats and parses in one culture everywhere,
        // so an app that boots its own way rather than through PreInit has to say so too
        if ( CultureInfo.CurrentCulture.Name != "en-US" )
        {
            var culture = CultureInfo.CreateSpecificCulture( "en-US" );

            // Default* covers the thread pool as well, so work that ran off the main thread comes
            // back with numbers the rest of the engine can read
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }

        ThreadPool.SetMinThreads( Environment.ProcessorCount, Environment.ProcessorCount );

        // TaskScheduler.UnobservedTaskException += ( _, args ) => log.Error( args.Exception, "Unobserved task exception" );
        // AppDomain.CurrentDomain.UnhandledException += ( _, args ) => log.Error( args.ExceptionObject as Exception, "AppDomain unhandled exception" );

        // Diagnostics.Logging.Enabled = true;
        // Diagnostics.Logging.OnException = ErrorReporter.ReportException;
        EngineFileSystem.Initialize( Environment.CurrentDirectory );
        EngineFileSystem.InitializeConfigFolder();
        EngineFileSystem.InitializeDataFolder();
        // Phase( "Managed filesystem" );

        // The UI reads files - stylesheets, images - through the context's mount. Core has
        // the engine styles, and this is an editor app so the editor's assets are part of it
        Sandbox.Engine.GlobalContext.Current.FileMount = new AggregateFileSystem();
        Sandbox.FileSystem.Mounted.CreateAndMount( EngineFileSystem.Root, "/core/" );
        Sandbox.FileSystem.Mounted.CreateAndMount( EngineFileSystem.Root, "/addons/editor/assets/" );

        // Engine controls find their [StyleSheet] attributes through the type library
        Game.TypeLibrary = new Sandbox.Internal.TypeLibrary();
        Game.TypeLibrary.AddIntrinsicTypes();
        Game.TypeLibrary.AddAssembly( typeof( Vector3 ).Assembly, false );
        Game.TypeLibrary.AddAssembly( typeof( Sandbox.UI.Panel ).Assembly, false );

        Sandbox.Application.TryLoadVersionInfo( Environment.CurrentDirectory );

        ErrorReporter.Initialize();
    }

    static void WarmRenderLayers()
    {
        Sandbox.Rendering.RenderPipeline.WarmUp();

        // Native's shadow callback reads its convar mid-render, so its compute shader would be made there otherwise
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor( typeof( Sandbox.Rendering.ContactShadows ).TypeHandle );
    }

}