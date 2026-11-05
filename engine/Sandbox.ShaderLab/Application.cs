using System;

namespace Sandbox.ShaderLab;

public class Application
{
    public void Run()
    {
        var game = new Game();
        game.Initialize();

        while ( game.IsRunning )
        {
            game.ProcessInput();
            game.Update();
            game.Render();
        }
        game.Cleanup();
    }

}
