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

using NativeEngine;
using Sandbox;
using Sandbox.Engine;
using System.Runtime.InteropServices;

public class SdlTest4
{
    private CMaterialSystem2AppSystemDict _appSystem;

    RenderDisplayModeUsage GetModeUsage( IntPtr Handle )
    {
        var flags = Sdl.GetWindowFlags( Handle );
        return (flags & Sdl.WindowFlags.Fullscreen) != 0 ? RenderDisplayModeUsage.CooperativeFullscreen
            : (flags & Sdl.WindowFlags.Borderless) != 0 ? RenderDisplayModeUsage.BorderlessWindow : RenderDisplayModeUsage.BorderedWindow;
    }

    public void Run()
    {
        Initialize();

        var window = Sdl.CreateWindow(
            "SDL Test Window",
            800,
            600,
            Sdl.WindowFlags.Hidden | Sdl.WindowFlags.Resizable | Sdl.WindowFlags.Vulkan );

        if ( window == IntPtr.Zero )
            throw new InvalidOperationException( $"Couldn't create the SDL window: {Sdl.GetError()}" );

        var mode = new RenderDeviceInfo_t
        {
            m_DisplayMode = new() { m_nWidth = 800, m_nHeight = 600, m_Format = ImageFormat.RGBA8888 },
            m_nModeUsage = GetModeUsage( window ),
            m_nMultisampleType = RenderMultisampleType.RENDER_MULTISAMPLE_NONE,
            m_bWaitForVSync = 0,
            m_bIsMainWindow = 0
        };

        SwapChainHandle_t swapChain = default;
        RenderBufferHandle_t vertexBuffer = default;
        NativeEngine.VertexLayout layout = default;
        IMaterial material = default;
        try
        {
            swapChain = g_pRenderDevice.CreateSwapChain( window, mode, "Test" );
            if ( swapChain == default )
                throw new InvalidOperationException( "Couldn't create the SDL swap chain" );

            // Each vertex is two float3 values: clip-space position, then RGB color.
            const int stride = 6 * sizeof( float );
            layout = NativeEngine.VertexLayout.Create( "SdlTest4_Triangle", stride );
            layout.Add( "position", 0, (uint)ColorFormat.COLOR_FORMAT_R32G32B32_FLOAT, 0 );
            layout.Add( "color", 0, (uint)ColorFormat.COLOR_FORMAT_R32G32B32_FLOAT, 3 * sizeof( float ) );
            layout.Build();

            vertexBuffer = g_pRenderDevice.CreateGPUBuffer(
                RenderBufferType.RENDER_BUFFER_TYPE_SEMISTATIC,
                new BufferDesc { m_nElementCount = 3, m_nElementSizeInBytes = stride },
                RenderBufferFlags.RENDER_BUFFER_USAGE_VERTEX_BUFFER,
                "SdlTest4_Triangle" );
            if ( vertexBuffer == IntPtr.Zero )
                throw new InvalidOperationException( "Couldn't create the triangle vertex buffer" );

            material = MaterialSystem2.CreateRawMaterial( "SdlTest4_Triangle", "shaders/sdl_triangle.shader", true );
            if ( material.IsNull || !material.IsLoaded() )
                throw new InvalidOperationException( "Couldn't load shaders/sdl_triangle.shader. Compile it with ShaderCompiler first." );

            UploadVertices( vertexBuffer );

            if ( !Sdl.ShowWindow( window ) )
                throw new InvalidOperationException( $"Couldn't show the SDL window: {Sdl.GetError()}" );

            while ( true )
            {
                while ( Sdl.PollEvent( out var sdlEvent ) )
                {
                    if ( sdlEvent.Type is Sdl.EventType.Quit or Sdl.EventType.WindowCloseRequested )
                        return;
                }

                if ( !Sdl.GetWindowSizeInPixels( window, out var width, out var height ) )
                    throw new InvalidOperationException( $"Couldn't get the SDL window size: {Sdl.GetError()}" );
                if ( width < 1 || height < 1 )
                {
                    Thread.Sleep( 10 );
                    continue;
                }

                if ( width != mode.m_DisplayMode.m_nWidth || height != mode.m_DisplayMode.m_nHeight
                    || !g_pRenderDevice.CanRenderToSwapChain( swapChain ) )
                {
                    g_pRenderDevice.Flush();
                    g_pRenderDevice.ForceFlushGPU( swapChain );
                    mode.m_DisplayMode.m_nWidth = width;
                    mode.m_DisplayMode.m_nHeight = height;
                    if ( !g_pRenderDevice.UpdateSwapChain( swapChain, mode ) )
                        throw new InvalidOperationException( "Couldn't update the SDL swap chain" );
                }

                var context = g_pRenderDevice.CreateRenderContext( 0 );
                try
                {
                    context.BindRenderTargets( swapChain, true, false );
                    context.SetViewport( 0, 0, width, height );
                    context.Clear( new Vector4( 0.08f, 0.08f, 0.1f, 1 ), true, false, false );

                    var materialMode = material.GetMode();
                    if ( materialMode.IsNull || !RenderTools.SetRenderState( context, context.GetAttributesPtrForModify(), materialMode, layout, default ) )
                        throw new InvalidOperationException( "Couldn't bind the triangle shader and render state" );
                    if ( !context.BindVertexBuffer( 0, vertexBuffer, 0, stride ) )
                        throw new InvalidOperationException( "Couldn't bind the triangle vertex buffer" );
                    context.Draw( RenderPrimitiveType.RENDER_PRIM_TRIANGLES, 0, 3 );

                    g_pRenderDevice.BeginSubmittingDisplayLists( swapChain );
                    context.Submit();

                    if ( Environment.GetCommandLineArgs().Contains( "--verify-triangle" ) )
                    {
                        VerifyTriangle( swapChain, width, height );
                        return;
                    }
                }
                finally
                {
                    g_pRenderDevice.ReleaseRenderContext( context );
                }

                if ( !g_pRenderDevice.Present( swapChain ) )
                    throw new InvalidOperationException( "Couldn't present the SDL swap chain" );
            }
        }
        finally
        {
            g_pRenderDevice.Flush();
            if ( swapChain != default ) g_pRenderDevice.ForceFlushGPU( swapChain );
            if ( !material.IsNull ) material.DestroyStrongHandle();
            if ( vertexBuffer != IntPtr.Zero ) g_pRenderDevice.DestroyGPUBuffer( vertexBuffer );
            if ( !layout.IsNull )
            {
                layout.Free();
                layout.Destroy();
            }
            if ( swapChain != default ) g_pRenderDevice.DestroySwapChain( swapChain );
            Sdl.DestroyWindow( window );
            EngineGlobal.SourceEngineShutdown( _appSystem, false );
            _appSystem.Destroy();
            _appSystem = default;
        }
    }

