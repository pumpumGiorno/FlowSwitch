// -----------------------------------------------------------------------------
//  FlowSwitch shaders (Shader Model 5.0)
//
//  Every surface of the overlay is drawn analytically: rounded rectangles are
//  signed distance fields, shadows are Gaussian-integrated SDFs, glows are
//  Gaussian falloffs. No bitmaps are used for chrome, so everything stays
//  crisp at any size, DPI and animation scale.
//
//  All colour maths happens in sRGB-encoded space (like CSS / XAML) and every
//  pixel shader outputs PREMULTIPLIED alpha. Pure light (glows, orbit lines)
//  is emitted with alpha = 0, i.e. additive under premultiplied blending.
//
//  The per-draw constant layout (P[0..11]) is documented in
//  FlowSwitch.Core/Scene/DrawCommand.cs and written by SceneComposer.cs.
//  VSQuad has a CPU mirror in FlowSwitch.Core/Scene/Projection.cs.
// -----------------------------------------------------------------------------

cbuffer FrameCB : register(b0)
{
    float4 gViewport;   // width, height, 1/width, 1/height
    float4 gCamera;     // time, focal length (px), vanishing point x, y
    float4 gBackdrop;   // max lod, frost lod bias, grain, OLED
    float4 gGlobals;    // has backdrop, adaptive exposure, overlay alpha, dither seed
};

cbuffer DrawCB : register(b1)
{
    float4 P[12];
};

Texture2D gBackdropTex : register(t0);
Texture2D gTex : register(t1);
SamplerState gLinear : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 local : TEXCOORD0;
    float2 screen : TEXCOORD1;
};

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

// ----------------------------- helpers -----------------------------

float Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float3 Dither(float3 c, float2 px)
{
    float n = Hash12(px + gGlobals.w * 97.0) + Hash12(px.yx * 1.37 + 11.0) - 1.0;
    return c + n * (1.0 / 255.0);
}

float SdRoundRect(float2 p, float2 halfSize, float r)
{
    r = min(r, min(halfSize.x, halfSize.y));
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float Erf(float x)
{
    // Abramowitz-Stegun style approximation, max error ~1e-3 - plenty for shadows.
    float s = sign(x);
    float a = abs(x);
    float t = 1.0 / (1.0 + 0.47047 * a);
    float y = 1.0 - (0.3480242 * t - 0.0958798 * t * t + 0.7478556 * t * t * t) * exp(-a * a);
    return s * y;
}

// Coverage of a Gaussian-blurred shape given its signed distance.
float GaussCoverage(float d, float sigma)
{
    return 0.5 - 0.5 * Erf(d / (sigma * 1.41421356));
}

bool HasFlag(float flags, float bit)
{
    return fmod(floor(flags / bit), 2.0) >= 1.0;
}

// Smooth sampling of the backdrop pyramid: trilinear + a 4-tap tent to hide
// the blockiness of the small levels when they are magnified.
// By convention level 0 of the backdrop pyramid is half the viewport resolution.
float3 SampleBackdrop(float2 uv, float lod)
{
    lod = clamp(lod, 0.0, gBackdrop.x);
    float2 texel = exp2(lod) * 2.0 * gViewport.zw;
    float3 c = gBackdropTex.SampleLevel(gLinear, uv + texel * float2(-0.5, -0.5), lod).rgb;
    c += gBackdropTex.SampleLevel(gLinear, uv + texel * float2(0.5, -0.5), lod).rgb;
    c += gBackdropTex.SampleLevel(gLinear, uv + texel * float2(-0.5, 0.5), lod).rgb;
    c += gBackdropTex.SampleLevel(gLinear, uv + texel * float2(0.5, 0.5), lod).rgb;
    return c * 0.25;
}

// ----------------------------- vertex shaders -----------------------------

// A quad in a card's local plane, optionally rotated in 3D and projected with a
// simple pinhole camera whose screen plane is z = 0.
VSOut VSQuad(uint vid : SV_VertexID)
{
    float2 corner = float2((vid & 1u) != 0u ? 1.0 : -1.0, (vid & 2u) != 0u ? 1.0 : -1.0);
    float2 ext = P[1].xy + P[1].zw;
    float2 local = P[3].xy + corner * ext;

    // Floor reflections mirror the geometry vertically (card flag 4).
    float mirror = HasFlag(P[11].w, 4.0) ? -1.0 : 1.0;
    float3 p = float3(local.x, local.y * mirror, 0.0);

    float cr = cos(P[2].z), sr = sin(P[2].z);
    p.xy = float2(p.x * cr - p.y * sr, p.x * sr + p.y * cr);
    float cy = cos(P[2].x), sy = sin(P[2].x);
    p = float3(p.x * cy, p.y, p.x * sy);
    float cp = cos(P[2].y), sp = sin(P[2].y);
    p = float3(p.x, p.y * cp - p.z * sp, p.y * sp + p.z * cp);
    p *= P[0].z;

    float3 world = float3(P[0].xy + p.xy, p.z + P[2].w);
    float focal = gCamera.y;
    float w = max(0.05, (focal + world.z) / focal);
    float2 vp = gCamera.zw;
    float2 screen = vp + (world.xy - vp) / w;

    VSOut o;
    float2 ndc = float2(screen.x * gViewport.z * 2.0 - 1.0, 1.0 - screen.y * gViewport.w * 2.0);
    o.pos = float4(ndc * w, 0.5 * w, w);
    o.local = local;
    o.screen = screen;
    return o;
}

VSOut VSFullscreen(uint vid : SV_VertexID)
{
    float2 uv = float2((vid << 1) & 2u, vid & 2u);
    VSOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.5, 1.0);
    o.local = uv;
    o.screen = uv * gViewport.xy;
    return o;
}

