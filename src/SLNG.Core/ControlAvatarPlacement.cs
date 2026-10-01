using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// FEAT-ANIMESH-01: how a control avatar -- the bodyless avatar skeleton an animated-mesh object
/// owns -- is turned in the world.
/// </summary>
/// <remarks>
/// Ported from <c>LLControlAvatar::matchVolumeTransform</c> (llcontrolavatar.cpp:203-246) and
/// <c>LLSkinningUtil::getUnscaledQuaternion</c> (llskinningutil.cpp:383-406). The skeleton's world
/// rotation is <c>bind_rot * obj_rot</c>: the root prim's own rotation, preceded by the rotation
/// that undoes whatever axis conversion the mesh's bind shape matrix carries. That is what makes a
/// robot exported Y-up (or Z-forward, or whatever the creator's tool wrote) stand upright without
/// the creator rotating the prim.
///
/// <para>Everything here is in the viewer's ROW-VECTOR convention (<c>v * M</c>), which
/// <see cref="Matrix4x4"/> shares, so matrices map one to one. The quaternion step deliberately
/// goes through matrices: the viewer's <c>a * b</c> reads "a first, then b", while
/// <see cref="Quaternion"/>'s operator reads the other way round.</para>
/// </remarks>
public static class ControlAvatarPlacement
{
    /// <summary>Smallest determinant of the row-normalised 3x3 the viewer still inverts
    /// (<c>LLMatrix3::invert</c>, VERY_SMALL_DETERMINANT). A matrix of unit rows has |det| = 1 when
    /// they are orthogonal, so anything near zero means two rows are (nearly) parallel.</summary>
    private const double SmallDeterminant = 1e-6;

    /// <summary>The rotation of the world frame the control avatar's skeleton sits in: the bind
    /// rotation first, then the object's rotation (<c>mRoot->setWorldRotation(bind_rot * obj_rot)</c>).
    /// </summary>
    /// <param name="bindShape">The bind shape matrix of the ROOT prim's mesh skin. A root prim
    /// with no skin has no bind rotation: use <see cref="Compose"/> with
    /// <see cref="Quaternion.Identity"/> for that.</param>
    /// <param name="objectRotation">The root prim's world rotation.</param>
    public static Quaternion WorldRotation(Matrix4x4 bindShape, Quaternion objectRotation)
        => Compose(BindRotation(bindShape), objectRotation);

    /// <summary>
    /// <c>bind_rot</c>: the rotation half of a bind shape matrix, inverted. Takes the upper-left
    /// 3x3, divides each ROW by its own length (a zero-length row is left alone), inverts that and
    /// converts it to a quaternion -- <c>getUnscaledQuaternion</c> line for line.
    /// </summary>
    /// <remarks>
    /// Normalising rows, not columns, matters once the scale is not uniform. With the viewer's
    /// <c>p * M</c> order, <c>M = S * R</c> (scale, then rotate) has rows that are R's rows times
    /// the scale, so the rows normalise back to R exactly; <c>M = R * S</c> (rotate, then stretch)
    /// does not, and what comes out is only approximately a rotation. The quaternion extraction
    /// then assumes it is one regardless, exactly as the viewer's does.
    ///
    /// <para>A singular or non-finite matrix gives the identity. (The viewer leaves its
    /// normalised matrix uninverted in that case and converts THAT; the result is garbage either
    /// way and never reached by a mesh anyone could have uploaded.)</para>
    /// </remarks>
    public static Quaternion BindRotation(Matrix4x4 bindShape)
    {
        var m = new double[3, 3]
        {
            { bindShape.M11, bindShape.M12, bindShape.M13 },
            { bindShape.M21, bindShape.M22, bindShape.M23 },
            { bindShape.M31, bindShape.M32, bindShape.M33 },
        };

        for (int r = 0; r < 3; r++)
        {
            double len = Math.Sqrt(m[r, 0] * m[r, 0] + m[r, 1] * m[r, 1] + m[r, 2] * m[r, 2]);
            if (double.IsNaN(len) || double.IsInfinity(len)) return Quaternion.Identity;
            if (len <= 0.0) continue;
            for (int c = 0; c < 3; c++) m[r, c] /= len;
        }

        if (!TryInvert(m, out var inverse)) return Quaternion.Identity;
        return ToQuaternion(inverse);
    }

