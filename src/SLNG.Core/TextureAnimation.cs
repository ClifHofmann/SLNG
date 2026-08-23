using System;

namespace SLNG.Core;

/// <summary>SL's texture-animation mode bits (llprimitive/lltextureanim.h:52-61). The wire
/// carries them as one byte in the ObjectUpdate TextureAnim block.</summary>
[Flags]
public enum TextureAnimFlags : byte
{
    Off = 0x00,
    /// <summary>Animation is running. Without this bit the whole block is inert.</summary>
    On = 0x01,
    Loop = 0x02,
    Reverse = 0x04,
    PingPong = 0x08,
    /// <summary>Slide continuously instead of stepping frame to frame.</summary>
    Smooth = 0x10,
    /// <summary>Animate the face's texture ROTATION (radians) instead of stepping frames.</summary>
    Rotate = 0x20,
    /// <summary>Animate the face's texture SCALE instead of stepping frames.</summary>
    Scale = 0x40,
}

/// <summary>Which components of a face's texture placement one animation step actually drives.
/// Whatever is NOT flagged keeps the face's own TextureEntry value -- the viewer reads those back
/// off the TE per face (llvovolume.cpp, LLVOVolume::animateTextures).</summary>
[Flags]
public enum TextureAnimResult
{
    None = 0,
    Translate = 1,
    Scale = 2,
    Rotate = 4,
}

/// <summary>
/// The ObjectUpdate TextureAnim block: SL's llSetTextureAnim state for one object.
/// Engine- and protocol-neutral mirror of LibreMetaverse's Primitive.TextureAnimation.
/// </summary>
/// <param name="Mode">Mode bits; <see cref="TextureAnimFlags.On"/> gates everything else.</param>
/// <param name="Face">SL face number this animation applies to, or -1 for every face. The wire
/// value is a U8 that the viewer reads into an S8 (lltextureanim.cpp, unpackTAMessage), so 255
/// means "all faces" -- see <see cref="FromWire"/>.</param>
/// <param name="SizeX">Frame-grid columns.</param>
/// <param name="SizeY">Frame-grid rows.</param>
/// <param name="Start">Frame (or angle/scale) the animation starts at.</param>
/// <param name="Length">Number of frames to play; 0 means "the whole SizeX*SizeY grid".</param>
/// <param name="Rate">Frames per second. Also radians/second for Rotate and units/second for
/// Scale, since those modes reinterpret the frame counter directly.</param>
public readonly record struct TextureAnimation(
    TextureAnimFlags Mode, sbyte Face, byte SizeX, byte SizeY, float Start, float Length, float Rate)
{
    public bool IsOn => (Mode & TextureAnimFlags.On) != 0;

    /// <summary>Applies the viewer's own unpack-time normalisation (lltextureanim.cpp,
    /// unpackTAMessage) to the raw wire bytes. Two rules live there and nowhere else, so a
    /// straight field copy from LibreMetaverse (which does neither) is wrong:
    ///
    ///  * <paramref name="face"/> is read into a SIGNED byte, which is what turns the wire's 255
    ///    into -1 = "all faces". Left unsigned it would mean face 255, i.e. no face at all.
    ///  * a non-SMOOTH animation clamps SizeX/SizeY up to 1. The grid arithmetic divides by them,
    ///    and a stepped animation authored with a 0 there must behave as a 1x1 grid rather than
    ///    fall into the sizeless branch.
    /// </summary>
    public static TextureAnimation FromWire(byte mode, byte face, byte sizeX, byte sizeY, float start, float length, float rate)
    {
        var flags = (TextureAnimFlags)mode;
        if ((flags & TextureAnimFlags.Smooth) == 0)
        {
            if (sizeX < 1) sizeX = 1;
            if (sizeY < 1) sizeY = 1;
        }
        return new TextureAnimation(flags, unchecked((sbyte)face), sizeX, sizeY, start, length, rate);
    }
}

/// <summary>One evaluated animation step: the texture placement to use this frame, plus which of
/// its components the animation actually owns.</summary>
public readonly record struct TextureAnimFrame(
    TextureAnimResult Driven, float OffsetS, float OffsetT, float ScaleS, float ScaleT, float Rotation)
{
    public static readonly TextureAnimFrame Idle = new(TextureAnimResult.None, 0f, 0f, 1f, 1f, 0f);
}

