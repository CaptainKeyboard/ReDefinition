using System;

namespace ReDefinition.Atmospheres
{
    // The per-layer seeds of the noise (the same in the layer binder and in the tests).
    internal static class NoiseSeeds
    {
        // The layer's lookup offset in m: a place of its own from its seed (so that two layers do not share a pattern).
        internal static void BaseOffset(uint seed, int layer, out double x, out double y, out double z)
        {
            double a, b, c;
            NoiseHash.Vec(NoiseHash.Hash3(layer, 17, 29, seed), out a, out b, out c);
            x = a * 1.0e5;
            y = b * 1.0e5;
            z = c * 1.0e5;
        }

        // The seed of a layer's untiling displacement (a 24-bit number, so that the shader reads it exactly as a float).
        internal static uint LayerSeed(uint seed, int layer)
        {
            return NoiseHash.Hash3(layer, 3, 5, seed) & 0xFFFFFFu;
        }
    }

    // The three volumes of a noise set (the bytes the generators make or the GPU textures hold).
    internal sealed class NoiseVolumeSet
    {
        internal int ShapeN, DetailN, CurlN;
        internal byte[] Shape, Detail, Curl;
        // The shape volume's mip chain without its first level (level m is ShapeMips[m - 1], of size ShapeN >> m): each level is the 2 x 2 x 2 mean of the one above, equalised again
        // (NoiseVolumes.ShapeMips), so every level has the uniform spread of values the coverage threshold relies on. Null: none (a set made without the chain).
        internal byte[][] ShapeMips;
        // The number of levels of a volume of size n (down to 1 x 1 x 1).
        internal static int MipLevels(int n)
        {
            int levels = 1;
            while (n > 1) { n >>= 1; levels++; }
            return levels;
        }
        // The heap volume (CLOUDR1 P2): null where no type asks for a heap preset or it is not built yet.
        internal HeapVolumeSet Heap;
        // The milliseconds the generation took.
        internal double GenerationMs;
        // The mean of the detail volume (0 to 1): what the erosion reads where the distance LOD has dropped the detail.
        internal double DetailMean;

        // Generates the set at the sizes (blocking; the loader runs this on a worker).
        internal static NoiseVolumeSet Generate(NoiseSizes sizes, uint seed)
        {
            var set = new NoiseVolumeSet
            {
                ShapeN = sizes.Shape,
                DetailN = sizes.Detail,
                CurlN = sizes.Curl,
                Shape = NoiseVolumes.Shape(sizes.Shape, seed + NoiseVolumes.ShapeSeedOffset),
                Detail = NoiseVolumes.Detail(sizes.Detail, seed + NoiseVolumes.DetailSeedOffset, CloudLookTuning.HeapDetailHurst),
                Curl = NoiseVolumes.Curl(sizes.Curl, seed + NoiseVolumes.CurlSeedOffset)
            };
            set.ShapeMips = NoiseVolumes.ShapeMips(set.Shape, set.ShapeN);
            long sum = 0;
            for (int i = 0; i < set.Detail.Length; i++) sum += set.Detail[i];
            set.DetailMean = sum / (255.0 * set.Detail.Length);
            return set;
        }
    }

