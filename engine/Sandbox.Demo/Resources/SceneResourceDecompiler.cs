using System.Buffers.Binary;
using System.Text.Json;

namespace Sandbox.Demo;

public static class SceneResourceDecompiler
{
	const uint DataBlockId = 0x41544144;
	const uint BlobBlockId = 0x4F4C4244;
	const int ResourceHeaderSize = 16;
	const int ResourceBlockEntrySize = 12;

	readonly record struct ResourceBlock( uint Id, int Offset, int Length );

	/// <summary>
	/// Extracts the editable scene JSON and optional blob data beside a compiled scene resource.
	/// Existing output files are not overwritten.
	/// </summary>
	/// <param name="compiledScenePath">Path to the .scene_c file.</param>
	/// <returns>The path to the generated .scene file.</returns>
	public static string Decompile( string compiledScenePath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( compiledScenePath );

		var inputPath = Path.GetFullPath( compiledScenePath );
		if ( !inputPath.EndsWith( ".scene_c", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "The input path must end with .scene_c.", nameof( compiledScenePath ) );

		if ( !File.Exists( inputPath ) )
			throw new FileNotFoundException( "Compiled scene file was not found.", inputPath );

		var scenePath = inputPath[..^2];
		var blobPath = scenePath + "_d";
		if ( File.Exists( scenePath ) || File.Exists( blobPath ) )
			throw new IOException( $"A scene or blob output already exists beside '{inputPath}'." );

		var compiledData = File.ReadAllBytes( inputPath );
		var blocks = ReadBlocks( compiledData );
		var dataBlock = FindRequiredBlock( blocks, DataBlockId, "DATA" );
		var sceneData = compiledData.AsSpan( dataBlock.Offset, dataBlock.Length );

		if ( sceneData.IsEmpty || sceneData[^1] != 0 )
			throw new InvalidDataException( "The compiled scene DATA block is not null-terminated." );

		sceneData = sceneData[..^1];
		var sceneBytes = sceneData.ToArray();
		using ( var document = JsonDocument.Parse( sceneBytes ) )
		{
			if ( document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty( "GameObjects", out var gameObjects )
				|| gameObjects.ValueKind != JsonValueKind.Array )
			{
				throw new InvalidDataException( "The DATA block does not contain scene JSON." );
			}
		}

		ResourceBlock? blobBlock = FindOptionalBlock( blocks, BlobBlockId, "DBLO" );
		var blobBytes = blobBlock is { } blob
			? compiledData.AsSpan( blob.Offset, blob.Length ).ToArray()
			: null;

		if ( blobBytes is null && File.Exists( blobPath ) )
			throw new IOException( $"A blob file already exists beside '{inputPath}', but the compiled resource has no DBLO block." );

		WriteOutputs( scenePath, blobPath, sceneBytes, blobBytes );
		return scenePath;
	}

	static ResourceBlock[] ReadBlocks( byte[] data )
	{
		if ( data.Length < ResourceHeaderSize )
			throw new InvalidDataException( "The compiled resource header is incomplete." );

		var nonStreamingSize = BinaryPrimitives.ReadUInt32LittleEndian( data );
		if ( nonStreamingSize < ResourceHeaderSize || nonStreamingSize > data.Length )
			throw new InvalidDataException( "The compiled resource has an invalid non-streaming size." );

		var blockArrayRelativeOffset = BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( 8 ) );
		var blockCount = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( 12 ) );
		var blockArrayOffset = checked( 8L + blockArrayRelativeOffset );
		var blockArrayEnd = checked( blockArrayOffset + blockCount * ResourceBlockEntrySize );

		if ( blockCount == 0 || blockArrayOffset < ResourceHeaderSize || blockArrayEnd > nonStreamingSize )
			throw new InvalidDataException( "The compiled resource has an invalid block table." );

		var blocks = new ResourceBlock[checked( (int)blockCount )];
		for ( var i = 0; i < blocks.Length; i++ )
		{
			var entryOffset = checked( (int)(blockArrayOffset + i * ResourceBlockEntrySize) );
			var blockId = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( entryOffset ) );
			var dataOffsetRelative = BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( entryOffset + 4 ) );
			var blockLength = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( entryOffset + 8 ) );
			var blockOffset = checked( (long)entryOffset + 4 + dataOffsetRelative );
			var blockEnd = checked( blockOffset + blockLength );

			if ( blockOffset < blockArrayEnd || blockEnd > nonStreamingSize || blockLength > int.MaxValue )
				throw new InvalidDataException( "The compiled resource contains an invalid block range." );

			blocks[i] = new ResourceBlock( blockId, (int)blockOffset, (int)blockLength );
		}

		return blocks;
	}

	static ResourceBlock FindRequiredBlock( ResourceBlock[] blocks, uint id, string name )
	{
		return FindOptionalBlock( blocks, id, name )
			?? throw new InvalidDataException( $"The compiled scene has no {name} block." );
	}

	static ResourceBlock? FindOptionalBlock( ResourceBlock[] blocks, uint id, string name )
	{
		ResourceBlock? found = null;
		foreach ( var block in blocks )
		{
			if ( block.Id != id )
				continue;

			if ( found is not null )
				throw new InvalidDataException( $"The compiled scene contains multiple {name} blocks." );

			found = block;
		}

		return found;
	}

	static void WriteOutputs( string scenePath, string blobPath, byte[] sceneBytes, byte[] blobBytes )
	{
		var suffix = $".{Guid.NewGuid():N}.tmp";
		var sceneTempPath = scenePath + suffix;
		var blobTempPath = blobBytes is null ? null : blobPath + suffix;
		var sceneCreated = false;

		try
		{
			File.WriteAllBytes( sceneTempPath, sceneBytes );
			if ( blobTempPath is not null )
				File.WriteAllBytes( blobTempPath, blobBytes );

			File.Move( sceneTempPath, scenePath );
			sceneCreated = true;

			if ( blobTempPath is not null )
				File.Move( blobTempPath, blobPath );
		}
		catch
		{
			if ( sceneCreated )
				File.Delete( scenePath );

			throw;
		}
		finally
		{
			if ( File.Exists( sceneTempPath ) )
				File.Delete( sceneTempPath );
			if ( blobTempPath is not null && File.Exists( blobTempPath ) )
				File.Delete( blobTempPath );
		}
	}
}
