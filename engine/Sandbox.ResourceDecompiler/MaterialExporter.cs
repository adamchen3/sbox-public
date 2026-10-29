using System.Text;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace Sandbox.ResourceDecompiler;

internal sealed class MaterialExporter : IShaderDataProvider
{
	readonly BasicShaderDataProvider fallback = new();
	public bool UsedFallback { get; private set; }

	public ContentFile Extract( Resource resource, ResourceFileLoader loader )
	{
		var material = (Material)resource.DataBlock!;
		var serializer = KVSerializer.Create( KVSerializationFormat.KeyValues1Text );
		var scalarSource = new MaterialExtract( material, resource.EditInfo, null, this ).ToValveMaterial();
		using var input = new MemoryStream( Encoding.UTF8.GetBytes( scalarSource ) );
		var document = serializer.Deserialize( input );
		document.Root.Remove( "Compiled Textures" );
		var output = new ContentFile();
		try
		{
			foreach ( var (parameter, texturePath) in material.TextureParams )
			{
				var mappings = GetInputsForTexture( parameter, material ).Where( mapping => !material.VectorParams.ContainsKey( mapping.Name ) ).ToArray();
				foreach ( var (_, name) in mappings )
					document.Root.Remove( name );
				if ( mappings.Length == 0 )
					continue;

				var textureResource = loader.LoadFileCompiled( texturePath );
				if ( textureResource is null )
					continue;
				if ( textureResource.DataBlock is not Texture texture )
					throw new InvalidDataException( $"Material texture is not a texture resource: {texturePath}" );
				if ( texture.IsHighDynamicRange || texture.Depth > 1 || texture.Flags.HasFlag( VTexFlags.CUBE_TEXTURE ) )
					throw new NotSupportedException( $"Unpacking HDR, array, or cubemap material inputs is not supported: {texturePath}" );

				var materialPath = resource.FileName![..^2];
				var materialDirectory = Path.GetDirectoryName( materialPath ) ?? "";
				var unpack = mappings.Select( mapping => new MaterialExtract.UnpackInfo
				{
					TextureType = mapping.Name,
					Channel = mapping.Channel,
					FileName = Path.Combine( materialDirectory, $"{Path.GetFileNameWithoutExtension( materialPath )}.recovered_{mapping.Name}.png" ).Replace( '\\', '/' )
				} ).ToArray();

				var images = new TextureExtract( textureResource ).ToMaterialMaps( unpack );
				images.Data = null;
				images.FileName = materialPath;
				output.AdditionalFiles.Add( images );
				if ( unpack.Any( map => !images.SubFiles.Any( file => file.FileName == Path.GetFileName( map.FileName ) ) ) )
					throw new InvalidDataException( $"Texture exporter did not emit every material map: {texturePath}" );
				foreach ( var map in unpack )
					document.Root.Add( map.TextureType, map.FileName );
			}

			using var stream = new MemoryStream();
			serializer.Serialize( stream, document );
			output.Data = stream.ToArray();
			return output;
		}
		catch
		{
			output.Dispose();
			throw;
		}
	}

	public IEnumerable<(ChannelMapping Channel, string Name)> GetInputsForTexture( string textureType, Material material )
	{
		var shader = Path.GetFileNameWithoutExtension( material.ShaderName );
		if ( shader is "line" or "glass" or "foliage" or "bark" or "water_simple" )
		{
			switch ( textureType )
			{
				case "g_tColor":
					return shader == "line"
						? [(ChannelMapping.RGBA, "TextureColor")]
						: [(ChannelMapping.RGB, "TextureColor"), (ChannelMapping.A, "TextureTranslucency")];
				case "g_tNormal":
					return shader == "line"
						? [(ChannelMapping.RGB, "TextureNormal")]
						: [(ChannelMapping.RGB, "TextureNormal"), (ChannelMapping.A, "TextureTintMask")];
				case "g_tRma":
					return shader == "line"
						? [(ChannelMapping.R, "TextureRoughness"), (ChannelMapping.G, "TextureMetalness"), (ChannelMapping.B, "TextureAmbientOcclusion")]
						: [(ChannelMapping.R, "TextureRoughness"), (ChannelMapping.G, "TextureMetalness"), (ChannelMapping.B, "TextureAmbientOcclusion"), (ChannelMapping.A, "TextureBlendMask")];
			}
		}

		UsedFallback = true;
		return fallback.GetInputsForTexture( textureType, material );
	}

	public string? GetSuffixForInputTexture( string inputName, Material material ) => fallback.GetSuffixForInputTexture( inputName, material );
}
