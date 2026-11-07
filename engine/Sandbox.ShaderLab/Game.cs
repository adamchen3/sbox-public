using System;
using System.Diagnostics;
using NativeEngine;
using Sandbox;
using Sandbox.Engine;
using System.Runtime.InteropServices;
using Mono.Cecil.Cil;

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
    private RenderBufferHandle_t VertexBuffer;
    private RenderBufferHandle_t IndexBuffer;
    private NativeEngine.VertexLayout Layout;
    private IMaterial ShaderMaterial;


    public void Initialize()
    {
        InitEngine();

        CreateWindow();
        if ( !Sdl.ShowWindow( WindowHandle ) )
            throw new InvalidOperationException( $"Couldn't show the SDL window: {Sdl.GetError()}" );

        InitRendering();
        LastUpdateTimestamp = Stopwatch.GetTimestamp();
        InitShader();
        IsRunning = true;
    }

    private void InitShader()
    {
        // 每个vertex的大小，单位：Byte
        const int vertexSize = 3 * sizeof( float );
        Layout = NativeEngine.VertexLayout.Create( "Unlit", vertexSize );
        Layout.Add( "POSITION", 0, (uint)ColorFormat.COLOR_FORMAT_R32G32B32_FLOAT, 0 );
        Layout.Build();

        VertexBuffer = g_pRenderDevice.CreateGPUBuffer(
            RenderBufferType.RENDER_BUFFER_TYPE_SEMISTATIC,
            new BufferDesc { m_nElementCount = 4, m_nElementSizeInBytes = vertexSize },
            RenderBufferFlags.RENDER_BUFFER_USAGE_VERTEX_BUFFER,
            "Unlit_VertexBuffer"
        );
        if ( VertexBuffer == IntPtr.Zero )
            throw new InvalidOperationException( "Couldn't create the vertex buffer." );

        IndexBuffer = g_pRenderDevice.CreateGPUBuffer(
            RenderBufferType.RENDER_BUFFER_TYPE_SEMISTATIC,
            new BufferDesc { m_nElementCount = 6, m_nElementSizeInBytes = sizeof( uint ) },
            RenderBufferFlags.RENDER_BUFFER_USAGE_INDEX_BUFFER,
            "Unlit_IndexBuffer"
        );
        if ( IndexBuffer == IntPtr.Zero )
            throw new InvalidOperationException( "Couldn't create the index buffer." );

        ShaderMaterial = MaterialSystem2.CreateRawMaterial( "Unlit_Material", "ShaderLab/Shaders/Unlit.shader", true );

        if ( ShaderMaterial.IsNull || !ShaderMaterial.IsLoaded() )
        {
            throw new InvalidOperationException( "Couldn't load Shader." );
        }

        float[] vertices = [
            -1.0f, -1.0f, 0.5f,
            1.0f, -1.0f, 0.5f,
            1.0f, 1.0f, 0.5f,
            -1.0f, 1.0f, 0.5f
        ];

        int[] indices = [
            0, 1, 2,
            2, 3, 0
        ];

        var byteCount = vertices.Length * sizeof( float );
        var indexByteCount = indices.Length * sizeof( int );
        var data = Marshal.AllocHGlobal( byteCount );
        var indexData = Marshal.AllocHGlobal( indexByteCount );
        try
        {
            Marshal.Copy( vertices, 0, data, vertices.Length );
            Marshal.Copy( indices, 0, indexData, indices.Length );
            var context = g_pRenderDevice.CreateRenderContext( 0 );
            try
            {
                RenderTools.SetGPUBufferData( context, VertexBuffer, data, (uint)byteCount, 0 );
                RenderTools.SetGPUBufferData( context, IndexBuffer, indexData, (uint)indexByteCount, 0 );
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
            Marshal.FreeHGlobal( indexData );
        }
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
        IsRunning = false;
        ClearPressedKeys();
        LastUpdateTimestamp = 0;

        try
        {
            if ( SwapChain != default
                || VertexBuffer != IntPtr.Zero || IndexBuffer != IntPtr.Zero )
            {
                g_pRenderDevice.Flush();
                if ( SwapChain != default )
                    g_pRenderDevice.ForceFlushGPU( SwapChain );
            }


            if ( !ShaderMaterial.IsNull )
            {
                ShaderMaterial.DestroyStrongHandle();
                ShaderMaterial = default;
            }

            if ( IndexBuffer != IntPtr.Zero )
            {
                g_pRenderDevice.DestroyGPUBuffer( IndexBuffer );
                IndexBuffer = default;
            }

            if ( VertexBuffer != IntPtr.Zero )
            {
                g_pRenderDevice.DestroyGPUBuffer( VertexBuffer );
                VertexBuffer = default;
            }

            if ( !Layout.IsNull )
            {
                Layout.Free();
                Layout.Destroy();
                Layout = default;
            }

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

        var context = g_pRenderDevice.CreateRenderContext( 0 );
        try
        {
            context.BindRenderTargets( SwapChain, true, false );
            context.SetViewport( 0, 0, width, height );
            context.Clear( new Vector4( ClearColor ), true, false, false );

            var materialMode = ShaderMaterial.GetMode();

            if ( materialMode.IsNull || !RenderTools.SetRenderState( context, context.GetAttributesPtrForModify(), materialMode, Layout, default ) )
                throw new InvalidOperationException( "Couldn't bind the triangle shader and render state" );

            if ( !context.BindVertexBuffer( 0, VertexBuffer, 0 ) )
                throw new InvalidOperationException( "Couldn't bind the triangle vertex buffer" );

            if ( !context.BindIndexBuffer( IndexBuffer, sizeof( uint ), 0 ) )
                throw new InvalidOperationException( "Couldn't bind the triangle index buffer" );

            context.DrawIndexed( RenderPrimitiveType.RENDER_PRIM_TRIANGLES, 0, 6, 0, 0 );

            g_pRenderDevice.BeginSubmittingDisplayLists( SwapChain );
            context.Submit();

            if ( !g_pRenderDevice.Present( SwapChain ) )
                throw new InvalidOperationException( "Couldn't present the SDL swap chain" );
        }
        finally
        {
            g_pRenderDevice.ReleaseRenderContext( context );
        }

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
