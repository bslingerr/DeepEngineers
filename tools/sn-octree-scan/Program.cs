// Scratch probe: read Subnautica's compiled octree batches (read-only) and derive
// coarse terrain statistics for the world-mapping probe. Format as read from the
// decompiled BatchOctrees.LoadOctrees / Octree.Read / Octree.GetNodeId:
// int32 version (>=4), then for each octree in a 5x5x5 batch (x outer, y, z inner)
// that lies inside the 128x100x128 octree bounds: u16 nodeCount, nodeCount*4 bytes
// node = {type, density, firstChild lo, firstChild hi}; leaf when firstChild == 0;
// child index = first + (x>=h?4:0) + (y>=h?2:0) + (z>=h?1:0).
// Solid rule (VoxelandData.OctNode): density==0 -> type!=0; else (density-126)/126 > 0.
using System.Text.RegularExpressions;

string dir = args[0];
string outDir = args[1];
Directory.CreateDirectory(outDir);
const int W = 4096, H = 3200, OS = 32, B = 5;
const int OX = -2048, OY = -3040, OZ = -2048;
const int R = 4; // output resolution in metres
int GW = W / R;
var top = new short[GW * GW];      // highest solid voxel world y in 4x4 column, short.MinValue = none
var lowFloor = new short[GW * GW]; // lowest "floor" (solid with empty directly above) world y
var lowSolid = new short[GW * GW]; // lowest solid voxel world y
Array.Fill(top, short.MinValue); Array.Fill(lowFloor, short.MaxValue); Array.Fill(lowSolid, short.MaxValue);
var floorHist = new long[H];
var solid = new bool[OS * OS * OS];
long nodes = 0, octrees = 0, emptyOctrees = 0, files = 0;
var rx = new Regex(@"compiled-batch-(\d+)-(\d+)-(\d+)\.optoctrees$");
byte[] buf = new byte[65536 * 4];

void Fill(byte[] d, int n, int x0, int y0, int z0, int size)
{
    int o = n * 4;
    int fc = d[o + 2] | (d[o + 3] << 8);
    if (fc == 0 || size == 1)
    {
        byte t = d[o], den = d[o + 1];
        bool s = den == 0 ? t != 0 : den > 126;
        if (!s) return;
        for (int x = x0; x < x0 + size; x++)
            for (int y = y0; y < y0 + size; y++)
                for (int z = z0; z < z0 + size; z++)
                    solid[(x * OS + y) * OS + z] = true;
        return;
    }
    int h = size >> 1;
    for (int c = 0; c < 8; c++)
        Fill(d, fc + c, x0 + ((c & 4) != 0 ? h : 0), y0 + ((c & 2) != 0 ? h : 0), z0 + ((c & 1) != 0 ? h : 0), h);
}

foreach (var path in Directory.EnumerateFiles(dir, "*.optoctrees"))
{
    var m = rx.Match(path);
    if (!m.Success) continue;
    int bx = int.Parse(m.Groups[1].Value), by = int.Parse(m.Groups[2].Value), bz = int.Parse(m.Groups[3].Value);
    files++;
    using var br = new BinaryReader(File.OpenRead(path));
    int ver = br.ReadInt32();
    if (ver < 4) { Console.Error.WriteLine($"old version {ver} {path}"); continue; }
    for (int ix = 0; ix < B; ix++)
        for (int iy = 0; iy < B; iy++)
            for (int iz = 0; iz < B; iz++)
            {
                int px = bx * B + ix, py = by * B + iy, pz = bz * B + iz;
                if (px >= 128 || py >= 100 || pz >= 128) continue;
                int count = br.ReadUInt16();
                octrees++;
                if (count == 0) { emptyOctrees++; continue; }
                int bytes = count * 4;
                int got = 0;
                while (got < bytes) { int r = br.Read(buf, got, bytes - got); if (r <= 0) throw new Exception("eof"); got += r; }
                nodes += count;
                Array.Clear(solid);
                Fill(buf, 0, 0, 0, 0, OS);
                int vx0 = px * OS, vy0 = py * OS, vz0 = pz * OS;
                for (int x = 0; x < OS; x++)
                    for (int z = 0; z < OS; z++)
                    {
                        int gi = ((vz0 + z) / R) * GW + (vx0 + x) / R;
                        for (int y = OS - 1; y >= 0; y--)
                        {
                            if (!solid[(x * OS + y) * OS + z]) continue;
                            short wy = (short)(vy0 + y + OY);
                            if (wy > top[gi]) top[gi] = wy;
                            break;
                        }
                        for (int y = 0; y < OS; y++)
                        {
                            bool s = solid[(x * OS + y) * OS + z];
                            if (!s) continue;
                            short wy = (short)(vy0 + y + OY);
                            if (wy < lowSolid[gi]) lowSolid[gi] = wy;
                            bool above = y + 1 < OS && solid[(x * OS + y + 1) * OS + z];
                            if (y + 1 < OS && !above)
                            {
                                floorHist[vy0 + y]++;
                                if (wy < lowFloor[gi]) lowFloor[gi] = wy;
                            }
                        }
                    }
            }
}

void Save(string name, short[] a)
{
    using var bw = new BinaryWriter(File.Create(Path.Combine(outDir, name)));
    foreach (var v in a) bw.Write(v);
}
Save("top.i16", top); Save("lowfloor.i16", lowFloor); Save("lowsolid.i16", lowSolid);
using (var sw = new StreamWriter(Path.Combine(outDir, "floorhist.csv")))
{
    sw.WriteLine("world_y,floor_voxels");
    for (int y = 0; y < H; y++) if (floorHist[y] > 0) sw.WriteLine($"{y + OY},{floorHist[y]}");
}
Console.WriteLine($"files {files} octrees {octrees} empty {emptyOctrees} nodes {nodes}");
