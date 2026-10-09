
using Sandbox;
using Sandbox.Utility;
using Sandbox.Engine;

public static class LoaderTest
{
    public static async Task Run()
    {
        // https://sbox.game/facepunch/blockparty
        Console.WriteLine( "Starting LoaderTest..." );
        const string org = "facepunch";
        const string package = "blockparty";
        var packageIdent = Package.FormatIdent( org, package );

#pragma warning disable CA2000 // Backend retains ownership of its handler
        Backend.Initialize( new ForwardingHandler() );
#pragma warning restore CA2000

        // var res = await Backend.Package.GetList(); // 这个需要授权才能访问
        // Console.WriteLine( $"Retrieved package list: {res.Title} packages found." );

        // 根据包标识去获取对应的资源文件的下载路径。
        // 代码的话，目前只有dll的路径了，不返回cll的路径了。

        // 第一种方法最简单，直接使用IPackage的GetManifest接口去获取清单。
        // var manifest = await Backend.Package.GetManifest( packageIdent );
        // foreach ( var file in manifest.Files )
        // {
        //     Console.WriteLine( $"Found url: {file.Url}" );
        //     Console.WriteLine( $"Found file: {file.Path}" );
        // }
        // return;

        // 第二种方法是，通过包标识获取整个包的信息PackageDTO。
        // PackageDto的Version[PackageVersion]数据包含了manifest的url。
        // var packageDto = await Backend.Package.Get( packageIdent );
        // var version = packageDto.Version;
        // var url = version.ManifestUrl;
        // Console.WriteLine( $"Retrieved manifest URL: {url}" );
        // var manifestSchema = await Web.DownloadJson<ManifestSchema>( url );
        // foreach ( var file in manifestSchema.Files )
        // {
        //     Console.WriteLine( $"Found url: {file.Url}" );
        //     Console.WriteLine( $"Found file: {file.Path}" );
        // }
        // return;

        // 第三种方法是，通过IVersion的GetList接口获取一个包的所有版本信息，每个版本信息都有ManifestUrl。
        var versions = await Backend.Version.GetList( packageIdent );
        foreach ( var versionInfo in versions )
        {
            Console.WriteLine( $"Engine Version: {versionInfo.EngineVersion}" );
            Console.WriteLine( $"Manifest URL: {versionInfo.ManifestUrl}" );
        }

    }

    private sealed class ForwardingHandler : DelegatingHandler
    {
#pragma warning disable CA2000 // The delegating handler owns and disposes its inner handler.
        public ForwardingHandler() : base( new HttpClientHandler() )
        {
        }
#pragma warning restore CA2000
    }
}