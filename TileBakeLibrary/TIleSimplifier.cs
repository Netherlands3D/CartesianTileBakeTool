using g3;
using gs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using TileBakeLibrary.BinaryMesh;
using TileBakeLibrary.Coordinates;

namespace TileBakeLibrary
{
    public class TIleSimplifier
    {
		public void SimplifyTiles(string sourceFolder, string targetFolder, float targetGeometricError, bool combineSubobjects=false)
		{
			//TODO: create outputFolder
			var filter = $"*.bin";
			string[] binFiles = Directory.GetFiles(Path.GetDirectoryName(sourceFolder), filter);

            //foreach (string filePath in binFiles)
            //{
            //    SimplifyTile(filePath, targetFolder, targetGeometricError, combineSubobjects);
            //}

            Parallel.ForEach(binFiles, new ParallelOptions { MaxDegreeOfParallelism = 6 }, filePath =>
            {
                SimplifyTile(filePath, targetFolder, targetGeometricError, combineSubobjects);
            });
            Console.WriteLine("finished simplifying");

		}

		private void SimplifyTile(string sourceFilePath,string targetFolder, float targetGeometricError, bool combineSubobjects)
		{
            if (sourceFilePath.Contains("-data"))
            {
				return;
            }
			Tile originalTile = new Tile();
			originalTile.filePath = sourceFilePath;
			originalTile.size = new System.Numerics.Vector2(1000, 1000);

            string[] folderparts = sourceFilePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string[] filenameMainParts = folderparts[folderparts.Length - 1].Split('.');
            string[] filenameparts = filenameMainParts[0].Split('_');
            filenameparts[0] = filenameparts[0].Replace("Terrain", "");
            filenameparts[0] = filenameparts[0].Replace("buildings-", "");
            //string[] locationParts = filenameparts[1].Split('_');
            double posX = double.Parse(filenameparts[0]);
            double posY = double.Parse(filenameparts[1]);
            originalTile.position = new Coordinates.Vector2Double(posX, posY);

            BinaryMeshData bmd = new BinaryMeshData();
			bmd.ImportData(originalTile);
			bmd = null;

			Tile newTile = new Tile();
			string newFilename = $"{targetFolder}/{filenameMainParts[0]}.{targetGeometricError}.bin";
			newTile.filePath = newFilename;
			newTile.position = originalTile.position;
			newTile.size = originalTile.size;


            newTile = createSimplifiedTIle(originalTile, newTile, targetGeometricError, combineSubobjects);
            if (newTile == null)
            {
                return;
            }
            if (newTile.SubObjects.Count>0)
            {
				bmd = new BinaryMeshData();
				bmd.ExportData(newTile);
				bmd = null;
			}
			
		}
		

		Tile createSimplifiedTIle(Tile originalTile,Tile newTile, float geometricError, bool combineSubobjects)
		{
			DMesh3 mesh = new DMesh3(false,false,false,true);
            
            int groepnummer = 0;
			int basevertex = 0;
#pragma warning disable CS0219 // Variable is assigned but its value is never used
            int allocatedgroups = 0;
#pragma warning restore CS0219 // Variable is assigned but its value is never used
			foreach (var subobject in originalTile.SubObjects)
			{
                mesh.AllocateTriangleGroup();
                
                
                for (int i = 0; i < subobject.vertices.Count; i++)
                {
                    mesh.AppendVertex(new Vector3d(subobject.vertices[i].X, subobject.vertices[i].Y, subobject.vertices[i].Z));

                }
                for (int i = 0; i < subobject.triangleIndices.Count; i += 3)
                {
                    
                    mesh.AppendTriangle(subobject.triangleIndices[i]+basevertex, subobject.triangleIndices[i + 1]+basevertex, subobject.triangleIndices[i + 2]+basevertex, groepnummer);
                }
                basevertex += subobject.vertices.Count;
                groepnummer++;
            }
           

            
            //MergeCoincidentEdges merg = new MergeCoincidentEdges(mesh);
            //merg.Apply();

            MeshNormals.QuickCompute(mesh);

            if (mesh.CheckValidity(true, FailMode.ReturnOnly) == false)
            {
               
                if (mesh.VertexCount<3)
                {
                Console.WriteLine("invalid " + originalTile.filePath);
                    return originalTile;
                }
            }

            // setup up the reducer
            Reducer reducer = new Reducer(mesh);
            // set reducer to preserve bounds

            MeshConstraints constraints = new MeshConstraints();
            constraints.AllocateSetID();
            
            reducer.SetExternalConstraints(constraints);
            
           
            reducer.ReduceToEdgeLength(geometricError);
            mesh = reducer.Mesh;
            //WriteMesh outputMesh = new WriteMesh(mesh);

			List<Vector3Double> vertices = new List<Vector3Double>();
            int[] mapV = new int[mesh.MaxVertexID];
            int nAccumCountV = 0;
			// get all the vertices
            foreach (int vi in mesh.VertexIndices())
            {
                mapV[vi] = nAccumCountV++;
                Vector3d v = mesh.GetVertex(vi);
                vertices.Add(new Vector3Double(v.x, v.y, v.z));
            }
			Dictionary<int, SubObject> subobjectDictionary = new Dictionary<int, SubObject>();
            foreach (int ti in mesh.TriangleIndices())
            {
                int trianglegroup = mesh.GetTriangleGroup(ti);
                int subobjectId = 0;
                if (combineSubobjects)
                {
                    subobjectId = originalTile.SubObjects[trianglegroup].parentSubmeshIndex;
                }
                else
                {
                    subobjectId = trianglegroup;
                }

                if (subobjectDictionary.ContainsKey(subobjectId) ==false)
                {
                    subobjectDictionary.Add(subobjectId, new SubObject());

                        subobjectDictionary[subobjectId].parentSubmeshIndex = originalTile.SubObjects[trianglegroup].parentSubmeshIndex;
                        subobjectDictionary[subobjectId].id = originalTile.SubObjects[trianglegroup].id;

                }

                Index3i t = mesh.GetTriangle(ti);
                int startvertex = subobjectDictionary[subobjectId].vertices.Count;
                subobjectDictionary[subobjectId].vertices.Add(vertices[mapV[t[0]]]);
                subobjectDictionary[subobjectId].vertices.Add(vertices[mapV[t[1]]]);
                subobjectDictionary[subobjectId].vertices.Add(vertices[mapV[t[2]]]);
                subobjectDictionary[subobjectId].triangleIndices.Add(startvertex);
                subobjectDictionary[subobjectId].triangleIndices.Add(startvertex+1);
                subobjectDictionary[subobjectId].triangleIndices.Add(startvertex+2);
                
            }
            foreach (KeyValuePair<int,SubObject> kvp in subobjectDictionary)
            {
                kvp.Value.CalculateNormals();
                kvp.Value.MergeSimilarVertices();
                
                newTile.SubObjects.Add(kvp.Value);
            }
            return newTile;

        }

	}

	
}