/// <summary>
/// Port of LLViewerTextureAnim::animateTextures (newview/llviewertextureanim.cpp) -- the
/// frame-counter maths behind llSetTextureAnim.
/// </summary>
public static class TextureAnimator
{
    /// <summary>Evaluates the animation at <paramref name="elapsedSeconds"/> since it started.
    ///
    /// The viewer keeps a per-object timer and, in SMOOTH mode, accumulates
    /// <c>getElapsedTimeAndResetF32() * mRate + mLastTime</c> instead of reading total elapsed
    /// time. That accumulation equals <c>elapsed * rate</c> for a constant rate, which is the
    /// only case that can be observed: the rate changes only when a new TextureAnim block
    /// arrives, and that restarts the animation here anyway. Keeping this a pure function of
    /// elapsed time is also what makes it testable without a clock.</summary>
    public static TextureAnimFrame Evaluate(in TextureAnimation anim, float elapsedSeconds)
    {
        if (!anim.IsOn) return TextureAnimFrame.Idle;

        var mode = anim.Mode;

        float numFrames = anim.Length != 0f
            ? anim.Length
            : Math.Max(1f, anim.SizeX * anim.SizeY);

        float fullLength;
        if ((mode & TextureAnimFlags.PingPong) != 0)
        {
            if ((mode & TextureAnimFlags.Smooth) != 0)
                fullLength = 2f * numFrames;
            else if ((mode & TextureAnimFlags.Loop) != 0)
                fullLength = Math.Max(1f, 2f * numFrames - 2f);
            else
                fullLength = Math.Max(1f, 2f * numFrames - 1f);
        }
        else
        {
            fullLength = numFrames;
        }

        float frameCounter = elapsedSeconds * anim.Rate;

        if ((mode & TextureAnimFlags.Loop) != 0)
            frameCounter %= fullLength;
        else
            frameCounter = Math.Min(fullLength - 1f, frameCounter);

        if ((mode & TextureAnimFlags.Smooth) == 0)
        {
            // The +0.01 is the viewer's own step bias; the second clamp is there because that
            // bias can push the counter past the end of the animation.
            frameCounter = MathF.Floor(frameCounter + 0.01f);
            frameCounter = Math.Min(fullLength - 1f, frameCounter);
        }

        if ((mode & TextureAnimFlags.PingPong) != 0 && frameCounter >= numFrames)
        {
            frameCounter = (mode & TextureAnimFlags.Smooth) != 0
                ? numFrames - (frameCounter - numFrames)
                : (numFrames - 1.99f) - (frameCounter - numFrames);
        }

        if ((mode & TextureAnimFlags.Reverse) != 0)
        {
            frameCounter = (mode & TextureAnimFlags.Smooth) != 0
                ? numFrames - frameCounter
                : (numFrames - 0.99f) - frameCounter;
        }

        frameCounter += anim.Start;

        if ((mode & TextureAnimFlags.Smooth) == 0)
            frameCounter = MathF.Round(frameCounter, MidpointRounding.AwayFromZero);

        if ((mode & TextureAnimFlags.Rotate) != 0)
        {
            return new TextureAnimFrame(TextureAnimResult.Rotate, 0f, 0f, 1f, 1f, frameCounter);
        }

        if ((mode & TextureAnimFlags.Scale) != 0)
        {
            return new TextureAnimFrame(TextureAnimResult.Scale, 0f, 0f, frameCounter, frameCounter, 0f);
        }

        // Frame stepping. With a frame grid this is a scrolling window over the sheet; without one
        // (SMOOTH with SizeX/SizeY 0 -- the classic "scroll the texture sideways" setup) the
        // counter slides U directly at full scale.
        if (anim.SizeX != 0 && anim.SizeY != 0)
        {
            float scaleS = 1f / anim.SizeX;
            float scaleT = 1f / anim.SizeY;
            float xFrame = frameCounter % anim.SizeX;
            int yFrame = (int)(frameCounter / anim.SizeX);
            return new TextureAnimFrame(
                TextureAnimResult.Translate | TextureAnimResult.Scale,
                (-0.5f + 0.5f * scaleS) + xFrame * scaleS,
                (0.5f - 0.5f * scaleT) - yFrame * scaleT,
                scaleS, scaleT, 0f);
        }

        return new TextureAnimFrame(
            TextureAnimResult.Translate,
            // scale_s is 1 here, so the viewer's (-0.5 + 0.5*scale_s) centring term cancels and
            // the offset is the raw frame counter.
            frameCounter,
            0f, 1f, 1f, 0f);
    }
}
