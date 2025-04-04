using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Toy2LevelDump {
    class Program {
        public static string filepath = "";
        public static FileReader fs;
        public static int ngnPtrGeom;
        public static int ngnPtrMxDS;
        public static bool hasExtracted;

        public static List<Vector3> mxTrans = new List<Vector3>();
        public static List<Vector3> mxRot = new List<Vector3>();
        public static List<Vector3> mxScale = new List<Vector3>();

        /// <summary>Each List<Vector3> in the outer List<list> is a "shape" so to speak, just stored in a very simple way</summary>
        public static List<List<Vector3>> t2verts = new List<List<Vector3>>();
        public static List<List<Vector3>> rawt2verts = new List<List<Vector3>>(); //unmodified vertex data
        public static List<List<SPrim>> t2prims = new List<List<SPrim>>();
        public static List<List<T2Color>> t2color = new List<List<T2Color>>();

        public static Matrix4D matrix = new Matrix4D();
        public static Matrix4D matrixInverse = new Matrix4D().NegateSpecific(new Vector3(1, 1, 0));


        static void Main(string[] args) {
            int extractType = -1;

            if(args.Length < 1) {
preload:
                Console.WriteLine("Drag&Drop or provide a .NGN file to extract!");
                string file = Console.ReadLine();
                if(System.IO.File.Exists(file)) {
                    filepath = file;
                } else {
                    goto preload;
                }
            } else {
                filepath = args[0];
            }
                
            if (filepath != "") {
                fs = new FileReader(filepath);
init:
                string cinput = QueryExtractType();
                if(Int32.TryParse(cinput, out extractType)) {
                    ExtractGeometryData(extractType);
                    Console.Clear();
                    Console.WriteLine("Done! What next?");
                    goto init;
                } else {
                    Console.Clear();
                    Console.WriteLine("Invalid selection!");
                    goto init;
                }

            }
        }


        public static string QueryExtractType() {
            Console.WriteLine("Loaded file:" + filepath + "\n\n");
            Console.WriteLine(
                "What would you like to extract?\n" +
                "1) All shapes into a transformed .OBJ file (4 for raw data)\n" +
                "2) Separate CSV file for each shape's vertex data (5 for raw data)\n" +
                "3) Custom data format for easy for viewing and comparing data (6 for raw data)\n" +
                "7) Custom data format showing the difference values between fixed and raw verts [after fixing lookat shapes]"
            );
            return Console.ReadLine();
        }

        public static void ExtractGeometryData(int extractType) {
            if(!hasExtracted) {
                GetFuncPointers();
                ExtractMatrixData(ngnPtrMxDS);
                ExtractShapeData(ngnPtrGeom);
                hasExtracted = true;
            }

            if(extractType == 0) {
            } else if(extractType == 1 || extractType == 4) {
                ExportToOBJ(extractType == 1 ? t2verts : rawt2verts, extractType == 4);
            } else if(extractType == 2 || extractType == 5) {
                List<List<Vector3>> data = extractType == 2 ? t2verts : rawt2verts;
                int h = 0;                
                foreach(List<Vector3> shape in data) {
                    StringBuilder sbcsv = new StringBuilder();
                    foreach(Vector3 vec in shape) {
                        sbcsv.Append(vec.ToStringRaw());
                    }
                    h++;
                    File.WriteAllText(filepath + ".verts.Shape" + h + (extractType == 2 ? "" : ".raw") + ".csv", sbcsv.ToString());
                }
                
            } else if(extractType == 3 || extractType == 6) {
                List<List<Vector3>> data = extractType == 3 ? t2verts : rawt2verts;
                int h = 0;
                StringBuilder diff1 = new StringBuilder();
                foreach(List<Vector3> shape in data) {
                    diff1.Append("#Shape" + h + "\n");
                    foreach(Vector3 vec in shape) {
                        diff1.Append(vec.ToString() + "\n");
                    }
                    h++;
                }
                File.WriteAllText(filepath + ".verts" + (extractType == 3 ? "" : ".raw") + ".txt", diff1.ToString());
            } else if(extractType == 7) {
                List<List<Vector3>> data = t2verts;
                List<List<Vector3>> dataraw = rawt2verts;
                int h = 0;
                int i = 0;
                StringBuilder diff1 = new StringBuilder();
                foreach(List<Vector3> shape in data) {
                    diff1.Append("#Shape" + h + "\n");
                    foreach(Vector3 vec in shape) {
                        
                        diff1.Append(new Vector3(vec.X - dataraw[h][i].X, vec.Y - dataraw[h][i].Y, vec.Z - dataraw[h][i].Z).ToString() + "\n");
                        i++;
                    }
                    h++;
                    i = 0;
                }
                File.WriteAllText(filepath + ".verts.diff.txt", diff1.ToString());
            }
        }

        public static void GetFuncPointers() {
            int functionLength;
            int functionID;
            for(;;){
                if(fs.pos >= fs.fstream.Length) break;

                functionID = fs.readint(4);
                functionLength = fs.readint(4);

                //get offsets for the geom and matrix data, but ONLY for the real data and not for the LOD data by checking if we already have it
                if(ngnPtrGeom == 0 && functionID == 256) ngnPtrGeom = fs.pos;
                if(ngnPtrMxDS == 0 && functionID == 257) ngnPtrMxDS = fs.pos;

                fs.seek(functionLength);
            }
        }

        public static void ExtractShapeData(int DataLocation) {
            fs.setPos(DataLocation);

            int functionLength;
            int functionID;
            int fsPredicted;
            int fsFinal = 0;
            int shapeCount;
            int fsVerts = 0;

            shapeCount = fs.readint(4);
            for(int i = 0;i < shapeCount;i++) {
                t2verts.Add(new List<Vector3>());
                rawt2verts.Add(new List<Vector3>());
                t2prims.Add(new List<SPrim>());
                t2color.Add(new List<T2Color>());

                //!MOD! - translate the verts as we import them
                //each shape is tied to matrix (DynamicScaler) data, and all verts in a shape are affected the same way
                //so, first we need a clean matrix
                matrix.MakeIdentity();
                //next, merge in the scaling data for this shape
                matrix.BuildScale(mxScale[i]);
                //the same for the position/transform data
                matrix.BuildTransform(mxTrans[i]);
                //and we must get all required rotations. note that this is a complicated calculation, largely following what the game does and using the game's sine generator
                matrix.BuildT2TranslationMatrix(mxRot[i]);
                //^^ now we can use this matrix when we are pulling the verts in via ExtractShapeVerts()

                for(;;){
                    functionID = fs.readint(4);
                    functionLength = fs.readint(4);
                    fsPredicted = fs.pos + functionLength;

                    if(functionID == 0) break;

                    if(functionID == 67) { fsVerts = fs.pos; fs.seek(functionLength); } 
                    else if(functionID == 68) ExtractPrimData(i, fs.pos);
                    else fs.seek(functionLength);

                    if(fs.pos != fsPredicted) fs.setPos(fsPredicted);
                }

                fsFinal = fs.pos;

                //we have to run this here because we need the primitive data first. eek this layout kinda blows
                if(fsVerts != 0) ExtractShapeVerts(i, fsVerts);
                fs.setPos(fsFinal);
                fsVerts = 0;
            }
        }

        public static void ExtractMatrixData(int DataLocation) {
            fs.setPos(DataLocation);

            int shapeCount = fs.readint(4);
            int shapeDataLength = fs.readint(4);
            bool unknown = shapeDataLength == 40;
            for(int i = 0;i < shapeCount;i++) {
                mxTrans.Add(new Vector3(fs.readflt(4), fs.readflt(4), fs.readflt(4)));
                mxRot.Add(new Vector3(fs.readint(4), fs.readint(4), fs.readint(4)));
                mxScale.Add(new Vector3(fs.readflt(4), fs.readflt(4), fs.readflt(4)));
                fs.seek(4); //skip shapeid
                if(!unknown) fs.seek(4);
            }
        }

        public static void ExtractPrimData(int currentShape, int DataLocation) {
            fs.setPos(DataLocation);

            int PrimitiveCount = fs.readint(4); //the number of prims for this shape
            int vertexCount = 0;
            for(int i = 0;i < PrimitiveCount;i++) {
                SPrim prim = new SPrim();
                prim.type = fs.readint(4); // the id of this primitive
                fs.seek(2); //skip reading the material id
                vertexCount = fs.readint(2); //how many vertices make up this primitive
                for(int j = 0;j < vertexCount;j++) {
                    prim.vertices.Add(fs.readint(2)); //collect each Vector based on the prim stucture
                }
                prim.lookat = prim.type == 5 || prim.type == 6;
                prim.vertCount = prim.vertices.Count;
                t2prims[currentShape].Add(prim);
            }
        }

        public static void ExtractShapeVerts(int currentShape, int DataLocation) {
            fs.setPos(DataLocation);

            int VertexFlags = fs.readint(4);
            int VertexBaseDataLength = fs.readint(4); //always seems to be 36
            int vertex_Count = fs.readint(4);
            int VertexDataLength = 0;
            //parse flags, changes how much data per line
            if((VertexFlags & 1) == 1) VertexDataLength = VertexBaseDataLength - 12;
            if((VertexFlags & 2) == 1) VertexDataLength -= 12;
            if((VertexFlags & 4) == 1) VertexDataLength = 4; /*Has Vertex Color Data*/
            if((VertexFlags & 8) == 1) VertexDataLength = 4; /*Has extra unknown Data*/
            if(VertexDataLength > 0) {
                for(int i = 0;i < vertex_Count;i++) {
                    if((VertexFlags & 1) != 0) {
                        t2verts[currentShape].Add(new Vector3(fs.readflt(4) * 1.0F, fs.readflt(4) * 1.0F, fs.readflt(4) * 1.0F));
                    }

                    fs.seek(12); //skip through the raw vtx data
                    if((VertexFlags & 4) != 0) {
                        T2Color col = new T2Color();
                        col.A = fs.readint(1);  //A
                        col.R = fs.readint(1);  //R
                        col.G = fs.readint(1);  //G
                        col.B = fs.readint(1);  //B
                        t2color[currentShape].Add(col);
                    }
                    //fs.seek(4); //skip over color data
                    if((VertexFlags & 8) != 0) fs.seek(4); //idk, skip if true
                    if((VertexFlags & 240) != 0) fs.seek(8); //skip UV coords
                }
            }


            //!MOD! - transform type 5&6 prims into type 4 and fix "lookat" shapes
            //note that when working with prim.vertices, the value of a prim.vertices[x] == the _vertex slot_ for all vertices in this shape
            //  so, for example prim.vertices[0] might == 3
            //  this is a reference to the 3rd vertex we've collected (e.g. t2verts[currentShape][prim.vertices[3])
            //  and __NOT__ the 0th vertex we collected (e.g. t2verts[currentShape][prim.vertices[0])
            //  therefore doing t2verts[currentShape][prim.vertices[0]] would give us the value of the third vertex we collected in t2verts[currentShape]
            for(int i = 0;i < t2prims[currentShape].Count;i++) {
                SPrim prim = t2prims[currentShape][i];
                if(prim.type == 5 || prim.type == 6) {
                    //first we need to fix their position
                    //  these are reffered to as "lookat" shapes, as when you move around the world, they always rotate to perfectly face you
                    //  so, for each primitive > 4 verts, all of their positions are amended by the first vertex in the prim (vertex 0) aka the anchor point
                    //  additionally, if the prim has 6 vertices, then the second vertex is just ignored and left untouched
                    //  finally, the first vertex, the anchor, gets culled from the list as it's not actually technically a valid vertex for the final render output

                    //now we have to transform all of the valid vertices around the 0th vertex based on the order of this prim's verts
                    Matrix4D lookatMatrix = new Matrix4D();
                    lookatMatrix.BuildTransform(t2verts[currentShape][prim.vertices[0]]);

                    //now, using the matrix from the 0th vertex, we can transform all of the good vertices in this primitive from the end-4
                    for(int h = prim.vertices.Count - 4;h < prim.vertices.Count;h++) {
                        t2verts[currentShape][prim.vertices[h]].Transform(lookatMatrix);
                    }

                    //additionally we have to swap the index order for vert 3&4!
                    //  i'm not 100% sure why this is the case, but... it re-arranges the vertex order to be correct
                    int tmp = prim.vertices[4];
                    prim.vertices[4] = prim.vertices[3];
                    prim.vertices[3] = tmp;

                    //finally, we cull the anchor point
                    prim.vertices.RemoveAt(0);

                    //and set the prim to a valid type
                    prim.type = 4;
                }
            }

            rawt2verts[currentShape] = t2verts[currentShape].Copy();

            //!MOD! - Translate this vertex using the shape's matrix data 
            foreach(Vector3 vector in t2verts[currentShape]) {
                vector.Transform(matrix);//now we use the global matrix for this shape to transform the verts                        
                vector.Transform(matrixInverse);//then we need it inverted across X&Y because of how the data is stored
            }

        }


        public static void ExportToOBJ(List<List<Vector3>> data, bool isRaw) {
            StringBuilder sbobj = new StringBuilder();
            sbobj.Append("# OBJ Export - Toy2LevelDump\n");
            sbobj.Append("# File created " + DateTime.Now + "\n");
            int i = 0;
            int vOffset = 1;
            int vAccumulator = 0;
            foreach(List<Vector3> shape in data) {
                int j = 0;
                sbobj.Append("o " + "Shape" + i.ToString().PadLeft(1, '0') + "\n");


                for(int k = 0;k < shape.Count;k++) {
                    sbobj.Append("v  " + shape[k].ToOBJ() + (" " + (double)t2color[i][k].R / 255 + " " + (double)t2color[i][k].G / 255 + " " + (double)t2color[i][k].B / 255) + "\n");
                }
                sbobj.Append("# " + shape.Count + " vertices\n\n");

                foreach(SPrim prim in t2prims[i]) {
                    string objname = $"{""}_{("Shape" + i.ToString().PadLeft(1, '0'))}_face{j.ToString().PadLeft(2, '0')}";

                    int primtype = (prim.type == 1) ? 3 : prim.type;

                    sbobj.Append("g " + objname + "\n");

                    for(int k = 0;k < prim.vertices.Count;k += primtype) {
                        sbobj.Append("\nf ");
                        for(int l = 0;l < primtype;l++) {
                            sbobj.Append(Convert.ToString(prim.vertices[k + l] + vOffset) + " ");
                        }
                    }
                    if(prim.lookat) {
                        vAccumulator += (primtype * (prim.vertices.Count / primtype)) + (prim.vertCount - prim.vertices.Count);
                    } else {
                        vAccumulator += primtype * (prim.vertices.Count / primtype);
                    }
                    sbobj.Append("\n# " + prim.vertices.Count / primtype + " polygons\n\n\n");
                    j++;
                }
                vOffset += vAccumulator;
                vAccumulator = 0;
                i++;
            }

            System.IO.File.WriteAllText(filepath + (isRaw ? "_raw" : "") +"_export.obj", sbobj.ToString());

        }
    }









    /// <summary>Simple prim structure</summary>
    public class SPrim {
        public int type = 0;
        public bool lookat = false;
        public int vertCount = 0;
        public List<int> vertices = new List<int>();
    }

    public class T2Color {
        public int A = 0;
        public int R = 0;
        public int G = 0;
        public int B = 0;
    }



    public class Vector3 {
        public float X = 0.0f;
        public float Y = 0.0f;
        public float Z = 0.0f;

        public Vector3(float xx, float yy, float zz) {
            this.X = xx;
            this.Y = yy;
            this.Z = zz;
        }

        public void Transform(Matrix4D Mat) {
            float xx = this.X;
            float yy = this.Y;
            xx = (this.X * Convert.ToSingle(Mat._matrix[0, 0])) + (this.Y * Convert.ToSingle(Mat._matrix[1, 0])) + (this.Z * Convert.ToSingle(Mat._matrix[2, 0])) + Convert.ToSingle(Mat._matrix[0, 3]);
            yy = (this.X * Convert.ToSingle(Mat._matrix[0, 1])) + (this.Y * Convert.ToSingle(Mat._matrix[1, 1])) + (this.Z * Convert.ToSingle(Mat._matrix[2, 1])) + Convert.ToSingle(Mat._matrix[1, 3]);
            this.Z = (this.X * Convert.ToSingle(Mat._matrix[0, 2])) + (this.Y * Convert.ToSingle(Mat._matrix[1, 2])) + (this.Z * Convert.ToSingle(Mat._matrix[2, 2])) + Convert.ToSingle(Mat._matrix[2, 3]);
            this.X = xx;
            this.Y = yy;
        }

        public override string ToString() {
            return "{" + X.ToString().PadRight(0, '0') + "," + Y.ToString().PadRight(0, '0') + "," + Z.ToString() + "}";
        }

        public string ToStringRaw() {
            return X.ToString() + "," + Y.ToString() + "," + Z.ToString() + ",";
        }

        public string ToOBJ() {
            return X + " " + Y + " " + Z;
        }
    }



    [DebuggerTypeProxy(typeof(Matrix4DProxy))]
    public class Matrix4D {
        public double[,] _matrix = new double[4, 4];
        public static float[] sineData = new float[65535];
        public static bool sineHasCached = false;

        public Matrix4D() => this.MakeIdentity();

        /// <summary>
        /// Reset this matrix back to a default identity matrix
        /// 1000
        /// 0100
        /// 0010
        /// 0001
        /// </summary>
        public Matrix4D MakeIdentity() {
            Array.Clear(_matrix, 0, _matrix.Length);
            this._matrix[0, 0] = 1;
            this._matrix[1, 1] = 1;
            this._matrix[2, 2] = 1;
            this._matrix[3, 3] = 1;
            return this;
        }

        public Matrix4D BuildScale(Vector3 ScaleVector) {
            for(int i = 0;i < 3;i++) {
                this._matrix[i, 0] *= ScaleVector.X;
                this._matrix[i, 1] *= ScaleVector.Y;
                this._matrix[i, 2] *= ScaleVector.Z;
            }
            return this;
        }

        public Matrix4D BuildTransform(Vector3 TransformVector) {
            this._matrix[0, 3] += TransformVector.X;
            this._matrix[1, 3] += TransformVector.Y;
            this._matrix[2, 3] += TransformVector.Z;
            return this;
        }

        public static void BuildSineCache() {
            for(int i = 0;i < 65535;i++) {
                sineData[i] = (float)Math.Sin(i * 0.000095873802);
            }
        }

        public Matrix4D BuildT2TranslationMatrix(Vector3 RotationVector) {

            if(!sineHasCached) BuildSineCache();

            //Z
            float sz = sineData[(int)(RotationVector.Z) + 16384 & 0xFFFF];
            float cz = sineData[(int)(RotationVector.Z) & 0xFFFF];
            double GTM0 = this._matrix[0, 0];
            double GTM4 = this._matrix[1, 0];
            double GTM8 = this._matrix[2, 0];
            double GTM12 = this._matrix[3, 0];
            this._matrix[0, 0] = GTM0 * sz - cz * this._matrix[0, 1]; // [00]
            this._matrix[0, 1] = GTM0 * cz + sz * this._matrix[0, 1]; // [01]
            this._matrix[1, 0] = GTM4 * sz - cz * this._matrix[1, 1]; // [04]
            this._matrix[1, 1] = sz * this._matrix[1, 1] + GTM4 * cz; // [05]
            this._matrix[2, 0] = GTM8 * sz - cz * this._matrix[2, 1]; // [08]
            this._matrix[2, 1] = sz * this._matrix[2, 1] + GTM8 * cz; // [09]
            this._matrix[3, 0] = GTM12 * sz - cz * this._matrix[3, 1]; // [12]
            this._matrix[3, 1] = sz * this._matrix[3, 1] + GTM12 * cz; // [13]

            //Y
            float sy = sineData[(int)(RotationVector.Y) + 16384 & 0xFFFF];
            float cy = sineData[(int)(RotationVector.Y) & 0xFFFF];
            GTM0 = this._matrix[0, 0];
            GTM4 = this._matrix[1, 0];
            GTM8 = this._matrix[2, 0];
            GTM12 = this._matrix[3, 0];
            this._matrix[0, 0] = cy * this._matrix[0, 2] + GTM0 * sy; // [00]
            this._matrix[0, 2] = sy * this._matrix[0, 2] - GTM0 * cy; // [02]
            this._matrix[1, 0] = cy * this._matrix[1, 2] + GTM4 * sy; // [04]
            this._matrix[1, 2] = sy * this._matrix[1, 2] - GTM4 * cy; // [06]
            this._matrix[2, 0] = GTM8 * sy + cy * this._matrix[2, 2]; // [08]
            this._matrix[2, 2] = sy * this._matrix[2, 2] - GTM8 * cy; // [10]
            this._matrix[3, 0] = cy * this._matrix[3, 2] + GTM12 * sy; // [12]
            this._matrix[3, 2] = sy * this._matrix[3, 2] - GTM12 * cy; // [14]

            //X
            float sx = sineData[(int)(RotationVector.X) + 16384 & 0xFFFF];
            float cx = sineData[(int)(RotationVector.X) & 0xFFFF];
            double GTM1 = this._matrix[0, 1];
            double GTM5 = this._matrix[1, 1];
            double GTM9 = this._matrix[2, 1];
            double GTM13 = this._matrix[3, 1];
            this._matrix[0, 1] = GTM1 * sx - cx * this._matrix[0, 2]; // [01]
            this._matrix[0, 2] = sx * this._matrix[0, 2] + GTM1 * cx; // [02]
            this._matrix[1, 1] = GTM5 * sx - cx * this._matrix[1, 2]; // [05]
            this._matrix[1, 2] = sx * this._matrix[1, 2] + GTM5 * cx; // [06]
            this._matrix[2, 1] = GTM9 * sx - cx * this._matrix[2, 2]; // [09]
            this._matrix[2, 2] = sx * this._matrix[2, 2] + GTM9 * cx; // [10]
            this._matrix[3, 1] = GTM13 * sx - cx * this._matrix[3, 2]; // [13]
            this._matrix[3, 2] = GTM13 * cx + sx * this._matrix[3, 2]; // [14]

            return this;
        }

        public Matrix4D NegateSpecific(Vector3 NegateMask) {
            if(NegateMask.Z != 0) this._matrix[0, 0] = -Math.Abs(NegateMask.Z);
            if(NegateMask.Y != 0) this._matrix[1, 1] = -Math.Abs(NegateMask.Y);
            if(NegateMask.X != 0) this._matrix[2, 2] = -Math.Abs(NegateMask.X);
            this._matrix[3, 3] = -1;
            return this;
        }

    }

    class Matrix4DProxy {
        private Matrix4D _content;
        private const int _pad = 10;
        public Matrix4DProxy(Matrix4D content) {
            _content = content;
        }
        public string Display {
            get {
                return
                Convert.ToString(_content._matrix[0, 0]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[0, 1]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[0, 2]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[0, 3]).PadRight(_pad, ' ') + " " + "\n" +
                Convert.ToString(_content._matrix[1, 0]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[1, 1]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[1, 2]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[1, 3]).PadRight(_pad, ' ') + " " + "\n" +
                Convert.ToString(_content._matrix[2, 0]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[2, 1]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[2, 2]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[2, 3]).PadRight(_pad, ' ') + " " + "\n" +
                Convert.ToString(_content._matrix[3, 0]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[3, 1]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[3, 2]).PadRight(_pad, ' ') + " " + Convert.ToString(_content._matrix[3, 3]).PadRight(_pad, ' ') + " ";
            }
        }
    }



    public class FileReader {
        public byte[] fstream;
        private int foffset;
        public int pos => this.foffset;

        public FileReader(string path) {
            fstream = System.IO.File.ReadAllBytes(path);
        }

        public int setPos(int newPos) {
            if(newPos < 0) newPos = 0;
            if(newPos > fstream.Length) newPos = fstream.Length;
            return this.foffset = newPos;
        }

        public void seek(int amount) {
            setPos(this.foffset + amount);
        }

        public byte[] readBatch(ref int seekPTR, int count = 1) {
            byte[] newbytes = new byte[count];
            Buffer.BlockCopy(fstream, seekPTR, newbytes, 0, count);
            seekPTR += count;
            Array.Resize(ref newbytes, 4);
            return newbytes;
        }

        public int readint(int count = 1, int offset = -1) {
            byte[] newbytes = readBatch(ref this.foffset, count);
            if(count == 1) return newbytes[0];
            else return BitConverter.ToInt32(newbytes, 0);
        }

        public Single readflt(int count = 1, int offset = -1) {
            byte[] newbytes = readBatch(ref this.foffset, count);
            if(count == 1) return newbytes[0];
            else return BitConverter.ToSingle(newbytes, 0);
        }
    }

    public static class ObjectExtentions {
        public static List<Vector3> Copy(this List<Vector3> original) {
            List<Vector3> copy = new List<Vector3>();
            int i = 0;
            foreach(Vector3 t in original) {
                copy.Add(new Vector3(original[i].X, original[i].Y, original[i++].Z));
            }
            return copy;
        }
    }

}