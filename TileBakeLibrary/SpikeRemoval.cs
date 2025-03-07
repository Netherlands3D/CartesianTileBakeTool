using g3;
using Netherlands3D.Gltf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TileBakeLibrary;
using TileBakeLibrary.BinaryMesh;
using TileBakeLibrary.Coordinates;

using g3;
using gs;

namespace TileBakeLibrary
{
    public static class SpikeRemoval
    {
        enum SpikeStatus
        {
            Undefined,
            Spike,
            NoSpike
        }

        struct point
        {
            public float east;
            public float north;
            public int vertexIndex;
            public int subobjectIndex;
            public int pointindex;
            public float originalElevation;
            public float newElevation;
            public SpikeStatus status;
        }
        struct StatisticalProperties
        {
            public float mean;
            public double standardDeviation;
            public float ceiling;
            public float floor;

            public StatisticalProperties(point[] points, float sigma)
            {
                mean = 0;
                standardDeviation = 0;
                ceiling = 0;
                floor = 0;
                Calculate(points,sigma);
            }

            private void Calculate(point[] points, float sigma)
            {
                // get the mean of the total set
                float standardMean = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    standardMean += points[i].originalElevation;
                }
                standardMean /= points.Length;
                // get the standardDeviation
                double deviationSum = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    deviationSum += Math.Pow(standardMean - points[i].originalElevation, 2d);
                }
                standardDeviation = Math.Sqrt(deviationSum / points.Length);

                // get the winsorisation cut-offs
                float windsorisationFloor = (float)(standardMean - (sigma * standardDeviation));
                float winsorisationCeiling = (float)(standardMean + (sigma * standardDeviation));

                // get the winsorised mean
                int vertexcount = 0;
                float totalValue = 0;
                for(int i = 0;i < points.Length;i++)
                {
                    if(points[i].originalElevation > windsorisationFloor && points[i].originalElevation < winsorisationCeiling)
                    {
                        totalValue+= points[i].originalElevation;
                        vertexcount++;
                    }
                }
                mean = totalValue/vertexcount;

