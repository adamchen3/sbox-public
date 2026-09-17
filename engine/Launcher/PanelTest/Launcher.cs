using Sandbox.UI;
using Sandbox.UI.Construct;
using System.Diagnostics;

namespace Sandbox;

public static class Launcher
{
    public static int Main()
    {
        var appSystem = new PanelTestAppSystem();
        appSystem.Run();

        return 0;
    }
}

public class PanelTestAppSystem : PanelAppSystem
{
    Editor.PanelWindow window;

    protected override void OnInitialized()
    {

        window = new Editor.PanelWindow( "Welcome to the s&box editor", new Vector2( 1100, 660 ), new Vector2( -1, -1 ), borderless: true, vsync: true );
        window.Root.AddChild( new RootPanel() );
    }

}

class RootPanel : Panel
{
    Label fpsLabel;

    public RootPanel()
    {

        StyleSheet.Parse( @"
            .backdrop {
                position: absolute;
                left: 0px;
                top: 0px;
                width: 50%;
                height: 100%;
                background-color: green;
                filter: blur( 8px ) saturate( 1.1 ) brightness( 0.45 );
            }

            .backdrop2 {
                position: absolute;
                right: 0px;
                top: 0px;
                width: 50%;
                height: 100%;
                background-color: yellow;
                // filter: blur( 8px ) saturate( 1.1 ) brightness( 0.45 );
            }

            .box {
                position: absolute;
                width: 200px;
                height: 200px;
                background-color: blue;
                // filter: saturate( 1.1 );
            }

            .box2 {
                position: absolute;
                right: 0px;
                width: 200px;
                height: 200px;
                background-color: blue;
                filter: saturate( 1.1 );
            }

            .inner-box {
                position: absolute;
                width: 100px;
                height: 100px;
                background-color: red;
                // filter: brightness( 0.45 );
            }

            .inner-box2 {
                position: absolute;
                right: 0px;
                width: 100px;
                height: 100px;
                background-color: red;
                // filter: brightness( 0.45 );
            }
            " );

        var backdrop = AddChild<Panel>();
        backdrop.AddClass( "backdrop" );

        var backdrop2 = AddChild<Panel>();
        backdrop2.AddClass( "backdrop2" );

        fpsLabel = Add.Label( "", "fps" );

        var box = AddChild<Panel>();
        box.AddClass( "box" );

        var box2 = AddChild<Panel>();
        box2.AddClass( "box2" );

        var innerBox = AddChild<Panel>();
        innerBox.AddClass( "inner-box" );

        var innerBox2 = AddChild<Panel>();
        innerBox2.AddClass( "inner-box2" );
    }

    int frameCount;
    Stopwatch fpsTimer = Stopwatch.StartNew();

    public override void Tick()
    {
        frameCount++;

        if ( fpsTimer.ElapsedMilliseconds < 500 ) return;

        fpsLabel.Text = $"{frameCount * 1000 / fpsTimer.ElapsedMilliseconds} fps";
        frameCount = 0;
        fpsTimer.Restart();
    }
}