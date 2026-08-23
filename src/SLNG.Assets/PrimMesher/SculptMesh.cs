/*
 * Copyright (c) Contributors
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace SLNG.Assets.PrimMesher
{
    public class SculptMesh
    {
        public enum SculptType
        {
            sphere = 1,
            torus = 2,
            plane = 3,
            cylinder = 4
        }

        public List<Coord> coords = new();
        public List<Face> faces = new();
        public List<Coord> normals = new();
        public List<UVCoord> uvs = new();

        public List<ViewerFace> viewerFaces = new();


        public SculptMesh(string fileName, int sculptType, int lod, int viewerMode, int mirror, int invert)
        {
            using (var bitmap = SKBitmap.Decode(fileName))
            {
                if (bitmap == null) throw new FileNotFoundException("Unable to decode sculpt image", fileName);
                _SculptMesh(bitmap, (SculptType)sculptType, lod, viewerMode != 0, mirror != 0, invert != 0);
            }
        }

        /// <summary>
        ///     ** Experimental ** May disappear from future versions ** not recommended for use in applications
        ///     Construct a sculpt mesh from a 2D array of floats
        /// </summary>
        /// <param name="zMap"></param>
        /// <param name="xBegin"></param>
        /// <param name="xEnd"></param>
        /// <param name="yBegin"></param>
        /// <param name="yEnd"></param>
        /// <param name="viewerMode"></param>
        public SculptMesh(float[,] zMap, float xBegin, float xEnd, float yBegin, float yEnd, bool viewerMode)
        {
            float xStep, yStep;
            float uStep, vStep;

            var numYElements = zMap.GetLength(0);
            var numXElements = zMap.GetLength(1);

            try
            {
                xStep = (xEnd - xBegin) / (numXElements - 1);
                yStep = (yEnd - yBegin) / (numYElements - 1);

                uStep = 1.0f / (numXElements - 1);
                vStep = 1.0f / (numYElements - 1);
            }
            catch (DivideByZeroException)
            {
                return;
            }

            coords = new List<Coord>();
            faces = new List<Face>();
            normals = new List<Coord>();
            uvs = new List<UVCoord>();

            viewerFaces = new List<ViewerFace>();

            int y;
            const int xStart = 0;
            const int yStart = 0;

            for (y = yStart; y < numYElements; y++)
            {
                var rowOffset = y * numXElements;

                int x;
                for (x = xStart; x < numXElements; x++)
                {
                    /*
                    *   p1-----p2
                    *   | \ f2 |
                    *   |   \  |
                    *   | f1  \|
                    *   p3-----p4
                    */

                    var p4 = rowOffset + x;
                    var p3 = p4 - 1;

                    var p2 = p4 - numXElements;
                    var p1 = p3 - numXElements;

                    var c = new Coord(xBegin + x * xStep, yBegin + y * yStep, zMap[y, x]);
                    coords.Add(c);
                    if (viewerMode)
                    {
                        normals.Add(new Coord());
                        uvs.Add(new UVCoord(uStep * x, 1.0f - vStep * y));
                    }

                    if (y > 0 && x > 0)
                    {
                        Face f1, f2;

                        if (viewerMode)
                        {
                            f1 = new Face(p1, p4, p3, p1, p4, p3)
                            {
                                uv1 = p1,
                                uv2 = p4,
                                uv3 = p3
                            };

                            f2 = new Face(p1, p2, p4, p1, p2, p4)
                            {
                                uv1 = p1,
                                uv2 = p2,
                                uv3 = p4
                            };
                        }
                        else
                        {
                            f1 = new Face(p1, p4, p3);
                            f2 = new Face(p1, p2, p4);
                        }

                        faces.Add(f1);
                        faces.Add(f2);
                    }
                }
            }

            if (viewerMode)
                calcVertexNormals(SculptType.plane, numXElements, numYElements);
        }

        public SculptMesh(SKBitmap sculptBitmap, SculptType sculptType, int lod, bool viewerMode)
        {
            _SculptMesh(sculptBitmap, sculptType, lod, viewerMode, false, false);
        }

        public SculptMesh(SKBitmap sculptBitmap, SculptType sculptType, int lod, bool viewerMode, bool mirror,
            bool invert)
        {
            _SculptMesh(sculptBitmap, sculptType, lod, viewerMode, mirror, invert);
        }

        /// <summary>Builds from a map sampled at an EXPLICIT grid (the viewer's, see
        /// SlSculptResolution) rather than at PrimMesher's halved one.</summary>
        public SculptMesh(SKBitmap sculptBitmap, SculptType sculptType, int gridWidth, int gridHeight,
            bool viewerMode, bool mirror, bool invert)
        {
            _SculptMesh(new SculptMap(sculptBitmap, gridWidth, gridHeight).ToRows(mirror),
                sculptType, viewerMode, mirror, invert);
        }

        public SculptMesh(List<List<Coord>> rows, SculptType sculptType, bool viewerMode, bool mirror, bool invert)
        {
            _SculptMesh(rows, sculptType, viewerMode, mirror, invert);
        }

        public SculptMesh(SculptMesh sm)
        {
            coords = new List<Coord>(sm.coords);
            faces = new List<Face>(sm.faces);
            viewerFaces = new List<ViewerFace>(sm.viewerFaces);
            normals = new List<Coord>(sm.normals);
            uvs = new List<UVCoord>(sm.uvs);
        }

        public SculptMesh SculptMeshFromFile(string fileName, SculptType sculptType, int lod, bool viewerMode)
        {
            using (var img = SKImage.FromEncodedData(fileName))
            using (var bitmap = SKBitmap.FromImage(img))
            {
                var sculptMesh = new SculptMesh(bitmap, sculptType, lod, viewerMode);
                return sculptMesh;
            }
        }

        // bitmap2Coords / bitmap2CoordsSampled lived here and were DEAD CODE: nothing called
        // either of them. The live path is SculptMap(bitmap, lod).ToRows(mirror) -- see
        // _SculptMesh below. They are deleted rather than left in place because they were
        // actively misleading: bitmap2Coords box-FILTERED the map (averaging neighbouring texels,
        // which flattens exactly the ridges a sculpt is made of) and bitmap2CoordsSampled divided
        // by 256 where the viewer divides by 255 (sculpt_rgb_to_vector, llvolume.cpp:2892). Both
        // read like the algorithm in use and neither is; one investigation has already been
        // spent on them.

        private void _SculptMesh(SKBitmap sculptBitmap, SculptType sculptType, int lod, bool viewerMode, bool mirror,
            bool invert)
        {
            _SculptMesh(new SculptMap(sculptBitmap, lod).ToRows(mirror), sculptType, viewerMode, mirror, invert);
        }

        private void _SculptMesh(List<List<Coord>> rows, SculptType sculptType, bool viewerMode, bool mirror,
            bool invert)
        {
            coords = new List<Coord>();
            faces = new List<Face>();
            normals = new List<Coord>();
            uvs = new List<UVCoord>();

            sculptType = (SculptType) ((int) sculptType & 0x07);

            if (mirror)
                invert = !invert;

            viewerFaces = new List<ViewerFace>();

            var width = rows[0].Count;

            int imageY;

            // Close the horizontal (X/profile) wrap ring by making each row's LAST column re-sample
            // column 0. Verified against LLVolume::sculptGenerateMapVertices (llvolume.cpp:
            // 3101-3115): at the x == sculpt_width boundary, sphere/torus/cylinder REPLACE that
            // index with x = 0 (plane instead clamps to sculpt_width - 1, which is why plane is
            // excluded here and keeps its real last column). The viewer therefore never samples the
            // map's own final column for a wrapping type -- its last output column IS column 0.
            //
            // This used to APPEND column 0 as an extra column instead of replacing the last one.
            // SculptMap already emits a (width+1)-wide grid whose final column is the map's native
            // last column, so appending produced ... 62, 63, 0 where the viewer has ... 62, 0 --
            // an extra, viewer-invisible sliver of geometry spanning the seam. On a sculpt whose
            // final column is not a near-duplicate of column 0 (most of them), that sliver drags
            // the seam inward and pinches it: a round cushion rendered as a teardrop with a visible
            // point on one side, live-confirmed 2026-08-01 against a Firestorm top-down reference.
            //
            // (The even/odd `rows.Count % 2` branch this replaced earlier was a separate, also-real
            // bug: for the odd case it OVERWROTE column 0 with the FAR edge's data, discarding the
            // real left-edge column and creasing any organic sculpt. Both are the same class of
            // seam-handling error; this is the form the viewer actually implements.)
            if (sculptType != SculptType.plane)
            {
                foreach (List<Coord> row in rows)
                    row[row.Count - 1] = row[0];
            }

            var topPole = rows[0][width / 2];
            var bottomPole = rows[rows.Count - 1][width / 2];

            if (sculptType == SculptType.sphere)
                if (rows.Count % 2 == 0)
                {
                    var count = rows[0].Count;
                    var topPoleRow = new List<Coord>(count);
                    var bottomPoleRow = new List<Coord>(count);

                    for (var i = 0; i < count; i++)
                    {
                        topPoleRow.Add(topPole);
                        bottomPoleRow.Add(bottomPole);
                    }
                    rows.Insert(0, topPoleRow);
                    rows.Add(bottomPoleRow);
                }
                else
                {
                    var count = rows[0].Count;

                    var topPoleRow = rows[0];
                    var bottomPoleRow = rows[rows.Count - 1];

                    for (var i = 0; i < count; i++)
                    {
                        topPoleRow[i] = topPole;
                        bottomPoleRow[i] = bottomPole;
                    }
                }

            // Same replace-don't-append rule on the vertical wrap, for the one type that wraps in
            // Y: llvolume.cpp:3083-3086 maps y == sculpt_height to y = 0 for TORUS (every other
            // type clamps to sculpt_height - 1, which is what SculptMap's final row already holds).
            if (sculptType == SculptType.torus)
                rows[rows.Count - 1] = rows[0];

            var coordsDown = rows.Count;
            var coordsAcross = rows[0].Count;
            var lastColumn = coordsAcross - 1;

            var widthUnit = 1.0f / (coordsAcross - 1);
            var heightUnit = 1.0f / (coordsDown - 1);

            for (imageY = 0; imageY < coordsDown; imageY++)
            {
                var rowOffset = imageY * coordsAcross;

                int imageX;
                for (imageX = 0; imageX < coordsAcross; imageX++)
                {
                    /*
                    *   p1-----p2
                    *   | \ f2 |
                    *   |   \  |
                    *   | f1  \|
                    *   p3-----p4
                    */

                    var p4 = rowOffset + imageX;
                    var p3 = p4 - 1;

                    var p2 = p4 - coordsAcross;
                    var p1 = p3 - coordsAcross;

                    coords.Add(rows[imageY][imageX]);
                    if (viewerMode)
                    {
                        normals.Add(new Coord());
                        uvs.Add(new UVCoord(widthUnit * imageX, heightUnit * imageY));
                    }

                    if (imageY > 0 && imageX > 0)
                    {
                        Face f1, f2;

                        if (viewerMode)
                        {
                            if (invert)
                            {
                                f1 = new Face(p1, p4, p3, p1, p4, p3)
                                {
                                    uv1 = p1,
                                    uv2 = p4,
                                    uv3 = p3
                                };

                                f2 = new Face(p1, p2, p4, p1, p2, p4)
                                {
                                    uv1 = p1,
                                    uv2 = p2,
                                    uv3 = p4
                                };
                            }
                            else
                            {
                                f1 = new Face(p1, p3, p4, p1, p3, p4)
                                {
                                    uv1 = p1,
                                    uv2 = p3,
                                    uv3 = p4
                                };

                                f2 = new Face(p1, p4, p2, p1, p4, p2)
                                {
                                    uv1 = p1,
                                    uv2 = p4,
                                    uv3 = p2
                                };
                            }
                        }
                        else
                        {
                            if (invert)
                            {
                                f1 = new Face(p1, p4, p3);
                                f2 = new Face(p1, p2, p4);
                            }
                            else
                            {
                                f1 = new Face(p1, p3, p4);
                                f2 = new Face(p1, p4, p2);
                            }
                        }

                        faces.Add(f1);
                        faces.Add(f2);
                    }
                }
            }

            if (viewerMode)
                calcVertexNormals(sculptType, coordsAcross, coordsDown);
        }

        /// <summary>
        ///     Duplicates a SculptMesh object. All object properties are copied by value, including lists.
        /// </summary>
        /// <returns></returns>
        public SculptMesh Copy()
        {
            return new SculptMesh(this);
        }

        private void calcVertexNormals(SculptType sculptType, int xSize, int ySize)
        {
            // compute vertex normals by summing all the surface normals of all the triangles sharing
            // each vertex and then normalizing
            var numFaces = faces.Count;
            for (var i = 0; i < numFaces; i++)
            {
                var face = faces[i];
                var surfaceNormal = face.SurfaceNormal(coords);
                normals[face.n1] += surfaceNormal;
                normals[face.n2] += surfaceNormal;
                normals[face.n3] += surfaceNormal;
            }

            var numNormals = normals.Count;
            for (var i = 0; i < numNormals; i++)
                normals[i] = normals[i].Normalize();

            if (sculptType != SculptType.plane)
                for (var y = 0; y < ySize; y++)
                {
                    var rowOffset = y * xSize;

                    normals[rowOffset] = normals[rowOffset + xSize - 1] =
                        (normals[rowOffset] + normals[rowOffset + xSize - 1]).Normalize();
                }

            foreach (var vf in faces.Select(face => new ViewerFace(0)
                     {
                         v1 = coords[face.v1],
                         v2 = coords[face.v2],
                         v3 = coords[face.v3],
                         coordIndex1 = face.v1,
                         coordIndex2 = face.v2,
                         coordIndex3 = face.v3,
                         n1 = normals[face.n1],
                         n2 = normals[face.n2],
                         n3 = normals[face.n3],
                         uv1 = uvs[face.uv1],
                         uv2 = uvs[face.uv2],
                         uv3 = uvs[face.uv3]
                     }))
            {
                viewerFaces.Add(vf);
            }
        }

        /// <summary>
        ///     Adds a value to each XYZ vertex coordinate in the mesh
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        public void AddPos(float x, float y, float z)
        {
            int i;
            var numVerts = coords.Count;

            for (i = 0; i < numVerts; i++)
            {
                var vert = coords[i];
                vert.X += x;
                vert.Y += y;
                vert.Z += z;
                coords[i] = vert;
            }

            if (viewerFaces != null)
            {
                var numViewerFaces = viewerFaces.Count;

                for (i = 0; i < numViewerFaces; i++)
                {
                    var v = viewerFaces[i];
                    v.AddPos(x, y, z);
                    viewerFaces[i] = v;
                }
            }
        }

        /// <summary>
        ///     Rotates the mesh
        /// </summary>
        /// <param name="q"></param>
        public void AddRot(Quat q)
        {
            int i;
            var numVerts = coords.Count;

            for (i = 0; i < numVerts; i++)
                coords[i] *= q;

            var numNormals = normals.Count;
            for (i = 0; i < numNormals; i++)
                normals[i] *= q;

            if (viewerFaces != null)
            {
                var numViewerFaces = viewerFaces.Count;

                for (i = 0; i < numViewerFaces; i++)
                {
                    var v = viewerFaces[i];
                    v.v1 *= q;
                    v.v2 *= q;
                    v.v3 *= q;

                    v.n1 *= q;
                    v.n2 *= q;
                    v.n3 *= q;

                    viewerFaces[i] = v;
                }
            }
        }

        public void Scale(float x, float y, float z)
        {
            int i;
            var numVerts = coords.Count;

            var m = new Coord(x, y, z);
            for (i = 0; i < numVerts; i++)
                coords[i] *= m;

            if (viewerFaces != null)
            {
                var numViewerFaces = viewerFaces.Count;
                for (i = 0; i < numViewerFaces; i++)
                {
                    var v = viewerFaces[i];
                    v.v1 *= m;
                    v.v2 *= m;
                    v.v3 *= m;
                    viewerFaces[i] = v;
                }
            }
        }

        public void DumpRaw(string path, string name, string title)
        {
            if (path == null)
                return;
            var fileName = name + "_" + title + ".raw";
            var completePath = System.IO.Path.Combine(path, fileName);
            var sw = new StreamWriter(completePath);

            for (var i = 0; i < faces.Count; i++)
            {
                var s = coords[faces[i].v1].ToString();
                s += " " + coords[faces[i].v2];
                s += " " + coords[faces[i].v3];

                sw.WriteLine(s);
            }

            sw.Close();
        }
    }
}