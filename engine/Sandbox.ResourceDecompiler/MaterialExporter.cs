using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sandbox.ResourceDecompiler;

internal static class MaterialExporter
{
	public static string Export( ResourceContainer container, string source, OutputFiles output )
	{
		var material = BinaryKv3.Read( container.ReadBlock( "DATA" ) );
		var shader = material["m_shaderName"]?.GetValue<string>()
			?? throw new InvalidDataException( "Compiled material has no shader name." );
		var text = new StringBuilder( "\"Layer0\"\n{\n" );
		Add( text, "shader", shader );
		foreach ( var (array, value) in new[] { ("m_intParams", "m_nValue"), ("m_floatParams", "m_flValue"), ("m_vectorParams", "m_value"), ("m_textureParams", "m_pValue") } )
		{
			if ( material[array] is not JsonArray parameters )
				throw new InvalidDataException( $"Missing material parameter array {array}." );
			foreach ( var parameter in parameters )
			{
				var name = parameter?["m_name"]?.GetValue<string>()
					?? throw new InvalidDataException( "Material parameter has no name." );
				var item = parameter[value] ?? throw new InvalidDataException( $"Material parameter {name} has no value." );
				var encoded = item is JsonArray vector
					? "[" + string.Join( " ", vector.Select( component => component?.ToJsonString() ?? throw new InvalidDataException( "Null material vector component." ) ) ) + "]"
					: array == "m_textureParams" ? CompiledTexture( item.GetValue<string>() ) : item.ToJsonString();
				Add( text, name, encoded );
			}
		}
		text.AppendLine( "\t\"Attributes\"\n\t{" );
		var recoveredAttributes = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var systemAttributes = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		// Inline TextureColor regenerates these dimensions; direct compiled bindings do not.
		var regeneratesColor = material["m_vectorParams"] is JsonArray vectors
			&& vectors.Any( item => item?["m_name"]?.GetValue<string>() == "TextureColor" );
		foreach ( var (array, value) in new[] { ("m_intAttributes", "m_nValue"), ("m_floatAttributes", "m_flValue"), ("m_vectorAttributes", "m_value"), ("m_textureAttributes", "m_pValue"), ("m_stringAttributes", "m_value") } )
		{
			if ( material[array] is not JsonArray attributes )
				throw new InvalidDataException( $"Missing material attribute array {array}." );
			foreach ( var attribute in attributes )
			{
				var name = attribute?["m_name"]?.GetValue<string>() ?? throw new InvalidDataException( "Material attribute has no name." );
				var item = attribute[value] ?? throw new InvalidDataException( $"Material attribute {name} has no value." );
				if ( name is "RepresentativeTextureWidth" or "RepresentativeTextureHeight" )
				{
					if ( !regeneratesColor )
						systemAttributes[name] = item.ToJsonString();
					continue;
				}
				recoveredAttributes[name] = item is JsonArray vector
					? "[" + string.Join( " ", vector.Select( component => component?.ToJsonString() ?? throw new InvalidDataException( "Null material attribute component." ) ) ) + "]"
					: array is "m_textureAttributes" or "m_stringAttributes" ? item.GetValue<string>() : item.ToJsonString();
			}
		}
		foreach ( var (name, value) in recoveredAttributes )
			Add( text, name, value );
		text.AppendLine( "\t}" );
		text.AppendLine( "\t\"SystemAttributes\"\n\t{" );
		foreach ( var (name, value) in systemAttributes )
			Add( text, name, value );
		text.AppendLine( "\t}" );
		text.AppendLine( "}" );
		output.Add( source, Encoding.UTF8.GetBytes( text.ToString() ) );
		output.Add( source + ".runtime.json", Encoding.UTF8.GetBytes( material.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) ) );
		return "Independent material reconstruction: shader parameters and attributes recovered, with direct compiled texture bindings. Full parsed runtime data retained in .runtime.json. Dynamic expressions and editor-only authoring properties require review; keep compiled texture dependencies.";
	}

	static void Add( StringBuilder text, string name, string value ) => text.Append( '\t' ).Append( Quote( name ) ).Append( '\t' ).AppendLine( Quote( value ) );
	static string CompiledTexture( string path ) => path.EndsWith( ".vtex", StringComparison.OrdinalIgnoreCase )
		? path + "_c" : path.EndsWith( ".vtex_c", StringComparison.OrdinalIgnoreCase )
		? path : throw new NotSupportedException( $"Material texture is not a compiled VTEX reference: {path}" );
	static string Quote( string value ) => "\"" + value.Replace( "\\", "\\\\", StringComparison.Ordinal ).Replace( "\"", "\\\"", StringComparison.Ordinal ).Replace( "\r", "\\r", StringComparison.Ordinal ).Replace( "\n", "\\n", StringComparison.Ordinal ) + "\"";
}
