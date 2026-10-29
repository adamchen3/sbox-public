using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sandbox.ResourceDecompiler.Tests;

[TestClass]
public sealed class ModelBufferTests
{
	[TestMethod]
	public void RawBuffersUseFieldRelativePointersAndDoNotMistakeDataForCodecHeader()
	{
		var data = Fixture();
		var model = new ModelBuffers( data );
		Assert.AreEqual( 1, model.Vertices.Count );
		Assert.AreEqual( 3, model.Vertices[0].Count );
		Assert.AreEqual( 20, model.Vertices[0].Stride );
		Assert.AreEqual( "position", model.Vertices[0].Attributes[0].Name );
		Assert.AreEqual( 6, model.Vertices[0].Attributes[0].Format );
		Assert.AreEqual( 12, model.Vertices[0].Attributes[1].Offset );
		Assert.AreEqual( (byte)0xa1, model.Vertices[0].Data[0] );
		CollectionAssert.AreEqual( new byte[] { 0, 0, 1, 0, 2, 0 }, model.Indices[0].Data );
	}

	[TestMethod]
	public void TruncatedBufferPayloadIsRejected()
	{
		Assert.ThrowsException<InvalidDataException>( () => new ModelBuffers( Fixture()[..^1] ) );
	}

	[TestMethod]
	public void InvalidBufferPointerIsRejected()
	{
		var data = Fixture();
		Write( data, 32, int.MaxValue - 32 );
		Assert.ThrowsException<InvalidDataException>( () => new ModelBuffers( data ) );
	}

	[TestMethod]
	public void InvalidAttributeTableIsRejected()
	{
		var data = Fixture();
		Write( data, 28, 33 );
		Assert.ThrowsException<InvalidDataException>( () => new ModelBuffers( data ) );
	}

	[TestMethod]
	public void InvalidCompressedVertexBufferIsRejected()
	{
		var data = Fixture();
		Write( data, 36, 59 );
		Assert.ThrowsException<InvalidDataException>( () => new ModelBuffers( data ) );
	}

	[TestMethod]
	public void OnlyDefaultVisibleLodZeroMeshesAreSelected()
	{
		var model = JsonNode.Parse( """
			{
				"m_refLODGroupMasks": [1, 2, 1, 3, 255],
				"m_refMeshGroupMasks": [1, 1, 2, 4, -1],
				"m_nDefaultMeshGroupMask": 5
			}
			""" )!.AsObject();
		CollectionAssert.AreEquivalent( new[] { 0, 3, 4 }, ModelExporter.DefaultMeshes( model, 5 ).ToArray() );
		CollectionAssert.AreEquivalent( new[] { 1, 3, 4 }, ModelExporter.DefaultMeshes( model, 5, 1 ).ToArray() );
		model["m_nDefaultMeshGroupMask"] = 0;
		Assert.AreEqual( 0, ModelExporter.DefaultMeshes( model, 5 ).Count );
	}

	[TestMethod]
	public void InconsistentMeshMaskMappingIsRejected()
	{
		var model = JsonNode.Parse( """{"m_refLODGroupMasks":[1],"m_refMeshGroupMasks":[-1],"m_nDefaultMeshGroupMask":-1}""" )!.AsObject();
		Assert.ThrowsException<NotSupportedException>( () => ModelExporter.DefaultMeshes( model, 2 ) );
	}

	[TestMethod]
	public void InstancedVertexAttributesAreRejected()
	{
		var data = Fixture();
		Write( data, 112, 1 );
		Assert.ThrowsException<NotSupportedException>( () => new ModelBuffers( data ) );
	}

	static byte[] Fixture()
	{
		var data = new byte[242];
		Write( data, 0, 16 ); Write( data, 4, 1 );
		Write( data, 8, 32 ); Write( data, 12, 1 );
		Write( data, 16, 3 ); Write( data, 20, 20 );
		Write( data, 24, 40 ); Write( data, 28, 2 );
		Write( data, 32, 144 ); Write( data, 36, 60 );
		Write( data, 40, 3 ); Write( data, 44, 2 );
		Write( data, 56, 180 ); Write( data, 60, 6 );
		Encoding.ASCII.GetBytes( "position" ).CopyTo( data, 64 );
		Write( data, 100, 6 );
		Encoding.ASCII.GetBytes( "texcoord" ).CopyTo( data, 120 );
		Write( data, 156, 34 ); Write( data, 160, 12 );
		Write( data, 176, 0x3f8000a1 );
		new byte[] { 0, 0, 1, 0, 2, 0 }.CopyTo( data, 236 );
		return data;
	}

	static void Write( byte[] data, int offset, int value ) => BinaryPrimitives.WriteInt32LittleEndian( data.AsSpan( offset ), value );
}