    // The numbers of one layer's noise: the scale (the shape volume's finest lattice cell, m), the switches, the layer's offset (m, its seed
    // and drift), the vertical scale of the spherical lattice (1: the planet-centred lattice itself) and the extra motion of the lookup.
    internal struct NoiseLayerParams
    {
        internal double ScaleM;
        internal bool Untile, Curl;
        internal double OffsetX, OffsetY, OffsetZ;
        internal uint Seed;
        // P03: the vertical scale of the lattice about the layer's reference radius (1: planet-centred lattice, < 1: the lattice follows the sphere's angles).
        internal double SphereScale, ReferenceRadiusM;
        // E40 L6 shape: the mip level of the shape volume the layer reads (LayerNoise.SmoothingLod from the cfg key shapeSmoothing; 0: the volume itself, between two levels the two are blended).
        internal double ShapeLod;
        // The type's smoothing in noise scales (CloudTypeDef.ShapeSmoothing, else the layer's) as the heap read takes it: the heap volume has its own voxels per noise scale, so the shape's mip level
        // (ShapeLod) does not carry over. 0: none.
        internal double SmoothingScales;
        // P18: the distance the detail has risen along the local up (m).
        internal double UpDistanceM;
        // The distance the whole pattern has been carried by the flow map (m); the lookup moves the other way.
        internal double FlowX, FlowY, FlowZ;
        // Overrides of the model constants (0: the compiled value).
        internal double WarpAmplitude, WarpCell, CurlAmplitude;
        // True: the erosion detail is read at every sample however coarse the march is (the distance LOD off).
        internal bool NoDistanceLod;
        // True: no fine octave of the erosion detail.
        internal bool NoFineDetail;
        // layers.densityRemap (CloudLookTuning): the share of the erosion that also thins a closed deck's interior; 0: the erosion acts only in the soft edge.
        internal double DensityRemap;
        // E40 L2 D1 (cfg key fibreStretch): the lookup is compressed along the unit axis (AxisX, AxisY, AxisZ) by 1 - Stretch, i.e. the features are 1 / (1 - Stretch) times longer along it;
        // 0: isotropic. The shader's axis is the body's pole (the third row of the world-to-map matrix), (0, 0, 1) in the planet frame of the tests.
        internal double Stretch, AxisX, AxisY, AxisZ;
        // PUFF (cloud.billow.*): the detail's contrast about its mean minus 1 (0: as it was), the weight of the fine octave (null: NoiseModel.FineDetailWeight) and its frequency
        // (0 or less: NoiseModel.FineDetailFrequency). The gain toward the top and the light tap belong to the shader's density and light march and have no twin here.
        internal double BillowContrastDelta, FineFrequency;
        internal double? FineWeight;
        // CLOUDR1 P2: the heap preset the point's type reads (0: the shape volume; HeapModel.PresetOf) and whether the march's spacing adds to the mip level of the heap read (the octaves
        // below the footprint are dropped, never the ones above it). The twin of the shader's gHeapPreset and _HeapP.y.
        internal int HeapPreset;
        internal bool HeapFootLod, HeapIso;

        internal static NoiseLayerParams Default(double scaleM)
        {
            return new NoiseLayerParams { ScaleM = scaleM, Untile = true, Curl = true, Seed = 1, SphereScale = 1.0 };
        }

        internal double FineWeightValue { get { return FineWeight.HasValue ? FineWeight.Value : NoiseModel.FineDetailWeight; } }
        internal double FineFrequencyValue { get { return FineFrequency > 0.0 ? FineFrequency : NoiseModel.FineDetailFrequency; } }
    }

    // The shape and erosion model on the CPU (package T02): the function the shader evaluates, in double precision, for the tests, for
    // the parity checks and for the consumers that read the layers on the CPU. P02 base shape, P04 erosion, P05 curl, P11 untiled lookup,
    // P03 spherical lattice, P18 upward speed (P12, the edge hardness by type, went with layers.softDensity).
    internal static class CloudShapeModel
    {
        // Smooth vector value noise of the integer hash at a point in lattice units, each component -1 to 1; it has no period.
        internal static void ValueVec(double px, double py, double pz, uint seed, out double vx, out double vy, out double vz)
        {
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py), iz = (int)Math.Floor(pz);
            double u = NoiseHash.Fade(px - ix), v = NoiseHash.Fade(py - iy), w = NoiseHash.Fade(pz - iz);
            vx = vy = vz = 0.0;
            for (int dx = 0; dx <= 1; dx++)
                for (int dy = 0; dy <= 1; dy++)
                    for (int dz = 0; dz <= 1; dz++)
                    {
                        double a, b, c;
                        NoiseHash.Vec(NoiseHash.Hash3(ix + dx, iy + dy, iz + dz, seed), out a, out b, out c);
                        double wt = (dx == 1 ? u : 1.0 - u) * (dy == 1 ? v : 1.0 - v) * (dz == 1 ? w : 1.0 - w);
                        vx += wt * (a * 2.0 - 1.0);
                        vy += wt * (b * 2.0 - 1.0);
                        vz += wt * (c * 2.0 - 1.0);
                    }
        }

        // The seed of the untiling warp of a layer.
        internal static uint WarpSeed(uint layerSeed)
        {
            return layerSeed + 4001u;
        }

        // The point the volumes are read at: the planet-centred point (optionally on the spherical lattice), the layer's offset, minus the
        // distance the flow has carried the pattern, the fibre stretch along the axis, then the untiling displacement.
        internal static void LookupPoint(ref NoiseLayerParams prm, double px, double py, double pz, out double qx, out double qy, out double qz)
        {
            double dx, dy, dz;
            LookupPoint(ref prm, px, py, pz, out qx, out qy, out qz, out dx, out dy, out dz);
        }

