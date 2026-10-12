namespace WadSmith.App.ThreeD;

/// <summary>
/// Where the 3D view looks from: a position in map units (z up), a direction (yaw 0 faces east, 90
/// north; pitch negative looks down) and a vertical field of view in degrees.
/// </summary>
internal readonly record struct Camera3D(double X, double Y, double Z, double Yaw, double Pitch, double Fov = 74)
{
    public (double X, double Y, double Z) Forward
    {
        get
        {
            var yaw = Yaw * Math.PI / 180;
            var pitch = Pitch * Math.PI / 180;
            return (Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        }
    }

    /// <summary>How far an orbit camera must stand to fit something <paramref name="size"/> across into a view of this shape.</summary>
    public static double FitDistance(double size, double aspect, double fov = 74)
    {
        var tanV = Math.Tan(fov * Math.PI / 360);
        var tanH = tanV * aspect;
        return size / 2 / Math.Min(tanV, tanH) * 1.05 + 128;
    }

    /// <summary>A camera at <paramref name="distance"/> from a target, looking at it (orbiting).</summary>
    public static Camera3D Orbit(double tx, double ty, double tz, double yaw, double pitch, double distance)
    {
        var y = yaw * Math.PI / 180;
        var p = pitch * Math.PI / 180;
        return new Camera3D(tx - Math.Cos(p) * Math.Cos(y) * distance, ty - Math.Cos(p) * Math.Sin(y) * distance, tz - Math.Sin(p) * distance, yaw, pitch);
    }

    /// <summary>
    /// The view-projection matrix (column-major, for OpenGL). <paramref name="flipY"/> renders upside
    /// down, so a read-back image comes out top row first.
    /// </summary>
    public float[] ViewProjection(double aspect, bool flipY)
    {
        var (fx, fy, fz) = Forward;

        // right = forward × up (z), then up = right × forward.
        var rx = fy;
        var ry = -fx;
        var rz = 0.0;
        var rl = Math.Sqrt(rx * rx + ry * ry);
        if (rl < 1e-9)
        {
            rx = 1;
            ry = 0;
            rl = 1;
        }

        rx /= rl;
        ry /= rl;
        var ux = ry * fz - rz * fy;
        var uy = rz * fx - rx * fz;
        var uz = rx * fy - ry * fx;

        double[] view =
        [
            rx, ux, -fx, 0,
            ry, uy, -fy, 0,
            rz, uz, -fz, 0,
            -(rx * X + ry * Y + rz * Z), -(ux * X + uy * Y + uz * Z), fx * X + fy * Y + fz * Z, 1,
        ];

        const double near = 2;
        const double far = 131072;
        var f = 1 / Math.Tan(Fov * Math.PI / 360);
        var flip = flipY ? -1 : 1;
        double[] projection =
        [
            f / aspect, 0, 0, 0,
            0, f * flip, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0,
        ];

        var result = new float[16];
        for (var col = 0; col < 4; col++)
        {
            for (var row = 0; row < 4; row++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++) sum += projection[k * 4 + row] * view[col * 4 + k];
                result[col * 4 + row] = (float)sum;
            }
        }

        return result;
    }
}
