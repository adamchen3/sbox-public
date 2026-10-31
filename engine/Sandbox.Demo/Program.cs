using Sandbox.Demo;
using Sandbox.Demo.Shader;
// Console.WriteLine($"AppContent Base Directory: {AppContext.BaseDirectory}");
// Console.WriteLine($"Environment Current Directory: {Environment.CurrentDirectory}");
// Console.WriteLine($"Environment Process Path: {Environment.ProcessPath}");

// await ApiTest.Run();
// await LoaderTest.Run();
// SdlTest.Run();
// new SdlTest2().Run();
// new SdlTest3().Run();
// new SdlTest4().Run();

var shaderResult = ShaderCompiler.Run( args );
if ( shaderResult.HasValue )
	return shaderResult.Value;

SceneResourceDecompiler.Decompile( args[0] );
return 0;