        // The same with the untiling displacement that was added to the point (zero without the warp).
        internal static void LookupPoint(ref NoiseLayerParams prm, double px, double py, double pz, out double qx, out double qy, out double qz, out double dx, out double dy, out double dz)
        {
            dx = dy = dz = 0.0;
            if (prm.SphereScale != 1.0 && prm.SphereScale > 0.0)
            {
                double r = Math.Sqrt(px * px + py * py + pz * pz);
                double g = prm.ReferenceRadiusM + (r - prm.ReferenceRadiusM) * prm.SphereScale;
                double k = r > 0.0 ? g / r : 1.0;
                px *= k;
                py *= k;
                pz *= k;
            }
            qx = px + prm.OffsetX - prm.FlowX;
            qy = py + prm.OffsetY - prm.FlowY;
            qz = pz + prm.OffsetZ - prm.FlowZ;
            if (prm.Stretch != 0.0)
            {
                double along = (qx * prm.AxisX + qy * prm.AxisY + qz * prm.AxisZ) * prm.Stretch;
                qx -= prm.AxisX * along;
                qy -= prm.AxisY * along;
                qz -= prm.AxisZ * along;
            }
            if (!prm.Untile) return;
            double amp = (prm.WarpAmplitude > 0.0 ? prm.WarpAmplitude : NoiseModel.WarpAmplitudeScales) * prm.ScaleM;
            double cell = (prm.WarpCell > 0.0 ? prm.WarpCell : NoiseModel.WarpCellScales) * prm.ScaleM;
            double wx, wy, wz;
            ValueVec(qx / cell, qy / cell, qz / cell, WarpSeed(prm.Seed), out wx, out wy, out wz);
            dx = amp * wx;
            dy = amp * wy;
            dz = amp * wz;
            qx += dx;
            qy += dy;
            qz += dz;
        }

        // The point the heap volume is read at: the lookup point with the untiling displacement's radial part taken out and HeapModel.WarpShare of its horizontal part kept, and with
        // prm.HeapIso the lattice's vertical compression (SphereScale) taken back. p is the planet-centred point, q the lookup point and d the displacement LookupPoint returned.
        internal static void HeapLookup(ref NoiseLayerParams prm, double px, double py, double pz, double qx, double qy, double qz, double dx, double dy, double dz,
                                        out double hx, out double hy, out double hz)
        {
            double r = Math.Sqrt(px * px + py * py + pz * pz);
            hx = qx - dx;
            hy = qy - dy;
            hz = qz - dz;
            if (r > 0.0)
            {
                double ux = px / r, uy = py / r, uz = pz / r, along = dx * ux + dy * uy + dz * uz;
                hx += HeapModel.WarpShare * (dx - ux * along);
                hy += HeapModel.WarpShare * (dy - uy * along);
                hz += HeapModel.WarpShare * (dz - uz * along);
            }
            else
            {
                hx += HeapModel.WarpShare * dx;
                hy += HeapModel.WarpShare * dy;
                hz += HeapModel.WarpShare * dz;
            }
            // The lattice's vertical compression taken back (cloud.heap.iso), as the erosion detail reads it.
            if (prm.HeapIso && prm.SphereScale != 1.0 && prm.SphereScale > 0.0 && r > 0.0)
            {
                double back = (1.0 - prm.SphereScale) * (r - prm.ReferenceRadiusM) / r;
                hx += px * back;
                hy += py * back;
                hz += pz * back;
            }
        }

