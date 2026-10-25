// The opacity micromaps' CPU side (RaytracingOmmBake.h).

#include "RaytracingOmmBake.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

namespace redefinition
{
    namespace omm
    {
        namespace
        {
            // The levels whose tables are made, 0 to 8, the finest the baker bakes.
            constexpr uint32_t kTableLevels = 9;

            // One level's micro-triangles along the curve, and the cells of the grid by the
            // micro-triangle that fills them: an upright cell (a, b) the triangle (a, b),
            // (a + 1, b), (a, b + 1); an inverted one the triangle (a + 1, b), (a + 1, b + 1),
            // (a, b + 1) -- the cell's other half.
            struct Level
            {
                // The micro-triangles in curve order; per cell, row by row, the index of its upright
                // and of its inverted micro-triangle (UINT32_MAX for none); the grid's side, 2^level.
                std::vector<Corners> triangles;
                std::vector<uint32_t> upright;
                std::vector<uint32_t> inverted;
                uint32_t side = 0;
            };

            // A point on the barycentric grid, in grid steps.
            struct Point
            {
                // Its u and v, as Corners has them.
                int32_t u, v;
            };

            // The curve is defined recursively, "by traversing into the sub-triangle nearest
            // vertex 0, then the middle triangle with ordering flipped, then nearest vertex 1,
            // then nearest vertex 2 with ordering flipped" (Vulkan specification, ray traversal,
            // opacity micromap); a triangle's own vertices a, b, c are in the order its children
            // enter them. Each child as the corners of the six points a, b, c and the midpoints
            // of ab, bc and ca (indices 0 to 5), in the order it takes them.
            constexpr int kChildren[4][3] = {
                { 0, 3, 5 },
                { 5, 4, 3 },
                { 3, 1, 4 },
                { 4, 5, 2 },
            };

            // The triangle a, b, c at `depth` split along the curve down to `levels`, its
            // micro-triangles appended to the level in curve order.
            void Descend(Level& level, Point a, Point b, Point c, uint32_t depth, uint32_t levels)
            {
                if (depth == levels)
                {
                    Corners corners = {};
                    corners.u[0] = a.u; corners.v[0] = a.v;
                    corners.u[1] = b.u; corners.v[1] = b.v;
                    corners.u[2] = c.u; corners.v[2] = c.v;
                    level.triangles.push_back(corners);
                    return;
                }
                const Point p[6] = { a, b, c,
                                     { (a.u + b.u) / 2, (a.v + b.v) / 2 },
                                     { (b.u + c.u) / 2, (b.v + c.v) / 2 },
                                     { (c.u + a.u) / 2, (c.v + a.v) / 2 } };
                for (const auto& child : kChildren)
                    Descend(level, p[child[0]], p[child[1]], p[child[2]], depth + 1, levels);
            }

            // A level's table, made on first use under a lock and kept for the process.
            const Level& LevelOf(uint32_t levels)
            {
                static std::mutex lock;
                static std::unique_ptr<Level> built[kTableLevels];
                std::lock_guard<std::mutex> guard(lock);
                std::unique_ptr<Level>& level = built[levels];
                if (level != nullptr)
                    return *level;
                level = std::make_unique<Level>();
                const int32_t side = 1 << levels;
                level->side = static_cast<uint32_t>(side);
                level->triangles.reserve(MicroTriangleCount(levels));
                // The base triangle's vertices 0, 1 and 2.
                Descend(*level, { 0, 0 }, { side, 0 }, { 0, side }, 0, levels);
                level->upright.assign(static_cast<size_t>(side) * side, UINT32_MAX);
                level->inverted.assign(static_cast<size_t>(side) * side, UINT32_MAX);
                for (uint32_t i = 0; i < level->triangles.size(); ++i)
                {
                    const Corners& c = level->triangles[i];
                    const int32_t minU = std::min({ c.u[0], c.u[1], c.u[2] });
                    const int32_t minV = std::min({ c.v[0], c.v[1], c.v[2] });
                    const int32_t maxU = std::max({ c.u[0], c.u[1], c.u[2] });
                    // The upright triangle has its right angle at the lowest corner in both.
                    bool upright = false;
                    for (int k = 0; k < 3; ++k)
                        upright = upright || (c.u[k] == minU && c.v[k] == minV);
                    (void)maxU;
                    (upright ? level->upright : level->inverted)[static_cast<size_t>(minV) * side + minU] = i;
                }
                return *level;
            }
        }

