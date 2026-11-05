using System;
using NativeEngine;

namespace Sandbox.ShaderLab;

public partial class Game
{
    private IntPtr WindowHandle { get; set; }

    private void CreateWindow()
    {
        WindowHandle = Sdl.CreateWindow(
            "SDL Test Window",
            800,
            600,
            Sdl.WindowFlags.Hidden | Sdl.WindowFlags.Resizable | Sdl.WindowFlags.Vulkan );
    }

}