                double winsorisedDeviationSum = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    if (points[i].originalElevation > windsorisationFloor && points[i].originalElevation < winsorisationCeiling)
                    {
                        winsorisedDeviationSum += Math.Pow(mean - points[i].originalElevation, 2d); ;
                    }
                }
                standardDeviation = Math.Sqrt(winsorisedDeviationSum / points.Length);
                floor = (float)(mean-(sigma*standardDeviation));
                ceiling = (float)(mean+(sigma*standardDeviation));

            }
        }

     
       
    
        

        public static void RemoveSpikes(string inputfile, bool compress = false)
        {

            int filecount;
            int activeFiles=0;
            int compressing = 0;
            int done=0;

            if (inputfile.Contains(".bin")) //single file
            {
                removeSpikesFromSingleFile(inputfile);
                if (compress) BrotliCompress.Compress(inputfile);
                return;
            }
            string[] binFiles = findNewFiles(inputfile);

            filecount = binFiles.Length;
            Console.WriteLine("removing spikes from " + filecount + "tiles");
            Console.WriteLine("");
            ParallelOptions parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 32 // Limit to 32 threads
            };
            Parallel.ForEach(binFiles, parallelOptions, filename =>
            {
                Interlocked.Increment(ref activeFiles);
                WriteSpikeRemovalStatusToConsole(filecount, done, activeFiles);
                removeSpikesFromSingleFile(filename);
                Interlocked.Decrement(ref activeFiles);
                Interlocked.Increment(ref done);
                WriteSpikeRemovalStatusToConsole(filecount, done, activeFiles);
            });


            //foreach (var filename in binFiles)
            //{
            //    Interlocked.Increment(ref activeFiles);
            //    WriteSpikeRemovalStatusToConsole(filecount, done, activeFiles);
            //    removeSpikesFromSingleFile(filename);
            //    Interlocked.Decrement(ref activeFiles);
            //    Interlocked.Increment(ref done);
            //    WriteSpikeRemovalStatusToConsole(filecount, done, activeFiles);

            //}

            if (compress)
            {
                done = 0;
                Parallel.ForEach(binFiles, filename =>
                {
                    Interlocked.Increment(ref compressing);
                    WriteCompressingStatusToConsole(filecount, done, compressing);
                    BrotliCompress.Compress(filename);
                    Interlocked.Decrement(ref compressing);
                    Interlocked.Increment(ref done);
                    WriteCompressingStatusToConsole(filecount, done, compressing);
                });
            }


            foreach (var filename in binFiles)
            {

                removeSpikesFromSingleFile(filename);

            }
        }

        private static void WriteSpikeRemovalStatusToConsole(int filecount, int done, int active)
        {
           
                Console.Write("\rdone: " + done + " of " + filecount + " | removing spikes: " + active +  "             ");

        }
        private static void WriteCompressingStatusToConsole(int filecount, int done, int active)
        {
            Console.Write("\rdone: " + done + " of " + filecount + " | compressing: " + active + "             ");
        }

        static string[] findNewFiles(string filepath)
        {
            var binFilesFilter = $"*.bin";

            string[] allBinFiles = Directory.GetFiles(Path.GetDirectoryName(filepath), binFilesFilter);
            List<string> newFiles = new List<string>();
            for (int i = 0; i < allBinFiles.Length; i++)
            {
                bool isNew = true;
                if (allBinFiles[i].EndsWith("data.bin"))
                {
                    continue;
                }
                if (System.IO.File.Exists(allBinFiles[i]+".br"))
                {
                    if (System.IO.File.GetCreationTimeUtc(allBinFiles[i] + ".br") > System.IO.File.GetCreationTimeUtc(allBinFiles[i]))
                    {
                        isNew = false;
                    }
                }
                if (isNew == true)
                {
                    newFiles.Add(allBinFiles[i]);
                }
            }
            return newFiles.ToArray();
        }

        static void removeSpikesFromSingleFile(string inputfile)
        {
            BinaryMeshData binaryMeshData = new BinaryMeshData();
            Tile tile = new Tile();
            tile.filePath = inputfile;
            binaryMeshData.ImportData(tile);

            //MeshData mesh = BinaryMeshReader.ReadBinaryMesh(tile.filePath);

            point[] allPoints = readPoints(tile);
            StatisticalProperties fullTileStatistics = new StatisticalProperties(allPoints, 5);
            if (fullTileStatistics.standardDeviation < 5)
            {
                allPoints = MovePoints(allPoints, fullTileStatistics);
            }
            else
            {
                allPoints = MovePointsTiled(allPoints);
            }

            tile = ApplyChangesToTile(tile, allPoints);
            //mesh = ApplyChangesToMesh(mesh, allPoints);

            recalculateNormals(tile);
            //mesh = RecalculateNormals(mesh);
            
            //BinaryMeshWriter.WriteMesh(mesh, tile.filePath);
            binaryMeshData.ExportData(tile);
            
        }
        static void recalculateNormals(Tile tile)
        {
            for (int i = 0; i < tile.SubObjects.Count; i++)
            {
                tile.SubObjects[i].CalculateNormals();
            }
        }

        static point[] readPoints(MeshData mesh)
        {
            point[] points = new point[mesh.vertices.Count];
            for (int i = 0; i < mesh.vertices.Count; i++)
            {
                point newpoint = new point();
                newpoint.originalElevation = mesh.vertices[i].Y;
                newpoint.east = mesh.vertices[i].X;
                newpoint.north = mesh.vertices[i].Z;
                newpoint.newElevation = newpoint.originalElevation;
                points[i] = newpoint;
                //newpoint.vertexIndex = j;
                //newpoint.subobjectIndex = i;
                //newpoint.status = SpikeStatus.Undefined;
                //newpoint.pointindex = pointindex;
                //pointindex++;
                //points.Add(newpoint);
            }
            return points;
        }

        static point[] readPoints(Tile tile)
        {
            int pointindex = 0;
            List<point> points = new List<point>();
            for (int i = 0; i < tile.SubObjects.Count; i++)
            {
                for(int j = 0; j < tile.SubObjects[i].vertices.Count; j++)
                {
                    point newpoint = new point();
                    newpoint.originalElevation = (float)tile.SubObjects[i].vertices[j].Z;
                    newpoint.east = (float)tile.SubObjects[i].vertices[j].X;
                    newpoint.north = (float)tile.SubObjects[i].vertices[j].Y;
                    newpoint.newElevation = newpoint.originalElevation;
                    newpoint.vertexIndex = j;
                    newpoint.subobjectIndex = i;
                    newpoint.status = SpikeStatus.Undefined;
                    newpoint.pointindex = pointindex;
                    pointindex++;
                    points.Add(newpoint);
                }
            }

            
            return points.ToArray();
        }

        private static point[] MovePoints(point[] points,StatisticalProperties statistics)
        {
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i].originalElevation < statistics.floor)
                {
                    if (points[i].status != SpikeStatus.NoSpike)
                    {
                        points[i].status = SpikeStatus.Spike;
                        points[i].newElevation = statistics.mean;
                    }
                }
                else if (points[i].originalElevation > statistics.ceiling)
                {
                    if (points[i].status != SpikeStatus.NoSpike)
                    {
                        points[i].status = SpikeStatus.Spike;
                        points[i].newElevation = statistics.mean;
                    }
                }
                else
                {
                    points[i].status = SpikeStatus.NoSpike;
                    points[i].newElevation = points[i].originalElevation;
                }

            }

            return points;
        }

        private static point[] MovePointsTiled(point[] points)
        {
            int stepsize = 150;
            int checkDistance = 2 * stepsize;
            List<point> pointlist = new List<point>();
            point[] tilepoints;

            for (int east = -500; east < 500; east += stepsize)
            {
                for (int north = -500; north < 500; north += stepsize)
                {
                    pointlist.Clear();
                    for (int i = 0; i < points.Length; i++)
                    {
                        if (points[i].east < east) continue;
                        if (points[i].east > east+checkDistance) continue;
                        if (points[i].north < north) continue;
                        if (points[i].north > north + checkDistance) continue;
                        pointlist.Add(points[i]);
                    }
                    tilepoints = pointlist.ToArray();
                    StatisticalProperties tileStatistics = new StatisticalProperties(tilepoints, 3f);
                    tilepoints = MovePoints(tilepoints, tileStatistics);
                    for (int i = 0;i < tilepoints.Length; i++)
                    {
                        points[tilepoints[i].pointindex] = tilepoints[i];
                    }
                }
            }

            return points;
        }

        private static MeshData  ApplyChangesToMesh(MeshData mesh, point[] points)
        {
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 pos = new Vector3(points[i].east, points[i].newElevation, points[i].north);
                mesh.vertices[i] = pos;
            }
            return mesh;
        }

        private static Tile ApplyChangesToTile(Tile tile, point[] points)
        {
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i].status == SpikeStatus.Spike)
                {
                    Vector3Double newPosition = new Vector3Double(points[i].east, points[i].north, points[i].newElevation);
                    tile.SubObjects[points[i].subobjectIndex].vertices[points[i].vertexIndex] = newPosition;
                    
                }
            }
            return tile;
        }

        private static MeshData RecalculateNormals(MeshData mesh)
        {
            DMesh3 smartmesh = new DMesh3(false, false, false, false);
            for (int i = 0; i < mesh.vertices.Count; i++)
            {
                smartmesh.AppendVertex(new Vector3d(mesh.vertices[i].X, mesh.vertices[i].Y, mesh.vertices[i].Z));

            }
            for (int i = 0; i < mesh.indexCount/3; i += 3)
            {
                smartmesh.AppendTriangle(mesh.indices[i], mesh.indices[i + 1], mesh.indices[i + 2]);
            }
            
            MeshNormals.QuickCompute(smartmesh);

            Vector3d vector;
            Vector3d normal;
            int[] mapV = new int[smartmesh.MaxVertexID];
            int nAccumCountV = 0;

            int index = 0;
            mesh.normals.Clear();
            foreach (int vi in smartmesh.VertexIndices())
            {
                mapV[vi] = nAccumCountV++;
               
                normal = smartmesh.GetVertexNormal(vi);
                mesh.normals.Add(new Vector3((float)normal.x, (float)normal.y, (float)normal.z));

                index++;
            }
            return mesh;

        }

        public static void TestVertexCounts(List<string> filenames)
        {
            int errorcount = 0;
            int subObjectVertexCount = 0;
            int meshvertexcount = 0;
            for (int i = 0; i < filenames.Count; i++)
            {
                subObjectVertexCount = 0;
                BinaryMeshData binaryMeshData = new BinaryMeshData();
                Tile tile = new Tile();
                tile.filePath = filenames[i];
                binaryMeshData.ImportData(tile);
                subObjectVertexCount = 0;
                for (int j = 0; j < tile.SubObjects.Count; j++)
                {
                    subObjectVertexCount += tile.SubObjects[j].vertices.Count;
                }

                MeshData mesh = BinaryMeshReader.ReadBinaryMesh(filenames[i]);
                meshvertexcount = mesh.vertexCount;

                if (subObjectVertexCount!=meshvertexcount)
                {
                    Console.WriteLine(filenames[i]);
                    errorcount++;
                }

            }
            Console.WriteLine(errorcount + " tiles with errors");
        }
    }
}
