HEADER
{
    DevShader = true;
    Description = "SDL low-level triangle demo: clip-space positions and vertex colors.";
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
    float3 color : COLOR0 < Semantic( Color ); >;
};

struct PS_INPUT
{
    float4 position : SV_Position;
    float3 color : COLOR0;
};

VS
{
    PS_INPUT MainVs( VS_INPUT input )
    {
        PS_INPUT output;
        output.position = float4( input.position, 1.0 );
        output.color = input.color;
        return output;
    }
}

PS
{
    RenderState( DepthEnable, false );
    RenderState( DepthWriteEnable, false );
    RenderState( CullMode, NONE );
    RenderState( BlendEnable, false );
    RenderState( ColorWriteEnable0, RGBA );

    float4 MainPs( PS_INPUT input ) : SV_Target0
    {
        return float4( input.color, 1.0 );
    }
}
