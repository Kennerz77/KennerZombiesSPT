using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Readers for Greyhound's SEModel / SEAnim exports (dtzxporter spec, version 1).
// Kept free of UnityEngine so the parsing can be tested outside the editor.
namespace Zombies.SEFormats
{
    public struct SEVec3 { public float X, Y, Z; }
    public struct SEQuat { public float X, Y, Z, W; }

    public class SEBone
    {
        public string Name;
        public int Parent;
        public SEVec3 GlobalPosition, LocalPosition;
        public SEQuat GlobalRotation, LocalRotation;
        public bool HasGlobal, HasLocal;
    }

    public class SEMesh
    {
        public int VertexCount;
        public SEVec3[] Positions;
        public float[] UVs;             // u,v per vertex for UV layer 0
        public SEVec3[] Normals;
        public int MaxInfluence;
        public int[] WeightBones;       // VertexCount * MaxInfluence
        public float[] WeightValues;    // VertexCount * MaxInfluence
        public int[] Faces;             // 3 per face
        public int[] MaterialIndices;
    }

    public class SEMaterial
    {
        public string Name;
        public string DiffuseMap, NormalMap, SpecularMap;
    }

    public class SEModel
    {
        public List<SEBone> Bones = new List<SEBone>();
        public List<SEMesh> Meshes = new List<SEMesh>();
        public List<SEMaterial> Materials = new List<SEMaterial>();

        public static SEModel Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                string magic = Encoding.ASCII.GetString(r.ReadBytes(7));
                if (magic != "SEModel")
                    throw new InvalidDataException($"{path}: not an SEModel file");
                r.ReadUInt16(); // version
                long headerStart = r.BaseStream.Position;
                ushort headerSize = r.ReadUInt16();

                byte dataFlags = r.ReadByte();
                byte boneFlags = r.ReadByte();
                byte meshFlags = r.ReadByte();
                uint boneCount = r.ReadUInt32();
                uint meshCount = r.ReadUInt32();
                uint matCount = r.ReadUInt32();
                r.BaseStream.Position = headerStart + headerSize;

                var model = new SEModel();
                bool hasBones = (dataFlags & 1) != 0;
                bool hasMeshes = (dataFlags & 2) != 0;
                bool hasMaterials = (dataFlags & 4) != 0;

                if (hasBones)
                {
                    for (int i = 0; i < boneCount; i++)
                        model.Bones.Add(new SEBone { Name = ReadString(r) });

                    foreach (SEBone bone in model.Bones)
                    {
                        r.ReadByte(); // flags
                        bone.Parent = r.ReadInt32();
                        if ((boneFlags & 1) != 0)
                        {
                            bone.HasGlobal = true;
                            bone.GlobalPosition = ReadVec3(r);
                            bone.GlobalRotation = ReadQuat(r);
                        }
                        if ((boneFlags & 2) != 0)
                        {
                            bone.HasLocal = true;
                            bone.LocalPosition = ReadVec3(r);
                            bone.LocalRotation = ReadQuat(r);
                        }
                        if ((boneFlags & 4) != 0)
                            ReadVec3(r); // scale, unused
                    }
                }

                int boneIndexSize = IndexSize(boneCount);

                if (hasMeshes)
                {
                    for (int m = 0; m < meshCount; m++)
                    {
                        r.ReadByte(); // mesh flags (per-mesh, unused)
                        int layers = r.ReadByte();
                        int influence = r.ReadByte();
                        int vertexCount = (int)r.ReadUInt32();
                        int faceCount = (int)r.ReadUInt32();
                        if ((meshFlags & 1) == 0) layers = 0;
                        if ((meshFlags & 8) == 0) influence = 0;

                        var mesh = new SEMesh { VertexCount = vertexCount, MaxInfluence = influence };

                        mesh.Positions = new SEVec3[vertexCount];
                        for (int v = 0; v < vertexCount; v++)
                            mesh.Positions[v] = ReadVec3(r);

                        if (layers > 0)
                        {
                            mesh.UVs = new float[vertexCount * 2];
                            for (int v = 0; v < vertexCount; v++)
                            {
                                for (int l = 0; l < layers; l++)
                                {
                                    float u = r.ReadSingle();
                                    float vv = r.ReadSingle();
                                    if (l == 0)
                                    {
                                        mesh.UVs[v * 2] = u;
                                        mesh.UVs[v * 2 + 1] = vv;
                                    }
                                }
                            }
                        }

                        if ((meshFlags & 2) != 0)
                        {
                            mesh.Normals = new SEVec3[vertexCount];
                            for (int v = 0; v < vertexCount; v++)
                                mesh.Normals[v] = ReadVec3(r);
                        }

                        if ((meshFlags & 4) != 0)
                            r.ReadBytes(vertexCount * 4); // vertex colors, unused

                        if (influence > 0)
                        {
                            mesh.WeightBones = new int[vertexCount * influence];
                            mesh.WeightValues = new float[vertexCount * influence];
                            for (int i = 0; i < vertexCount * influence; i++)
                            {
                                mesh.WeightBones[i] = ReadIndex(r, boneIndexSize);
                                mesh.WeightValues[i] = r.ReadSingle();
                            }
                        }

                        int faceIndexSize = IndexSize((uint)vertexCount);
                        mesh.Faces = new int[faceCount * 3];
                        for (int i = 0; i < faceCount * 3; i++)
                            mesh.Faces[i] = ReadIndex(r, faceIndexSize);

                        mesh.MaterialIndices = new int[layers];
                        for (int l = 0; l < layers; l++)
                            mesh.MaterialIndices[l] = r.ReadInt32();

                        model.Meshes.Add(mesh);
                    }
                }

