HEADER
{
    DevShader = true;
    Description = "Bird image background";
    Version = 1;
}

MODES
{
    Default();
}

FEATURES
{
}

COMMON
{
    #include "system.fxc"
}

struct VS_INPUT
{
    float3 position : POSITION < Semantic( PosXyz ); >;
    float2 uv : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PS_INPUT
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VS
{
    PS_INPUT MainVs( VS_INPUT input )
    {
        PS_INPUT output;
        output.position = float4( input.position, 1.0 );
        output.uv = input.uv;
        return output;
    }
}

PS
{
    RenderState( DepthEnable, false );
    RenderState( DepthWriteEnable, false );
    Texture2D g_tBackground < Attribute( "BackgroundTexture" ); SrgbRead( true ); >;
    SamplerState g_sBackground < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

    float4 MainPs( PS_INPUT input ) : SV_Target0
    {
        return g_tBackground.SampleLevel( g_sBackground, input.uv, 0 );
    }
}
