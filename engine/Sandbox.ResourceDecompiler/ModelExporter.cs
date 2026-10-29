using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sandbox.ResourceDecompiler;

internal static class ModelExporter
{
	public static (string? Primary, string Warning) Export( ResourceContainer container, string source, string contentRoot, OutputFiles output )
	{
		var model = BinaryKv3.Read( container.ReadBlock( "DATA" ) );
		var meshes = container.ReadBlocks( "MDAT" );
		var buffers = container.ReadBlocks( "MBUF" );
		RuntimeDataExporter.PreserveBlocks( container, source, output );
		output.Add( source + ".runtime.json", Encoding.UTF8.GetBytes( model.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) ) );
		if ( meshes.Count == 0 && buffers.Count == 0 )
			return (null, "Model has no embedded render mesh. Runtime DATA recovered as JSON only; external meshes and physics-only authoring are not reconstructed.");
		if ( meshes.Count == 0 || meshes.Count != buffers.Count )
			throw new NotSupportedException( "Model has no matching embedded MDAT/MBUF mesh pairs. External meshes and physics-only models are not yet reconstructed." );
		var mtl = new StringBuilder();
		var remaps = new StringBuilder();
		var materialFile = Path.GetFileName( source ) + ".recovered.mtl";
		var slot = 0;
		var runtimeMeshes = new JsonObject[meshes.Count];
		for ( var mesh = 0; mesh < meshes.Count; mesh++ )
		{
			runtimeMeshes[mesh] = BinaryKv3.Read( meshes[mesh] );
			output.Add( source + $".mesh{mesh}.runtime.json", Encoding.UTF8.GetBytes( runtimeMeshes[mesh].ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) ) );
		}
		var distances = Array( model, "m_lodGroupSwitchDistances" );
		if ( distances.Count > 8 )
			throw new NotSupportedException( "Models with more than eight LODs are not verified." );
		var lods = new List<(string Mesh, float Distance)>();
		for ( var lod = 0; lod < Math.Max( 1, distances.Count ); lod++ )
		{
			var selectedMeshes = DefaultMeshes( model, meshes.Count, lod );
			var obj = new StringBuilder();
			obj.Append( "mtllib " ).AppendLine( materialFile );
			var vertexStart = 1;
			var firstSlot = slot;
			foreach ( var mesh in selectedMeshes.Order() )
			{
				var binary = new ModelBuffers( buffers[mesh] );
				foreach ( var scene in Array( runtimeMeshes[mesh], "m_sceneObjects" ) )
					foreach ( var draw in Array( scene, "m_drawCalls" ) )
					{
						if ( draw?["m_nPrimitiveType"]?.GetValue<string>() != "RENDER_PRIM_TRIANGLES" )
							throw new NotSupportedException( "Only triangle-list model draw calls are supported." );
						var bindings = Array( draw, "m_vertexBuffers" );
						if ( bindings.Count != 1 )
							throw new NotSupportedException( "Models with multiple vertex streams per draw call are not yet supported." );
						var vb = At( binary.Vertices, Int( bindings[0], "m_hBuffer" ) );
						var ib = At( binary.Indices, Int( draw["m_indexBuffer"], "m_hBuffer" ) );
						var position = vb.Attributes.SingleOrDefault( attribute => attribute.Name.Equals( "POSITION", StringComparison.OrdinalIgnoreCase ) && attribute.Index == 0 )
							?? throw new InvalidDataException( "Model vertex buffer has no POSITION attribute." );
						var uv = vb.Attributes.SingleOrDefault( attribute => attribute.Name.Equals( "TEXCOORD", StringComparison.OrdinalIgnoreCase ) && attribute.Index == 0 );
						if ( position.Format != 6 || (uv is not null && uv.Format is not (16 or 34)) )
							throw new NotSupportedException( "Unsupported model position/UV vertex format." );
						var firstVertex = Int( draw, "m_nBaseVertex" );
						var vertexCount = Int( draw, "m_nVertexCount" );
						var vertexOffset = Int( bindings[0], "m_nBindOffsetBytes" );
						var indexOffset = Int( draw["m_indexBuffer"], "m_nBindOffsetBytes" );
						if ( firstVertex < 0 || vertexCount <= 0 || firstVertex > vb.Count - vertexCount || vertexOffset < 0 || indexOffset < 0 )
							throw new InvalidDataException( "Invalid model draw vertex range." );
						if ( position.Offset < 0 || position.Offset > vb.Stride - 12 || (uv is not null && (uv.Offset < 0 || uv.Offset > vb.Stride - (uv.Format == 34 ? 4 : 8))) )
							throw new InvalidDataException( "Model attribute lies outside its vertex stride." );
						var name = $"recovered_{slot++}";
						obj.Append( "o " ).AppendLine( name );
						for ( var i = 0; i < vertexCount; i++ )
						{
							var offset = checked(vertexOffset + (firstVertex + i) * vb.Stride);
							ModelBuffers.Check( vb.Data, offset, vb.Stride );
							// The official OBJ importer maps (x,y,z) to (z,x,y) and flips V.
							obj.Append( "v " ).Append( Float( vb.Data, offset + position.Offset + 4 ) ).Append( ' ' )
								.Append( Float( vb.Data, offset + position.Offset + 8 ) ).Append( ' ' ).AppendLine( Float( vb.Data, offset + position.Offset ) );
							obj.Append( "vt " );
							if ( uv is null ) obj.AppendLine( "0 0" );
							else
							{
								var p = offset + uv.Offset;
								var u = uv.Format == 34 ? (float)BitConverter.UInt16BitsToHalf( BinaryPrimitives.ReadUInt16LittleEndian( vb.Data.AsSpan( p ) ) ) : ReadFloat( vb.Data, p );
								var v = uv.Format == 34 ? (float)BitConverter.UInt16BitsToHalf( BinaryPrimitives.ReadUInt16LittleEndian( vb.Data.AsSpan( p + 2 ) ) ) : ReadFloat( vb.Data, p + 4 );
								obj.Append( Number( u ) ).Append( ' ' ).AppendLine( Number( 1 - v ) );
							}
						}
						var start = Int( draw, "m_nStartIndex" );
						var count = Int( draw, "m_nIndexCount" );
						if ( start < 0 || count <= 0 || count % 3 != 0 || start > ib.Count - count )
							throw new InvalidDataException( "Invalid model draw index range." );
						obj.Append( "usemtl " ).AppendLine( name );
						for ( var i = 0; i < count; i++ )
						{
							var offset = checked(indexOffset + (start + i) * ib.Stride);
							ModelBuffers.Check( ib.Data, offset, ib.Stride );
							var index = ib.Stride == 2 ? BinaryPrimitives.ReadUInt16LittleEndian( ib.Data.AsSpan( offset ) ) : BinaryPrimitives.ReadUInt32LittleEndian( ib.Data.AsSpan( offset ) );
							if ( index >= vertexCount )
								throw new NotSupportedException( "Model index is not relative to the draw call base vertex." );
							if ( i % 3 == 0 ) obj.Append( "f" );
							var vertex = checked(vertexStart + (int)index);
							obj.Append( ' ' ).Append( vertex ).Append( '/' ).Append( vertex );
							if ( i % 3 == 2 ) obj.AppendLine();
						}
						vertexStart = checked(vertexStart + vertexCount);
						var material = draw["m_material"]?.GetValue<string>() ?? throw new InvalidDataException( "Model draw has no material." );
						mtl.Append( "newmtl " ).AppendLine( name ).AppendLine( "Kd 1 1 1" );
						remaps.Append( "{ from = " ).Append( Kv3Source.Quote( name + ".vmat" ) )
							.Append( " to = " ).Append( Kv3Source.Quote( material ) ).AppendLine( " }," );
					}
			}
			if ( slot == firstSlot )
			{
				if ( lod != 0 )
					throw new NotSupportedException( $"Model LOD {lod} has no recoverable default-visible geometry." );
				return (null, "Model has no recoverable default-bodygroup LOD0 draw calls. Runtime DATA and embedded mesh metadata recovered as JSON only; no placeholder .vmdl was written.");
			}
			var meshPath = source + (lod == 0 ? ".recovered.obj" : $".recovered.lod{lod}.obj");
			output.Add( meshPath, Encoding.UTF8.GetBytes( obj.ToString() ) );
			var distance = distances.Count == 0 ? 0 : float.Parse( distances[lod]?.ToJsonString() ?? throw new InvalidDataException( "Missing model LOD switch distance." ), CultureInfo.InvariantCulture );
			if ( !float.IsFinite( distance ) || distance < 0 || (lod > 0 && distance < lods[^1].Distance) )
				throw new InvalidDataException( "Invalid model LOD switch distances." );
			lods.Add( (Path.GetRelativePath( contentRoot, meshPath ).Replace( '\\', '/' ), distance) );
		}
		output.Add( source + ".recovered.mtl", Encoding.UTF8.GetBytes( mtl.ToString() ) );
		output.Add( source, Encoding.UTF8.GetBytes( Definition( lods, remaps.ToString() ) ) );
		return (source, "Independent static model reconstruction: default-bodygroup positions, triangle topology, UV0, material bindings and LOD geometry/switch distances recovered from embedded buffers. Normals/tangents regenerate; skinning, animation, physics, morphs, selectable bodygroups, extra UV/color channels and original authoring hierarchy are not reconstructed. Runtime metadata retained as JSON. Review static pose before recompilation.");
	}

	internal static string Definition( string mesh, string remaps ) => Definition( [(mesh, 0)], remaps );

	static string Definition( IReadOnlyList<(string Mesh, float Distance)> lods, string remaps ) => $$"""
		<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->
		{
			rootNode =
			{
				_class = "RootNode"
				children =
				[
					{
						_class = "LODGroupList"
						children = [
							{{string.Join( ",\n", lods.Select( ( lod, i ) => $"{{ _class = \"LODGroup\" switch_threshold = {Number( lod.Distance )} meshes = [ \"recovered_lod{i}\" ] }}" ) )}}
						]
					},
					{
						_class = "MaterialGroupList"
						children = [ { _class = "DefaultMaterialGroup" remaps = [ {{remaps}} ] use_global_default = false } ]
					},
					{
						_class = "RenderMeshList"
						children =
						[
							{{string.Join( ",\n", lods.Select( ( lod, i ) => $$"""
								{
									_class = "RenderMeshFile"
									name = "recovered_lod{{i}}"
									filename = {{Kv3Source.Quote( lod.Mesh )}}
									import_translation = [ 0.0, 0.0, 0.0 ]
									import_rotation = [ 0.0, 0.0, 0.0 ]
									import_scale = 1.0
									align_origin_x_type = "None"
									align_origin_y_type = "None"
									align_origin_z_type = "None"
									parent_bone = ""
								}
								""" ) )}}
						]
					}
				]
			}
		}
		""";

	static JsonArray Array( JsonNode? node, string key ) => node?[key] as JsonArray ?? throw new InvalidDataException( $"Missing model array {key}." );
	internal static IReadOnlySet<int> DefaultMeshes( JsonObject model, int meshCount, int lod = 0 )
	{
		var lodMasks = Array( model, "m_refLODGroupMasks" );
		var groupMasks = Array( model, "m_refMeshGroupMasks" );
		if ( lodMasks.Count != meshCount || groupMasks.Count != meshCount )
			throw new NotSupportedException( "Model embedded mesh masks do not match its MDAT blocks." );
		var defaultGroup = Mask( model["m_nDefaultMeshGroupMask"] );
		var result = new HashSet<int>();
		for ( var mesh = 0; mesh < meshCount; mesh++ )
		{
			if ( (Mask( lodMasks[mesh] ) & (1UL << lod)) != 0 && (Mask( groupMasks[mesh] ) & defaultGroup) != 0 )
				result.Add( mesh );
		}
		return result;
	}

	static ulong Mask( JsonNode? node )
	{
		var text = node?.ToJsonString() ?? throw new InvalidDataException( "Missing model visibility mask." );
		return text.StartsWith( "-", StringComparison.Ordinal ) ? unchecked((ulong)long.Parse( text, CultureInfo.InvariantCulture )) : ulong.Parse( text, CultureInfo.InvariantCulture );
	}
	static int Int( JsonNode? node, string key ) => node?[key]?.GetValue<int>() ?? throw new InvalidDataException( $"Missing model integer {key}." );
	static ModelBuffers.Buffer At( IReadOnlyList<ModelBuffers.Buffer> buffers, int index ) => index >= 0 && index < buffers.Count ? buffers[index] : throw new InvalidDataException( "Invalid model buffer handle." );
	static float ReadFloat( byte[] data, int offset ) => BitConverter.Int32BitsToSingle( ModelBuffers.I32( data, offset ) );
	static string Float( byte[] data, int offset ) => Number( ReadFloat( data, offset ) );
	static string Number( float value ) => float.IsFinite( value ) ? value.ToString( "R", CultureInfo.InvariantCulture ) : throw new InvalidDataException( "Non-finite model position/UV." );
}
