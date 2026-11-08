using System.Runtime.InteropServices;
using NativeEngine;
using SkiaSharp;

namespace Sandbox.ShaderLab.Effects;

internal class BirdBackgroundEffect : IEffect
{
	private RenderBufferHandle_t VertexBuffer;
	private RenderBufferHandle_t IndexBuffer;
	private NativeEngine.VertexLayout BackgroundLayout;
	private NativeEngine.VertexLayout UnlitLayout;
	private IMaterial BackgroundMaterial;
	private IMaterial UnlitMaterial;
	private ITexture BackgroundTexture;

	public bool Initilize()
	{
		const int vertexSize = 5 * sizeof( float );
		BackgroundLayout = NativeEngine.VertexLayout.Create( "BirdBackground", vertexSize );
		BackgroundLayout.Add( "POSITION", 0, (uint)ColorFormat.COLOR_FORMAT_R32G32B32_FLOAT, 0 );
		BackgroundLayout.Add( "TEXCOORD", 0, (uint)ColorFormat.COLOR_FORMAT_R32G32_FLOAT, 3 * sizeof( float ) );
		BackgroundLayout.Build();

		UnlitLayout = NativeEngine.VertexLayout.Create( "BirdBackground_Unlit", vertexSize );
		UnlitLayout.Add( "POSITION", 0, (uint)ColorFormat.COLOR_FORMAT_R32G32B32_FLOAT, 0 );
		UnlitLayout.Build();

		VertexBuffer = g_pRenderDevice.CreateGPUBuffer(
			RenderBufferType.RENDER_BUFFER_TYPE_SEMISTATIC,
			new BufferDesc { m_nElementCount = 8, m_nElementSizeInBytes = vertexSize },
			RenderBufferFlags.RENDER_BUFFER_USAGE_VERTEX_BUFFER,
			"BirdBackground_VertexBuffer"
		);

		if ( VertexBuffer == IntPtr.Zero )
			throw new InvalidOperationException( "Couldn't create the bird background vertex buffer." );

		IndexBuffer = g_pRenderDevice.CreateGPUBuffer(
			RenderBufferType.RENDER_BUFFER_TYPE_SEMISTATIC,
			new BufferDesc { m_nElementCount = 6, m_nElementSizeInBytes = sizeof( uint ) },
			RenderBufferFlags.RENDER_BUFFER_USAGE_INDEX_BUFFER,
			"BirdBackground_IndexBuffer"
		);
		if ( IndexBuffer == IntPtr.Zero )
			throw new InvalidOperationException( "Couldn't create the bird background index buffer." );

		BackgroundMaterial = MaterialSystem2.CreateRawMaterial( "BirdBackground_Material", "ShaderLab/Shaders/BirdBackground.shader", true );
		if ( BackgroundMaterial.IsNull || !BackgroundMaterial.IsLoaded() )
			throw new InvalidOperationException( "Couldn't load the bird background shader." );

		UnlitMaterial = MaterialSystem2.CreateRawMaterial( "BirdBackground_UnlitMaterial", "ShaderLab/Shaders/Unlit.shader", true );
		if ( UnlitMaterial.IsNull || !UnlitMaterial.IsLoaded() )
			throw new InvalidOperationException( "Couldn't load the Unlit shader." );

		LoadBackgroundTexture();

		float[] vertices = {
			-1.0f, -1.0f, 0.5f, 0.0f, 1.0f,
			 1.0f, -1.0f, 0.5f, 1.0f, 1.0f,
			 1.0f,  1.0f, 0.5f, 1.0f, 0.0f,
			-1.0f,  1.0f, 0.5f, 0.0f, 0.0f,
			// -0.35f, -0.35f, 0.5f, 0.0f, 1.0f,
			//  0.35f, -0.35f, 0.5f, 1.0f, 1.0f,
			//  0.35f,  0.35f, 0.5f, 1.0f, 0.0f,
			// -0.35f,  0.35f, 0.5f, 0.0f, 0.0f
			-1.0f, -1.0f, 0.5f, 0.0f, 1.0f,
			 1.0f, -1.0f, 0.5f, 1.0f, 1.0f,
			 1.0f,  1.0f, 0.5f, 1.0f, 0.0f,
			-1.0f,  1.0f, 0.5f, 0.0f, 0.0f,
		};
		int[] indices = { 0, 1, 2, 2, 3, 0 };

		var vertexData = Marshal.AllocHGlobal( vertices.Length * sizeof( float ) );
		try
		{
			var indexData = Marshal.AllocHGlobal( indices.Length * sizeof( int ) );
			try
			{
				Marshal.Copy( vertices, 0, vertexData, vertices.Length );
				Marshal.Copy( indices, 0, indexData, indices.Length );
				var context = g_pRenderDevice.CreateRenderContext( 0 );
				try
				{
					RenderTools.SetGPUBufferData( context, VertexBuffer, vertexData, (uint)(vertices.Length * sizeof( float )), 0 );
					RenderTools.SetGPUBufferData( context, IndexBuffer, indexData, (uint)(indices.Length * sizeof( int )), 0 );
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
				Marshal.FreeHGlobal( indexData );
			}
		}
		finally
		{
			Marshal.FreeHGlobal( vertexData );
		}

		return true;
	}

	private void LoadBackgroundTexture()
	{
		var path = Path.Combine( AppContext.BaseDirectory, "Textures", "bird.png" );
		using var stream = File.OpenRead( path );
		using var codec = SKCodec.Create( stream )
			?? throw new InvalidOperationException( $"Couldn't decode the background image: {path}" );
		var info = new SKImageInfo( codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul );
		using var bitmap = SKBitmap.Decode( codec, info )
			?? throw new InvalidOperationException( $"Couldn't decode the background pixels: {path}" );

		var config = new TextureCreationConfig_t
		{
			m_nWidth = checked((short)bitmap.Width),
			m_nHeight = checked((short)bitmap.Height),
			m_nDepth = 1,
			m_nNumMipLevels = 1,
			m_nImageFormat = ImageFormat.RGBA8888,
			m_nUsage = TextureUsage.TEXTURE_USAGE_STATIC
		};
		BackgroundTexture = g_pRenderDevice.FindOrCreateTexture2(
			"BirdBackground_Texture", true, config, bitmap.GetPixels(), bitmap.ByteCount );

		if ( BackgroundTexture.IsNull || !BackgroundTexture.IsStrongHandleValid() || BackgroundTexture.IsError() )
			throw new InvalidOperationException( $"Couldn't create the background texture: {path}" );
	}

	public void Render( SwapChainHandle_t swapChain, int width, int height )
	{
		var context = g_pRenderDevice.CreateRenderContext( 0 );
		try
		{
			context.BindRenderTargets( swapChain, true, true );
			context.SetViewport( 0, 0, width, height );
			context.Clear( new Vector4( 0, 0, 0, 1 ), true, true, true );

			var attributes = context.GetAttributesPtrForModify();
			attributes.SetTextureValue( "BackgroundTexture", BackgroundTexture, -1 );
			var backgroundMode = BackgroundMaterial.GetMode();
			if ( backgroundMode.IsNull || !RenderTools.SetRenderState( context, attributes, backgroundMode, BackgroundLayout, default ) )
				throw new InvalidOperationException( "Couldn't bind the bird background render state." );

			if ( !context.BindVertexBuffer( 0, VertexBuffer, 0 ) )
				throw new InvalidOperationException( "Couldn't bind the bird background vertex buffer." );
			if ( !context.BindIndexBuffer( IndexBuffer, sizeof( uint ), 0 ) )
				throw new InvalidOperationException( "Couldn't bind the bird background index buffer." );

			context.DrawIndexed( RenderPrimitiveType.RENDER_PRIM_TRIANGLES, 0, 6, 4, 0 );

			var unlitMode = UnlitMaterial.GetMode();
			if ( unlitMode.IsNull || !RenderTools.SetRenderState( context, attributes, unlitMode, UnlitLayout, default ) )
				throw new InvalidOperationException( "Couldn't bind the Unlit overlay render state." );

			context.DrawIndexed( RenderPrimitiveType.RENDER_PRIM_TRIANGLES, 0, 6, 4, 4 );

			g_pRenderDevice.BeginSubmittingDisplayLists( swapChain );
			context.Submit();
			if ( !g_pRenderDevice.Present( swapChain ) )
				throw new InvalidOperationException( "Couldn't present the SDL swap chain." );
		}
		finally
		{
			g_pRenderDevice.ReleaseRenderContext( context );
		}
	}

	public void Cleanup()
	{
		if ( !UnlitMaterial.IsNull )
		{
			UnlitMaterial.DestroyStrongHandle();
			UnlitMaterial = default;
		}
		if ( !BackgroundMaterial.IsNull )
		{
			BackgroundMaterial.DestroyStrongHandle();
			BackgroundMaterial = default;
		}
		if ( !BackgroundTexture.IsNull )
		{
			BackgroundTexture.DestroyStrongHandle();
			BackgroundTexture = default;
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
		if ( !UnlitLayout.IsNull )
		{
			UnlitLayout.Free();
			UnlitLayout.Destroy();
			UnlitLayout = default;
		}
		if ( !BackgroundLayout.IsNull )
		{
			BackgroundLayout.Free();
			BackgroundLayout.Destroy();
			BackgroundLayout = default;
		}
	}
}
