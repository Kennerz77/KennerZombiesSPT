// SPZombies headshot gore (licence text: data\gore\LICENSE-gore.txt).
// Changes for SPZombies: namespace, log prefix, file location, zombies-only (our zombies in a zombies match).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// braced namespace + c# 9 only — the unity batch check compiles this file as-is
namespace KennerZombies
{
    // reads gore-heads.bin (tools/extract_gore_heads.py): the the event's skull-open head
    // meshes keyed by the base head they replace, plus the shared flesh textures
    internal sealed class ZombiesGorePack : IDisposable
    {
        internal const string FileName = "gore-heads.bin";
        private const uint Magic = 0x4847_5A4D; // 'MZGH'
        private const uint SupportedVersion = 1;

        internal sealed class Head
        {
            internal string Prefab = string.Empty;
            internal string[] BaseMeshes = Array.Empty<string>();
            internal int FleshSubmesh;
            internal string[] BonePaths = Array.Empty<string>();
            internal string RootBone = string.Empty;
            internal Mesh Mesh = null!;
        }

        private readonly List<Head> _heads = new List<Head>();
        private readonly Dictionary<string, Head> _byBaseMesh = new Dictionary<string, Head>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Head> _byPrefab = new Dictionary<string, Head>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Mesh> _meshes = new HashSet<Mesh>();

        internal Texture2D? Diffuse { get; private set; }
        internal Texture2D? Normal { get; private set; }
        internal Texture2D? Gloss { get; private set; }
        internal IReadOnlyList<Head> Heads => _heads;

        internal bool IsGoreMesh(Mesh mesh) => _meshes.Contains(mesh);

        internal bool TryFind(string baseMeshName, string prefabName, out Head head)
        {
            return _byBaseMesh.TryGetValue(baseMeshName, out head!) || _byPrefab.TryGetValue(prefabName, out head!);
        }

        // keepReadable is for the offline check only; in game the cpu copy is dropped
        internal static ZombiesGorePack Load(string path, bool keepReadable = false)
        {
            var pack = new ZombiesGorePack();
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                if (reader.ReadUInt32() != Magic)
                    throw new InvalidDataException("not a gore pack");
                var version = reader.ReadUInt32();
                if (version != SupportedVersion)
                    throw new InvalidDataException("unsupported gore pack version " + version);

                var textureCount = reader.ReadInt32();
                for (var i = 0; i < textureCount; i++)
                    pack.ReadTexture(reader);

                var headCount = reader.ReadInt32();
                for (var i = 0; i < headCount; i++)
                    pack.ReadHead(reader, keepReadable);

                if (stream.Position != stream.Length)
                    throw new InvalidDataException("trailing bytes in gore pack");
                if (!pack.Diffuse || !pack.Normal || !pack.Gloss)
                    throw new InvalidDataException("gore pack is missing a flesh texture");
                return pack;
            }
            catch
            {
                pack.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var head in _heads)
            {
                if (head.Mesh)
                    UnityEngine.Object.Destroy(head.Mesh);
            }
            _heads.Clear();
            _byBaseMesh.Clear();
            _byPrefab.Clear();
            _meshes.Clear();
            if (Diffuse) UnityEngine.Object.Destroy(Diffuse);
            if (Normal) UnityEngine.Object.Destroy(Normal);
            if (Gloss) UnityEngine.Object.Destroy(Gloss);
            Diffuse = Normal = Gloss = null;
        }

        private void ReadTexture(BinaryReader reader)
        {
            var role = ReadString(reader);
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var format = reader.ReadInt32();
            var mips = reader.ReadInt32();
            var linear = reader.ReadByte() != 0;
            var data = reader.ReadBytes(reader.ReadInt32());
            if (format != (int)TextureFormat.DXT1 && format != (int)TextureFormat.DXT5)
                throw new InvalidDataException("unexpected texture format " + format);

            var texture = new Texture2D(width, height, (TextureFormat)format, mips, linear)
            {
                name = "gore_flesh_" + role,
                anisoLevel = 4,
            };
            texture.LoadRawTextureData(data);
            texture.Apply(false, true);
            switch (role)
            {
                case "diffuse": Diffuse = texture; break;
                case "normal": Normal = texture; break;
                case "gloss": Gloss = texture; break;
                default: UnityEngine.Object.Destroy(texture); break;
            }
        }

        private void ReadHead(BinaryReader reader, bool keepReadable)
        {
            var head = new Head { Prefab = ReadString(reader) };
            head.BaseMeshes = new string[reader.ReadInt32()];
            for (var i = 0; i < head.BaseMeshes.Length; i++)
                head.BaseMeshes[i] = ReadString(reader);
            var meshName = ReadString(reader);
            head.FleshSubmesh = reader.ReadInt32();
            head.BonePaths = new string[reader.ReadInt32()];
            for (var i = 0; i < head.BonePaths.Length; i++)
                head.BonePaths[i] = ReadString(reader);
            head.RootBone = ReadString(reader);

            var count = reader.ReadInt32();
            if (count <= 0 || count > ushort.MaxValue)
                throw new InvalidDataException(meshName + ": bad vertex count");
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new Vector4[count];
            var uv = new Vector2[count];
            var weights = new BoneWeight[count];
            for (var i = 0; i < count; i++)
                vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (var i = 0; i < count; i++)
                normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (var i = 0; i < count; i++)
                tangents[i] = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (var i = 0; i < count; i++)
                uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            for (var i = 0; i < count; i++)
            {
                int b0 = reader.ReadByte(), b1 = reader.ReadByte(), b2 = reader.ReadByte(), b3 = reader.ReadByte();
                weights[i] = new BoneWeight
                {
                    boneIndex0 = b0, boneIndex1 = b1, boneIndex2 = b2, boneIndex3 = b3,
                    weight0 = reader.ReadSingle(), weight1 = reader.ReadSingle(),
                    weight2 = reader.ReadSingle(), weight3 = reader.ReadSingle(),
                };
            }

            var bindposes = new Matrix4x4[reader.ReadInt32()];
            if (bindposes.Length != head.BonePaths.Length)
                throw new InvalidDataException(meshName + ": bone path / bind pose count mismatch");
            for (var i = 0; i < bindposes.Length; i++)
            {
                var pose = new Matrix4x4();
                // stored row by row
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 4; column++)
                        pose[row, column] = reader.ReadSingle();
                }
                bindposes[i] = pose;
            }

            var submeshes = new int[reader.ReadInt32()][];
            if (head.FleshSubmesh < 0 || head.FleshSubmesh >= submeshes.Length)
                throw new InvalidDataException(meshName + ": flesh submesh out of range");
            for (var s = 0; s < submeshes.Length; s++)
            {
                var indices = new int[reader.ReadInt32()];
                for (var i = 0; i < indices.Length; i++)
                    indices[i] = reader.ReadUInt16();
                submeshes[s] = indices;
            }
            var center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var extent = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            var mesh = new Mesh { name = meshName };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.boneWeights = weights;
            mesh.bindposes = bindposes;
            mesh.subMeshCount = submeshes.Length;
            for (var s = 0; s < submeshes.Length; s++)
                mesh.SetTriangles(submeshes[s], s, false);
            mesh.bounds = new Bounds(center, extent * 2f);
            mesh.UploadMeshData(!keepReadable);

            head.Mesh = mesh;
            _heads.Add(head);
            _meshes.Add(mesh);
            _byPrefab[head.Prefab] = head;
            foreach (var baseMesh in head.BaseMeshes)
                _byBaseMesh[baseMesh] = head;
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadUInt16();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