// ----------------------------- backdrop -----------------------------
//  P0 = alpha, blur lod, dim, vignette
//  P1 = ambient rgb, intensity        P2 = ambient centre xy (px), radius xy (px)
//  P3 = left light rgb, intensity     P4 = right light rgb, intensity
//  P5 = left centre xy, right centre xy
//  P6 = base tint rgb, gradient       P7 = grain, adaptive, parallax xy (px)

float4 PSBackdrop(VSOut i) : SV_Target
{
    float2 px = i.screen;
    float2 uv = px * gViewport.zw;
    float alpha = P[0].x;
    float dim = P[0].z;
    bool hasBackdrop = gGlobals.x > 0.5;
    float3 baseTint = P[6].rgb;

    float3 col = baseTint;
    if (hasBackdrop)
    {
        float2 uvp = uv + P[7].zw * gViewport.zw;
        col = SampleBackdrop(uvp, P[0].y);
        if (P[7].y > 0.5)
        {
            // Auto theme: bright desktops get dimmed more so glass keeps its depth.
            float3 avg = gBackdropTex.SampleLevel(gLinear, float2(0.5, 0.5), gBackdrop.x + 2.0).rgb;
            float lum = dot(avg, LUMA);
            dim = saturate(dim + (lum - 0.3) * 0.55 * alpha);
        }
        float luma = dot(col, LUMA);
        col = lerp(col, luma.xxx, 0.22 * alpha);
    }

    // Grade towards the deep base tint; a touch darker at the top for a cinematic falloff.
    col = lerp(col, baseTint, dim);
    col *= 1.0 - P[6].w * 0.22 * (1.0 - uv.y) * alpha;

    // Ambient light from the selected app, plus faint neighbours.
    float3 light = 0;
    float2 d0 = (px - P[2].xy) / max(P[2].zw, 1.0);
    light += P[1].rgb * P[1].w * exp(-dot(d0, d0) * 1.7);
    float2 d1 = (px - P[5].xy) / max(P[2].zw * 0.75, 1.0);
    light += P[3].rgb * P[3].w * exp(-dot(d1, d1) * 2.2);
    float2 d2 = (px - P[5].zw) / max(P[2].zw * 0.75, 1.0);
    light += P[4].rgb * P[4].w * exp(-dot(d2, d2) * 2.2);

    // Vignette.
    float2 v = (uv - 0.5) * float2(1.0, 1.2);
    float vig = 1.0 - P[0].w * smoothstep(0.3, 1.0, length(v));

    if (gBackdrop.w > 0.5)
    {
        // OLED: keep true blacks black.
        col = max(col - 0.012 * alpha, 0.0);
    }

    float grain = (Hash12(px + frac(gCamera.x * 7.0) * 311.0) - 0.5) * P[7].x;

    if (hasBackdrop)
    {
        col = (col + light) * vig + grain;
        return float4(Dither(col, px) * alpha, alpha);
    }

    // No capture available: a translucent scrim over the live desktop.
    float a = alpha * saturate(dim + 0.08);
    float3 premul = baseTint * vig * a + light * alpha + grain * a;
    return float4(Dither(premul, px), a);
}

