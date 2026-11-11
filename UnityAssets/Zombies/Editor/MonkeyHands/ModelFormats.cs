using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Readers for the the original mod-tools binary exports (*.xmodel_bin / *.xanim_bin) in the the original arms-rig port.
// A file is "*LZ4*", the uncompressed size (uint32) and one raw LZ4 block. Inside is the chunked binary form of the
// text xmodel_export / xanim_export: each record starts with a 16-bit id, often with a 16-bit value packed after it,
// and is 4-byte aligned. The ids below were mapped against the files themselves (every file parses to its last byte).
// Kept free of UnityEngine so the parsing can be tested outside the editor.
namespace ZombiesMonkeyHands.ModelFormats
{
    public struct SrcVec { public float X, Y, Z; }

    /// <summary>A bone or anim part pose in the original game space: position (inches) and the bone's own X/Y/Z axes in world space.</summary>
    public struct SrcPose
    {
        public SrcVec Pos, AxisX, AxisY, AxisZ;
    }

    public class SrcBone
    {
        public string Name;
        public int Parent;
        public SrcPose Rest;
    }

    public class SrcCorner
    {
        public int Vertex;
        public SrcVec Normal;
        public float U, V;
    }

    public class SrcFace
    {
        public int Object, Material;
        public SrcCorner[] Corners = new SrcCorner[3];
    }

    public class SrcVertex
    {
        public SrcVec Pos;
        public List<(int bone, float weight)> Weights = new List<(int, float)>();
    }

    public class SrcMaterial
    {
        public string Name, Type, Image;
    }

    public class SrcModel
    {
        public List<SrcBone> Bones = new List<SrcBone>();
        public List<SrcVertex> Vertices = new List<SrcVertex>();
        public List<SrcFace> Faces = new List<SrcFace>();
        public List<string> Objects = new List<string>();
        public List<SrcMaterial> Materials = new List<SrcMaterial>();

        public int BoneIndex(string name) => Bones.FindIndex(b => b.Name == name);