        // From the level's table (LevelOf).
        const Corners& MicroTriangle(uint32_t level, uint32_t index)
        {
            return LevelOf(std::min(level, kTableLevels - 1)).triangles[index];
        }

        uint32_t MicroTriangleAt(uint32_t levels, double u, double v)
        {
            const Level& level = LevelOf(std::min(levels, kTableLevels - 1));
            const double side = static_cast<double>(level.side);
            double scaledU = std::clamp(u, 0.0, 1.0) * side;
            double scaledV = std::clamp(v, 0.0, 1.0) * side;
            int32_t a = static_cast<int32_t>(std::floor(scaledU));
            int32_t b = static_cast<int32_t>(std::floor(scaledV));
            const int32_t last = static_cast<int32_t>(level.side) - 1;
            // "clamping floor(u) as needed to keep the sum less than or equal to 1 << subdivisionlevel"
            a = std::min(a, last);
            b = std::min(b, last);
            if (a + b > last)
                a = last - b;
            const double fu = scaledU - a, fv = scaledV - b;
            const size_t cell = static_cast<size_t>(b) * level.side + a;
            if (fu + fv < 1.0 || a + b == last)
                return level.upright[cell];
            return level.inverted[cell];
        }

        double Coverage(const uint8_t* alpha, double u, double v)
        {
            // The sampler (linear, wrapped): texel centres at half-integers.
            const double x = u * kTextureSize - 0.5, y = v * kTextureSize - 0.5;
            const double fx = std::floor(x), fy = std::floor(y);
            const double wx = x - fx, wy = y - fy;
            const auto texel = [&](double ix, double iy)
            {
                const int n = static_cast<int>(kTextureSize);
                const int tx = ((static_cast<int>(ix) % n) + n) % n;
                const int ty = ((static_cast<int>(iy) % n) + n) % n;
                return alpha[ty * n + tx] / 255.0;
            };
            return (texel(fx, fy) * (1.0 - wx) + texel(fx + 1.0, fy) * wx) * (1.0 - wy)
                   + (texel(fx, fy + 1.0) * (1.0 - wx) + texel(fx + 1.0, fy + 1.0) * wx) * wy;
        }

        State StateOf(const Baked& baked, size_t triangle, uint32_t index)
        {
            const int32_t at = baked.triangleIndex[triangle];
            if (at < 0)
                return at == -1 ? kTransparent : at == -2 ? kOpaque : at == -3 ? kUnknownTransparent : kUnknownOpaque;
            const Entry& entry = baked.entries[static_cast<size_t>(at)];
            const uint32_t bit = index * 2;
            return static_cast<State>((baked.data[entry.byteOffset + bit / 8] >> (bit % 8)) & 3u);
        }

        namespace
        {
            // A point in texel space.
            struct Point2
            {
                // Its coordinates, in texels.
                double x, y;
            };

            // The least and greatest alpha among the texels any bilinear sample inside a
            // triangle reads. In texel space a sample at p reads the texels floor(p) and
            // floor(p) + 1 in each axis; texel i is read from [i - 1, i + 1). The triangle is
            // taken a little larger than it is, to cover what a ray's barycentrics, rounded in
            // the hardware, may place in a neighbouring micro-triangle.
            struct Range
            {
                // The alpha bytes, 0 to 255; an empty range until a texel is met.
                int least = 255;
                int greatest = 0;
            };

            // How much larger the triangle is taken, in texels.
            constexpr double kMargin = 0.002;

            // Whether the triangle meets the box [x0, x1] x [y0, y1]: a separating axis among
            // the box's own and the triangle's edges' normals.
            bool Meets(const Point2 (&p)[3], double x0, double x1, double y0, double y1)
            {
                for (int k = 0; k < 3; ++k)
                {
                    const Point2 a = p[k], b = p[(k + 1) % 3];
                    const double nx = b.y - a.y, ny = a.x - b.x;
                    double lowest = nx * x0 + ny * y0, highest = lowest;
                    for (const double corner : { nx * x1 + ny * y0, nx * x0 + ny * y1, nx * x1 + ny * y1 })
                    {
                        lowest = std::min(lowest, corner);
                        highest = std::max(highest, corner);
                    }
                    double triLow = nx * p[0].x + ny * p[0].y, triHigh = triLow;
                    for (int m = 1; m < 3; ++m)
                    {
                        const double d = nx * p[m].x + ny * p[m].y;
                        triLow = std::min(triLow, d);
                        triHigh = std::max(triHigh, d);
                    }
                    if (lowest > triHigh || highest < triLow)
                        return false;
                }
                return true;
            }

