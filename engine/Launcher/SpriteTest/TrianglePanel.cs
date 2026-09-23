using Sandbox.UI;
using Sandbox.Rendering;
using Sandbox;

sealed class TrianglePanel : Panel, IPanelDraw
{
    readonly GpuBuffer<Vertex> vertexBuffer;

    public TrianglePanel()
    {
        Style.Width = Length.Percent( 100 );
        Style.FlexGrow = 1;
        Style.Set( "background-color: #121418;" );

        vertexBuffer = new GpuBuffer<Vertex>( 3, GpuBuffer.UsageFlags.Vertex, "SpriteTest_Triangle" );
        vertexBuffer.SetData( new[]
        {
                new Vertex( new Vector3( 550, 120, 0 ), Color.Red ),
                new Vertex( new Vector3( 300, 540, 0 ), Color.Green ),
                new Vertex( new Vector3( 800, 540, 0 ), Color.Blue ),
            } );
    }

    void IPanelDraw.Draw( CommandList cl )
    {
        cl.Draw( vertexBuffer, Material.UI.Basic );
    }

    public override void OnDeleted()
    {
        vertexBuffer.Dispose();
        base.OnDeleted();
    }
}