        public static SrcModel Load(string path)
        {
            var r = new SrcReader(SrcReader.Decompress(path), path);
            var model = new SrcModel();
            int section = 0; // 1 bones, 2 vertices, 3 faces, 4 materials
            int bone = -1;
            SrcVertex vertex = null;
            SrcFace face = null;
            int corner = -1;

            while (!r.End)
            {
                int start = r.Pos;
                ushort id = r.U16();
                switch (id)
                {
                    case 0x46C8: r.Skip(2); break; // MODEL
                    case 0x24D1: r.Skip(2); break; // VERSION
                    case 0x76BA: r.Skip(2); section = 1; break; // NUMBONES
                    case 0xF099: // BONE index parent "name"
                        r.Skip(2);
                        int index = r.I32(), parent = r.I32();
                        while (model.Bones.Count <= index) model.Bones.Add(new SrcBone());
                        model.Bones[index].Name = r.Str();
                        model.Bones[index].Parent = parent;
                        break;
                    case 0xDD9A: bone = r.U16(); break; // BONE (pose block)
                    case 0x9383: // OFFSET
                        r.Skip(2);
                        SrcVec offset = r.Vec();
                        if (section == 1) model.Bones[bone].Rest.Pos = offset;
                        else if (section == 2) vertex.Pos = offset;
                        break;
                    case 0x1C56: r.Skip(2); r.Vec(); break; // SCALE
                    case 0xDCFD: model.Bones[bone].Rest.AxisX = r.Axis(); break; // X
                    case 0xCCDC: model.Bones[bone].Rest.AxisY = r.Axis(); break; // Y
                    case 0xFCBF: model.Bones[bone].Rest.AxisZ = r.Axis(); break; // Z
                    case 0x950D: r.Skip(2); section = 2; break; // NUMVERTS
                    case 0x2AEC: r.Skip(2); r.I32(); section = 2; break; // NUMVERTS32
                    case 0x8F03: // VERT (u16): a new vertex, or a face corner's vertex
                    case 0xB097: // VERT32
                        int v = id == 0x8F03 ? r.U16() : SkipThen(r, 2, () => r.I32());
                        if (section == 2)
                        {
                            while (model.Vertices.Count <= v) model.Vertices.Add(new SrcVertex());
                            vertex = model.Vertices[v];
                        }
                        else
                        {
                            face.Corners[++corner] = new SrcCorner { Vertex = v };
                        }
                        break;
                    case 0xEA46: r.Skip(2); break; // BONES (weight count)
                    case 0xF1AB: // BONE weight: index, weight
                        int wb = r.U16();
                        vertex.Weights.Add((wb, r.F32()));
                        break;
                    case 0xBE92: r.Skip(2); r.I32(); section = 3; break; // NUMFACES
                    case 0x562F: // TRI object material (u8 u8)
                        face = new SrcFace { Object = r.U8(), Material = r.U8() };
                        model.Faces.Add(face);
                        corner = -1;
                        break;
                    case 0x6711: // TRI16 object material (u16 u16)
                        r.Skip(2);
                        face = new SrcFace { Object = r.U16(), Material = r.U16() };
                        model.Faces.Add(face);
                        corner = -1;
                        break;
                    case 0x89EC: face.Corners[corner].Normal = r.Axis(); break; // NORMAL
                    case 0x6DD8: r.Skip(6); break; // COLOR (faces and materials)
                    case 0x1AD4: // UV count u v [u v ...]
                        int layers = r.U16();
                        for (int l = 0; l < layers; l++)
                        {
                            float u = r.F32(), vv = r.F32();
                            if (l == 0 && section == 3)
                            {
                                face.Corners[corner].U = u;
                                face.Corners[corner].V = vv;
                            }
                        }
                        break;
                    case 0x62AF: r.Skip(2); break; // NUMOBJECTS
                    case 0x87D4: r.Skip(2); model.Objects.Add(r.Str()); break; // OBJECT index "name"
                    case 0xA1B2: r.Skip(2); section = 4; break; // NUMMATERIALS
                    case 0xA700: // MATERIAL index "name" "type" "image"
                        r.Skip(2);
                        model.Materials.Add(new SrcMaterial { Name = r.Str(), Type = r.Str(), Image = r.Str() });
                        break;
                    // Material lighting properties we don't use
                    case 0x6DAB: case 0x37FF: case 0x4265: case 0x317C: case 0xE593: r.Skip(18); break; // 4 floats
                    case 0xC835: case 0xFE0C: case 0x7E24: case 0x7D76: case 0x83C7: r.Skip(10); break; // 2 floats
                    case 0x5CD2: r.Skip(6); break; // PHONG
                    default:
                        throw new InvalidDataException($"{path}: unknown chunk 0x{id:X4} at 0x{start:X}");
                }
            }
            return model;
        }

        private static int SkipThen(SrcReader r, int skip, Func<int> read)
        {
            r.Skip(skip);
            return read();
        }
    }

    public class SrcAnim
    {
        public float FrameRate;
        public List<string> Parts = new List<string>();
        /// <summary>Per frame, per part (same order as <see cref="Parts"/>): the pose in view space.</summary>
        public List<SrcPose[]> Frames = new List<SrcPose[]>();

        public int PartIndex(string name) => Parts.IndexOf(name);

