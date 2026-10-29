using System.Text;
using System.Text.RegularExpressions;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace Sandbox.ResourceDecompiler;

internal static class NativeExport
{
	public static ContentFile Texture( Resource resource )
	{
		var texture = (Texture)resource.DataBlock!;
		var exporter = new TextureExtract( resource ) { IgnoreVtexFile = false, LatLongCombineCubemap = true };
		var content = exporter.ToContentFile();
		try
		{
			if ( texture.IsRawAnyImage && content.Data is { } image )
			{
				content.AddSubFile( Path.ChangeExtension( Path.GetFileName( resource.FileName! ), TextureExtract.GetImageOutputExtension( texture ) ), () => image );
				content.Data = Encoding.UTF8.GetBytes( exporter.ToValveTexture() );
			}
			else if ( content.Data is null && texture.Flags.HasFlag( VTexFlags.CUBE_TEXTURE ) && texture.IsHighDynamicRange && texture.Depth == 1 )
			{
				// VRF emits a lat-long EXR for HDR cubemaps, but does not emit its vtex definition.
				var definition = Regex.Replace( exporter.ToValveTexture(), "(\"m_(?:outputTypeString|typeString)\"\\s+\"string\"\\s+)\"2D\"", "$1\"CUBE\"" );
				content.Data = Encoding.UTF8.GetBytes( definition.Replace( "\"srgb\"", "\"linear\"", StringComparison.Ordinal ) );
			}

			if ( content.Data is null )
				throw new NotSupportedException( "This texture is an array or non-HDR cubemap; rebuilding its vtex input layout is not supported." );
			return content;
		}
		catch
		{
			content.Dispose();
			throw;
		}
	}

	public static bool PrepareModel( Resource resource )
	{
		var meshes = resource.DataBlock switch
		{
			Model model => model.GetEmbeddedMeshes().Select( entry => entry.Mesh ),
			Mesh mesh => [mesh],
			_ => []
		};
		var regenerateNormals = false;
		foreach ( var mesh in meshes )
		{
			var buffers = mesh.VBIB.VertexBuffers;
			for ( var i = 0; i < buffers.Count; i++ )
			{
				var buffer = buffers[i];
				if ( !buffer.InputLayoutFields.Any( field => field.SemanticName == "NORMAL" && field.Format == DXGI_FORMAT.R10G10B10A2_UNORM ) )
					continue;

				// VRF cannot decode this s&box tangent frame. Importing the geometry without it
				// lets ModelDoc regenerate normals instead of fabricating decoded values.
				buffer.InputLayoutFields = buffer.InputLayoutFields.Where( field => field.SemanticName is not ("NORMAL" or "TANGENT") ).ToArray();
				buffers[i] = buffer;
				regenerateNormals = true;
			}
		}

		return regenerateNormals;
	}
}
