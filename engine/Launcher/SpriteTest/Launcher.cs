using System;
using Editor;

namespace Sandbox;

public static class Launcher
{
    public static int Main()
    {
        var appSystem = new SpriteTestAppSystem();
        appSystem.Run();
        return 0;
    }
}