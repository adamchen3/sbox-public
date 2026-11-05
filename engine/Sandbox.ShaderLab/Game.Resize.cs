using NativeEngine;

namespace Sandbox.ShaderLab;

public partial class Game
{
    

    private bool UpdateSwapChainSize( out int width, out int height )
    {
        if ( !Sdl.GetWindowSizeInPixels( WindowHandle, out width, out height ) )
            throw new InvalidOperationException( $"Couldn't get the SDL window size: {Sdl.GetError()}" );

        if ( width < 1 || height < 1 )
            return false;

        if ( width != SwapChainMode.m_DisplayMode.m_nWidth
            || height != SwapChainMode.m_DisplayMode.m_nHeight
            || !g_pRenderDevice.CanRenderToSwapChain( SwapChain ) )
        {
            g_pRenderDevice.Flush();
            g_pRenderDevice.ForceFlushGPU( SwapChain );
            var mode = SwapChainMode;
            mode.m_DisplayMode.m_nWidth = width;
            mode.m_DisplayMode.m_nHeight = height;

            if ( !g_pRenderDevice.UpdateSwapChain( SwapChain, mode ) )
                throw new InvalidOperationException( "Couldn't update the SDL swap chain" );

            SwapChainMode = mode;
        }

        return true;
    }
}
