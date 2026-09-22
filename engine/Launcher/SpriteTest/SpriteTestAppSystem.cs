using Editor;
using Sandbox;

internal class SpriteTestAppSystem : PanelAppSystem
{
    PanelWindow window;
    protected override void OnInitialized()
    {
        base.OnInitialized();
        window = new PanelWindow( "Sprite Test", new Vector2( 1100, 660 ), new Vector2( -1, -1 ), borderless: true );
    }
}