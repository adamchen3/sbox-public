using Sandbox;
using Sandbox.UI;

internal class Program
{
    private static void Main( string[] args )
    {
        var appSystem = new PanelTestAppSystem();
		appSystem.Run();
    }
}

public class PanelTestAppSystem : PanelAppSystem
{
    Editor.PanelWindow window;

    protected override void OnInitialized()
    {

        window = new Editor.PanelWindow( "Welcome to the s&box editor", new Vector2( 1100, 660 ), new Vector2( -1, -1 ), borderless: true, vsync: true );
        window.MinSize = new Vector2( 880, 540 );
        window.CanMaximize = false;
        var panel = new Panel();
        window.Root.AddChild( panel );

        panel.StyleSheet.Parse( @"
            .backdrop {
                position: absolute;
                left: 0px;
                top: 0px;
                width: 100%;
                height: 100%;

                filter: blur( 8px ) saturate( 1.1 ) brightness( 0.45 );
	            opacity: 0;
	            transition: opacity 1.2s ease-out;
            }
            .backdrop.visible { opacity: 0.65; }
        " );

        panel.AddClass( "backdrop" );

        panel.Style.Set( "background-image", $"url( https://cdn.sbox.game/upload/i/024da13a/34f6/433d/899d/15c8285f2b94/image.webp )" );
        panel.AddClass( "visible" );

    }

}