        // Wrapped trilinear lookup of a byte volume (n^3, `stride` bytes per voxel, channel c) at unit coordinates; 0 to 1.
        internal static double Trilinear(byte[] vol, int n, int stride, int channel, double ux, double uy, double uz)
        {
            double x = ux * n - 0.5, y = uy * n - 0.5, z = uz * n - 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
            double fx = x - x0, fy = y - y0, fz = z - z0;
            double acc = 0.0;
            for (int dz = 0; dz <= 1; dz++)
                for (int dy = 0; dy <= 1; dy++)
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        int i = ((NoiseHash.Mod(z0 + dz, n) * n + NoiseHash.Mod(y0 + dy, n)) * n + NoiseHash.Mod(x0 + dx, n)) * stride + channel;
                        acc += (dx == 1 ? fx : 1.0 - fx) * (dy == 1 ? fy : 1.0 - fy) * (dz == 1 ? fz : 1.0 - fz) * vol[i];
                    }
            return acc / 255.0;
        }

        // The mip level the shape volume is read at for a layer's smoothing: the box of `scales` noise scales over the volume's voxel (the volume spans ShapePeriodScales scales in
        // `size` voxels); 0 below one voxel, and never past the last level.
        internal static double SmoothingLod(double scales, int size)
        {
            if (!(scales > 0.0) || size < 2) return 0.0;
            double voxels = scales * size / NoiseModel.ShapePeriodScales;
            double lod = voxels > 1.0 ? Math.Log(voxels, 2.0) : 0.0;
            return Math.Min(lod, NoiseVolumeSet.MipLevels(size) - 1);
        }

        // The base shape noise at a lookup point (0 to 1).
        internal static double ShapeAt(NoiseVolumeSet set, ref NoiseLayerParams prm, double qx, double qy, double qz)
        {
            double period = NoiseModel.ShapePeriodScales * prm.ScaleM;
            double ux = qx / period, uy = qy / period, uz = qz / period;
            if (!(prm.ShapeLod > 0.0) || set.ShapeMips == null || set.ShapeMips.Length == 0) return Trilinear(set.Shape, set.ShapeN, 1, 0, ux, uy, uz);
            // The mip chain: the level below the asked one and the one above are read trilinear each and blended by the fraction (the GPU's trilinear filter).
            double lod = Math.Min(prm.ShapeLod, set.ShapeMips.Length);
            int below = (int)Math.Floor(lod);
            double f = lod - below;
            double a = below == 0 ? Trilinear(set.Shape, set.ShapeN, 1, 0, ux, uy, uz) : Trilinear(set.ShapeMips[below - 1], set.ShapeN >> below, 1, 0, ux, uy, uz);
            if (f <= 0.0 || below >= set.ShapeMips.Length) return a;
            return a + (Trilinear(set.ShapeMips[below], set.ShapeN >> (below + 1), 1, 0, ux, uy, uz) - a) * f;
        }

        // The macro shape of a heap preset (1 or 2) at a lookup point (0 to 1): the heap volume read trilinear at the mip level of the smoothing (prm.ShapeLod) and, with prm.HeapFootLod,
        // of the march's spacing footM (m): the level where the volume's voxel equals the spacing. cavity: the cavity channel there (1: none).
        internal static double HeapAt(HeapVolumeSet heap, ref NoiseLayerParams prm, int preset, double qx, double qy, double qz, double footM, out double cavity)
        {
            double period = HeapModel.PeriodScales * prm.ScaleM;
            double ux = qx / period, uy = qy / period, uz = qz / period;
            // The mip level of the type's smoothing (in noise scales) and, with HeapFootLod, of the march's spacing, in this volume's voxels (N over PeriodScales per noise scale).
            double lod = 0.0;
            double voxelsPerScale = heap.N / HeapModel.PeriodScales;
            if (prm.SmoothingScales > 0.0)
            {
                double v = prm.SmoothingScales * voxelsPerScale;
                if (v > 1.0) lod = Math.Log(v, 2.0);
            }
            if (prm.HeapFootLod && footM > 0.0 && prm.ScaleM > 0.0)
            {
                double voxels = footM / prm.ScaleM * voxelsPerScale;
                if (voxels > 1.0) lod = Math.Max(lod, Math.Log(voxels, 2.0));
            }
            // The levels below HeapModel.AnalyticLevelMin voxels are box means that are not rank-uniform (the last one is a constant 0.5): the far view's mean takes over beyond the last analytic level.
            lod = Math.Min(lod, Math.Log(heap.N / (double)HeapModel.AnalyticLevelMin, 2.0));
            lod = Math.Min(lod, heap.Levels.Length - 1);
            int below = (int)Math.Floor(lod);
            double f = lod - below;
            int channel = preset - 1, cavChannel = 2 + channel;
            double a = Trilinear(heap.Levels[below], heap.N >> below, 4, channel, ux, uy, uz);
            double ca = Trilinear(heap.Levels[below], heap.N >> below, 4, cavChannel, ux, uy, uz);
            if (f > 0.0 && below + 1 < heap.Levels.Length)
            {
                a += (Trilinear(heap.Levels[below + 1], heap.N >> (below + 1), 4, channel, ux, uy, uz) - a) * f;
                ca += (Trilinear(heap.Levels[below + 1], heap.N >> (below + 1), 4, cavChannel, ux, uy, uz) - ca) * f;
            }
            cavity = ca;
            return a;
        }

        // The erosion detail at a lookup point (0 to 1): the curl displaces the lookup, the rise of the detail moves it along the local up
        // (the unit vector nx, ny, nz at the point).
        internal static double DetailAt(NoiseVolumeSet set, ref NoiseLayerParams prm, double qx, double qy, double qz, double nx, double ny, double nz)
        {
            return DetailAt(set, ref prm, qx, qy, qz, nx, ny, nz, 1.0);
        }

        // The same read at freq times the frequency (the fine octave; the volume's offset decorrelates it from the first read).
        internal static double DetailAt(NoiseVolumeSet set, ref NoiseLayerParams prm, double qx, double qy, double qz, double nx, double ny, double nz, double freq)
        {
            double sp = NoiseModel.ShapePeriodScales * prm.ScaleM, dp = NoiseModel.DetailPeriodScales * prm.ScaleM;
            if (prm.Curl)
            {
                double amp = (prm.CurlAmplitude > 0.0 ? prm.CurlAmplitude : NoiseModel.CurlAmplitudeScales) * prm.ScaleM;
                double cx = Trilinear(set.Curl, set.CurlN, 4, 0, qx / sp, qy / sp, qz / sp) * 2.0 - 1.0;
                double cy = Trilinear(set.Curl, set.CurlN, 4, 1, qx / sp, qy / sp, qz / sp) * 2.0 - 1.0;
                double cz = Trilinear(set.Curl, set.CurlN, 4, 2, qx / sp, qy / sp, qz / sp) * 2.0 - 1.0;
                qx += cx * amp;
                qy += cy * amp;
                qz += cz * amp;
            }
            qx -= nx * prm.UpDistanceM;
            qy -= ny * prm.UpDistanceM;
            qz -= nz * prm.UpDistanceM;
            if (freq != 1.0)
            {
                qx = qx * freq + 0.31 * dp;
                qy = qy * freq + 0.57 * dp;
                qz = qz * freq + 0.83 * dp;
            }
            return Trilinear(set.Detail, set.DetailN, 1, 0, qx / dp, qy / dp, qz / dp);
        }

        // The soft coverage share s (0 to 1) eaten into by the detail: the share of the column that is cloud at the point (P04). Nothing hardens the edge of the soft
        // water content (the edge hardness by type of package T02 went with layers.softDensity, E40 step (c)).
        internal static double Erode(double s, double detail, double erosion)
        {
            double er = erosion * (1.0 - detail);
            return Math.Min(1.0, Math.Max(0.0, (s - er) / Math.Max(1.0 - er, 1e-3)));
        }

        // The distance LOD of the erosion detail (0: full detail, 1: dropped): the march's sample spacing (footM, m) against the detail's finest
        // cell. The detail is read in full while the spacing is within DetailLodFullFootCells cells, faded out until DetailLodOffFootCells cells;
        // beyond that the samples are too far apart to show the cells (they would only shimmer), and the erosion reads the volume's mean.
        internal static double DetailLod(ref NoiseLayerParams prm, double footM)
        {
            return DetailLod(ref prm, footM, 1.0);
        }

        // The same for the fine octave, whose cell is freq times smaller.
        internal static double DetailLod(ref NoiseLayerParams prm, double footM, double freq)
        {
            if (prm.NoDistanceLod || footM <= 0.0) return 0.0;
            double cell = NoiseModel.DetailFinestCellScales * prm.ScaleM / freq;
            double full = NoiseModel.DetailLodFullFootCells * cell, off = NoiseModel.DetailLodOffFootCells * cell;
            double x = (footM - full) / Math.Max(off - full, 1e-3);
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        // The coverage share of a layer at a point: the map's value cov plus the shape noise moves the threshold, the soft edge turns it into
        // a share, the detail erodes it. noiseAmount 0 or no volumes: the plain threshold (T01's function).
        internal static double Share(NoiseVolumeSet set, ref NoiseLayerParams prm, double px, double py, double pz, double cov, double coverage, double edge,
                                     double noiseAmount, double erosion, bool fine)
        {
            return Share(set, ref prm, px, py, pz, cov, coverage, edge, noiseAmount, erosion, fine, 0.0);
        }

        // The same with the march's sample spacing (footM, m; 0: the sample stands alone, no LOD).
        internal static double Share(NoiseVolumeSet set, ref NoiseLayerParams prm, double px, double py, double pz, double cov, double coverage, double edge,
                                     double noiseAmount, double erosion, bool fine, double footM)
        {
            edge = Math.Max(edge, 1e-4);
            if (set == null || noiseAmount <= 0.0)
            {
                double plain = (cov + coverage - 1.0) / edge + 0.5;
                return plain <= 0.0 ? 0.0 : plain >= 1.0 ? 1.0 : plain;
            }
            double qx, qy, qz;
            double wdx, wdy, wdz;
            LookupPoint(ref prm, px, py, pz, out qx, out qy, out qz, out wdx, out wdy, out wdz);
            double n;
            if (prm.HeapPreset > 0 && set.Heap != null)
            {
                double cavity;
                double hx, hy, hz;
                HeapLookup(ref prm, px, py, pz, qx, qy, qz, wdx, wdy, wdz, out hx, out hy, out hz);
                n = HeapAt(set.Heap, ref prm, prm.HeapPreset, hx, hy, hz, footM, out cavity);
            }
            else n = ShapeAt(set, ref prm, qx, qy, qz);
            double x = (cov + (n - 0.5) * noiseAmount + coverage - 1.0) / edge + 0.5;
            double s = x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
            if (s <= 0.0 || erosion <= 0.0 || !fine) return s;
            // DensityRemap 0 (the default): the erosion is read only in the soft edge, a closed deck (s = 1) keeps the share 1.
            if (s >= 1.0 && !(prm.DensityRemap > 0.0)) return s;
            double lod = DetailLod(ref prm, footM);
            double d = set.DetailMean;
            double r = Math.Sqrt(px * px + py * py + pz * pz);
            // The erosion detail reads the lattice at its true vertical scale: a layer's vertical scale (E40 L5 shape) lengthens the SHAPE's features in the vertical, the detail's
            // cauliflower must stay as tall as it is wide, or the surface streaks vertically. The shape's sphere scale moved the point by -(1 - w)(r - reference) along the up; here it is moved back.
            if (prm.SphereScale != 1.0 && prm.SphereScale > 0.0 && r > 0.0)
            {
                double back = (1.0 - prm.SphereScale) * (r - prm.ReferenceRadiusM) / r;
                qx += px * back;
                qy += py * back;
                qz += pz * back;
            }
            if (lod < 1.0)
            {
                d = DetailAt(set, ref prm, qx, qy, qz, px / r, py / r, pz / r);
                if (lod > 0.0) d += (set.DetailMean - d) * lod;
            }
            if (!prm.NoFineDetail)
            {
                double freq = prm.FineFrequencyValue;
                double lod2 = DetailLod(ref prm, footM, freq);
                double d2 = set.DetailMean;
                if (lod2 < 1.0)
                {
                    d2 = DetailAt(set, ref prm, qx, qy, qz, px / r, py / r, pz / r, freq);
                    if (lod2 > 0.0) d2 += (set.DetailMean - d2) * lod2;
                }
                d += (d2 - d) * prm.FineWeightValue;
            }
            // PUFF: the detail's contrast about its mean (0: as it was).
            if (prm.BillowContrastDelta != 0.0) d = Math.Min(1.0, Math.Max(0.0, set.DetailMean + (d - set.DetailMean) * (1.0 + prm.BillowContrastDelta)));
            double eroded = Erode(s, d, erosion);
            // layers.densityRemap: the erosion also thins the interior of a closed deck, by the share of the detail's gap (the structure a flat slab lacks).
            if (prm.DensityRemap > 0.0) eroded *= InteriorThinning(prm.DensityRemap, erosion, d);
            return eroded;
        }

        // The factor the interior erosion multiplies the share by: 1 - remap * erosion * (1 - detail), 0 to 1.
        internal static double InteriorThinning(double remap, double erosion, double detail)
        {
            double f = 1.0 - remap * erosion * (1.0 - detail);
            return f < 0.0 ? 0.0 : f > 1.0 ? 1.0 : f;
        }
    }
}
