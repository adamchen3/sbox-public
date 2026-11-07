HEADER
{
    DevShader = true;
    Description = "Unlit Shader";
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
};

struct PS_INPUT
{
    float4 position : SV_Position;
};

VS
{
    PS_INPUT MainVs( VS_INPUT input )
    {
        PS_INPUT output;
        output.position = float4( input.position, 1.0 );
        return output;
    }
}

PS
{
    float4 MainPs( PS_INPUT input ) : SV_Target0
    {
        return float4( 0.0, 1.0, 0.0, 1.0 );
    }
}