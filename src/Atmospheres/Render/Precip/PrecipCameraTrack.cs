namespace ReDefinition.Atmospheres
{
    // The camera's place in the box frame, accumulated from its movement. The world position cannot be used directly: the floating
    // origin shifts it by large jumps. The position relative to the body centre is free of those, but its coordinates in a frame that
    // turns with the position (east, up, north) are always (0, r, 0). So the movement of the body-fixed position between two frames is
    // projected onto the axes of the frame of THIS frame and added up: the box stays where the air is, the camera moves through it,
    // and a shift of the floating origin changes nothing. Pure.
    internal sealed class PrecipCameraTrack
    {
        private double px, py, pz;
        private bool has;

        // The accumulated place along east, up and north, in metres.
        internal double E, U, N;

        // Forgets everything (the body changed).
        internal void Reset()
        {
            has = false;
            E = 0.0;
            U = 0.0;
            N = 0.0;
        }

        // One frame on: the body-fixed position of the camera and the unit axes of the frame at it, in the same coordinates.
        internal void Update(double x, double y, double z, double ex, double ey, double ez, double ux, double uy, double uz, double nx, double ny, double nz)
        {
            if (has)
            {
                double dx = x - px, dy = y - py, dz = z - pz;
                E += dx * ex + dy * ey + dz * ez;
                U += dx * ux + dy * uy + dz * uz;
                N += dx * nx + dy * ny + dz * nz;
            }
            px = x;
            py = y;
            pz = z;
            has = true;
        }
    }
}
