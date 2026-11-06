struct VSO { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSO main(uint id : SV_VertexID)
{
    VSO o;
    float2 uv = float2(id & 1, (id >> 1) & 1);
    o.uv = uv;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    return o;
}
