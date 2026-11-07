using NativeEngine;

using Sandbox.ShaderLab.Effects;

namespace Sandbox.ShaderLab;


public partial class Game
{

    public bool IsRunning { get; private set; }
    private IEffect? _effect;


    public void Initialize()
    {
        InitEngine();

        CreateWindow();
        if ( !Sdl.ShowWindow( WindowHandle ) )
            throw new InvalidOperationException( $"Couldn't show the SDL window: {Sdl.GetError()}" );

        InitRendering();
        InitEffect();
        IsRunning = true;
    }

    private void InitEffect()
    {
        _effect = new UnlitEffect();
        if ( !_effect.Initilize() )
            throw new InvalidOperationException( "Couldn't initialize the effect." );
    }

    public void ProcessInput()
    {
        while ( Sdl.PollEvent( out var sdlEvent ) )
        {
            switch ( sdlEvent.Type )
            {
                case Sdl.EventType.Quit:
                case Sdl.EventType.WindowCloseRequested:
                    IsRunning = false;
                    break;
                case Sdl.EventType.WindowFocusLost:
                    break;
                case Sdl.EventType.KeyDown:
                case Sdl.EventType.KeyUp:
                    break;
            }
        }
    }

    public void Update()
    {

    }

    public void Cleanup()
    {
        IsRunning = false;
        try
        {
            if ( SwapChain != default )
            {
                g_pRenderDevice.Flush();
                if ( SwapChain != default )
                    g_pRenderDevice.ForceFlushGPU( SwapChain );
            }

            _effect?.Cleanup();

            if ( SwapChain != default )
            {
                g_pRenderDevice.DestroySwapChain( SwapChain );
                SwapChain = default;
                SwapChainMode = default;
            }
        }
        finally
        {
            try
            {
                if ( WindowHandle != IntPtr.Zero )
                {
                    Sdl.DestroyWindow( WindowHandle );
                    WindowHandle = IntPtr.Zero;
                }
            }
            finally
            {
                if ( _appSystem.IsValid )
                {
                    try
                    {
                        if ( _enginePreInitStarted )
                        {
                            EngineGlobal.SourceEngineShutdown( _appSystem, false );
                            _enginePreInitStarted = false;
                        }
                    }
                    finally
                    {
                        _appSystem.Destroy();
                        _appSystem = default;
                    }
                }
            }
        }
    }

    public void Render()
    {
        if ( !UpdateSwapChainSize( out var width, out var height ) )
            return;
        _effect?.Render( SwapChain, width, height );
    }

}
