using System;
using System.Diagnostics;
using NativeEngine;
using Sandbox;
using Sandbox.Engine;
using System.Runtime.InteropServices;

namespace Sandbox.ShaderLab;


public partial class Game
{
    private const int ScancodeRight = 79;
    private const int ScancodeLeft = 80;
    private const int ScancodeDown = 81;
    private const int ScancodeUp = 82;
    private const int ScancodeLeftShift = 225;
    private const int ScancodeRightShift = 229;
    private const float ColorChangeSpeed = 0.5f;

    public bool IsRunning { get; private set; }

    private Vector4 ClearColor = new Vector4( 1.0f, 0.08f, 0.1f, 1f );
    private bool UpPressed;
    private bool RightPressed;
    private bool DownPressed;
    private bool LeftPressed;
    private bool LeftShiftPressed;
    private bool RightShiftPressed;
    private long LastUpdateTimestamp;


    public void Initialize()
    {
        InitEngine();

        CreateWindow();
        if ( !Sdl.ShowWindow( WindowHandle ) )
            throw new InvalidOperationException( $"Couldn't show the SDL window: {Sdl.GetError()}" );

        InitRendering();
        LastUpdateTimestamp = Stopwatch.GetTimestamp();
        IsRunning = true;
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
                    ClearPressedKeys();
                    break;
                case Sdl.EventType.KeyDown:
                case Sdl.EventType.KeyUp:
                    SetKeyPressed( sdlEvent.Key.Scancode, sdlEvent.Type == Sdl.EventType.KeyDown );
                    break;
            }
        }
    }

    public void Update()
    {
        var now = Stopwatch.GetTimestamp();
        var deltaTime = (float)(now - LastUpdateTimestamp) / Stopwatch.Frequency;
        LastUpdateTimestamp = now;

        var amount = ColorChangeSpeed * deltaTime;
        var shiftPressed = LeftShiftPressed || RightShiftPressed;
        ClearColor = new Vector4(
            AdjustChannel( ClearColor.x, UpPressed, shiftPressed, amount ),
            AdjustChannel( ClearColor.y, RightPressed, shiftPressed, amount ),
            AdjustChannel( ClearColor.z, DownPressed, shiftPressed, amount ),
            AdjustChannel( ClearColor.w, LeftPressed, shiftPressed, amount ) );
    }

    public void Cleanup()
    {
    }

    public void Render()
    {
        if ( !UpdateSwapChainSize( out var width, out var height ) )
            return;

        RenderContext.BindRenderTargets( SwapChain, true, false );
        RenderContext.SetViewport( 0, 0, width, height );
        RenderContext.Clear( new Vector4( ClearColor ), true, false, false );

        g_pRenderDevice.BeginSubmittingDisplayLists( SwapChain );
        RenderContext.Submit();

        if ( !g_pRenderDevice.Present( SwapChain ) )
            throw new InvalidOperationException( "Couldn't present the SDL swap chain" );
    }

    private void SetKeyPressed( int scancode, bool pressed )
    {
        switch ( scancode )
        {
            case ScancodeUp: UpPressed = pressed; break;
            case ScancodeRight: RightPressed = pressed; break;
            case ScancodeDown: DownPressed = pressed; break;
            case ScancodeLeft: LeftPressed = pressed; break;
            case ScancodeLeftShift: LeftShiftPressed = pressed; break;
            case ScancodeRightShift: RightShiftPressed = pressed; break;
        }
    }

    private void ClearPressedKeys()
    {
        UpPressed = false;
        RightPressed = false;
        DownPressed = false;
        LeftPressed = false;
        LeftShiftPressed = false;
        RightShiftPressed = false;
    }

    private static float AdjustChannel( float value, bool pressed, bool shiftPressed, float amount )
    {
        if ( !pressed )
            return value;

        return Math.Clamp( value + (shiftPressed ? -amount : amount), 0f, 1f );
    }
}
