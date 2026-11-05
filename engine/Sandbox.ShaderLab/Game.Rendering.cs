using System;
using NativeEngine;

namespace Sandbox.ShaderLab;

public partial class Game
{

    private IRenderContext RenderContext { get; set; }
    private SwapChainHandle_t SwapChain { get; set; }
    private RenderDeviceInfo_t SwapChainMode { get; set; }

    private void InitRendering()
    {
        if ( WindowHandle == IntPtr.Zero )
            throw new InvalidOperationException( "Window has not been created." );

        if ( !Sdl.GetWindowSizeInPixels( WindowHandle, out var width, out var height ) )
            throw new InvalidOperationException( $"Couldn't get the SDL window size: {Sdl.GetError()}" );

        if ( width < 1 || height < 1 )
            throw new InvalidOperationException( "The SDL window has an invalid size." );

        RenderContext = g_pRenderDevice.CreateRenderContext( 0 );

        SwapChainMode = new RenderDeviceInfo_t
        {
            m_DisplayMode = new() { m_nWidth = width, m_nHeight = height, m_Format = ImageFormat.RGBA8888 },
            m_nModeUsage = GetModeUsage( WindowHandle ),
            m_nMultisampleType = RenderMultisampleType.RENDER_MULTISAMPLE_NONE,
            m_bWaitForVSync = 0,
            m_bIsMainWindow = 0
        };

        SwapChain = g_pRenderDevice.CreateSwapChain( WindowHandle, SwapChainMode, "Test" );

        if ( SwapChain == default )
            throw new InvalidOperationException( "Couldn't create the SDL swap chain" );
    }
}