    static void VerifyTriangle( SwapChainHandle_t swapChain, int width, int height )
    {
        g_pRenderDevice.ForceFlushGPU( swapChain );
        var color = g_pRenderDevice.GetSwapChainTexture( swapChain, SwapChainBuffer.BufferColor );
        var pixels = Marshal.AllocHGlobal( width * height * 4 );
        try
        {
            var rect = new NativeRect { x = 0, y = 0, w = width, h = height };
            var destination = rect;
            if ( !g_pRenderDevice.ReadTexturePixels( color, ref rect, 0, 0, ref destination, pixels, ImageFormat.RGBA8888, width * 4 ) )
                throw new InvalidOperationException( "Couldn't read back the triangle frame" );

            var center = (height / 2 * width + width / 2) * 4;
            var red = Marshal.ReadByte( pixels, center );
            var green = Marshal.ReadByte( pixels, center + 1 );
            var blue = Marshal.ReadByte( pixels, center + 2 );
            // Swap-chain writes encode the shader's linear output as sRGB.
            if ( Math.Abs( red - 188 ) > 5 || Math.Abs( green - 137 ) > 5 || Math.Abs( blue - 137 ) > 5
                || Math.Abs( Marshal.ReadByte( pixels ) - 80 ) > 3
                || Math.Abs( Marshal.ReadByte( pixels, 1 ) - 80 ) > 3
                || Math.Abs( Marshal.ReadByte( pixels, 2 ) - 89 ) > 3 )
                throw new InvalidOperationException( $"Triangle readback failed: center RGB = ({red}, {green}, {blue})" );

            Console.WriteLine( $"Triangle readback passed: center RGB = ({red}, {green}, {blue}), background RGB = ({Marshal.ReadByte( pixels )}, {Marshal.ReadByte( pixels, 1 )}, {Marshal.ReadByte( pixels, 2 )})" );
        }
        finally
        {
            Marshal.FreeHGlobal( pixels );
            if ( !color.IsNull ) color.DestroyStrongHandle();
        }
    }

    static void UploadVertices( RenderBufferHandle_t vertexBuffer )
    {
        float[] vertices =
        [
             0.0f,  0.5f, 0.5f, 1, 0, 0,
            -0.5f, -0.5f, 0.5f, 0, 1, 0,
             0.5f, -0.5f, 0.5f, 0, 0, 1
        ];
        var byteCount = vertices.Length * sizeof( float );
        var data = Marshal.AllocHGlobal( byteCount );
        try
        {
            Marshal.Copy( vertices, 0, data, vertices.Length );
            var context = g_pRenderDevice.CreateRenderContext( 0 );
            try
            {
                RenderTools.SetGPUBufferData( context, vertexBuffer, data, (uint)byteCount, 0 );
                g_pRenderDevice.BeginSubmittingDisplayLists();
                context.Submit();
                context.ForceFlushGPU();
            }
            finally
            {
                g_pRenderDevice.ReleaseRenderContext( context );
            }
        }
        finally
        {
            Marshal.FreeHGlobal( data );
        }
    }

    protected void Initialize()
    {
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

        _appSystem = CMaterialSystem2AppSystemDict.Create( new MaterialSystem2AppSystemDictCreateInfo() );
        _appSystem.SetModGameSubdir( "core" );

        // 执行了这个之后，g_pRenderDevice就能正常访问了。
        if ( !NativeEngine.EngineGlobal.SourceEnginePreInit( "-nosteam -novr -panelapp", _appSystem ) )
        {
            throw new Exception( "SourceEnginePreInit failed" );
        }

        // if ( !NativeEngine.EngineGlobal.SourceEnginePanelAppInit( _appSystem, false ) )
        // {
        //     throw new Exception( "SourceEnginePanelAppInit failed" );
        // }

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