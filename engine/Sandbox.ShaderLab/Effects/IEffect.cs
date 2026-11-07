using System;

namespace Sandbox.ShaderLab.Effects;

internal interface IEffect
{
    public bool Initilize();
    public void Render( SwapChainHandle_t swapChain, int width, int height );
    public void Cleanup();
}
