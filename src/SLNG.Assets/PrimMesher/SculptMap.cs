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
using SkiaSharp;

namespace SLNG.Assets.PrimMesher
{
    public class SculptMap
    {
        public byte[] blueBytes = Array.Empty<byte>();
        public byte[] greenBytes = Array.Empty<byte>();
        public int height;
        public byte[] redBytes = Array.Empty<byte>();
        public int width;

        public SculptMap()
        {
        }

        public SculptMap(SKBitmap bm, int lod)
        {
            if (bm == null) throw new ArgumentNullException(nameof(bm));

            var bmW = bm.Width;
            var bmH = bm.Height;

            if (bmW == 0 || bmH == 0)
                throw new Exception("SculptMap: bitmap has no data");

            // desired pixel budget for LOD
            var numLodPixels = (lod * 2) * (lod * 2);

            var smallMap = bmW * bmH <= lod * lod;

            // compute target width/height by repeatedly halving until under budget
            width = bmW;
            height = bmH;
            while (width * height > numLodPixels)
            {
                width >>= 1;
                height >>= 1;
            }

            // final shrink if still larger than lod*lod
            if (width * height > lod * lod)
            {
                width >>= 1;
                height >>= 1;
            }

            try
            {
                // allocate arrays: smallMap uses exact size, otherwise allocate (width+1)*(height+1)
                var numBytes = smallMap ? width * height : (width + 1) * (height + 1);
                redBytes = new byte[numBytes];
                greenBytes = new byte[numBytes];
                blueBytes = new byte[numBytes];

                // Always sample the ORIGINAL, native-resolution bitmap -- never a pre-scaled copy.
                // Verified against the real viewer (LLVolume::sculptGenerateMapVertices,
                // llvolume.cpp:3070-3071): SL reduces a sculpt map's LOD purely by point-sampling
                // fewer vertices at a computed integer stride into the native texel array; it never
                // filters/averages RGB when producing a lower-resolution vertex grid. Each texel is
                // an independent, unrelated vertex XYZ (not a photographic signal), so the previous
                // bilinear pre-scale (ScaleImage, SKFilterMode.Linear) blended adjacent vertex
                // positions together -- clipping extrema (a flat cap's peak height gets pulled down
                // toward the side wall's) and chamfering sharp profile edges, rendering e.g. a tall
                // drum-shaped seat as a visibly flattened, rounded-off dish. This also drops the
                // ScaleImage/needsScaling machinery entirely, since it existed only to feed that
                // now-removed pre-scale.
                var pix = bm.PeekPixels(); // low-overhead access to pixel data
                var byteNdx = 0;

                if (smallMap)
                {
                    // smallMap means bmW*bmH <= lod*lod, so width==bmW and height==bmH exactly
                    // (neither halving loop above can have fired) -- a direct 1:1 read already
                    // matches the source pixel-for-pixel, same as before this fix.
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var c = pix.GetPixelColor(x, y);
                            redBytes[byteNdx] = c.Red;
                            greenBytes[byteNdx] = c.Green;
                            blueBytes[byteNdx] = c.Blue;
                            ++byteNdx;
                        }
                    }
                }
                else
                {
                    // Proportional point-sample into a (width+1)x(height+1) buffer: the extra row/
                    // column (index == width/height) duplicates the last real row/column, giving
                    // SculptMesh's wrap-seam stitching (see its own doc comment) a clean edge to
                    // close against instead of an out-of-range read.
                    for (var y = 0; y <= height; y++)
                    {
                        var sy = Math.Min(bmH - 1, (int)((float)y / height * bmH));
                        for (var x = 0; x <= width; x++)
                        {
                            var sx = Math.Min(bmW - 1, (int)((float)x / width * bmW));
                            var c = pix.GetPixelColor(sx, sy);
                            redBytes[byteNdx] = c.Red;
                            greenBytes[byteNdx] = c.Green;
                            blueBytes[byteNdx] = c.Blue;
                            ++byteNdx;
                        }
                    }

                    // the consumer expects width/height incremented to match the buffer above
                    width++;
                    height++;
                }
            }
            catch (Exception e)
            {
                throw new Exception("Caught exception processing byte arrays in SculptMap(): e: " + e);
            }
        }

        public List<List<Coord>> ToRows(bool mirror)
        {
            var numRows = height;
            var numCols = width;

            var rows = new List<List<Coord>>(numRows);

            const float pixScale = 1.0f / 255.0f;

            var smNdx = 0;
            for (var rowNdx = 0; rowNdx < numRows; rowNdx++)
            {
                var row = new List<Coord>(numCols);
                for (var colNdx = 0; colNdx < numCols; colNdx++)
                {
                    var r = redBytes[smNdx] * pixScale - 0.5f;
                    var g = greenBytes[smNdx] * pixScale - 0.5f;
                    var b = blueBytes[smNdx] * pixScale - 0.5f;

                    row.Add(mirror ? new Coord(-r, g, b) : new Coord(r, g, b));
                    ++smNdx;
                }
                rows.Add(row);
            }
            return rows;
        }
    }
}