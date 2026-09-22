using Microsoft.Extensions.Caching.Memory;

using Sandbox;

public static class ApiTest
{
    public static async Task Run()
    {
#pragma warning disable CA2000 // Dispose objects before losing scope
        // Backend/HttpClient will take over ownership/disposal of this handler
        Backend.Initialize( new CachingHandler() );
#pragma warning restore CA2000 // Dispose objects before losing scope
        Console.WriteLine( "Hello, World!" );
        var outputDirectory = Path.Combine( AppContext.BaseDirectory, "sections" );
        Directory.CreateDirectory( outputDirectory );

        var news = await Backend.News.GetNews( 10, 0 );
        Console.WriteLine( "Fetched news:" );
        foreach ( var item in news )
        {
            Console.WriteLine( $"- {item.Title}" );
            Console.WriteLine( $"- {item.Package}" );
            Console.WriteLine( $"- {item.Summary}" );
            Console.WriteLine( $"- {item.Image}" );
            foreach ( var section in item.Sections )
            {
                Console.WriteLine( $"  - {section.Title}" );
                var sectionPath = Path.Combine( outputDirectory, $"{GetSafeFileName( section.Title )}.html" );
                // await File.WriteAllTextAsync(sectionPath, section.Contents ?? string.Empty);
                // Console.WriteLine($"  - Saved: {sectionPath}");
            }
            Console.WriteLine();
            Console.WriteLine();
        }
    }
    static string GetSafeFileName( string? title )
    {
        var safeTitle = string.IsNullOrWhiteSpace( title ) ? "section" : title.Trim();

        foreach ( var invalidChar in Path.GetInvalidFileNameChars() )
        {
            safeTitle = safeTitle.Replace( invalidChar, '_' );
        }

        return safeTitle;
    }
}

public class CachingHandler : DelegatingHandler
{
    private readonly MemoryCache _cache;

    public CachingHandler()
    {
        var options = new MemoryCacheOptions();

        _cache = new MemoryCache( options );
        InnerHandler = new BackendHttpHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
    {
        if ( request.Method != HttpMethod.Get )
        {
            return await base.SendAsync( request, cancellationToken );
        }

        var cacheKey = request.RequestUri.ToString();

        if ( _cache.TryGetValue( cacheKey, out string cachedContent ) )
        {
            return new HttpResponseMessage( System.Net.HttpStatusCode.OK )
            {
                Content = new StringContent( cachedContent )
            };
        }

        {
            var response = await base.SendAsync( request, cancellationToken );

            if ( response.IsSuccessStatusCode )
            {
                var content = await response.Content.ReadAsStringAsync();
                _cache.Set( cacheKey, content, TimeSpan.FromMinutes( 1 ) );

                return new HttpResponseMessage( System.Net.HttpStatusCode.OK )
                {
                    Content = new StringContent( content )
                };
            }

            return response;
        }
    }
}


class BackendHttpHandler : DelegatingHandler
{
    public static bool backend_debug { get; set; } = false;

    public BackendHttpHandler()
    {
        // default handler
        InnerHandler = new HttpClientHandler()
        {
            // Skip revocation checks
            CheckCertificateRevocationList = false,

            // Log any SSL exceptions, but let them continue. People in very forign countries
            // regularly have problems due to having to use proxies etc
            ServerCertificateCustomValidationCallback = ( message, cert, chain, errors ) =>
            {
                if ( errors != System.Net.Security.SslPolicyErrors.None )
                {
                    Console.WriteLine( $"SSL Error: {errors}" );
                }

                // allow even with the ssl error
                return true;
            }
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
    {
        var url = request.RequestUri.ToString();

        if ( backend_debug )
        {
            Console.WriteLine( $"[Api] [{request.Method}] {url}" );
        }

        int tries = 0;

        retry:

        AddHeaders( request );

        tries++;
        var response = await base.SendAsync( request, cancellationToken );

        if ( backend_debug )
        {
            Console.WriteLine( $"[Api] Response: [{response.StatusCode}]" );
        }

        //
        // Our servers are down, retry?
        //
        if ( response.StatusCode >= System.Net.HttpStatusCode.InternalServerError )
        {
            if ( tries <= 5 )
            {
                await Task.Delay( 500 * tries );
                goto retry;
            }
        }


        return response;

    }

    private void AddHeaders( HttpRequestMessage request )
    {
        //
        // Session authorization
        //
        // if (AccountInformation.Session is not null)
        // {
        //     request.Headers.Remove("Authorization");
        //     request.Headers.Add("Authorization", $"session {AccountInformation.Session}");

        //     request.Headers.Remove("X-User-Id");
        //     request.Headers.Add("X-User-Id", $"{AccountInformation.SteamId}");

        //     //	Log.Info( $"{AccountInformation.Session}" );
        //     //	Log.Info( $"{Steamworks.SteamClient.SteamId}" );
        // }

        //
        // Just for fun and diagnostics
        //
        if ( !request.Headers.Contains( "X-Api-Version" ) )
        {
            request.Headers.Add( "Api-Version", "v3" );
            request.Headers.Add( "X-Api-Version", "25" );
            request.Headers.Add( "X-Engine-Version", "29" );
            request.Headers.Add( "X-Network-Version", $"1104" );
            request.Headers.Add( "X-Session", Guid.NewGuid().ToString( "N" ) );
            request.Headers.Add( "X-Framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription );
            request.Headers.Add( "User-Agent", $"Sandbox/1.0 ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})" );
        }
    }
}