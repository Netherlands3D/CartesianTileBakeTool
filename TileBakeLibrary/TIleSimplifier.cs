using g4;
using gs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TileBakeLibrary.BinaryMesh;
using TileBakeLibrary.Coordinates;

namespace TileBakeLibrary
{
    public class TileSimplifier
    {
        public void SimplifyTiles(string sourceFolder, string targetFolder, int squareMetersPerVertex, bool combineSubobjects = false)
        {
            // Ensure target folder exists
            Directory.CreateDirectory(targetFolder);

            var filter = $"*.bin";

            string path = Directory.Exists(sourceFolder)
                ? sourceFolder
                : Path.GetDirectoryName(sourceFolder);

            string[] binFiles = Directory.GetFiles(path, filter);

            Parallel.ForEach(binFiles, new ParallelOptions { MaxDegreeOfParallelism = 6 }, filePath =>
            {
                SimplifyTile(filePath, targetFolder, squareMetersPerVertex, combineSubobjects);
            });

            Console.WriteLine("Finished simplifying.");
        }

        private void SimplifyTile(string sourceFilePath, string targetFolder, int squareMetersPerVertex, bool combineSubobjects)
        {
            string fileName = Path.GetFileName(sourceFilePath);

            if (fileName.Contains("-data"))
                return;

            var match = Regex.Match(fileName, @"(\d+)_([\d]+)");

            if (!match.Success)
            {
                Console.WriteLine($"Could not find coordinates in filename: {fileName}");
                return;
            }

            if (!double.TryParse(match.Groups[1].Value, out double posX) ||
                !double.TryParse(match.Groups[2].Value, out double posY))
            {
                Console.WriteLine($"Could not parse X/Y from: {match.Groups[1].Value}, {match.Groups[2].Value}");
                return;
            }

            Tile originalTile = new Tile
            {
                filePath = sourceFilePath,
                size = new Vector2(1000, 1000),
                position = new Coordinates.Vector2Double(posX, posY)
            };

            BinaryMeshData bmd = new BinaryMeshData();
            bmd.ImportData(originalTile);
            bmd = null;

            Tile newTile = new Tile
            {
                position = originalTile.position,
                size = originalTile.size
            };

            string baseFileName = Path.GetFileNameWithoutExtension(fileName);
            string newFilename = Path.Combine(targetFolder, $"{baseFileName}.{squareMetersPerVertex}.bin");
            newTile.filePath = newFilename;

            Console.WriteLine($"Simplifying {fileName}");

            double vertexDensity = 1.0 / squareMetersPerVertex;
            newTile = createSimplifiedTIle(originalTile, newTile, vertexDensity, combineSubobjects);

            if (newTile == null || newTile.SubObjects.Count == 0)
                return;

            bmd = new BinaryMeshData();
            bmd.ExportData(newTile);
            bmd = null;
        }

        private Tile createSimplifiedTIle(Tile originalTile, Tile newTile, double vertexDensity, bool combineSubobjects)
        {
            DMesh3 mesh = new DMesh3(false, false, false, true);

            int groepnummer = 0;
            int basevertex = 0;

            foreach (var subobject in originalTile.SubObjects)
            {
                mesh.AllocateTriangleGroup();

                foreach (var v in subobject.vertices)
                {
                    mesh.AppendVertex(new Vector3d(v.X, v.Y, v.Z));
                }

                for (int i = 0; i < subobject.triangleIndices.Count; i += 3)
                {
                    mesh.AppendTriangle(
                        subobject.triangleIndices[i] + basevertex,
                        subobject.triangleIndices[i + 1] + basevertex,
                        subobject.triangleIndices[i + 2] + basevertex,
                        groepnummer);
                }

                basevertex += subobject.vertices.Count;
                groepnummer++;
            }

            MeshNormals.QuickCompute(mesh);

            if (mesh.CheckValidity(true, FailMode.ReturnOnly) == false && mesh.VertexCount < 3)
            {
                Console.WriteLine("Invalid mesh: " + originalTile.filePath);
                return originalTile;
            }

            MergeCoincidentEdges merg = new MergeCoincidentEdges(mesh);
            merg.Apply();

            Reducer reducer = new Reducer(mesh);
            MeshConstraints constraints = new MeshConstraints();
            reducer.SetExternalConstraints(constraints);
            MeshConstraintUtil.FixAllBoundaryEdges(constraints, mesh);

            double area = MeshMeasurements.AreaT(mesh, mesh.TriangleIndices());
            int edgecount = mesh.BoundaryEdgeIndices().Count(p => p > -1);
            int maxSurfaceCount = (int)(area * vertexDensity) + edgecount;

            if (mesh.VertexCount > maxSurfaceCount)
            {
                reducer.ReduceToVertexCount(maxSurfaceCount);
                mesh = reducer.Mesh;
            }

            // Extract vertices
            List<Vector3Double> vertices = new List<Vector3Double>();
            int[] mapV = new int[mesh.MaxVertexID];
            int nAccumCountV = 0;

            foreach (int vi in mesh.VertexIndices())
            {
                mapV[vi] = nAccumCountV++;
                Vector3d v = mesh.GetVertex(vi);
                vertices.Add(new Vector3Double(v.x, v.y, v.z));
            }

            // Rebuild subobjects
            Dictionary<int, SubObject> subobjectDictionary = new Dictionary<int, SubObject>();

            foreach (int ti in mesh.TriangleIndices())
            {
                int group = mesh.GetTriangleGroup(ti);
                int subId = combineSubobjects
                    ? originalTile.SubObjects[group].parentSubmeshIndex
                    : group;

                if (!subobjectDictionary.ContainsKey(subId))
                {
                    subobjectDictionary[subId] = new SubObject
                    {
                        parentSubmeshIndex = originalTile.SubObjects[group].parentSubmeshIndex,
                        id = originalTile.SubObjects[group].id
                    };
                }

                Index3i t = mesh.GetTriangle(ti);
                int start = subobjectDictionary[subId].vertices.Count;

                subobjectDictionary[subId].vertices.Add(vertices[mapV[t[0]]]);
                subobjectDictionary[subId].vertices.Add(vertices[mapV[t[1]]]);
                subobjectDictionary[subId].vertices.Add(vertices[mapV[t[2]]]);
                subobjectDictionary[subId].triangleIndices.AddRange(new[] { start, start + 1, start + 2 });
            }

            foreach (var kvp in subobjectDictionary)
            {
                kvp.Value.CalculateNormals();
                kvp.Value.MergeSimilarVertices();
                newTile.SubObjects.Add(kvp.Value);
            }

            return newTile;
        }
    }
}
