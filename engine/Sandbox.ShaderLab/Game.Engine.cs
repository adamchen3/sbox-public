using System;
using NativeEngine;
using Sandbox;
using Sandbox.Engine;
using System.Runtime.InteropServices;

namespace Sandbox.ShaderLab;

public partial class Game
{
    private CMaterialSystem2AppSystemDict _appSystem;
    private bool _enginePreInitStarted;

    RenderDisplayModeUsage GetModeUsage( IntPtr Handle )
    {
        var flags = Sdl.GetWindowFlags( Handle );
        return (flags & Sdl.WindowFlags.Fullscreen) != 0 ? RenderDisplayModeUsage.CooperativeFullscreen
            : (flags & Sdl.WindowFlags.Borderless) != 0 ? RenderDisplayModeUsage.BorderlessWindow : RenderDisplayModeUsage.BorderedWindow;
    }

    string FindGameFolder()
    {
        var searchPaths = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach ( var searchPath in searchPaths )
        {
            for ( var directory = new DirectoryInfo( searchPath ); directory is not null; directory = directory.Parent )
            {
                foreach ( var candidate in new[] { directory.FullName, Path.Combine( directory.FullName, "game" ) } )
                {
                    var engineDll = Path.Combine( candidate, NetCore.NativeDllPath, "engine2.dll" );
                    if ( File.Exists( engineDll ) )
                        return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException( "Couldn't find game/bin/win64/engine2.dll. Run the demo from the repository or game folder." );
    }

    private void InitEngine()
    {
        var gameFolder = FindGameFolder();
        var nativeDllPath = Path.Combine( gameFolder, NetCore.NativeDllPath );
        NetCore.NativeDllPath = nativeDllPath;
        NetCore.InitializeInterop( gameFolder );

        if ( OperatingSystem.IsWindows() )
        {
            var path = System.Environment.GetEnvironmentVariable( "PATH" );
            path = $"{nativeDllPath};{path}";
            System.Environment.SetEnvironmentVariable( "PATH", path );
        }

        _appSystem = CMaterialSystem2AppSystemDict.Create( new MaterialSystem2AppSystemDictCreateInfo() );
        if ( !_appSystem.IsValid )
            throw new InvalidOperationException( "Couldn't create the engine app system." );
        _appSystem.SetModGameSubdir( "core" );

        // 执行了这个之后，g_pRenderDevice就能正常访问了。
        _enginePreInitStarted = true;
        if ( !NativeEngine.EngineGlobal.SourceEnginePreInit( "-nosteam -novr -panelapp", _appSystem ) )
        {
            throw new Exception( "SourceEnginePreInit failed" );
        }

        // 添加一下shader文件的搜索路径,。不行，没用的。
        // var shaderDirectory = Path.Combine( gameFolder, "ShaderLab" );
        // if ( !Directory.Exists( shaderDirectory ) )
        //     throw new DirectoryNotFoundException( $"Shader directory not found: {shaderDirectory}" );

        // NativeEngine.FullFileSystem.AddProjectPath( "shaderlab", shaderDirectory );

        // if ( !NativeEngine.EngineGlobal.SourceEnginePanelAppInit( _appSystem, false ) )
        // {
        //     throw new Exception( "SourceEnginePanelAppInit failed" );
        // }
    }
}
