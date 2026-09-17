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
                width: 100%;
                height: 100%;
                background-color: green;
                // filter: blur( 8px ) saturate( 1.1 ) brightness( 0.45 );
            }

            .outter-box {
                position: absolute;
                right: 0px;
                width: 300px;
                height: 300px;
                background-color: yellow;
                filter: saturate( 1.1 );
            }

            .box {
                position: absolute;
                right: 0px;
                width: 200px;
                height: 200px;
                background-color: blue;
                filter: saturate( 1.1 );
            }

            .inner-box {
                position: absolute;
                right: 0px;
                width: 100px;
                height: 100px;
                background-color: red;
                filter: brightness( 0.45 );
            }
            " );

        var backdrop = AddChild<Panel>();
        backdrop.AddClass( "backdrop" );


        fpsLabel = Add.Label( "", "fps" );

        var outterBox = AddChild<Panel>();
        outterBox.AddClass( "outter-box" );

        var box = AddChild<Panel>();
        box.AddClass( "box" );

        var innerBox = AddChild<Panel>();
        innerBox.AddClass( "inner-box" );

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