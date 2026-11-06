Texture2D t : register(t0);
SamplerState s : register(s0);
struct VSO { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
float4 main(VSO i) : SV_Target { return t.SampleLevel(s, i.uv, 0); }