// ----------------------------- glass card -----------------------------
//  P4 = preview rect (x0, y0, x1, y1, local px)   P5 = preview uv rect
//  P6 = outer radius, preview radius, border, glass opacity
//  P7 = accent rgb, focus
//  P8 = shadow offset y, shadow sigma, shadow alpha, aura strength
//  P9 = aura radius, hover, sheen, frost lod (<0 = off)
//  P10 = preview lod bias, brightness, desaturate, preview mix
//  P11 = glass tint rgb, flags (1 minimised, 2 hung, 4 reflection, 8 group)

float4 PSCard(VSOut i) : SV_Target
{
    float2 L = i.local;
    float2 halfSize = abs(P[1].xy);
    float2 oc = P[3].xy;
    float radius = P[6].x;
    float opacity = P[0].w;
    float3 accent = P[7].rgb;
    float focus = P[7].w;
    float hover = P[9].y;
    float flags = P[11].w;
    bool reflection = HasFlag(flags, 4.0);

    float aa = max(length(fwidth(L)) * 0.7, 0.35);
    float d = SdRoundRect(L - oc, halfSize, radius);
    float inside = 1.0 - smoothstep(-aa, aa, d);

    // Normalised position inside the outer rect: (0,0) top-left ... (1,1) bottom-right.
    float2 nuv = (L - oc) / (halfSize * 2.0) + 0.5;

    // -- Glass body --
    float3 tint = P[11].rgb;
    bool frost = P[9].w >= 0.0 && gGlobals.x > 0.5;
    float glassOpacity = P[6].w;
    float3 glass = tint;
    if (frost)
    {
        float3 behind = SampleBackdrop(i.screen * gViewport.zw, P[9].w);
        glass = lerp(behind * 0.62, tint, glassOpacity);
    }
    glass += accent * (0.035 + 0.05 * focus);
    glass *= 1.0 + 0.10 * (1.0 - nuv.y);
    glass += accent * 0.12 * smoothstep(0.55, 1.0, nuv.y) * (0.35 + 0.65 * focus);

    // Soft specular sheen across the upper-left of the glass.
    float2 sheenP = (nuv - float2(0.18, -0.1)) * float2(0.9, 1.7);
    float sheen = saturate(1.0 - length(sheenP));
    glass += P[9].z * sheen * sheen * (0.7 + 0.6 * hover);

    // -- Live preview --
    float4 rect = P[4];
    float2 pc = (rect.xy + rect.zw) * 0.5;
    float2 ph = (rect.zw - rect.xy) * 0.5;
    float pr = P[6].y;
    float pd = SdRoundRect(L - pc, ph, pr);
    float pin = 1.0 - smoothstep(-aa, aa, pd);

    float2 t = saturate((L - rect.xy) / max(rect.zw - rect.xy, 1e-3));
    float2 uv = lerp(P[5].xy, P[5].zw, t);
    float3 live = gTex.SampleBias(gLinear, uv, P[10].x).rgb;
    float brightness = P[10].y;
    float desat = P[10].z + (HasFlag(flags, 2.0) ? 0.45 : 0.0);
    live = lerp(live, dot(live, LUMA).xxx, saturate(desat)) * brightness;

    // Fallback surface: a deep accent gradient with a soft highlight (the icon is drawn on top).
    float2 fp = (L - pc) / max(ph, 1.0);
    float3 fallback = lerp(accent * 0.50, accent * 0.14, saturate(t.y * 0.9 + 0.1));
    fallback += accent * 0.35 * exp(-dot(fp - float2(0.0, -0.35), fp - float2(0.0, -0.35)) * 1.6);
    fallback = lerp(fallback, tint * 1.4, 0.35) * brightness;

    float3 content = lerp(fallback, live, P[10].w);

    // Hairline around the preview: dark outside, light inside - reads as a recessed screen.
    float pEdge = 1.0 - smoothstep(0.0, aa * 2.0 + 0.6, abs(pd));
    content = lerp(content, content * 0.55, pEdge * 0.35 * pin);

    float3 body = lerp(glass, content, pin);

    // -- Rim light --
    float border = P[6].z;
    float rim = (1.0 - smoothstep(0.0, aa * 1.6 + 0.9, -d)) * inside;
    float3 rimColor = lerp(float3(1.0, 1.0, 1.0) * 0.55, accent * 1.2, smoothstep(0.25, 1.0, nuv.y));
    body += rimColor * rim * border * (0.35 + 0.45 * focus + 0.4 * hover);

    // Minimised windows with a cached frame are shown slightly dimmer.
    if (HasFlag(flags, 1.0) && P[10].w > 0.5)
    {
        body = lerp(body, body * 0.72, pin);
    }

    float bodyA = inside * lerp(frost ? 1.0 : glassOpacity, 1.0, pin) * opacity;

    // -- Outside: shadow + aura --
    float outside = 1.0 - inside;
    float shadowSigma = max(P[8].y, 1.0);
    float sd = SdRoundRect(L - oc - float2(0.0, P[8].x), halfSize - shadowSigma * 0.35, radius);
    float shadow = GaussCoverage(sd, shadowSigma) * P[8].z;
    float aura = exp(-max(d, 0.0) / max(P[9].x, 1.0)) * P[8].w * 0.35;

    if (reflection)
    {
        // Mirrored copy on a glossy floor: strongest where it touches the card.
        float fade = saturate((L.y - (oc.y - halfSize.y)) / (halfSize.y * 2.0));
        fade = fade * fade * fade;
        bodyA *= fade;
        shadow = 0.0;
        aura = 0.0;
    }

    float3 premul = Dither(body, i.screen) * bodyA;
    float4 under = float4(accent * aura * outside * opacity, shadow * outside * opacity);
    return float4(premul, bodyA) + under * (1.0 - bodyA);
}

