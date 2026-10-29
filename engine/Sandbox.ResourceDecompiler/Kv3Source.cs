using System.Text;
using System.Text.Json.Nodes;

namespace Sandbox.ResourceDecompiler;

internal static class Kv3Source
{
	public static byte[] AnimationGraph( byte[] data )
	{
		var graph = BinaryKv3.Read( data );
		if ( graph["_class"]?.GetValue<string>() != "CAnimationGraph" || graph["m_nodeManager"] is not JsonObject )
			throw new InvalidDataException( "Compiled animation graph does not contain its authoring node graph." );
		var text = new StringBuilder( "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:animgraph2:version{0f7898b8-5471-45c4-9867-cd9c46bcfdb5} -->\n" );
		Write( text, graph );
		text.AppendLine();
		return Encoding.UTF8.GetBytes( text.ToString() );
	}

	static void Write( StringBuilder text, JsonNode? value )
	{
		if ( value is JsonObject obj )
		{
			text.AppendLine( "{" );
			foreach ( var (key, item) in obj )
			{
				text.Append( Quote( key ) ).Append( " = " );
				Write( text, item );
				text.AppendLine();
			}
			text.Append( '}' );
		}
		else if ( value is JsonArray array )
		{
			text.AppendLine( "[" );
			foreach ( var item in array )
			{
				Write( text, item );
				text.AppendLine( "," );
			}
			text.Append( ']' );
		}
		else if ( value is JsonValue scalar && scalar.TryGetValue<string>( out var stringValue ) )
			text.Append( Quote( stringValue ) );
		else
			text.Append( value?.ToJsonString() ?? "null" );
	}

	internal static string Quote( string value )
	{
		var text = new StringBuilder( "\"" );
		foreach ( var character in value )
		{
			text.Append( character switch
			{
				'\\' => "\\\\",
				'"' => "\\\"",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				_ when char.IsControl( character ) => throw new NotSupportedException( "Source text contains an unsupported control character." ),
				_ => character.ToString()
			} );
		}
		return text.Append( '"' ).ToString();
	}
}
