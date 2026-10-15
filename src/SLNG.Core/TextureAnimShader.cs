using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>FEAT-PERF-16: what the prim shaders need to run an llSetTextureAnim themselves, and a C#
/// MIRROR of the shader's formula (slng_tex_anim_* in app/materials/prim/prim_common.gdshaderinc) so tests
/// can hold it against <see cref="TextureAnimator.Evaluate"/>. Change both together.</summary>
public static class TextureAnimShader
{
    /// <summary>The two vec4 uniforms of an animated face.
    /// a = (mode bits, SizeX, SizeY, Start); b = (Length, Rate, start time, 1 = animated).
    /// A LOOP animation starts at time 0 (a shared epoch): its phase is not observable, and equal
    /// animations then pack to equal values, so faces can share a material (FEAT-PERF-20 A1). A
    /// non-looping one plays once from when it arrived, so it keeps its own start time.</summary>
    public static (Vector4 A, Vector4 B) Pack(in TextureAnimation anim, float nowSeconds)
    {
        float start = (anim.Mode & TextureAnimFlags.Loop) != 0 ? 0f : nowSeconds;
        return (new Vector4((byte)anim.Mode, anim.SizeX, anim.SizeY, anim.Start),
                new Vector4(anim.Length, anim.Rate, start, 1f));
    }

    /// <summary>The uniforms of a face that is NOT animated.</summary>
    public static readonly Vector4 Off = Vector4.Zero;

    /// <summary>C#'s float % (truncated, keeps the sign of the dividend). GLSL's mod() is floored, so the
    /// shader must use this, not mod().</summary>
    public static float Fmod(float a, float b) => a - b * MathF.Truncate(a / b);

    /// <summary>MathF.Round(x, MidpointRounding.AwayFromZero).</summary>
    public static float RoundAway(float x) => MathF.Sign(x) * MathF.Floor(MathF.Abs(x) + 0.5f);

    /// <summary>Line-for-line what the shader does. Returns the same frame TextureAnimator.Evaluate does,
    /// computed only from the packed uniforms and the time.</summary>
    public static TextureAnimFrame Evaluate(Vector4 a, Vector4 b, float timeSeconds)
    {
        if (b.W < 0.5f) return TextureAnimFrame.Idle;

        int mode = (int)(a.X + 0.5f);

        bool isLoop = (mode & 2) != 0;
        bool isReverse = (mode & 4) != 0;
        bool isPing = (mode & 8) != 0;
        bool isSmooth = (mode & 16) != 0;
        float sizeX = a.Y;
        float sizeY = a.Z;
        float start = a.W;
        float len = b.X;
        float rate = b.Y;
        float elapsed = timeSeconds - b.Z;

        float numFrames = len != 0f ? len : MathF.Max(1f, sizeX * sizeY);
        float fullLength = numFrames;
        if (isPing)
        {
            if (isSmooth) fullLength = 2f * numFrames;
            else if (isLoop) fullLength = MathF.Max(1f, 2f * numFrames - 2f);
            else fullLength = MathF.Max(1f, 2f * numFrames - 1f);
        }

        float fc = elapsed * rate;
        if (isLoop) fc = Fmod(fc, fullLength);
        else fc = MathF.Min(fullLength - 1f, fc);

        if (!isSmooth)
        {
            fc = MathF.Floor(fc + 0.01f);
            fc = MathF.Min(fullLength - 1f, fc);
        }
        if (isPing && fc >= numFrames)
        {
            fc = isSmooth ? numFrames - (fc - numFrames) : (numFrames - 1.99f) - (fc - numFrames);
        }
        if (isReverse)
        {
            fc = isSmooth ? numFrames - fc : (numFrames - 0.99f) - fc;
        }
        fc += start;
        if (!isSmooth) fc = RoundAway(fc);

        if ((mode & 32) != 0) return new TextureAnimFrame(TextureAnimResult.Rotate, 0f, 0f, 1f, 1f, fc);
        if ((mode & 64) != 0) return new TextureAnimFrame(TextureAnimResult.Scale, 0f, 0f, fc, fc, 0f);
        if (sizeX != 0f && sizeY != 0f)
        {
            float ss = 1f / sizeX;
            float st = 1f / sizeY;
            float xFrame = Fmod(fc, sizeX);
            float yFrame = MathF.Truncate(fc / sizeX);
            return new TextureAnimFrame(
                TextureAnimResult.Translate | TextureAnimResult.Scale,
                (-0.5f + 0.5f * ss) + xFrame * ss,
                (0.5f - 0.5f * st) - yFrame * st,
                ss, st, 0f);
        }
        return new TextureAnimFrame(TextureAnimResult.Translate, fc, 0f, 1f, 1f, 0f);
    }
}
