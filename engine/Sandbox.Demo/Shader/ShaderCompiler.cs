using System.Text;
using System.Text.RegularExpressions;

namespace Sandbox.Demo.Shader;

public static class ShaderCompiler
{
	static readonly Regex SectionPattern = new(
		@"^\s*(HEADER|MODES|FEATURES|COMMON|VS|PS|GS|HS|DS|CS|PS_RENDER_STATE|RTX)\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant );

	static readonly (string Section, string SlangStage, string EntryPoint)[] Stages =
	[
		("VS", "vertex", "MainVs"),
		("PS", "fragment", "MainPs"),
		("GS", "geometry", "MainGs"),
		("CS", "compute", "MainCs")
	];

	public readonly record struct Output( string Path, string CompileCommand );

	public static int? Run( string[] args )
	{
		if ( args.Length == 0 )
		{
			Console.Error.WriteLine( "Usage: Sandbox.Demo <scene_c path> | --shader-to-slang <shader path>" );
			return 1;
		}

		if ( !args[0].Equals( "--shader-to-slang", StringComparison.OrdinalIgnoreCase ) )
			return null;

		if ( args.Length != 2 )
		{
			Console.Error.WriteLine( "Usage: Sandbox.Demo --shader-to-slang <shader path>" );
			return 1;
		}

		foreach ( var output in Export( args[1] ) )
		{
			Console.WriteLine( $"Generated {output.Path}" );
			Console.WriteLine( $"Compile: {output.CompileCommand}" );
		}

		return 0;
	}

	/// <summary>
	/// Exports stage sources and Slang commands. Engine-provided includes must be available on disk;
	/// combo variants can be selected by adding -D flags to the generated commands.
	/// This exports shader programs, not the engine's material or render-state configuration.
	/// </summary>
	public static IReadOnlyList<Output> Export( string shaderPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( shaderPath );

		var inputPath = Path.GetFullPath( shaderPath );
		if ( !inputPath.EndsWith( ".shader", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "The input path must end with .shader.", nameof( shaderPath ) );

		if ( !File.Exists( inputPath ) )
			throw new FileNotFoundException( "Shader file was not found.", inputPath );

		var source = File.ReadAllText( inputPath );
		var coreShaderDirectory = FindCoreShaderDirectory( inputPath );
		var outputs = new List<Output>();

		foreach ( var stage in Stages )
		{
			var extracted = ExtractStage( source, stage.Section, out var found );
			if ( !found )
				continue;

			var outputPath = Path.Combine(
				Path.GetDirectoryName( inputPath )!,
				$"{Path.GetFileNameWithoutExtension( inputPath )}.{stage.Section}.slang" );

			var generatedSource = new StringBuilder()
				.AppendLine( $"#define PROGRAM VFX_PROGRAM_{stage.Section}" )
				.AppendLine( "#include \"system.fxc\"" )
				.Append( extracted )
				.ToString();

			File.WriteAllText( outputPath, generatedSource );

			var spirvPath = Path.ChangeExtension( outputPath, ".spv" );
			var command = $"slangc -enable-effect-annotations -target spirv -stage {stage.SlangStage} -entry {stage.EntryPoint} "
				+ $"-I \"{coreShaderDirectory}\" \"{outputPath}\" -o \"{spirvPath}\"";

			outputs.Add( new Output( outputPath, command ) );
		}

		if ( outputs.Count == 0 )
			throw new InvalidDataException( "The shader contains no VS, PS, GS, or CS program." );

		return outputs;
	}

	static string ExtractStage( string source, string requestedStage, out bool found )
	{
		var output = new StringBuilder( source.Length );
		var depth = 0;
		var inBlockComment = false;
		found = false;

		for ( var position = 0; position < source.Length; )
		{
			var lineEnd = source.IndexOfAny( ['\r', '\n'], position );
			if ( lineEnd < 0 )
				lineEnd = source.Length;

			var line = source.AsSpan( position, lineEnd - position );
			var section = depth == 0 && !inBlockComment
				? SectionPattern.Match( line.ToString() )
				: Match.Empty;

			if ( section.Success )
			{
				var sectionName = section.Groups[1].Value;
				var openBrace = FindOpeningBrace( source, lineEnd );
				var closeBrace = FindMatchingBrace( source, openBrace );

				if ( sectionName == requestedStage )
					found = true;

				if ( sectionName == "COMMON" || sectionName == requestedStage )
				{
					output.Append( source, openBrace + 1, closeBrace - openBrace - 1 );
					output.AppendLine();
				}
				else
				{
					output.AppendLine();
				}

				position = closeBrace + 1;
				if ( position < source.Length && source[position] == '\r' )
					position++;
				if ( position < source.Length && source[position] == '\n' )
					position++;
				continue;
			}

			output.Append( source, position, lineEnd - position );
			if ( lineEnd < source.Length )
				output.Append( source[lineEnd] );

			UpdateLexicalState( line, ref depth, ref inBlockComment );
			position = lineEnd < source.Length ? lineEnd + 1 : source.Length;
		}

		return output.ToString();
	}

	static int FindOpeningBrace( string source, int position )
	{
		var inBlockComment = false;

		for ( var i = position; i < source.Length; i++ )
		{
			var current = source[i];
			var next = i + 1 < source.Length ? source[i + 1] : '\0';

			if ( inBlockComment )
			{
				if ( current == '*' && next == '/' )
				{
					inBlockComment = false;
					i++;
				}
				continue;
			}

			if ( current == '/' && next == '*' )
			{
				inBlockComment = true;
				i++;
				continue;
			}

			if ( current == '/' && next == '/' )
			{
				while ( i < source.Length && source[i] != '\r' && source[i] != '\n' )
					i++;
				continue;
			}

			if ( current == '{' )
				return i;

			if ( !char.IsWhiteSpace( current ) )
				throw new InvalidDataException( "Expected an opening brace after a shader section name." );
		}

		throw new InvalidDataException( "Shader section has no opening brace." );
	}

	static int FindMatchingBrace( string source, int openBrace )
	{
		var depth = 1;
		var inBlockComment = false;
		var inString = false;
		var inCharacter = false;

		for ( var i = openBrace + 1; i < source.Length; i++ )
		{
			var current = source[i];
			var next = i + 1 < source.Length ? source[i + 1] : '\0';

			if ( inBlockComment )
			{
				if ( current == '*' && next == '/' )
				{
					inBlockComment = false;
					i++;
				}
				continue;
			}

			if ( inString || inCharacter )
			{
				if ( current == '\\' )
				{
					i++;
					continue;
				}

				if ( (inString && current == '"') || (inCharacter && current == '\'') )
				{
					inString = false;
					inCharacter = false;
				}
				continue;
			}

			if ( current == '/' && next == '*' )
			{
				inBlockComment = true;
				i++;
				continue;
			}

			if ( current == '/' && next == '/' )
			{
				while ( i < source.Length && source[i] != '\r' && source[i] != '\n' )
					i++;
				continue;
			}

			if ( current == '"' )
				inString = true;
			else if ( current == '\'' )
				inCharacter = true;
			else if ( current == '{' )
				depth++;
			else if ( current == '}' && --depth == 0 )
				return i;
		}

		throw new InvalidDataException( "Shader section has no matching closing brace." );
	}

	static void UpdateLexicalState( ReadOnlySpan<char> line, ref int depth, ref bool inBlockComment )
	{
		for ( var i = 0; i < line.Length; i++ )
		{
			var current = line[i];
			var next = i + 1 < line.Length ? line[i + 1] : '\0';

			if ( inBlockComment )
			{
				if ( current == '*' && next == '/' )
				{
					inBlockComment = false;
					i++;
				}
				continue;
			}

			if ( current == '/' && next == '*' )
			{
				inBlockComment = true;
				i++;
				continue;
			}

			if ( current == '/' && next == '/' )
				return;

			if ( current == '{' )
				depth++;
			else if ( current == '}' )
				depth--;
		}
	}

	static string FindCoreShaderDirectory( string inputPath )
	{
		var candidates = new List<string>();
		var engine = Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE" );
		if ( !string.IsNullOrWhiteSpace( engine ) )
			candidates.Add( Path.Combine( engine, "core", "shaders" ) );

		for ( var directory = new DirectoryInfo( Path.GetDirectoryName( inputPath )! );
			directory is not null;
			directory = directory.Parent )
		{
			candidates.Add( Path.Combine( directory.FullName, "core", "shaders" ) );
			candidates.Add( Path.Combine( directory.FullName, "shaders" ) );
		}

		return candidates.FirstOrDefault( path => File.Exists( Path.Combine( path, "system.fxc" ) ) )
			?? throw new DirectoryNotFoundException( "Could not locate s&box core shaders (system.fxc)." );
	}
}
