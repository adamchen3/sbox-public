using System.Text;

namespace Sandbox.ResourceDecompiler;

internal static class HdrImage
{
	internal static byte[] EncodeExr( int width, int height, float[] rgba )
	{
		if ( width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length || (long)rgba.Length * sizeof( float ) > 256 * 1024 * 1024 )
			throw new InvalidDataException( "Invalid or oversized floating-point image." );
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter( stream, Encoding.ASCII, leaveOpen: true );
		writer.Write( 20000630 );
		writer.Write( 2 );
		Attribute( "channels", "chlist", value =>
		{
			foreach ( var channel in new[] { "A", "B", "G", "R" } )
			{
				Text( value, channel );
				value.Write( 2 );
				value.Write( 0 );
				value.Write( 1 );
				value.Write( 1 );
			}
			value.Write( (byte)0 );
		} );
		Attribute( "compression", "compression", value => value.Write( (byte)0 ) );
		foreach ( var name in new[] { "dataWindow", "displayWindow" } )
			Attribute( name, "box2i", value => { value.Write( 0 ); value.Write( 0 ); value.Write( width - 1 ); value.Write( height - 1 ); } );
		Attribute( "lineOrder", "lineOrder", value => value.Write( (byte)0 ) );
		Attribute( "pixelAspectRatio", "float", value => value.Write( 1.0f ) );
		Attribute( "screenWindowCenter", "v2f", value => { value.Write( 0.0f ); value.Write( 0.0f ); } );
		Attribute( "screenWindowWidth", "float", value => value.Write( 1.0f ) );
		writer.Write( (byte)0 );
		var firstScanline = stream.Position + height * sizeof( long );
		var rowBytes = checked(width * 4 * sizeof( float ));
		for ( var y = 0; y < height; y++ )
			writer.Write( firstScanline + (long)y * (8 + rowBytes) );
		for ( var y = 0; y < height; y++ )
		{
			writer.Write( y );
			writer.Write( rowBytes );
			foreach ( var channel in new[] { 3, 2, 1, 0 } )
			{
				for ( var x = 0; x < width; x++ )
					writer.Write( rgba[(y * width + x) * 4 + channel] );
			}
		}
		return stream.ToArray();

		void Attribute( string name, string type, Action<BinaryWriter> write )
		{
			using var value = new MemoryStream();
			using var payload = new BinaryWriter( value, Encoding.ASCII, leaveOpen: true );
			write( payload );
			Text( writer, name );
			Text( writer, type );
			writer.Write( checked((int)value.Length) );
			writer.Write( value.ToArray() );
		}
	}

	static void Text( BinaryWriter writer, string value )
	{
		writer.Write( Encoding.ASCII.GetBytes( value ) );
		writer.Write( (byte)0 );
	}
}
