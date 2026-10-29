using System.Text;
using System.Text.Json;

namespace Sandbox.ResourceDecompiler;

internal static class RuntimeDataExporter
{
	public static string Export( ResourceContainer container, string source, OutputFiles output )
	{
		string warning;
		try
		{
			var data = BinaryKv3.Read( container.ReadBlock( "DATA" ) );
			output.Add( source + ".runtime.json", Encoding.UTF8.GetBytes( data.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) ) );
			warning = "Parsed runtime DATA retained as .runtime.json.";
		}
		catch ( NotSupportedException exception )
		{
			warning = $"DATA cannot be independently decoded: {exception.Message}";
		}
		PreserveBlocks( container, source, output );
		output.Commit( null );
		return $"{warning} Original resource blocks retained in .recovered.blocks. Editable authoring reconstruction is not independently verified for {Path.GetExtension( source )}; no placeholder source was written. Recompilation requires a matching engine/compiler and source schema.";
	}

	internal static void PreserveBlocks( ResourceContainer container, string source, OutputFiles output )
	{
		foreach ( var name in container.BlockNames )
		{
			var blocks = container.ReadBlocks( name );
			var safeName = name.All( char.IsAsciiLetterOrDigit ) ? name : Convert.ToHexString( Encoding.ASCII.GetBytes( name ) );
			for ( var i = 0; i < blocks.Count; i++ )
				output.Add( Path.Combine( source + ".recovered.blocks", $"{safeName}.{i}.bin" ), blocks[i] );
		}
		if ( container.StreamingSize > 0 )
		{
			if ( container.StreamingSize > 256 * 1024 * 1024 )
				throw new NotSupportedException( "Runtime reference streaming data exceeds the 256 MiB limit." );
			output.Add( Path.Combine( source + ".recovered.blocks", "stream.bin" ), container.ReadStreamingData( (uint)container.StreamingSize ) );
		}
	}
}