            // The Range of a triangle or micro-triangle, its corners in texel space: the texels whose
            // reach meets it, all of the texture where it spans nearly all of it.
            Range Footprint(const uint8_t* alpha, Point2 (&corners)[3])
            {
                // The triangle in texel space, grown by the margin about its centre.
                const Point2 centre = { (corners[0].x + corners[1].x + corners[2].x) / 3.0,
                                        (corners[0].y + corners[1].y + corners[2].y) / 3.0 };
                Point2 p[3];
                double minX = 1e300, maxX = -1e300, minY = 1e300, maxY = -1e300;
                for (int k = 0; k < 3; ++k)
                {
                    const double dx = corners[k].x - centre.x, dy = corners[k].y - centre.y;
                    const double length = std::sqrt(dx * dx + dy * dy);
                    const double grow = length > 1e-12 ? 1.0 + kMargin / length : 1.0;
                    p[k] = { centre.x + dx * grow, centre.y + dy * grow };
                    // A triangle too small to have a direction is a point: the box covers it.
                    minX = std::min(minX, p[k].x - kMargin);
                    maxX = std::max(maxX, p[k].x + kMargin);
                    minY = std::min(minY, p[k].y - kMargin);
                    maxY = std::max(maxY, p[k].y + kMargin);
                }
                const int n = static_cast<int>(kTextureSize);
                Range range;
                bool visited = false;
                const bool wide = maxX - minX >= n - 2 || maxY - minY >= n - 2;
                const long x0 = static_cast<long>(std::ceil(minX)) - 1, x1 = static_cast<long>(std::floor(maxX)) + 1;
                const long y0 = static_cast<long>(std::ceil(minY)) - 1, y1 = static_cast<long>(std::floor(maxY)) + 1;
                for (long j = y0; j <= y1; ++j)
                {
                    if (wide && j - y0 >= n + 1)
                        break;
                    for (long i = x0; i <= x1; ++i)
                    {
                        if (wide && i - x0 >= n + 1)
                            break;
                        if (!wide && !Meets(p, static_cast<double>(i) - 1.0, static_cast<double>(i) + 1.0,
                                            static_cast<double>(j) - 1.0, static_cast<double>(j) + 1.0))
                            continue;
                        const int tx = static_cast<int>(((i % n) + n) % n), ty = static_cast<int>(((j % n) + n) % n);
                        const int a = alpha[ty * n + tx];
                        range.least = std::min(range.least, a);
                        range.greatest = std::max(range.greatest, a);
                        visited = true;
                    }
                }
                // No texel found would call for transparent: it is unknown instead.
                if (!visited)
                {
                    range.least = 0;
                    range.greatest = 255;
                }
                return range;
            }

            // The state a Range calls for: transparent only where every texel is 0, opaque only
            // where every one is 255, otherwise unknown, leaning to the side of the range's middle.
            State StateFrom(const Range& range)
            {
                if (range.greatest == 0)
                    return kTransparent;
                if (range.least == 255)
                    return kOpaque;
                return range.least + range.greatest >= 255 ? kUnknownOpaque : kUnknownTransparent;
            }

            // The special index of a triangle uniform in that state (nvomm::kIndexFully*).
            int32_t Special(State state)
            {
                return state == kTransparent ? -1 : state == kOpaque ? -2 : state == kUnknownTransparent ? -3 : -4;
            }

        }

