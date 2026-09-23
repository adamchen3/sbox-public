using Editor;
using Sandbox;
using Sandbox.Rendering;
using Sandbox.UI;

internal class SpriteTestAppSystem : PanelAppSystem
{
    PanelWindow window;
    protected override void OnInitialized()
    {
        base.OnInitialized();
        window = new PanelWindow( "Sprite Test", new Vector2( 1100, 660 ), new Vector2( -1, -1 ), borderless: true );
        window.BackgroundColor = Color.FromBytes( 18, 20, 24 );
        window.Root.AddChild( new SpriteTestRoot( window ) );
    }

    sealed class SpriteTestRoot : Panel
    {
        public SpriteTestRoot( PanelWindow window )
        {
            Style.Width = Length.Percent( 100 );
            Style.Height = Length.Percent( 100 );
            Style.Set( "flex-direction: column; background-color: #121418;" );

            AddChild( new TitleBar( window ) );
            AddChild( new TrianglePanel() );
        }
    }

    sealed class TitleBar : Panel
    {
        public TitleBar( PanelWindow window )
        {
            AddClass( "window-drag" );
            Style.Set( "height: 38px; flex-shrink: 0; flex-direction: row; align-items: center; background-color: #1d2027; border-bottom: 1px solid #2d323c; pointer-events: all;" );

            var title = AddChild<Sandbox.UI.Label>();
            title.Text = "Sprite Test";
            title.Style.Set( "padding-left: 14px; font-size: 14px; font-weight: 600; color: #dce2ea;" );

            var spacer = Add.Panel();
            spacer.Style.FlexGrow = 1;

            var close = AddChild( new Sandbox.UI.Button( null, "close", window.Dispose ) );
            close.AddClass( "window-nodrag" );
            close.Style.Set( "width: 46px; height: 38px; flex-shrink: 0; align-items: center; justify-content: center; pointer-events: all; cursor: pointer; background-color: transparent; color: #dce2ea;" );
        }
    }

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
}