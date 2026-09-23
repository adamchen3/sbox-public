using Editor;
using Sandbox;

using Sandbox.UI;

internal class SpriteTestAppSystem : PanelAppSystem
{
    PanelWindow window;
    protected override void OnInitialized()
    {
        base.OnInitialized();
        window = new PanelWindow( "Sprite Test", new Vector2( 1100, 660 ), position: null, borderless: false );
        window.BackgroundColor = Color.FromBytes( 18, 20, 24 );
        window.Root.AddChild( new TrianglePanel() );
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


}