        void Bake(const TriangleUv* triangles, size_t count, const uint8_t* alpha, const Settings& settings, Baked& out)
        {
            out = Baked();
            out.triangleIndex.assign(count, 0);
            const uint32_t finest = std::min<uint32_t>(settings.maxLevel, 8);
            // Triangles with the same texture coordinates share an OMM; OMMs with the same
            // states share their bytes.
            std::unordered_map<std::string, int32_t> seenTriangles;
            std::unordered_map<std::string, int32_t> seenBlocks;
            uint64_t budget = settings.maxMicroTriangles;
            const double toTexels = static_cast<double>(kTextureSize);
            for (size_t t = 0; t < count; ++t)
            {
                const TriangleUv& tri = triangles[t];
                const std::string key(reinterpret_cast<const char*>(&tri), sizeof(tri));
                const auto seen = seenTriangles.find(key);
                if (seen != seenTriangles.end())
                {
                    out.triangleIndex[t] = seen->second;
                    continue;
                }
                const auto remember = [&](int32_t index)
                {
                    seenTriangles.emplace(key, index);
                    out.triangleIndex[t] = index;
                    if (index < 0)
                        ++out.uniform[index == -1 ? 0 : index == -2 ? 1 : index == -3 ? 2 : 3];
                    else
                        ++out.withOmm;
                };
                bool finite = true;
                for (int k = 0; k < 3; ++k)
                    finite = finite && std::isfinite(tri.uv[k][0]) && std::isfinite(tri.uv[k][1])
                             && std::fabs(tri.uv[k][0]) < 1e6f && std::fabs(tri.uv[k][1]) < 1e6f;
                if (!finite)
                {
                    remember(Special(kUnknownOpaque));
                    continue;
                }
                // The whole triangle first: most are wholly transparent or wholly opaque.
                const double u0 = tri.uv[0][0], v0 = tri.uv[0][1];
                const double du1 = tri.uv[1][0] - u0, dv1 = tri.uv[1][1] - v0;
                const double du2 = tri.uv[2][0] - u0, dv2 = tri.uv[2][1] - v0;
                Point2 whole[3] = { { u0 * toTexels - 0.5, v0 * toTexels - 0.5 },
                                    { tri.uv[1][0] * toTexels - 0.5, tri.uv[1][1] * toTexels - 0.5 },
                                    { tri.uv[2][0] * toTexels - 0.5, tri.uv[2][1] * toTexels - 0.5 } };
                const State all = StateFrom(Footprint(alpha, whole));
                if (all == kTransparent || all == kOpaque)
                {
                    remember(Special(all));
                    continue;
                }
                // About one micro-triangle to a texel of the triangle's area.
                const double area = std::fabs(du1 * dv2 - du2 * dv1) * 0.5 * toTexels * toTexels;
                uint32_t level = area < 1.0 ? 0u
                                 : static_cast<uint32_t>(std::clamp(std::lround(0.5 * std::log2(area)), 1l, static_cast<long>(finest)));
                while (level > 0 && MicroTriangleCount(level) > budget)
                    --level;
                if (level == 0)
                {
                    remember(Special(all));
                    continue;
                }
                const Level& table = LevelOf(level);
                const double side = static_cast<double>(table.side);
                const uint32_t cells = MicroTriangleCount(level);
                std::vector<uint8_t> states(cells);
                uint32_t counts[4] = {};
                for (uint32_t k = 0; k < cells; ++k)
                {
                    const Corners& c = table.triangles[k];
                    Point2 corners[3];
                    for (int m = 0; m < 3; ++m)
                    {
                        const double a = c.u[m] / side, b = c.v[m] / side;
                        corners[m] = { (u0 + du1 * a + du2 * b) * toTexels - 0.5, (v0 + dv1 * a + dv2 * b) * toTexels - 0.5 };
                    }
                    states[k] = StateFrom(Footprint(alpha, corners));
                    ++counts[states[k]];
                }
                int32_t uniformState = -1;
                for (int s = 0; s < 4; ++s)
                    if (counts[s] == cells)
                        uniformState = s;
                if (uniformState >= 0)
                {
                    remember(Special(static_cast<State>(uniformState)));
                    continue;
                }
                // Packed, two bits a micro-triangle, the first in the lowest bits.
                std::string block(OmmBytes(level), '\0');
                for (uint32_t k = 0; k < cells; ++k)
                    block[(k * 2) / 8] = static_cast<char>(block[(k * 2) / 8] | (states[k] << ((k * 2) % 8)));
                const std::string blockKey = std::string(1, static_cast<char>(level)) + block;
                const auto known = seenBlocks.find(blockKey);
                if (known != seenBlocks.end())
                {
                    remember(known->second);
                    continue;
                }
                Entry entry = {};
                entry.byteOffset = static_cast<uint32_t>((out.data.size() + kOmmAlignment - 1) / kOmmAlignment * kOmmAlignment);
                entry.level = static_cast<uint16_t>(level);
                entry.format = kFormat4State;
                out.data.resize(entry.byteOffset + block.size(), 0);
                memcpy(&out.data[entry.byteOffset], block.data(), block.size());
                const int32_t index = static_cast<int32_t>(out.entries.size());
                out.entries.push_back(entry);
                ++out.levelCount[level];
                for (int s = 0; s < 4; ++s)
                    out.microTriangles[s] += counts[s];
                budget -= std::min<uint64_t>(budget, cells);
                seenBlocks.emplace(blockKey, index);
                remember(index);
            }
            // The data ends at a multiple of the alignment, so that what follows it does too.
            out.data.resize((out.data.size() + kOmmAlignment - 1) / kOmmAlignment * kOmmAlignment, 0);
        }
    }
}
