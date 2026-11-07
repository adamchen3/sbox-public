using System;
using System.Runtime.InteropServices;
using NativeEngine;

namespace Sandbox.ShaderLab.Effects;

internal class UnlitEffect : IEffect
{

    private Vector4 ClearColor = new Vector4( 1.0f, 0.08f, 0.1f, 1f );

    private RenderBufferHandle_t VertexBuffer;
    private RenderBufferHandle_t IndexBuffer;
    private NativeEngine.VertexLayout Layout;
    private IMaterial ShaderMaterial;

    public bool Initilize()
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

        float[] vertices = new float[] {
            -1.0f, -1.0f, 0.5f,
            1.0f, -1.0f, 0.5f,
            1.0f, 1.0f, 0.5f,
            -1.0f, 1.0f, 0.5f
        };

        int[] indices = new int[] {
            0, 1, 2,
            2, 3, 0
        };

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
        return true;
    }

    public void Render( SwapChainHandle_t swapChain, int width, int height )
    {
        var context = g_pRenderDevice.CreateRenderContext( 0 );
        try
        {
            context.BindRenderTargets( swapChain, true, false );
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

            g_pRenderDevice.BeginSubmittingDisplayLists( swapChain );
            context.Submit();

            if ( !g_pRenderDevice.Present( swapChain ) )
                throw new InvalidOperationException( "Couldn't present the SDL swap chain" );
        }
        finally
        {
            g_pRenderDevice.ReleaseRenderContext( context );
        }
    }

    public void Cleanup()
    {
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
    }
}