                if (hasMaterials)
                {
                    for (int i = 0; i < matCount; i++)
                    {
                        var mat = new SEMaterial { Name = ReadString(r) };
                        if (r.ReadByte() != 0)
                        {
                            mat.DiffuseMap = ReadString(r);
                            mat.NormalMap = ReadString(r);
                            mat.SpecularMap = ReadString(r);
                        }
                        model.Materials.Add(mat);
                    }
                }

                return model;
            }
        }

        internal static int IndexSize(uint count) => count <= 0xFF ? 1 : count <= 0xFFFF ? 2 : 4;

        internal static int ReadIndex(BinaryReader r, int size) =>
            size == 1 ? r.ReadByte() : size == 2 ? r.ReadUInt16() : (int)r.ReadUInt32();

        internal static string ReadString(BinaryReader r)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = r.ReadByte()) != 0)
                bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        internal static SEVec3 ReadVec3(BinaryReader r) =>
            new SEVec3 { X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle() };

        internal static SEQuat ReadQuat(BinaryReader r) =>
            new SEQuat { X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), W = r.ReadSingle() };
    }

    public enum SEAnimType : byte { Absolute = 0, Additive = 1, Relative = 2, Delta = 3 }

    public struct SEKey<T> { public int Frame; public T Value; }

    public class SEAnimBone
    {
        public string Name;
        public List<SEKey<SEVec3>> Positions = new List<SEKey<SEVec3>>();
        public List<SEKey<SEQuat>> Rotations = new List<SEKey<SEQuat>>();
    }

    public class SEAnim
    {
        public SEAnimType Type;
        public bool Looped;
        public float FrameRate;
        public int FrameCount;
        public List<SEAnimBone> Bones = new List<SEAnimBone>();

        public static SEAnim Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                string magic = Encoding.ASCII.GetString(r.ReadBytes(6));
                if (magic != "SEAnim")
                    throw new InvalidDataException($"{path}: not an SEAnim file");
                r.ReadUInt16(); // version
                long headerStart = r.BaseStream.Position;
                ushort headerSize = r.ReadUInt16();

                var anim = new SEAnim();
                anim.Type = (SEAnimType)r.ReadByte();
                anim.Looped = (r.ReadByte() & 1) != 0;
                byte dataFlags = r.ReadByte();
                bool highPrecision = (r.ReadByte() & 1) != 0;
                r.ReadBytes(2);
                anim.FrameRate = r.ReadSingle();
                anim.FrameCount = (int)r.ReadUInt32();
                uint boneCount = r.ReadUInt32();
                int modifierCount = r.ReadByte();
                r.ReadBytes(3);
                r.ReadUInt32(); // note count
                r.BaseStream.Position = headerStart + headerSize;

                for (int i = 0; i < boneCount; i++)
                    anim.Bones.Add(new SEAnimBone { Name = SEModel.ReadString(r) });

                int boneIndexSize = SEModel.IndexSize(boneCount);
                for (int i = 0; i < modifierCount; i++)
                {
                    SEModel.ReadIndex(r, boneIndexSize);
                    r.ReadByte();
                }

                if ((dataFlags & 0x07) == 0)
                    return anim;

                int frameSize = SEModel.IndexSize((uint)anim.FrameCount);
                foreach (SEAnimBone bone in anim.Bones)
                {
                    r.ReadByte(); // bone flags
                    if ((dataFlags & 1) != 0)
                    {
                        int keys = SEModel.ReadIndex(r, frameSize);
                        for (int k = 0; k < keys; k++)
                            bone.Positions.Add(new SEKey<SEVec3>
                            {
                                Frame = SEModel.ReadIndex(r, frameSize),
                                Value = new SEVec3 { X = Comp(r, highPrecision), Y = Comp(r, highPrecision), Z = Comp(r, highPrecision) }
                            });
                    }
                    if ((dataFlags & 2) != 0)
                    {
                        int keys = SEModel.ReadIndex(r, frameSize);
                        for (int k = 0; k < keys; k++)
                            bone.Rotations.Add(new SEKey<SEQuat>
                            {
                                Frame = SEModel.ReadIndex(r, frameSize),
                                Value = new SEQuat { X = Comp(r, highPrecision), Y = Comp(r, highPrecision), Z = Comp(r, highPrecision), W = Comp(r, highPrecision) }
                            });
                    }
                    if ((dataFlags & 4) != 0)
                    {
                        int keys = SEModel.ReadIndex(r, frameSize);
                        for (int k = 0; k < keys; k++)
                        {
                            SEModel.ReadIndex(r, frameSize);
                            Comp(r, highPrecision); Comp(r, highPrecision); Comp(r, highPrecision);
                        }
                    }
                }

                return anim;
            }
        }

        private static float Comp(BinaryReader r, bool highPrecision) =>
            highPrecision ? (float)r.ReadDouble() : r.ReadSingle();
    }
}