// ----------------------------- sprite (icons, text) -----------------------------
//  P4 = rect (x0, y0, x1, y1, local px)   P5 = uv rect   P6 = premultiplied tint   P7 = lod bias

float4 PSSprite(VSOut i) : SV_Target
{
    float4 rect = P[4];
    float2 t = (i.local - rect.xy) / max(rect.zw - rect.xy, 1e-3);
    float2 uv = lerp(P[5].xy, P[5].zw, t);
    float4 c = gTex.SampleBias(gLinear, uv, P[7].x);
    return c * P[6] * P[0].w;
}

// ----------------------------- glow (soft light) -----------------------------
//  P4 = rgb, intensity   P5 = falloff, core, streak direction xy

float4 PSGlow(VSOut i) : SV_Target
{
    float2 n = (i.local - P[3].xy) / max(abs(P[1].xy), 1e-3);
    float2 streak = P[5].zw;
    float sl = length(streak);
    if (sl > 1e-3)
    {
        // Motion trail: compress the falloff along the direction of travel.
        float2 dir = streak / sl;
        float along = dot(n, dir);
        n -= dir * along * (sl / (1.0 + sl));
    }
    float r2 = dot(n, n);
    float k = P[5].x * 2.0;
    float g = exp(-r2 * k) + P[5].y * exp(-r2 * k * 7.0);
    g *= 1.0 - smoothstep(0.8, 1.0, sqrt(r2));
    float3 c = P[4].rgb * g * P[4].w * P[0].w;
    return float4(Dither(c, i.screen), 0.0);
}