        public static SrcAnim Load(string path)
        {
            var r = new SrcReader(SrcReader.Decompress(path), path);
            var anim = new SrcAnim();
            SrcPose[] frame = null;
            int part = -1;

            while (!r.End)
            {
                int start = r.Pos;
                ushort id = r.U16();
                switch (id)
                {
                    case 0x7AAC: r.Skip(2); break; // ANIMATION
                    case 0x24D1: r.Skip(2); break; // VERSION
                    case 0x9279: r.Skip(2); break; // NUMPARTS
                    case 0x360B: // PART index "name"
                        int index = r.U16();
                        while (anim.Parts.Count <= index) anim.Parts.Add(null);
                        anim.Parts[index] = r.Str();
                        break;
                    case 0x92D3: anim.FrameRate = r.U16(); break; // FRAMERATE
                    case 0xB917: r.Skip(2); r.I32(); break; // NUMFRAMES
                    case 0xC723: // FRAME n
                        r.Skip(2); r.I32();
                        frame = new SrcPose[anim.Parts.Count];
                        anim.Frames.Add(frame);
                        break;
                    case 0x745A: part = r.U16(); break; // PART (pose block)
                    case 0x9383: r.Skip(2); frame[part].Pos = r.Vec(); break; // OFFSET
                    case 0xDCFD: frame[part].AxisX = r.Axis(); break;
                    case 0xCCDC: frame[part].AxisY = r.Axis(); break;
                    case 0xFCBF: frame[part].AxisZ = r.Axis(); break;
                    case 0x7A6C: // NUMTRACKS: notetracks follow; the port keeps its sound notes in the GDT instead
                        return anim;
                    default:
                        throw new InvalidDataException($"{path}: unknown chunk 0x{id:X4} at 0x{start:X}");
                }
            }
            return anim;
        }
    }

    internal class SrcReader
    {
        private readonly byte[] _d;
        private readonly string _path;
        public int Pos;

        public SrcReader(byte[] data, string path)
        {
            _d = data;
            _path = path;
        }

        public bool End => Pos >= _d.Length;

        public void Skip(int n) => Pos += n;
        public byte U8() => _d[Pos++];
        public ushort U16() { ushort v = BitConverter.ToUInt16(_d, Pos); Pos += 2; return v; }
        public int I32() { int v = BitConverter.ToInt32(_d, Pos); Pos += 4; return v; }
        public float F32() { float v = BitConverter.ToSingle(_d, Pos); Pos += 4; return v; }
        public SrcVec Vec() => new SrcVec { X = F32(), Y = F32(), Z = F32() };

        /// <summary>Three normalised int16 (axis rows and normals), after the record's id.</summary>
        public SrcVec Axis()
        {
            float x = BitConverter.ToInt16(_d, Pos) / 32767f, y = BitConverter.ToInt16(_d, Pos + 2) / 32767f, z = BitConverter.ToInt16(_d, Pos + 4) / 32767f;
            Pos += 6;
            return new SrcVec { X = x, Y = y, Z = z };
        }

        /// <summary>Null-terminated string, padded to the 4-byte record alignment.</summary>
        public string Str()
        {
            int end = Array.IndexOf(_d, (byte)0, Pos);
            string s = Encoding.ASCII.GetString(_d, Pos, end - Pos);
            int length = end + 1 - Pos;
            // Records are aligned from their own start, which is always 4-aligned; strings start at offset 4 or 12
            Pos += (length + 3) & ~3;
            return s;
        }

        public static byte[] Decompress(string path)
        {
            byte[] s = File.ReadAllBytes(path);
            if (s.Length < 9 || Encoding.ASCII.GetString(s, 0, 5) != "*LZ4*")
                throw new InvalidDataException($"{path}: not an LZ4-packed model export");
            int rawSize = BitConverter.ToInt32(s, 5);
            var d = new byte[rawSize];
            int pos = 9, o = 0;
            while (pos < s.Length && o < rawSize)
            {
                int token = s[pos++];
                int literal = token >> 4;
                if (literal == 15) { int b; do { b = s[pos++]; literal += b; } while (b == 255); }
                Buffer.BlockCopy(s, pos, d, o, literal);
                pos += literal;
                o += literal;
                if (o >= rawSize || pos >= s.Length) break;
                int offset = s[pos] | (s[pos + 1] << 8);
                pos += 2;
                int match = token & 15;
                if (match == 15) { int b; do { b = s[pos++]; match += b; } while (b == 255); }
                match += 4;
                int from = o - offset;
                for (int i = 0; i < match; i++) d[o++] = d[from + i]; // may overlap: byte by byte
            }
            if (o != rawSize)
                throw new InvalidDataException($"{path}: LZ4 data ended at {o} of {rawSize} bytes");
            return d;
        }
    }
}
