using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace GC.Shared;

/// <summary>
/// Dreiecksnetz aus einer DLL (Blender-Export, Format GCM1), z. B. Tierpfeife, Seilglied, Seilrolle, Bärensattel.
/// Gesucht wird in den eingebetteten Dateien aller angemeldeten GC-Mods (GC-SDK): Eine Mod legt ihr Modell als
/// EmbeddedResource ab und liest es mit Gcm.Read("datei.gcm").
/// </summary>
public sealed class Gcm
{
	/// <summary>Der Kern und alle angemeldeten GC-Mods.</summary>
	private static IEnumerable<Assembly> Assemblies()
	{
		HashSet<Assembly> seen = new() { typeof(Gcm).Assembly };
		yield return typeof(Gcm).Assembly;
		foreach (GcModule module in GcKern.Modules)
		{
			Assembly assembly = module.Plugin.GetType().Assembly;
			if (seen.Add(assembly))
			{
				yield return assembly;
			}
		}
	}

	public Vector3[] Positions = Array.Empty<Vector3>();
	public Vector3[] Normals = Array.Empty<Vector3>();
	public Vector2[] Uvs = Array.Empty<Vector2>();
	public int[] Indices = Array.Empty<int>();

	/// <summary>
	/// 'GCM1', Ecken, Indizes, je Ecke Position, Normale, UV (Unity-Achsen). Prüft die Umlaufrichtung gegen die Normalen und
	/// dreht sie bei Bedarf um. null, wenn die Datei fehlt oder kaputt ist (steht dann im Log).
	/// </summary>
	public static Gcm? Read(string file)
	{
		try
		{
			string? resource = null;
			Assembly? owner = null;
			foreach (Assembly assembly in Assemblies())
			{
				foreach (string name in assembly.GetManifestResourceNames())
				{
					if (name.EndsWith("." + file, StringComparison.OrdinalIgnoreCase))
					{
						resource = name;
						owner = assembly;
					}
				}
			}
			if (resource == null || owner == null)
			{
				KernPlugin.Log.LogWarning($"Modell {file} fehlt in den DLLs der GC-Mods.");
				return null;
			}
			using Stream stream = owner.GetManifestResourceStream(resource)!;
			using BinaryReader reader = new(stream);
			if (new string(reader.ReadChars(4)) != "GCM1")
			{
				KernPlugin.Log.LogWarning($"Modell {file} hat ein unbekanntes Format.");
				return null;
			}
			int vertexCount = reader.ReadInt32();
			int indexCount = reader.ReadInt32();
			Gcm gcm = new()
			{
				Positions = new Vector3[vertexCount],
				Normals = new Vector3[vertexCount],
				Uvs = new Vector2[vertexCount],
				Indices = new int[indexCount],
			};
			for (int i = 0; i < vertexCount; i++)
			{
				gcm.Positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				gcm.Normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				gcm.Uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
			}
			for (int i = 0; i < indexCount; i++)
			{
				gcm.Indices[i] = reader.ReadInt32();
			}
			// Umlaufrichtung: Unity zeigt Dreiecke, deren Kreuzprodukt (b−a)×(c−a) zur Normalen zeigt
			double agreement = 0.0;
			for (int i = 0; i + 2 < indexCount; i += 3)
			{
				Vector3 a = gcm.Positions[gcm.Indices[i]], b = gcm.Positions[gcm.Indices[i + 1]], c = gcm.Positions[gcm.Indices[i + 2]];
				Vector3 n = gcm.Normals[gcm.Indices[i]] + gcm.Normals[gcm.Indices[i + 1]] + gcm.Normals[gcm.Indices[i + 2]];
				agreement += Mathf.Sign(Vector3.Dot(Vector3.Cross(b - a, c - a), n));
			}
			if (agreement < 0.0)
			{
				for (int i = 0; i + 2 < indexCount; i += 3)
				{
					(gcm.Indices[i + 1], gcm.Indices[i + 2]) = (gcm.Indices[i + 2], gcm.Indices[i + 1]);
				}
			}
			return gcm;
		}
		catch (Exception e)
		{
			KernPlugin.Log.LogWarning($"Modell {file} nicht geladen: {e.Message}");
			return null;
		}
	}

	public Mesh ToMesh(string name) => Combine(name, this);

	/// <summary>Ein Mesh, je Teil ein Untermesh (eigenes Material, z. B. Seil und Metall der Seilrolle).</summary>
	public static Mesh Combine(string name, params Gcm[] parts)
	{
		int vertexCount = 0;
		foreach (Gcm part in parts)
		{
			vertexCount += part.Positions.Length;
		}
		Vector3[] positions = new Vector3[vertexCount];
		Vector3[] normals = new Vector3[vertexCount];
		Vector2[] uvs = new Vector2[vertexCount];
		Mesh mesh = new() { name = name };
		if (vertexCount > 65000)
		{
			mesh.indexFormat = IndexFormat.UInt32;
		}
		int offset = 0;
		foreach (Gcm part in parts)
		{
			Array.Copy(part.Positions, 0, positions, offset, part.Positions.Length);
			Array.Copy(part.Normals, 0, normals, offset, part.Normals.Length);
			Array.Copy(part.Uvs, 0, uvs, offset, part.Uvs.Length);
			offset += part.Positions.Length;
		}
		mesh.vertices = positions;
		mesh.normals = normals;
		mesh.uv = uvs;
		mesh.subMeshCount = parts.Length;
		offset = 0;
		for (int i = 0; i < parts.Length; i++)
		{
			mesh.SetTriangles(parts[i].Indices, i, false, offset);
			offset += parts[i].Positions.Length;
		}
		mesh.RecalculateBounds();
		mesh.RecalculateTangents();
		return mesh;
	}
}