// ----------------------------- orbit line -----------------------------
//  P4 = rgb, alpha   P5 = radii xy, thickness, glow width
//  P6 = front alpha, back alpha, draw-in (0..1), unused
//  P7 = front highlight, unused, time, velocity

float4 PSOrbit(VSOut i) : SV_Target
{
    float2 L = i.local;
    float2 R = max(P[5].xy, 1.0);
    float thickness = P[5].z;
    float glowW = max(P[5].w, 1.0);

    // First-order distance to the ellipse.
    float k = length(L / R);
    float g = length(L / (R * R));
    float d = abs((k - 1.0) * k / max(g, 1e-5));

    float aa = max(length(fwidth(L)) * 0.7, 0.5);
    float lineCov = 1.0 - smoothstep(thickness * 0.5, thickness * 0.5 + aa, d);
    float glow = exp(-d / glowW) * 0.28;

    float theta = atan2(L.x / R.x, L.y / R.y);      // 0 = front (towards the viewer), +/-pi = back
    float front = (1.0 + cos(theta)) * 0.5;
    float a = lerp(P[6].y, P[6].x, pow(front, 1.3));

    // Draw-in: the orbit sweeps open from the front on both sides.
    float reach = P[6].z * 3.14159265;
    a *= 1.0 - smoothstep(reach - 0.35, reach + 0.02, abs(theta));

    // A soft brighter arc at the front, and a faint comet slowly travelling along the orbit.
    float hl = P[7].x * exp(-theta * theta / 0.55);
    float cometAngle = frac(P[7].z * 0.021 + P[7].w * 0.05) * 6.2831853 - 3.14159265;
    float dc = theta - cometAngle;
    dc = dc - 6.2831853 * floor((dc + 3.14159265) / 6.2831853);
    float comet = 0.55 * exp(-dc * dc / 0.05) * step(dc, 0.0) + 0.55 * exp(-dc * dc / 0.004);

    float intensity = (lineCov * 0.85 + glow) * a * (1.0 + hl + comet) * P[4].w * P[0].w;
    return float4(Dither(P[4].rgb * intensity, i.screen), 0.0);
}

// ----------------------------- glass pill (chrome) -----------------------------
//  P4 = radius, border, glass opacity, frost lod   P5 = tint rgb, accent mix
//  P6 = accent rgb, focus glow                        P7 = centre xy (local), shadow off, outline mode

float4 PSPill(VSOut i) : SV_Target
{
    float2 L = i.local - P[7].xy;
    float2 halfSize = abs(P[1].xy);
    float radius = P[4].x;
    float aa = max(length(fwidth(i.local)) * 0.7, 0.35);
    float d = SdRoundRect(L, halfSize, radius);
    float inside = 1.0 - smoothstep(-aa, aa, d);
    float2 nuv = L / (halfSize * 2.0) + 0.5;

    if (P[7].w > 0.5)
    {
        // Outline mode (quick-switch highlight around a real window): a crisp accent hairline
        // and a soft outer bloom, pure light, no body.
        float edge = 1.0 - smoothstep(0.6, 0.6 + aa * 1.5, abs(d + 0.9));
        float bloom = exp(-max(d, 0.0) / 22.0) * (1.0 - inside);
        float3 lightColor = P[6].rgb * (edge * 0.95 + bloom * 0.55) * P[6].w * P[0].w;
        return float4(Dither(lightColor, i.screen), 0.0);
    }

    float3 tint = P[5].rgb;
    float3 accent = P[6].rgb;
    bool frost = P[4].w >= 0.0 && gGlobals.x > 0.5;
    float3 glass = tint;
    if (frost)
    {
        glass = lerp(SampleBackdrop(i.screen * gViewport.zw, P[4].w) * 0.6, tint, P[4].z);
    }
    glass += accent * 0.16 * P[5].w;
    glass *= 1.0 + 0.12 * (1.0 - nuv.y);

    float rim = (1.0 - smoothstep(0.0, aa * 1.6 + 0.9, -d)) * inside;
    float3 rimColor = lerp(float3(0.62, 0.62, 0.62), accent, saturate(P[6].w + smoothstep(0.4, 1.0, nuv.y) * 0.4));
    glass += rimColor * rim * (0.25 + 0.5 * P[4].y + 0.6 * P[6].w);

    float bodyA = inside * (frost ? 1.0 : P[4].z) * P[0].w;
    float outside = 1.0 - inside;
    float shadow = GaussCoverage(SdRoundRect(L - float2(0.0, 3.0), halfSize, radius), 8.0) * 0.35 * (1.0 - saturate(P[7].z));
    float glow = exp(-max(d, 0.0) / 10.0) * P[6].w * 0.45;
    float4 under = float4(accent * glow * outside * P[0].w, shadow * outside * P[0].w);
    return float4(Dither(glass, i.screen) * bodyA, bodyA) + under * (1.0 - bodyA);
}