    /// <summary>The rotation "<paramref name="first"/> applied first, then <paramref name="second"/>"
    /// -- the viewer's <c>first * second</c>. Done with matrices so the operand order of
    /// <see cref="Quaternion"/>'s own operator never enters into it.</summary>
    public static Quaternion Compose(Quaternion first, Quaternion second)
    {
        var m = Matrix4x4.CreateFromQuaternion(first) * Matrix4x4.CreateFromQuaternion(second);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>A rotation as the roll / pitch / yaw an SL build floater or script shows, in
    /// degrees: <c>LLQuaternion::getEulerAngles</c> (llquaternion.cpp:888) with the viewer's
    /// <c>GIMBAL_THRESHOLD</c> (llmath.h:75). For log lines, so that a rotation can be read next
    /// to the same object's rotation field in another viewer.</summary>
    public static Vector3 EulerDegrees(Quaternion q)
    {
        const double GimbalThreshold = 0.000436;
        double x = q.X, y = q.Y, z = q.Z, w = q.W;

        double sx = 2 * (x * w - y * z);          // sine of the roll
        double sy = 2 * (y * w + x * z);          // sine of the pitch
        double ys = w * w - y * y;                // intermediate cosine 1
        double xz = x * x - z * z;                // intermediate cosine 2
        double cx = ys - xz;                      // cosine of the roll
        double cy = Math.Sqrt(sx * sx + cx * cx); // cosine of the pitch

        double roll, pitch, yaw;
        if (cy > GimbalThreshold)
        {
            roll = Math.Atan2(sx, cx);
            pitch = Math.Atan2(sy, cy);
            yaw = Math.Atan2(2 * (z * w - x * y), ys + xz);
        }
        else if (sy > 0)
        {
            roll = 0;
            pitch = Math.PI / 2;
            yaw = 2 * Math.Atan2(z + x, w + y);
        }
        else
        {
            roll = 0;
            pitch = -Math.PI / 2;
            yaw = 2 * Math.Atan2(z - x, w - y);
        }

        const double ToDegrees = 180.0 / Math.PI;
        return new Vector3((float)(roll * ToDegrees), (float)(pitch * ToDegrees), (float)(yaw * ToDegrees));
    }

    /// <summary>3x3 inverse by cofactors, refusing a determinant the viewer would not invert.</summary>
    private static bool TryInvert(double[,] m, out double[,] inverse)
    {
        inverse = new double[3, 3];
        double det =
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
          - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
          + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        if (double.IsNaN(det) || Math.Abs(det) <= SmallDeterminant) return false;

        inverse[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        inverse[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        inverse[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        inverse[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        inverse[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        inverse[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        inverse[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        inverse[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        inverse[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return true;
    }

    /// <summary><c>LLMatrix3::quaternion</c> (m3math.cpp) followed by <c>normalize()</c>: the
    /// trace-based extraction, taking the matrix in row-vector form.</summary>
    private static Quaternion ToQuaternion(double[,] m)
    {
        double[] q = new double[4]; // x, y, z, w
        double trace = m[0, 0] + m[1, 1] + m[2, 2];

        if (trace > 0.0)
        {
            double s = Math.Sqrt(trace + 1.0);
            q[3] = s / 2.0;
            s = 0.5 / s;
            q[0] = (m[1, 2] - m[2, 1]) * s;
            q[1] = (m[2, 0] - m[0, 2]) * s;
            q[2] = (m[0, 1] - m[1, 0]) * s;
        }
        else
        {
            int i = 0;
            if (m[1, 1] > m[0, 0]) i = 1;
            if (m[2, 2] > m[i, i]) i = 2;
            int j = (i + 1) % 3;
            int k = (j + 1) % 3;

            double s = Math.Sqrt((m[i, i] - (m[j, j] + m[k, k])) + 1.0);
            q[i] = s * 0.5;
            if (s != 0.0) s = 0.5 / s;
            q[3] = (m[j, k] - m[k, j]) * s;
            q[j] = (m[i, j] + m[j, i]) * s;
            q[k] = (m[i, k] + m[k, i]) * s;
        }

        double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (len <= 0.0 || double.IsNaN(len)) return Quaternion.Identity;
        return new Quaternion((float)(q[0] / len), (float)(q[1] / len), (float)(q[2] / len), (float)(q[3] / len));
    }
}
