using System;

namespace Sandbox.ShaderLab;

public class Application
{
    public void Run()
    {
        var game = new Game();
        try
        {
            game.Initialize();

            while ( game.IsRunning )
            {
                game.ProcessInput();
                if ( !game.IsRunning )
                    break;

                game.Update();
                game.Render();
            }
        }
        finally
        {
            game.Cleanup();
        }
    }

}