// ----------------------------- image processing (renderer-internal) -----------------------------
//  Fullscreen passes. gViewport = target size. P0 = source texel size xy, source lod, unused.

// 13-tap downsample (Jimenez 2014): a smooth, alias-free 2x reduction.
// P1.xy = uv extent of the valid content inside the source texture.
float4 PSDownsample(VSOut i) : SV_Target
{
    float2 uv = i.local * P[1].xy;
    float2 t = P[0].xy;
    float lod = P[0].z;
    float4 a = gTex.SampleLevel(gLinear, uv + t * float2(-2, -2), lod);
    float4 b = gTex.SampleLevel(gLinear, uv + t * float2(0, -2), lod);
    float4 c = gTex.SampleLevel(gLinear, uv + t * float2(2, -2), lod);
    float4 d = gTex.SampleLevel(gLinear, uv + t * float2(-1, -1), lod);
    float4 e = gTex.SampleLevel(gLinear, uv + t * float2(1, -1), lod);
    float4 f = gTex.SampleLevel(gLinear, uv + t * float2(-2, 0), lod);
    float4 g = gTex.SampleLevel(gLinear, uv, lod);
    float4 h = gTex.SampleLevel(gLinear, uv + t * float2(2, 0), lod);
    float4 j = gTex.SampleLevel(gLinear, uv + t * float2(-1, 1), lod);
    float4 k = gTex.SampleLevel(gLinear, uv + t * float2(1, 1), lod);
    float4 l = gTex.SampleLevel(gLinear, uv + t * float2(-2, 2), lod);
    float4 m = gTex.SampleLevel(gLinear, uv + t * float2(0, 2), lod);
    float4 n = gTex.SampleLevel(gLinear, uv + t * float2(2, 2), lod);
    float4 o = (d + e + j + k) * 0.125;
    o += (a + b + g + f) * 0.03125;
    o += (b + c + h + g) * 0.03125;
    o += (f + g + l + m) * 0.03125;
    o += (g + h + m + n) * 0.03125;
    return float4(o.rgb, 1.0);
}

// Area-weighted resample used to shrink captured windows to their cache size.
// P0.xy = source texel size, P0.z = taps per axis (1..6), P0.w = footprint in source texels.
// P1.xy = uv extent of the valid content inside the source texture.
float4 PSResample(VSOut i) : SV_Target
{
    float2 uv = i.local * P[1].xy;
    float2 t = P[0].xy;
    int taps = clamp((int)P[0].z, 1, 6);
    float footprint = P[0].w;
    float3 acc = 0;
    float wsum = 0;
    for (int y = 0; y < 6; y++)
    {
        if (y >= taps) break;
        for (int x = 0; x < 6; x++)
        {
            if (x >= taps) break;
            float2 o = (float2(x, y) + 0.5) / float(taps) - 0.5;
            float wgt = (1.0 - abs(o.x)) * (1.0 - abs(o.y));
            acc += gTex.SampleLevel(gLinear, uv + o * footprint * t, 0).rgb * wgt;
            wsum += wgt;
        }
    }
    return float4(acc / max(wsum, 1e-4), 1.0);
}
