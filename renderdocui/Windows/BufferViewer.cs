﻿/******************************************************************************
 * The MIT License (MIT)
 * 
 * Copyright (c) 2014 Crytek
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 ******************************************************************************/


using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using renderdocui.Code;
using renderdocui.Windows.Dialogs;
using renderdoc;

namespace renderdocui.Windows
{
    // since they're quite similar, the BufferViewer class displays both the geometry mesh
    // data as well as raw views of buffers (with custom formatting). See the two different constructors.
    //
    // When we go to fetch data we do that on a separate thread to parse the byte-stream that comes
    // back according to the format (since there can be mismatches between pipeline stages, we always take
    // the "output" of each stage.
    //
    // Once we have the data parsed we flag that and the UI then uses the VirtualMode on the datagridview
    // to populate rows lazily as and when they're needed.
    //
    // The threading is messy and with the 'hiding' of threading details behind invokes for the UI I suspect
    // this whole setup is fragile and/or not thread-safe in areas. It would be nice to control all the threading
    // explicitly myself but the UI interaction makes that murky, so bear in mind that you need to be able to
    // handle changing events while a thread is still going and about to populate some data etc, and be able
    // to abort that and start anew without anything breaking or racing.
    public partial class BufferViewer : DockContent, ILogViewerForm, IBufferFormatProcessor
    {
        #region Data Privates

        // we try to bundle up data so that as much as possible things don't change out from under a thread
        // or invoke and get us into an 'impossible' state.

        // This class describes the format/structure that we'll use to interpret the byte stream
        private class Input
        {
            public FormatElement[] BufferFormats = null;
            public ResourceId[] Buffers = null;
            public object[][] GenericValues = null;
            public uint[] Strides = null;
            public uint[] Offsets = null;

            public PrimitiveTopology Topology = PrimitiveTopology.Unknown;

            public FetchDrawcall Drawcall = null;

            public ResourceId IndexBuffer = ResourceId.Null;
            public uint IndexOffset = 0;
            public bool IndexRestart = true;
            public uint IndexRestartValue = uint.MaxValue;
        }

        // contains the raw bytes (and any state necessary from the drawcall itself)
        private class Dataset
        {
            public uint IndexCount = 0;

            public MeshFormat PostVS;

            public PrimitiveTopology Topology = PrimitiveTopology.Unknown;

            public byte[][] Buffers = null;
            public uint[] Indices = null; // 'displayed' indices from index buffer
            public uint[] DataIndices = null; // where to find the data, different only for PostVS
        }

        // we generate a UIState object with everything needed to populate the actual
        // visible data in the UI.
        private class UIState
        {
            public UIState(MeshDataStage stage)
            {
                m_Stage = stage;
            }

            public Input m_Input = null;

            public MeshDataStage m_Stage = MeshDataStage.VSIn;

            public Dataset m_Data = null;
            public Stream[] m_Stream = null;
            public BinaryReader[] m_Reader = null;

            public object[][] m_Rows = null;
            public byte[] m_RawData = null;
            public uint m_RawStride = 0;

            public DataGridView m_GridView = null;

            public DockContent m_DockContent = null;

            public Thread m_DataParseThread = null;
            private Object m_ThreadLock = new Object();

            public Vec3f m_MinBounds = new Vec3f(float.MaxValue, float.MaxValue, float.MaxValue);
            public Vec3f m_MaxBounds = new Vec3f(-float.MaxValue, -float.MaxValue, -float.MaxValue);

            public void AbortThread()
            {
                lock (m_ThreadLock)
                {
                    if (m_DataParseThread != null)
                    {
                        if (m_DataParseThread.ThreadState != ThreadState.Aborted &&
                            m_DataParseThread.ThreadState != ThreadState.Stopped)
                        {
                            m_DataParseThread.Abort();
                            m_DataParseThread.Join();
                        }

                        m_DataParseThread = null;
                    }
                }
            }
        }

        // one UI state for each stage
        private UIState m_VSIn = new UIState(MeshDataStage.VSIn);
        private UIState m_VSOut = new UIState(MeshDataStage.VSOut);
        private UIState m_GSOut = new UIState(MeshDataStage.GSOut);

        // this points to the 'highlighted'/current UI state.
        private UIState m_ContextUIState = null;

        // this becomes a 'cancel' flag for any in-flight invokes
        // to set data. Since we can't cancel then wait on an invoke
        // from the UI thread synchronously, we can just increment this
        // and anything in flight will bail out as soon as it notices this
        // is different.
        private int m_ReqID = 0;

        private BufferFormatSpecifier m_FormatSpecifier = null;

        private string m_FormatText = "";

        private UIState GetUIState(MeshDataStage type)
        {
            if (type == MeshDataStage.VSIn)
                return m_VSIn;
            if (type == MeshDataStage.VSOut)
                return m_VSOut;
            if (type == MeshDataStage.GSOut)
                return m_GSOut;

            return null;
        }

        private UIState GetUIState(object sender)
        {
            if (sender == vsInBufferView)
                return m_VSIn;
            if (sender == vsOutBufferView)
                return m_VSOut;
            if (sender == gsOutBufferView)
                return m_GSOut;

            return null;
        }

        #endregion

        #region Privates

        private Core m_Core;
        private ReplayOutput m_Output = null;

        private byte[] m_Zeroes = null;

        private OutputConfig m_OutConfig = new OutputConfig();
        private MeshDisplay m_MeshDisplay = new MeshDisplay();

        private IntPtr RenderHandle = IntPtr.Zero;

        // Cameras
        private TimedUpdate m_Updater = null;

        private Camera m_Camera = new Camera();

        private ArcballCamera m_Arcball = null;
        private FlyCamera m_Flycam = null;
        private CameraControls m_CurrentCamera = null;

        #endregion

        public BufferViewer(Core core, bool meshview)
        {
            InitializeComponent();

            Icon = global::renderdocui.Properties.Resources.icon;

            UI_SetupDocks(meshview);

            m_Zeroes = new byte[512];
            for (int i = 0; i < 512; i++) m_Zeroes[i] = 0;

            m_VSIn.m_GridView = vsInBufferView;
            m_VSOut.m_GridView = vsOutBufferView;
            m_GSOut.m_GridView = gsOutBufferView;

            m_ContextUIState = m_VSIn;

            DockHandler.GetPersistStringCallback = PersistString;

            exportToToolStripMenuItem.Enabled = exportToolItem.Enabled = false;

            m_Core = core;

            this.DoubleBuffered = true;

            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            RenderHandle = render.Handle;

            render.Painting = true;

            render.MouseWheel += render_MouseWheel;
            render.MouseWheelHandler = render_MouseWheel;
            (render as Control).KeyDown += new KeyEventHandler(BufferViewer_KeyDown);
            (render as Control).KeyUp += new KeyEventHandler(BufferViewer_KeyUp);
            ResetConfig();

            MeshView = meshview;

            if (!MeshView)
            {
                debugVertexToolItem.Visible = debugSep.Visible = false;
                instLabel.Visible = instSep.Visible = instanceIdxToolitem.Visible = false;
                syncViewsToolItem.Visible = false;
                highlightVerts.Visible = false;
                byteOffset.Text = "0";

                Text = "Buffer Contents";
            }
            else
            {
                byteOffset.Visible = false; byteOffsLab.Visible = false;
                byteOffset.Text = "0";

                Text = "Mesh Output";
            }

            m_Core.AddLogViewer(this);
        }

        private void ResetConfig()
        {
            m_OutConfig.m_Type = OutputType.MeshDisplay;

            m_MeshDisplay = new MeshDisplay();
            m_MeshDisplay.type = MeshDataStage.VSIn;
            m_MeshDisplay.fov = 90.0f;

            m_MeshDisplay.solidShadeMode = SolidShadeMode.None;
            solidShading.SelectedIndex = 0;

            m_MeshDisplay.thisDrawOnly = true;
            drawRange.SelectedIndex = 0;

            m_MeshDisplay.currentMeshColour = new FloatVector(1, 0, 0, 1);
            m_MeshDisplay.prevMeshColour = new FloatVector(0, 0, 0, 1);

            m_Arcball = new ArcballCamera(m_Camera);
            m_Flycam = new FlyCamera(m_Camera);
            m_CurrentCamera = m_Arcball;
            m_Updater = new TimedUpdate(10, TimerUpdate);

            m_Arcball.SpeedMultiplier = m_Flycam.SpeedMultiplier = (float)camSpeed.Value;

            fovGuess.Text = m_MeshDisplay.fov.ToString("G");
            controlType.SelectedIndex = 0;
        }
        private void UI_SetupDocks(bool meshview)
        {
            if (meshview)
            {
                var w = Helpers.WrapDockContent(dockPanel, previewTab, "Preview");
                w.CloseButton = false;
                w.CloseButtonVisible = false;
                w.Show(dockPanel, DockState.DockBottom);

                m_VSIn.m_DockContent = Helpers.WrapDockContent(dockPanel, vsInBufferView, "VS Input");
                m_VSIn.m_DockContent.CloseButton = false;
                m_VSIn.m_DockContent.CloseButtonVisible = false;
                m_VSIn.m_DockContent.Show(dockPanel, DockState.Document);

                m_GSOut.m_DockContent = Helpers.WrapDockContent(dockPanel, gsOutBufferView, "GS/DS Output");
                m_GSOut.m_DockContent.CloseButton = false;
                m_GSOut.m_DockContent.CloseButtonVisible = false;
                m_GSOut.m_DockContent.Show(m_VSIn.m_DockContent.Pane, DockAlignment.Right, 0.5);

                m_VSOut.m_DockContent = Helpers.WrapDockContent(dockPanel, vsOutBufferView, "VS Output");
                m_VSOut.m_DockContent.CloseButton = false;
                m_VSOut.m_DockContent.CloseButtonVisible = false;
                m_VSOut.m_DockContent.Show(m_GSOut.m_DockContent.Pane, m_GSOut.m_DockContent);
            }
            else
            {
                previewTab.Visible = false;
                vsOutBufferView.Visible = false;
                gsOutBufferView.Visible = false;

                var w = Helpers.WrapDockContent(dockPanel, vsInBufferView, "Buffer Contents");
                w.DockState = DockState.Document;
                w.Show();
            }
        }

        public class PersistData
        {
            public static int currentPersistVersion = 2;
            public int persistVersion = currentPersistVersion;

            public bool meshView;

            public static PersistData GetDefaults()
            {
                PersistData data = new PersistData();

                data.meshView = true;

                return data;
            }
        }

        public void InitFromPersistString(string str)
        {
            PersistData data = null;

            try
            {
                if (str.Length > GetType().ToString().Length)
                {
                    var reader = new StringReader(str.Substring(GetType().ToString().Length));

                    System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(PersistData));
                    data = (PersistData)xs.Deserialize(reader);

                    reader.Close();
                }
            }
            catch (System.Xml.XmlException)
            {
            }
            catch (InvalidOperationException)
            {
                // don't need to handle it. Leave data null and pick up defaults below
            }

            if (data == null || data.persistVersion != PersistData.currentPersistVersion)
            {
                data = PersistData.GetDefaults();
            }

            ApplyPersistData(data);
        }

        private void ApplyPersistData(PersistData data)
        {
            MeshView = data.meshView;
        }

        // note that raw buffer viewers do not persist deliberately
        private string PersistString()
        {
            if (!MeshView) return "";

            var writer = new StringWriter();

            writer.Write(GetType().ToString());

            PersistData data = new PersistData();

            data.meshView = MeshView;

            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(PersistData));
            xs.Serialize(writer, data);

            return writer.ToString();
        }

        #region ILogViewerForm

        public void OnLogfileClosed()
        {
            m_Output = null;

            ResetConfig();

            ClearStoredData();

            exportToToolStripMenuItem.Enabled = exportToolItem.Enabled = false;
        }

        public void OnLogfileLoaded()
        {
            ClearStoredData();

            exportToToolStripMenuItem.Enabled = exportToolItem.Enabled = true;

            var draw = m_Core.CurDrawcall;

            if (MeshView)
            {
                if (draw == null)
                {
                    m_VSIn.AbortThread();
                    m_VSOut.AbortThread();
                    m_GSOut.AbortThread();
                    return;
                }

                int curReq = m_ReqID;

                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    if (curReq != m_ReqID)
                        return;

                    m_Output = r.CreateOutput(RenderHandle);
                    m_Output.SetOutputConfig(m_OutConfig);
                    RT_UpdateRenderOutput(r);
                    m_Output.Display(); // pump the display once, this will fetch postvs data

                    m_VSIn.m_Input = GetCurrentMeshInput(draw, MeshDataStage.VSIn);
                    m_VSOut.m_Input = GetCurrentMeshInput(draw, MeshDataStage.VSOut);
                    m_GSOut.m_Input = GetCurrentMeshInput(draw, MeshDataStage.GSOut);

                    var contentsVSIn = RT_FetchBufferContents(MeshDataStage.VSIn, r, m_VSIn.m_Input);
                    var contentsVSOut = RT_FetchBufferContents(MeshDataStage.VSOut, r, m_VSOut.m_Input);
                    var contentsGSOut = RT_FetchBufferContents(MeshDataStage.GSOut, r, m_GSOut.m_Input);

                    if (curReq != m_ReqID)
                        return;

                    this.BeginInvoke(new Action(() =>
                    {
                        if (curReq != m_ReqID)
                            return;

                        UI_AutoFetchRenderComponents(MeshDataStage.VSIn, true);
                        UI_AutoFetchRenderComponents(MeshDataStage.VSOut, true);
                        UI_AutoFetchRenderComponents(MeshDataStage.GSOut, true);
                        UI_AutoFetchRenderComponents(MeshDataStage.VSIn, false);
                        UI_AutoFetchRenderComponents(MeshDataStage.VSOut, false);
                        UI_AutoFetchRenderComponents(MeshDataStage.GSOut, false);
                        UI_UpdateMeshRenderComponents();

                        UI_SetAllColumns();

                        UI_SetRowsData(MeshDataStage.VSIn, contentsVSIn, 0);
                        if (m_VSOut.m_Input != null)
                            UI_SetRowsData(MeshDataStage.VSOut, contentsVSOut, 0);
                        if (m_GSOut.m_Input != null)
                            UI_SetRowsData(MeshDataStage.GSOut, contentsGSOut, 0);

                        camGuess_PropChanged();
                    }));
                });
            }
            else
            {
                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    m_Output = r.CreateOutput(RenderHandle);
                    m_Output.SetOutputConfig(m_OutConfig);
                    RT_UpdateRenderOutput(r);
                });
            }
        }

        public void OnEventSelected(UInt32 frameID, UInt32 eventID)
        {
            ClearStoredData();

            var draw = m_Core.CurDrawcall;

            instanceIdxToolitem.Enabled = (draw != null && draw.numInstances > 1);

            if (!instanceIdxToolitem.Enabled)
                instanceIdxToolitem.Text = "0";

            if (MeshView && draw == null)
            {
                m_VSIn.AbortThread();
                m_VSOut.AbortThread();
                m_GSOut.AbortThread();
                return;
            }

            int[] horizscroll = new int[] {
                m_VSIn.m_GridView.HorizontalScrollingOffset,
                m_VSOut.m_GridView.HorizontalScrollingOffset,
                m_GSOut.m_GridView.HorizontalScrollingOffset,
            };

            int curReq = m_ReqID;

            m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
            {
                m_VSIn.AbortThread();
                m_VSOut.AbortThread();
                m_GSOut.AbortThread();

                if (curReq != m_ReqID)
                    return;

                if (MeshView)
                {
                    MeshDataStage[] stages = new MeshDataStage[] { MeshDataStage.VSIn, MeshDataStage.VSOut, MeshDataStage.GSOut };

                    FormatElement[] prevPos = new FormatElement[3];
                    FormatElement[] prevSecond = new FormatElement[3];

                    for (int i = 0; i < 3; i++)
                    {
                        prevPos[i] = GetPosHighlightFormatElement(stages[i]);
                        prevSecond[i] = GetSecondHighlightFormatElement(stages[i]);
                    }

                    m_VSIn.m_Input = GetCurrentMeshInput(draw, MeshDataStage.VSIn);
                    m_VSOut.m_Input = GetCurrentMeshInput(draw, MeshDataStage.VSOut);
                    m_GSOut.m_Input = GetCurrentMeshInput(draw, MeshDataStage.GSOut);

                    for(int i=0; i < 3; i++)
                    {
                        FormatElement curPos = GetPosHighlightFormatElement(stages[i]);
                        FormatElement curSecond = GetSecondHighlightFormatElement(stages[i]);
                        if (prevPos[i] != curPos) UI_AutoFetchRenderComponents(stages[i], true);
                        if (prevSecond[i] != curSecond) UI_AutoFetchRenderComponents(stages[i], false);
                    }
                }

                var contentsVSIn = RT_FetchBufferContents(MeshDataStage.VSIn, r, m_VSIn.m_Input);
                var contentsVSOut = RT_FetchBufferContents(MeshDataStage.VSOut, r, m_VSOut.m_Input);
                var contentsGSOut = RT_FetchBufferContents(MeshDataStage.GSOut, r, m_GSOut.m_Input);

                if (curReq != m_ReqID)
                    return;

                this.BeginInvoke(new Action(() =>
                {
                    if (curReq != m_ReqID)
                        return;

                    m_VSIn.AbortThread();
                    m_VSOut.AbortThread();
                    m_GSOut.AbortThread();

                    if (m_VSIn.m_Input != null)
                        UI_SetRowsData(MeshDataStage.VSIn, contentsVSIn, horizscroll[0]);
                    if (m_VSOut.m_Input != null)
                        UI_SetRowsData(MeshDataStage.VSOut, contentsVSOut, horizscroll[1]);
                    if (m_GSOut.m_Input != null)
                        UI_SetRowsData(MeshDataStage.GSOut, contentsGSOut, horizscroll[2]);

                    if (MeshView)
                        UI_UpdateMeshRenderComponents();

                    UI_SetAllColumns();

                    camGuess_PropChanged();

                    render.Invalidate();
                }));
            });
        }

#endregion


        #region Data Setting

        private void ClearStoredData()
        {
            UIState[] states = { m_VSIn, m_VSOut, m_GSOut };

            m_ReqID++;

            foreach (var s in states)
            {
                s.AbortThread();
                s.m_RawData = null;
                s.m_Data = null;
                s.m_Stream = null;
                s.m_Reader = null;
                s.m_RawStride = 0;
                s.m_Rows = null;
                s.m_GridView.RowCount = 0;
            }

            ClearHighlightVerts();
        }

        public bool MeshView;

        private int RowOffset
        {
            get
            {
                int row = 0;
                int.TryParse(rowOffset.Text, out row);
                return row;
            }
        }

        private uint ByteOffset
        {
            get
            {
                uint offs = 0;
                uint.TryParse(byteOffset.Text, out offs);
                return offs;
            }
        }

        #region Get Data Formats/Organisation

        public void ViewRawBuffer(bool isBuffer, ResourceId id)
        {
            ViewRawBuffer(isBuffer, id, "");
        }

        public void ViewRawBuffer(bool isBuffer, ResourceId id, string formatString)
        {
            if (m_Core.CurBuffers == null) return;

            m_FormatText = formatString;

            Text = "Buffer Contents";
            foreach (var b in m_Core.CurBuffers)
            {
                if (b.ID == id)
                {
                    Text = b.name + " - Contents";
                    break;
                }
            }

            Input input = new Input();

            string errors = "";

            FormatElement[] elems = FormatElement.ParseFormatString(formatString, true, out errors);

            input.Strides = new uint[] { elems.Last().offset + elems.Last().ByteSize };
            input.Buffers = new ResourceId[] { isBuffer ? id : ResourceId.Null, isBuffer ? ResourceId.Null : id };
            input.Offsets = new uint[] { 0 };
            input.IndexBuffer = ResourceId.Null;
            input.BufferFormats = elems;
            input.IndexOffset = 0;

            m_VSIn.m_Input = input;

            ShowFormatSpecifier();

            m_FormatSpecifier.SetErrors(errors);

            ClearStoredData();

            m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
            {
                var contents = RT_FetchBufferContents(MeshDataStage.VSIn, r, input);

                this.BeginInvoke(new Action(() =>
                {
                    UI_SetRowsData(MeshDataStage.VSIn, contents, 0);
                    UI_SetColumns(MeshDataStage.VSIn, input.BufferFormats);
                }));
            });
        }

        // used for the mesh view, to get the format of the mesh input from whichever stage that
        // we're looking at
        private Input GetCurrentMeshInput(FetchDrawcall draw, MeshDataStage type)
        {
            if (!MeshView)
                return null;

            Input ret = new Input();
            ret.Drawcall = draw;
            ret.Topology = draw.topology;

            ResourceId ibuffer = ResourceId.Null;
            uint ioffset = 0;

            m_Core.CurPipelineState.GetIBuffer(out ibuffer, out ioffset);

            if (draw != null && (draw.flags & DrawcallFlags.UseIBuffer) == 0)
            {
                ibuffer = ResourceId.Null;
                ioffset = 0;
            }

            ret.IndexBuffer = ibuffer;
            ret.IndexOffset = ioffset;
            ret.IndexRestart = m_Core.CurPipelineState.IsStripRestartEnabled();
            ret.IndexRestartValue = m_Core.CurPipelineState.GetStripRestartIndex(draw != null ? draw.indexByteWidth : 0);

            if (type != MeshDataStage.VSIn)
            {
                ShaderReflection details = null;

                if (type == MeshDataStage.VSOut)
                    details = m_Core.CurPipelineState.GetShaderReflection(ShaderStageType.Vertex);
                else if (type == MeshDataStage.GSOut)
                {
                    details = m_Core.CurPipelineState.GetShaderReflection(ShaderStageType.Geometry);
                    if (details == null)
                        details = m_Core.CurPipelineState.GetShaderReflection(ShaderStageType.Domain);
                }

                if (details == null)
                    return null;

                List<FormatElement> f = new List<FormatElement>();

                int posidx = -1;
                for (int i = 0; i < details.OutputSig.Length; i++)
                {
                    var sig = details.OutputSig[i];

                    f.Add(new FormatElement());

                    f[i].buffer = 0;
                    f[i].name = details.OutputSig[i].varName.Length > 0 ? details.OutputSig[i].varName : details.OutputSig[i].semanticIdxName;
                    f[i].format.compByteWidth = sizeof(float);
                    f[i].format.compCount = sig.compCount;
                    f[i].format.compType = sig.compType;
                    f[i].format.special = false;
                    f[i].format.rawType = 0;
                    f[i].perinstance = false;
                    f[i].instancerate = 1;
                    f[i].rowmajor = false;
                    f[i].matrixdim = 1;
                    f[i].systemValue = sig.systemValue;

                    if(f[i].systemValue == SystemAttribute.Position)
                        posidx = i;
                }

                // shift position attribute up to first, keeping order otherwise
                // the same
                if (posidx > 0)
                {
                    FormatElement pos = f[posidx];
                    f.RemoveAt(posidx);
                    f.Insert(0, pos);
                }
                
                uint offset = 0;
                for (int i = 0; i < details.OutputSig.Length; i++)
                {
                    f[i].offset = offset;
                    offset += f[i].format.compCount * sizeof(float);
                }

                ret.BufferFormats = f.ToArray();
                ret.Strides = new uint[] { offset };
                ret.Offsets = new uint[] { 0 };
                ret.Buffers = null;

                return ret;
            }

            CommonPipelineState.VBuffer[] vbs = m_Core.CurPipelineState.GetVBuffers();

            ResourceId[] bs = new ResourceId[vbs.Length];
            uint[] s = new uint[vbs.Length];
            uint[] o = new uint[vbs.Length];

            for (int i = 0; i < vbs.Length; i++)
            {
                bs[i] = vbs[i].Buffer;
                s[i] = vbs[i].ByteStride;
                o[i] = vbs[i].ByteOffset;
            }

            {
                FormatElement[] f = null;

                var vinputs = m_Core.CurPipelineState.GetVertexInputs();
                f = new FormatElement[vinputs.Length];

                ret.GenericValues = new object[vinputs.Length][];

                int i = 0;
                foreach (var a in vinputs)
                {
                    f[i] = new FormatElement(a.Name,
                                             a.VertexBuffer,
                                             a.RelativeByteOffset,
                                             a.PerInstance,
                                             a.InstanceRate,
                                             false, // row major matrix
                                             1, // matrix dimension
                                             a.Format,
                                             false);
                    ret.GenericValues[i] = a.GenericValue;
                    i++;
                }

                ret.BufferFormats = f;
                ret.Strides = s;
                ret.Offsets = o;
                ret.Buffers = bs;
            }

            return ret;
        }

        #endregion

        #region Get Actual Bytes

        private Dataset RT_FetchBufferContents(MeshDataStage type, ReplayRenderer r, Input input)
        {
            Dataset ret = new Dataset();

            ret.IndexCount = 0;

            if (input == null)
                return ret;

            if (!MeshView)
            {
                if (input != null && (input.Buffers[0] != ResourceId.Null || input.Buffers[1] != ResourceId.Null))
                {
                    ret.Buffers = new byte[1][];

                    if(input.Buffers[0] != ResourceId.Null)
                        ret.Buffers[0] = r.GetBufferData(input.Buffers[0], 0, 0);
                    else if (input.Buffers[1] != ResourceId.Null)
                        ret.Buffers[0] = r.GetTextureData(input.Buffers[1], 0, 0);

                    ret.Indices = null;
                    ret.DataIndices = null;
                    ret.IndexCount = (uint)ret.Buffers[0].Length / input.Strides[0];
                }

                return ret;
            }

            if (input.Drawcall == null) return ret;

            ret.IndexCount = input.Drawcall.numIndices;
            ret.Topology = input.Topology;

            if (type != MeshDataStage.VSIn)
            {
                ret.PostVS = r.GetPostVSData(Math.Min(m_MeshDisplay.curInstance, Math.Max(1U, input.Drawcall.numInstances)), type);

                ret.Buffers = new byte[1][];

                if (ret.PostVS.buf == ResourceId.Null)
                {
                    ret.IndexCount = 0;
                    ret.Topology = PrimitiveTopology.Unknown;
                }
                else
                {
                    ret.Buffers[0] = r.GetBufferData(ret.PostVS.buf, ret.PostVS.offset, 0);

                    ret.Topology = ret.PostVS.topo;

                    ret.IndexCount = ret.PostVS.numVerts;

                    uint stride = 0;
                    foreach (var f in input.BufferFormats)
                        stride += f.ByteSize;

                    if (stride != 0 && (input.Drawcall.flags & DrawcallFlags.UseIBuffer) == 0)
                        ret.IndexCount = Math.Min(ret.IndexCount, (uint)ret.Buffers[0].Length / stride);
                }

                ret.Indices = null;
                ret.DataIndices = null;

                if (ret.PostVS.buf != ResourceId.Null && type == MeshDataStage.VSOut &&
                    (input.Drawcall.flags & DrawcallFlags.UseIBuffer) > 0 && input.IndexBuffer != ResourceId.Null)
                {
                    ret.IndexCount = input.Drawcall.numIndices;

                    byte[] rawidxs = r.GetBufferData(input.IndexBuffer,
                                                     input.IndexOffset + input.Drawcall.indexOffset * input.Drawcall.indexByteWidth,
                                                     ret.IndexCount * input.Drawcall.indexByteWidth);

                    if (input.Drawcall.indexByteWidth == 0 || rawidxs == null || rawidxs.Length == 0)
                    {
                        ret.Indices = new uint[0] { };
                    }
                    else
                    {
                        ret.Indices = new uint[rawidxs.Length / input.Drawcall.indexByteWidth];

                        if (input.Drawcall.indexByteWidth == 2)
                        {
                            ushort[] tmp = new ushort[rawidxs.Length / 2];

                            Buffer.BlockCopy(rawidxs, 0, tmp, 0, rawidxs.Length);

                            for (int i = 0; i < tmp.Length; i++)
                            {
                                ret.Indices[i] = tmp[i];
                            }
                        }
                        else if (input.Drawcall.indexByteWidth == 4)
                        {
                            Buffer.BlockCopy(rawidxs, 0, ret.Indices, 0, rawidxs.Length);
                        }
                    }

                    rawidxs = r.GetBufferData(ret.PostVS.idxbuf, 0, 0);

                    if (input.Drawcall.indexByteWidth == 0 || rawidxs == null || rawidxs.Length == 0)
                    {
                        ret.DataIndices = new uint[0] { };
                    }
                    else
                    {
                        ret.DataIndices = new uint[rawidxs.Length / sizeof(uint)];
                        Buffer.BlockCopy(rawidxs, 0, ret.DataIndices, 0, rawidxs.Length);
                    }
                }

                return ret;
            }
            else if (input.Buffers != null && m_Output != null)
            {
                uint maxIndex = Math.Max(ret.IndexCount, 1) - 1;
                uint maxInstIndex = Math.Max(input.Drawcall.numInstances, 1) - 1;

                if ((input.Drawcall.flags & DrawcallFlags.UseIBuffer) != 0 &&
                    input.IndexBuffer != ResourceId.Null)
                {
                    byte[] rawidxs = r.GetBufferData(input.IndexBuffer,
                                                     input.IndexOffset + input.Drawcall.indexOffset * input.Drawcall.indexByteWidth,
                                                     ret.IndexCount * input.Drawcall.indexByteWidth);

                    if (input.Drawcall.indexByteWidth == 0 || rawidxs == null || rawidxs.Length == 0)
                    {
                        ret.Indices = new uint[0];
                    }
                    else
                    {
                        ret.Indices = new uint[rawidxs.Length / input.Drawcall.indexByteWidth];

                        if (input.Drawcall.indexByteWidth == 2)
                        {
                            ushort[] tmp = new ushort[rawidxs.Length / 2];

                            Buffer.BlockCopy(rawidxs, 0, tmp, 0, rawidxs.Length);

                            for (int i = 0; i < tmp.Length; i++)
                            {
                                ret.Indices[i] = tmp[i];
                            }
                        }
                        else if (input.Drawcall.indexByteWidth == 4)
                        {
                            Buffer.BlockCopy(rawidxs, 0, ret.Indices, 0, rawidxs.Length);
                        }
                    }

                    maxIndex = 0;
                    foreach (var i in ret.Indices)
                    {
                        if (input.Drawcall.indexByteWidth == 2 && i == input.IndexRestartValue && input.IndexRestart)
                            continue;
                        if (input.Drawcall.indexByteWidth == 4 && i == input.IndexRestartValue && input.IndexRestart)
                            continue;

                        maxIndex = Math.Max(maxIndex, i);
                    }
                }

                ret.DataIndices = ret.Indices;

                ret.Buffers = new byte[input.Buffers.Length][];
                for (int i = 0; i < input.Buffers.Length; i++)
                {
                    bool used = false;
                    bool pi = false;
                    bool pv = false;

                    foreach (var f in input.BufferFormats)
                    {
                        if (f.buffer == i)
                        {
                            if (f.perinstance)
                                pi = true;
                            else
                                pv = true;

                            used = true;
                        }
                    }

                    uint maxIdx = 0;
                    uint offset = 0;

                    if(used)
                    {
                        if (pi)
                        {
                            maxIdx = maxInstIndex;
                            offset = input.Drawcall.instanceOffset;
                        }
                        if (pv)
                        {
                            maxIdx = Math.Max(maxIndex, maxIdx);
                            offset = input.Drawcall.vertexOffset;
                        }

                        System.Diagnostics.Debug.Assert(pi != pv || (pi == false && pv == false));
                    }

                    if (!used || input.Buffers[i] == ResourceId.Null)
                    {
                        ret.Buffers[i] = null;
                    }
                    else
                    {
                        ret.Buffers[i] = r.GetBufferData(input.Buffers[i],
                                                          input.Offsets[i] + offset * input.Strides[i],
                                                          (maxIdx + 1) * input.Strides[i]);

                        if (ret.Buffers[i].Length < (maxIdx + 1) * input.Strides[i])
                        {
                            // clamped
                        }
                    }
                }

                return ret;
            }

            return ret;
        }

        #endregion

        #region Setting Column Headers

        private void UI_MergeColumns(DataGridView grid, int col, uint num, Graphics g)
        {
            for(int i=0; i < num; i++)
            {
                if (col + i >= grid.Columns.Count) break;

                DataGridViewCell hc = grid.Columns[col + i].HeaderCell;
                Rectangle hcRct = grid.GetCellDisplayRectangle(hc.ColumnIndex, -1, true);

                Rectangle left = new Rectangle(hcRct.Left, hcRct.Top + 2, 1, hcRct.Height - 4);
                Rectangle right = new Rectangle(hcRct.Left+hcRct.Width-1, hcRct.Top + 2, 1, hcRct.Height - 4);

                using (var brush = new SolidBrush(grid.Columns[col + i].HeaderCell.Style.BackColor))
                {
                    if (i != 0)
                        g.FillRectangle(brush, left);
                    if (i != num - 1)
                        g.FillRectangle(brush, right);
                }
            }
        }

        private void UI_UpdateMeshColumns(MeshDataStage type, FormatElement[] el)
        {
            bool active = (type == m_MeshDisplay.type);
            bool input = (type == MeshDataStage.VSIn);
            var bufView = GetUIState(type).m_GridView;

            if (bufView.ColumnCount == 0)
                return;

            int colidx = 2; // skip VTX and IDX columns
            Color defaultCol = bufView.Columns[0].DefaultCellStyle.BackColor;

            for (int e = 0; e < el.Length; e++)
            {
                for (int i = 0; i < el[e].format.compCount; i++)
                {
                    if (colidx >= bufView.ColumnCount)
                        return;

                    DataGridViewColumn col = bufView.Columns[colidx];
                    colidx++;

                    if (e == m_PosElement[(int)type] && active)
                    {
                        if (i != 3 || !input)
                            col.DefaultCellStyle.BackColor = Color.SkyBlue;
                        else
                            col.DefaultCellStyle.BackColor = Color.LightCyan;
                    }
                    else if (e == m_SecondElement[(int)type] && active && m_MeshDisplay.solidShadeMode == SolidShadeMode.Secondary)
                    {
                        if ((m_MeshDisplay.secondary.showAlpha && i == 3) ||
                            (!m_MeshDisplay.secondary.showAlpha && i != 3))
                            col.DefaultCellStyle.BackColor = Color.LightGreen;
                        else
                            col.DefaultCellStyle.BackColor = Color.FromArgb(200, 238, 200);
                    }
                    else
                        col.DefaultCellStyle.BackColor = defaultCol;
                }
            }
        }

        private void UI_SetMeshColumns(MeshDataStage type, FormatElement[] el)
        {
            var bufView = GetUIState(type).m_GridView;

            bufView.Columns.Clear();

            DataGridViewTextBoxColumn Column = new DataGridViewTextBoxColumn();

            Column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            Column.HeaderText = "VTX";
            Column.ReadOnly = true;
            Column.Frozen = true;
            Column.Width = 50;
            Column.DividerWidth = 0;
            Column.SortMode = DataGridViewColumnSortMode.Programmatic;
            Column.DefaultCellStyle.Padding = new Padding(0, 2, 0, 2);

            bufView.Columns.Add(Column);

            Column = new DataGridViewTextBoxColumn();

            Column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            Column.HeaderText = "IDX";
            Column.ReadOnly = true;
            Column.Frozen = true;
            Column.Width = 50;
            Column.DividerWidth = 10;
            Column.SortMode = DataGridViewColumnSortMode.Programmatic;
            Column.DefaultCellStyle.Padding = new Padding(0, 2, 0, 2);

            bufView.Columns.Add(Column);

            for(int e=0; e < el.Length; e++)
            {
                for (int i = 0; i < el[e].format.compCount; i++)
                {
                    DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();

                    col.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
                    col.MinimumWidth = 50;
                    col.HeaderText = el[e].name;
                    col.ReadOnly = true;
                    col.SortMode = DataGridViewColumnSortMode.Programmatic;
                    col.DefaultCellStyle.Padding = new Padding(0, 2, 0, 2);
                    bufView.Columns.Add(col);
                }

                if (e < el.Length-1)
                    bufView.Columns[bufView.Columns.Count - 1].DividerWidth = 10;
            }

            UI_UpdateMeshColumns(type, el);
        }

        private void UI_SetRawColumns(FormatElement[] el)
        {
            vsInBufferView.Columns.Clear();

            DataGridViewTextBoxColumn Column = new DataGridViewTextBoxColumn();

            Column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            Column.HeaderText = "Element";
            Column.ReadOnly = true;
            Column.Frozen = true;
            Column.Width = 70;
            Column.DividerWidth = 10;
            Column.SortMode = DataGridViewColumnSortMode.Programmatic;
            Column.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            Column.DefaultCellStyle.Padding = new Padding(0, 2, 0, 2);

            vsInBufferView.Columns.Add(Column);

            for (int e = 0; e < el.Length; e++)
            {
                for (int i = 0; i < el[e].format.compCount; i++)
                {
                    DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();

                    col.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
                    col.MinimumWidth = 50;
                    col.HeaderText = el[e].name;
                    col.ReadOnly = true;
                    col.SortMode = DataGridViewColumnSortMode.Programmatic;
                    col.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
                    col.DefaultCellStyle.Padding = new Padding(0, 2, 0, 2);

                    vsInBufferView.Columns.Add(col);
                }

                if (e < el.Length - 1)
                    vsInBufferView.Columns[vsInBufferView.Columns.Count - 1].DividerWidth = 10;
            }
        }

        private void UI_SetColumns(MeshDataStage type, FormatElement[] el)
        {
            if (MeshView)
                UI_SetMeshColumns(type, el);
            else
                UI_SetRawColumns(el);
        }

        private void UI_SetAllColumns()
        {
            if (m_VSIn.m_Input != null)
                UI_SetColumns(MeshDataStage.VSIn, m_VSIn.m_Input.BufferFormats);
            if (m_VSOut.m_Input != null)
                UI_SetColumns(MeshDataStage.VSOut, m_VSOut.m_Input.BufferFormats);
            if (m_GSOut.m_Input != null)
                UI_SetColumns(MeshDataStage.GSOut, m_GSOut.m_Input.BufferFormats);
        }

        private void UI_UpdateAllColumns()
        {
            if (m_VSIn.m_Input != null)
                UI_UpdateMeshColumns(MeshDataStage.VSIn, m_VSIn.m_Input.BufferFormats);
            if (m_VSOut.m_Input != null)
                UI_UpdateMeshColumns(MeshDataStage.VSOut, m_VSOut.m_Input.BufferFormats);
            if (m_GSOut.m_Input != null)
                UI_UpdateMeshColumns(MeshDataStage.GSOut, m_GSOut.m_Input.BufferFormats);
        }

        #endregion

        #region Setting Row Data

        private void UI_SetRowsData(MeshDataStage type, Dataset data, int horizScroll)
        {
            var state = GetUIState(type);

            Input input = state.m_Input;

            if(data.Buffers == null)
                return;

            var bufView = state.m_GridView;

            bufView.RowCount = 0;
            state.m_Data = data;

            {
                byte[][] d = data.Buffers;

                state.m_Stream = new Stream[d.Length];
                state.m_Reader = new BinaryReader[d.Length];

                state.m_MinBounds = new Vec3f(-1.0f, -1.0f, -1.0f);
                state.m_MaxBounds = new Vec3f(1.0f, 1.0f, 1.0f);

                var bufferFormats = input.BufferFormats;

                foreach (var el in bufferFormats)
                {
                    if (state.m_Stream[el.buffer] == null)
                    {
                        if (d[el.buffer] == null)
                        {
                            state.m_Stream[el.buffer] = new MemoryStream(m_Zeroes);
                            state.m_Reader[el.buffer] = new BinaryReader(state.m_Stream[el.buffer]);
                        }
                        else
                        {
                            state.m_Stream[el.buffer] = new MemoryStream(d[el.buffer]);
                            state.m_Reader[el.buffer] = new BinaryReader(state.m_Stream[el.buffer]);
                        }
                    }
                }

                state.m_RawStride = 0;

                foreach (var el in bufferFormats)
                    state.m_RawStride += el.ByteSize;

                state.m_Rows = new object[data.IndexCount][];

                if (!MeshView)
                {
                    state.m_RawData = d[0];

                    this.BeginInvoke(new Action(() =>
                    {
                        UI_ShowRows(state, horizScroll);
                    }));
                }
                else
                {
                    state.m_RawData = new byte[state.m_RawStride * data.IndexCount];
                    UI_FillRawData(state, horizScroll);
                }
            }
        }

        private void UI_FillRawData(UIState state, int horizScroll)
        {
            var data = state.m_Data;

            Input input = state.m_Input;
            uint instance = m_MeshDisplay.curInstance;

            Thread th = Helpers.NewThread(new ThreadStart(() =>
            {
                byte[][] d = data.Buffers;

                Stream rawStream = new MemoryStream(state.m_RawData);
                BinaryWriter rawWriter = new BinaryWriter(rawStream);

                uint rownum = 0;
                bool finished = false;

                var bufferFormats = input.BufferFormats;
                var generics = input.GenericValues;

                Vec3f minBounds = new Vec3f(float.MaxValue, float.MaxValue, float.MaxValue);
                Vec3f maxBounds = new Vec3f(-float.MaxValue, -float.MaxValue, -float.MaxValue);

                while (!finished)
                {
                    if (rownum >= data.IndexCount)
                    {
                        finished = true;
                        break;
                    }

                    uint index = rownum;

                    if (data.Indices != null)
                    {
                        if (rownum >= data.Indices.Length)
                        {
                            index = 0;
                        }
                        else
                        {
                            index = data.Indices[rownum];
                        }
                    }
                    else if ((input.Drawcall.flags & DrawcallFlags.UseIBuffer) != 0 && state == m_VSIn)
                    {
                        // no index buffer, but indexed drawcall
                        index = 0;
                    }

                    int elemsWithData = 0;

                    for (int el = 0; el < bufferFormats.Length; el++)
                    {
                        if (generics != null && generics[el] != null)
                        {
                            for(int g=0; g < generics[el].Length; g++)
                            {
                                if (generics[el][g] is uint)
                                    rawWriter.Write((uint)generics[el][g]);
                                else if (generics[el][g] is int)
                                    rawWriter.Write((int)generics[el][g]);
                                else if (generics[el][g] is float)
                                    rawWriter.Write((float)generics[el][g]);
                            }

                            continue;
                        }

                        try
                        {
                            byte[] bytedata = d[bufferFormats[el].buffer];
                            Stream strm = state.m_Stream[bufferFormats[el].buffer];
                            BinaryReader read = state.m_Reader[bufferFormats[el].buffer];

                            uint instIdx = 0;
                            // for instancing, need to handle instance rate being 0 (every instance takes index 0 in that case)
                            if (bufferFormats[el].perinstance)
                                instIdx = bufferFormats[el].instancerate > 0 ? (instance / (uint)bufferFormats[el].instancerate) : 0;
                            else
                                instIdx = index;

                            uint offs = input.Strides[bufferFormats[el].buffer] * instIdx + bufferFormats[el].offset;

                            if (!MeshView)
                                offs += ByteOffset;

                            bool outofBounds = false;

                            if (bytedata == null)
                            {
                                strm.Seek(0, SeekOrigin.Begin);
                            }
                            else if (offs >= bytedata.Length)
                            {
                                outofBounds = true;
                                strm = null;
                                read = new BinaryReader(new MemoryStream(m_Zeroes));
                            }
                            else
                            {
                                strm.Seek(offs, SeekOrigin.Begin);
                            }

                            string elname = bufferFormats[el].name.ToUpperInvariant();
                            var fmt = bufferFormats[el].format;
                            int byteWidth = (int)fmt.compByteWidth;

                            int bytesToRead = (int)(fmt.compByteWidth * fmt.compCount);

                            byte[] bytes = read.ReadBytes(bytesToRead);
                            rawWriter.Write(bytes);

                            if (bytes.Length != bytesToRead)
                                continue;

                            if (elname == "POSITION" || bufferFormats[el].systemValue == SystemAttribute.Position)
                            {
                                for (int i = 0; i < fmt.compCount; i++)
                                {
                                    float val = 0;

                                    if (fmt.compType == FormatComponentType.Float)
                                    {
                                        if (byteWidth == 4)
                                            val = BitConverter.ToSingle(bytes, i * byteWidth);
                                        else if (byteWidth == 2)
                                            val = fmt.ConvertFromHalf(BitConverter.ToUInt16(bytes, i * byteWidth));
                                    }
                                    else
                                    {
                                        if (byteWidth == 4)
                                            val = (float)BitConverter.ToUInt32(bytes, i * byteWidth);
                                        else if (byteWidth == 2)
                                            val = (float)BitConverter.ToUInt16(bytes, i * byteWidth);
                                        else if (byteWidth == 1)
                                            val = (float)bytes[i * byteWidth];
                                    }

                                    if (outofBounds) continue;

                                    if (i == 0)
                                    {
                                        minBounds.x = Math.Min(minBounds.x, val);
                                        maxBounds.x = Math.Max(maxBounds.x, val);
                                    }
                                    else if (i == 1)
                                    {
                                        minBounds.y = Math.Min(minBounds.y, val);
                                        maxBounds.y = Math.Max(maxBounds.y, val);
                                    }
                                    else if (i == 2)
                                    {
                                        minBounds.z = Math.Min(minBounds.z, val);
                                        maxBounds.z = Math.Max(maxBounds.z, val);
                                    }
                                }
                            }

                            elemsWithData++;
                        }
                        catch (System.IO.EndOfStreamException)
                        {
                            // don't increment elemsWithData
                        }
                    }

                    finished = (elemsWithData > 0);

                    rownum++;
                }
                
                this.BeginInvoke(new Action(() =>
                {
                    state.m_MinBounds = minBounds;
                    state.m_MaxBounds = maxBounds;

                    UI_ShowRows(state, horizScroll);
                }));
            }));

            th.Start();

            state.m_DataParseThread = th;
        }

        private bool SuppressCaching = false;

        private void UI_ShowRows(UIState state, int horizScroll)
        {
            var bufView = state.m_GridView;

            SuppressCaching = true;

            for (int i = 0; i < bufView.Columns.Count; i++)
            {
                if (bufView.Columns[i].AutoSizeMode == DataGridViewAutoSizeColumnMode.AllCells)
                {
                    bufView.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                }
            }

            bufView.RowCount = 0;

            if (state.m_Rows != null)
            {
                bufView.RowCount = state.m_Rows.Length;

                ScrollToRow(bufView, RowOffset);

                SuppressCaching = false;

                bufView.HorizontalScrollingOffset = horizScroll;
            }

            if (vsInBufferView.Focused && m_Core.LogLoaded)
            {
                debugVertex.Enabled = debugVertexToolItem.Enabled = true;
            }
        }

        private string ElementString(FormatElement el, object o)
        {
            if (o is float)
            {
                return Formatter.Format((float)o);
            }
            else if (o is double)
            {
                return Formatter.Format((double)o);
            }
            else if (o is uint)
            {
                uint u = (uint)o;

                if (el.format.compByteWidth == 4) return String.Format(el.hex ? "{0:X8}" : "{0}", u);
                if (el.format.compByteWidth == 2) return String.Format(el.hex ? "{0:X4}" : "{0}", u);
                if (el.format.compByteWidth == 1) return String.Format(el.hex ? "{0:X2}" : "{0}", u);

                return String.Format("{0}", (uint)o);
            }
            else if (o is int)
            {
                return String.Format("{0}", (int)o);
            }

            return o.ToString();
        }

        private void UI_CacheRow(UIState state, int rowIdx)
        {
            if (state.m_Rows[rowIdx] != null || SuppressCaching)
                return;

            var data = state.m_Data;
            Input input = state.m_Input;
            uint instance = m_MeshDisplay.curInstance;

            if (data.Buffers == null)
                return;

            {
                byte[][] d = data.Buffers;

                var bufferFormats = input.BufferFormats;
                var generics = input.GenericValues;
                uint rowlen = 0;

                foreach(var el in bufferFormats)
                    rowlen += el.format.compCount;

                {
                    if (rowIdx >= data.IndexCount)
                    {
                        return;
                    }

                    uint dataIndex = (uint)rowIdx;

                    bool outOfBoundsIdx = false;

                    if (data.DataIndices != null)
                    {
                        if (rowIdx >= data.DataIndices.Length)
                        {
                            dataIndex = 0;
                            outOfBoundsIdx = true;
                        }
                        else
                        {
                            dataIndex = data.DataIndices[rowIdx];
                        }
                    }
                    else if (input.Drawcall != null && (input.Drawcall.flags & DrawcallFlags.UseIBuffer) != 0 &&
                        (state == m_VSIn || state == m_VSOut))
                    {
                        // no index buffer, but indexed drawcall
                        dataIndex = 0;
                        outOfBoundsIdx = true;
                    }

                    uint displayIndex = dataIndex;
                    if (data.Indices != null && rowIdx < data.Indices.Length)
                        displayIndex = data.Indices[rowIdx];

                    object[] rowdata = null;

                    int x = 0;
                    if (MeshView)
                    {
                        rowdata = new object[2 + rowlen];

                        rowdata[0] = rowIdx;
                        if (outOfBoundsIdx)
                            rowdata[1] = "-";
                        else
                            rowdata[1] = displayIndex;

                        bool strip = state.m_Data.Topology == PrimitiveTopology.LineStrip ||
                                     state.m_Data.Topology == PrimitiveTopology.LineStrip_Adj ||
                                     state.m_Data.Topology == PrimitiveTopology.TriangleStrip ||
                                     state.m_Data.Topology == PrimitiveTopology.TriangleStrip_Adj;

                        if (state.m_Input.Drawcall.indexByteWidth == 2 && dataIndex == state.m_Input.IndexRestartValue && state.m_Input.IndexRestart && strip)
                            rowdata[1] = "-1";
                        if (state.m_Input.Drawcall.indexByteWidth == 4 && dataIndex == state.m_Input.IndexRestartValue && state.m_Input.IndexRestart && strip)
                            rowdata[1] = "-1";

                        x = 2;
                    }
                    else
                    {
                        rowdata = new object[1 + rowlen];

                        rowdata[0] = rowIdx;

                        x = 1;
                    }

                    for (int el = 0; el < bufferFormats.Length; el++)
                    {
                        int xstart = x;

                        if (generics != null && generics[el] != null)
                        {
                            for (int g = 0; g < generics[el].Length; g++)
                                rowdata[x++] = generics[el][g];

                            continue;
                        }

                        try
                        {
                            byte[] bytedata = d[bufferFormats[el].buffer];
                            Stream strm = state.m_Stream[bufferFormats[el].buffer];
                            BinaryReader read = state.m_Reader[bufferFormats[el].buffer];

                            uint instIdx = 0;
                            // for instancing, need to handle instance rate being 0 (every instance takes index 0 in that case)
                            if (bufferFormats[el].perinstance)
                                instIdx = bufferFormats[el].instancerate > 0 ? (instance / (uint)bufferFormats[el].instancerate) : 0;
                            else
                                instIdx = dataIndex;

                            uint offs = input.Strides[bufferFormats[el].buffer] * instIdx + bufferFormats[el].offset;

                            if (!MeshView)
                                offs += ByteOffset;

                            if (bytedata == null)
                            {
                                strm.Seek(0, SeekOrigin.Begin);
                            }
                            else if (offs >= bytedata.Length)
                            {
                                for (int i = 0; i < bufferFormats[el].format.compCount; i++, x++)
                                {
                                    rowdata[x] = "-";
                                }
                                continue;
                            }
                            else
                            {
                                strm.Seek(offs, SeekOrigin.Begin);
                            }

                            object[] elements = bufferFormats[el].GetObjects(read);

                            if (bufferFormats[el].matrixdim == 1)
                            {
                                for (int i = 0; i < elements.Length; i++)
                                    rowdata[x + i] = ElementString(bufferFormats[el], elements[i]);
                                x += elements.Length;
                            }
                            else
                            {
                                int cols = (int)bufferFormats[el].format.compCount;
                                int rows = (int)bufferFormats[el].matrixdim;

                                for (int col = 0; col < cols; col++)
                                {
                                    string[] colarr = new string[rows];
                                    for (int row = 0; row < rows; row++)
                                    {
                                        if (!bufferFormats[el].rowmajor)
                                            colarr[row] = ElementString(bufferFormats[el], elements[col * rows + row]);
                                        else
                                            colarr[row] = ElementString(bufferFormats[el], elements[row * cols + col]);
                                    }

                                    rowdata[x++] = colarr;
                                }
                            }
                        }
                        catch (System.IO.EndOfStreamException)
                        {
                            for (int i = 0; i < bufferFormats[el].format.compCount; i++)
                            {
                                rowdata[xstart + i] = "-";
                            }

                            x = (int)(xstart + bufferFormats[el].format.compCount);
                        }
                    }

                    if (rowdata != null)
                    {
                        state.m_Rows[rowIdx] = rowdata;
                    }
                }
            }
        }

        private void ScrollToRow(DataGridView view, int r)
        {
            try
            {
                int row = Math.Max(0, Math.Min(r, view.RowCount - 1));

                if (row < view.RowCount)
                {
                    view.FirstDisplayedScrollingRowIndex = row;
                    view.ClearSelection();
                    view.Rows[row].Selected = true;
                }
            }
            catch (InvalidOperationException)
            {
                // this can happen when the window is too small, all we can do is ignore it.
            }
        }

        private string CellValueToString(object val)
        {
            if (val == null)
                return "-";
            else if (val is float)
                return Formatter.Format((float)val);

            return val.ToString();
        }

        private void bufferView_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (!m_Core.LogLoaded)
            {
                e.Value = "";
                return;
            }

            var state = GetUIState(sender);

            if (state == null ||
                state.m_Rows == null ||
                state.m_Rows.Length == 0)
            {
                if (e.ColumnIndex == 1)
                    e.Value = "Loading...";
                else
                    e.Value = "";
                return;
            }

            UI_CacheRow(state, e.RowIndex);

            if (e.RowIndex >= state.m_Rows.Length ||
                state.m_Rows[e.RowIndex] == null ||
                e.ColumnIndex >= state.m_Rows[e.RowIndex].Length)
            {
                e.Value = "";
                return;
            }

            object val = state.m_Rows[e.RowIndex][e.ColumnIndex];

            if (val is object[])
            {
                string s = "";
                object[] arr = (object[])val;
                for(int i=0; i < arr.Length; i++)
                {
                    s += CellValueToString(arr[i]);
                    if (i + 1 < arr.Length)
                        s += Environment.NewLine;
                }
                e.Value = s;
            }
            else
            {
                e.Value = CellValueToString(val);
            }
        }

        #endregion

        #endregion

        #region Camera Controls

        private void enableCameraControls()
        {
            if (m_MeshDisplay.type == MeshDataStage.VSIn)
                aspectGuess.Enabled = nearGuess.Enabled = farGuess.Enabled = false;
            else
                aspectGuess.Enabled = nearGuess.Enabled = farGuess.Enabled = true;
        }

        private void configureCam_CheckedChanged(object sender, EventArgs e)
        {
            configCamControls.Visible = configureCam.Checked;

            enableCameraControls();
        }

        private void resetCam_Click(object sender, EventArgs e)
        {
            m_Arcball.Reset(new Vec3f(0.0f, 0.0f, -10.0f));

            if (m_MeshDisplay.type == MeshDataStage.VSIn)
            {
                m_Flycam.Reset(new Vec3f(0.0f, 0.0f, -10.0f));
                m_CurrentCamera = m_Arcball;
            }
            else if(m_MeshDisplay.type == MeshDataStage.VSOut)
            {
                if (m_Core.CurPipelineState.IsTessellationEnabled)
                    m_Flycam.Reset(new Vec3f(0.0f, 0.0f, -10.0f));
                else
                    m_Flycam.Reset(new Vec3f(0.0f, 0.0f, 0.0f));

                m_CurrentCamera = m_Flycam;
            }
            else if(m_MeshDisplay.type == MeshDataStage.GSOut)
            {
                m_Flycam.Reset(new Vec3f(0.0f, 0.0f, 0.0f));

                m_CurrentCamera = m_Flycam;
            }

            m_CurrentCamera.Apply();
            render.Invalidate();
        }

        private void fitScreen_Click(object sender, EventArgs e)
        {
            if(m_MeshDisplay.type != MeshDataStage.VSIn)
                return;

            controlType.SelectedIndex = 1;

            var state = GetUIState(m_MeshDisplay.type);

            Vec3f diag = state.m_MaxBounds.Sub(state.m_MinBounds);

            if(diag.x < 0.0f || diag.y < 0.0f || diag.z < 0.0f || diag.Length() <= 0.00001f)
                return;

            Vec3f middle = new Vec3f(state.m_MinBounds.x + diag.x / 2.0f,
                                     state.m_MinBounds.y + diag.y / 2.0f,
                                     state.m_MinBounds.z + diag.z / 2.0f);

            Vec3f pos = new Vec3f(middle);

            pos.z -= diag.Length();

            m_Flycam.Reset(pos);

            camSpeed.Value = Helpers.Clamp((decimal)(diag.Length() / 200.0f), camSpeed.Minimum, camSpeed.Maximum);

            m_CurrentCamera.Apply();
            render.Invalidate();
        }

        private void TimerUpdate()
        {
            if (m_CurrentCamera == null) return;

            m_CurrentCamera.Update();

            if (m_CurrentCamera.Dirty)
            {
                m_CurrentCamera.Apply();
                render.Invalidate();
            }
        }

        void BufferViewer_KeyUp(object sender, KeyEventArgs e)
        {
            m_CurrentCamera.KeyUp(sender, e);
        }

        void BufferViewer_KeyDown(object sender, KeyEventArgs e)
        {
            m_CurrentCamera.KeyDown(sender, e);
        }

        private void render_MouseWheel(object sender, MouseEventArgs e)
        {
            m_CurrentCamera.MouseWheel(sender, e);
        }

        private void render_MouseMove(object sender, MouseEventArgs e)
        {
            m_CurrentCamera.MouseMove(sender, e);
        }

        private void render_MouseClick(object sender, MouseEventArgs e)
        {
            m_CurrentCamera.MouseClick(sender, e);
        }

        private void render_MouseDown(object sender, MouseEventArgs e)
        {
            render.Focus();
        }

        private void camSpeed_ValueChanged(object sender, EventArgs e)
        {
            m_Arcball.SpeedMultiplier = m_Flycam.SpeedMultiplier = (float)camSpeed.Value;
        }

        private void controlType_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (controlType.SelectedIndex == 0)
            {
                m_CurrentCamera = m_Arcball;
                m_CurrentCamera.Reset(new Vec3f(0.0f, 0.0f, -10.0f));
            }
            else
            {
                m_CurrentCamera = m_Flycam;
                if (m_MeshDisplay.type == MeshDataStage.VSIn)
                {
                    m_CurrentCamera.Reset(new Vec3f(0.0f, 0.0f, -10.0f));
                }
                else
                {
                    if (m_Core.CurPipelineState.IsTessellationEnabled)
                        m_CurrentCamera.Reset(new Vec3f(0.0f, 0.0f, -10.0f));
                    else
                        m_CurrentCamera.Reset(new Vec3f(0.0f, 0.0f, 0.0f));
                }
            }

            UpdateHighlightVerts(GetUIState(m_MeshDisplay.type));

            m_CurrentCamera.Apply();
            render.Invalidate();
        }

        #endregion

        #region Handlers and Painting

        private void RT_UpdateRenderOutput(ReplayRenderer r)
        {
            if (m_Output == null) return;

            m_MeshDisplay.arcballCamera = m_Camera.IsArcball;
            m_MeshDisplay.cameraPos = new FloatVector(m_Camera.PositionParam);
            m_MeshDisplay.cameraRot = new FloatVector(m_Camera.RotationParam);

            m_Output.SetMeshDisplay(m_MeshDisplay);
        }

        private void bufferView_Paint(object sender, PaintEventArgs e)
        {
            Input input = GetUIState(sender).m_Input;

            if (input == null) return;

            uint i = 1;
            foreach (var el in input.BufferFormats)
            {
                UI_MergeColumns((DataGridView)sender, (int)i, el.format.compCount, e.Graphics);
                i += el.format.compCount;
            }
        }

        private void render_Paint(object sender, PaintEventArgs e)
        {
            if (m_Output == null || m_Core.Renderer == null)
            {
                e.Graphics.Clear(Color.Black);
                return;
            }

            m_Core.Renderer.Invoke((ReplayRenderer r) => { RT_UpdateRenderOutput(r);  if (m_Output != null) m_Output.Display(); });
        }

        private void BufferViewer_Load(object sender, EventArgs e)
        {
            if (m_Output == null && m_Core.LogLoaded)
            {
                OnLogfileLoaded();
            }

            matrixType.SelectedIndex = 0;
            configCamControls.Visible = false;
        }

        private void BufferViewer_FormClosed(object sender, FormClosedEventArgs e)
        {
            m_Core.RemoveLogViewer(this);

            m_Updater.Stop();
            m_Updater = null;

            m_ReqID++;
        }

        private void CSVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!m_Core.LogLoaded) return;

            csvSaveDialog.Title = "Saving CSV data for " + (m_ContextUIState == m_VSIn ? "VS Input" : "VS Output");
            DialogResult res = csvSaveDialog.ShowDialog();

            if (res == DialogResult.OK)
            {
                StreamWriter writer = File.CreateText(csvSaveDialog.FileName);

                if (MeshView)
                {
                    writer.Write("Vertex,");
                    writer.Write("Index,");
                }
                else
                {
                    writer.Write("Row,");
                }

                UIState ui = m_ContextUIState;
                for (int i = 0; i < ui.m_Input.BufferFormats.Length; i++)
                {
                    for (int j = 0; j < ui.m_Input.BufferFormats[i].format.compCount - 1; j++)
                        writer.Write(ui.m_Input.BufferFormats[i].name + " " + j + ",");
                    writer.Write(ui.m_Input.BufferFormats[i].name + " " + (ui.m_Input.BufferFormats[i].format.compCount - 1));

                    if (i < ui.m_Input.BufferFormats.Length - 1)
                        writer.Write(",");
                }

                writer.Write(Environment.NewLine);

                foreach (DataGridViewRow row in ui.m_GridView.Rows)
                {
                    for (int i = 0; i < row.Cells.Count; i++)
                    {
                        writer.Write(row.Cells[i].Value.ToString());
                        if (i < row.Cells.Count - 1)
                            writer.Write(",");
                    }

                    writer.Write(Environment.NewLine);
                }

                writer.Flush();
                writer.Close();
            }
        }

        private void rawToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!m_Core.LogLoaded) return;

            rawSaveDialog.Title = "Saving raw bytes for " + (m_ContextUIState == m_VSIn ? "VS Input" : "VS Output");
            DialogResult res = rawSaveDialog.ShowDialog();

            if (res == DialogResult.OK)
            {
                FileStream writer = File.Create(rawSaveDialog.FileName);

                UIState ui = m_ContextUIState;
                writer.Write(ui.m_RawData, 0, ui.m_RawData.Length);

                writer.Flush();
                writer.Close();
            }
        }

        private void meshStageDraw_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (previewTab.SelectedIndex == 0)
                m_MeshDisplay.type = MeshDataStage.VSIn;
            else if (previewTab.SelectedIndex == 1)
                m_MeshDisplay.type = MeshDataStage.VSOut;
            else if (previewTab.SelectedIndex == 2)
                m_MeshDisplay.type = MeshDataStage.GSOut;

            drawRange.Enabled = (m_MeshDisplay.type != MeshDataStage.VSIn);

            UI_UpdateMeshRenderComponents();

            if (previewTab.SelectedIndex == 0)
            {
                controlType.SelectedIndex = 0;
            }
            else if (previewTab.SelectedIndex == 1)
            {
                if (m_Core.CurPipelineState.IsTessellationEnabled)
                    controlType.SelectedIndex = 0;
                else
                    controlType.SelectedIndex = 1;
            }
            else if (previewTab.SelectedIndex == 2)
            {
                controlType.SelectedIndex = 1;
            }

            enableCameraControls();

            controlType_SelectedIndexChanged(sender, e);

            previewTable.Parent = previewTab.SelectedTab;
        }

        private int[] m_PosElement = new int[] { -1, -1, -1, -1 };
        private int[] m_SecondElement = new int[] { -1, -1, -1, -1 };
        private bool[] m_SecondShowAlpha = new bool[] { false, false, false, false };

        private int CurPosElement { get { return m_PosElement[(int)m_MeshDisplay.type]; } }
        private int CurSecondElement { get { return m_SecondElement[(int)m_MeshDisplay.type]; } }
        private bool CurSecondShowAlpha { get { return m_SecondShowAlpha[(int)m_MeshDisplay.type]; } }

        private FormatElement GetPosHighlightFormatElement(MeshDataStage stage)
        {
            var ui = GetUIState(stage);

            int idx = m_PosElement[(int)stage];

            if (ui.m_Input == null || ui.m_Input.BufferFormats == null ||
                idx == -1 || idx >= ui.m_Input.BufferFormats.Length)
                return null;

            return ui.m_Input.BufferFormats[idx];
        }

        private FormatElement GetSecondHighlightFormatElement(MeshDataStage stage)
        {
            var ui = GetUIState(stage);

            int idx = m_SecondElement[(int)stage];

            if (ui.m_Input == null || ui.m_Input.BufferFormats == null ||
                idx == -1 || idx >= ui.m_Input.BufferFormats.Length)
                return null;

            return ui.m_Input.BufferFormats[idx];
        }

        private void UI_AutoFetchRenderComponents(MeshDataStage stage, bool pos)
        {
            var ui = GetUIState(stage);

            if (pos)
            {
                int posEl = -1;

                if (ui.m_Input != null && ui.m_Input.BufferFormats != null)
                {
                    // prioritise system value over general "POSITION" string matching
                    for (int i = 0; i < ui.m_Input.BufferFormats.Length; i++)
                    {
                        FormatElement el = ui.m_Input.BufferFormats[i];

                        if (el.systemValue == SystemAttribute.Position)
                        {
                            posEl = i;
                            break;
                        }
                    }
                    for (int i = 0; posEl == -1 && i < ui.m_Input.BufferFormats.Length; i++)
                    {
                        FormatElement el = ui.m_Input.BufferFormats[i];

                        if (el.name.ToUpperInvariant() == "POSITION" ||
                            el.name.ToUpperInvariant() == "POSITION0" ||
                            el.name.ToUpperInvariant() == "POS" ||
                            el.name.ToUpperInvariant() == "POS0")
                        {
                            posEl = i;
                            break;
                        }
                    }
                }

                m_PosElement[(int)stage] = posEl;
            }
            else
            {
                int secondEl = -1;

                if (ui.m_Input != null && ui.m_Input.BufferFormats != null)
                {
                    // prioritise TEXCOORD over general COLOR
                    for (int i = 0; i < ui.m_Input.BufferFormats.Length; i++)
                    {
                        FormatElement el = ui.m_Input.BufferFormats[i];

                        if (el.name.ToUpperInvariant() == "TEXCOORD" ||
                            el.name.ToUpperInvariant() == "TEXCOORD0" ||
                            el.name.ToUpperInvariant() == "TEX" ||
                            el.name.ToUpperInvariant() == "TEX0" ||
                            el.name.ToUpperInvariant() == "UV" ||
                            el.name.ToUpperInvariant() == "UV0")
                        {
                            secondEl = i;
                            break;
                        }
                    }
                    for (int i = 0; secondEl == -1 && i < ui.m_Input.BufferFormats.Length; i++)
                    {
                        FormatElement el = ui.m_Input.BufferFormats[i];

                        if (el.name.ToUpperInvariant() == "COLOR" ||
                            el.name.ToUpperInvariant() == "COLOR0" ||
                            el.name.ToUpperInvariant() == "COL" ||
                            el.name.ToUpperInvariant() == "COL0")
                        {
                            secondEl = i;
                            break;
                        }
                    }
                }

                m_SecondElement[(int)stage] = secondEl;
            }
        }

        private void UI_UpdateMeshRenderComponents()
        {
            var ui = GetUIState(m_MeshDisplay.type);

            // set position data etc from postvs if relevant
            // also need to bake in drawcall offsets etc
            // set numVerts from drawcall or postvs data

            if (ui.m_Input == null || ui.m_Input.BufferFormats == null ||
                CurPosElement == -1 || CurPosElement >= ui.m_Input.BufferFormats.Length)
            {
                m_MeshDisplay.position.idxbuf = ResourceId.Null;
                m_MeshDisplay.position.idxoffs = 0;
                m_MeshDisplay.position.idxByteWidth = 0;

                m_MeshDisplay.position.buf = ResourceId.Null;
                m_MeshDisplay.position.offset = 0;
                m_MeshDisplay.position.stride = 0;

                m_MeshDisplay.position.compCount = 0;
                m_MeshDisplay.position.compByteWidth = 0;
                m_MeshDisplay.position.compType = FormatComponentType.None;
                m_MeshDisplay.position.specialFormat = SpecialFormat.Unknown;

                m_MeshDisplay.position.showAlpha = false;

                m_MeshDisplay.position.topo = PrimitiveTopology.Unknown;
                m_MeshDisplay.position.numVerts = 0;

                m_MeshDisplay.position.unproject = false;
                // near and far plane handled elsewhere
            }
            else
            {
                FormatElement pos = ui.m_Input.BufferFormats[CurPosElement];

                m_MeshDisplay.position.idxbuf = ResourceId.Null;
                m_MeshDisplay.position.idxoffs = 0;
                m_MeshDisplay.position.idxByteWidth = 0;

                m_MeshDisplay.position.buf = ResourceId.Null;
                m_MeshDisplay.position.offset = 0;
                m_MeshDisplay.position.stride = 0;

                m_MeshDisplay.position.compCount = pos.format.compCount;
                m_MeshDisplay.position.compByteWidth = pos.format.compByteWidth;
                m_MeshDisplay.position.compType = pos.format.compType;
                m_MeshDisplay.position.specialFormat = pos.format.special ? pos.format.specialFormat : SpecialFormat.Unknown;

                m_MeshDisplay.position.showAlpha = false;

                m_MeshDisplay.position.topo = PrimitiveTopology.Unknown;
                m_MeshDisplay.position.numVerts = 0;

                if (ui.m_Stage == MeshDataStage.VSIn && ui.m_Input.Drawcall != null)
                {
                    m_MeshDisplay.position.idxbuf = m_VSIn.m_Input.IndexBuffer;
                    m_MeshDisplay.position.idxoffs = m_VSIn.m_Input.IndexOffset +
                        ui.m_Input.Drawcall.indexOffset * ui.m_Input.Drawcall.indexByteWidth;
                    m_MeshDisplay.position.idxByteWidth = ui.m_Input.Drawcall.indexByteWidth;

                    m_MeshDisplay.position.buf = m_VSIn.m_Input.Buffers[pos.buffer];
                    m_MeshDisplay.position.offset = pos.offset + ui.m_Input.Offsets[pos.buffer] +
                        ui.m_Input.Drawcall.vertexOffset * ui.m_Input.Strides[pos.buffer];
                    m_MeshDisplay.position.stride = ui.m_Input.Strides[pos.buffer];

                    m_MeshDisplay.position.topo = ui.m_Input.Drawcall.topology;
                    m_MeshDisplay.position.numVerts = ui.m_Input.Drawcall.numIndices;
                }
                else if (ui.m_Stage != MeshDataStage.VSIn && ui.m_Data != null && ui.m_Data.PostVS.buf != ResourceId.Null)
                {
                    m_MeshDisplay.position.idxbuf = ui.m_Data.PostVS.idxbuf;
                    m_MeshDisplay.position.idxoffs = 0;
                    m_MeshDisplay.position.idxByteWidth = ui.m_Input.Drawcall.indexByteWidth;

                    m_MeshDisplay.position.buf = ui.m_Data.PostVS.buf;
                    m_MeshDisplay.position.offset = ui.m_Data.PostVS.offset + pos.offset;
                    m_MeshDisplay.position.stride = ui.m_Data.PostVS.stride;

                    m_MeshDisplay.position.topo = ui.m_Data.PostVS.topo;
                    m_MeshDisplay.position.numVerts = ui.m_Data.PostVS.numVerts;
                }

                if ((ui.m_Input.Drawcall.flags & DrawcallFlags.UseIBuffer) == 0)
                {
                    m_MeshDisplay.position.idxbuf = ResourceId.Null;
                    m_MeshDisplay.position.idxoffs = 0;
                    m_MeshDisplay.position.idxByteWidth = 0;
                }
                else
                {
                    m_MeshDisplay.position.idxByteWidth = Math.Max(1, m_MeshDisplay.position.idxByteWidth);
                }

                m_MeshDisplay.position.unproject = false;
                // near and far plane handled elsewhere

                if ((ui.m_Stage == MeshDataStage.VSOut && !m_Core.CurPipelineState.IsTessellationEnabled) || ui.m_Stage == MeshDataStage.GSOut)
                {
                    m_MeshDisplay.position.unproject = pos.systemValue == SystemAttribute.Position;
                }
            }

            if (ui.m_Input == null || ui.m_Input.BufferFormats == null ||
                CurSecondElement == -1 || CurSecondElement >= ui.m_Input.BufferFormats.Length)
            {
                m_MeshDisplay.secondary.idxbuf = ResourceId.Null;
                m_MeshDisplay.secondary.idxoffs = 0;
                m_MeshDisplay.secondary.idxByteWidth = 0;

                m_MeshDisplay.secondary.buf = ResourceId.Null;
                m_MeshDisplay.secondary.offset = 0;
                m_MeshDisplay.secondary.stride = 0;

                m_MeshDisplay.secondary.compCount = 0;
                m_MeshDisplay.secondary.compByteWidth = 0;
                m_MeshDisplay.secondary.compType = FormatComponentType.None;
                m_MeshDisplay.secondary.specialFormat = SpecialFormat.Unknown;

                m_MeshDisplay.secondary.showAlpha = false;

                m_MeshDisplay.secondary.topo = PrimitiveTopology.Unknown;
                m_MeshDisplay.secondary.numVerts = 0;

                m_MeshDisplay.secondary.unproject = false;
            }
            else
            {
                FormatElement tex = ui.m_Input.BufferFormats[CurSecondElement];

                m_MeshDisplay.secondary.compCount = tex.format.compCount;
                m_MeshDisplay.secondary.compByteWidth = tex.format.compByteWidth;
                m_MeshDisplay.secondary.compType = tex.format.compType;
                m_MeshDisplay.secondary.specialFormat = tex.format.special ? tex.format.specialFormat : SpecialFormat.Unknown;

                m_MeshDisplay.secondary.showAlpha = CurSecondShowAlpha;

                if (ui.m_Stage == MeshDataStage.VSIn && ui.m_Input.Drawcall != null)
                {
                    m_MeshDisplay.secondary.buf = m_VSIn.m_Input.Buffers[tex.buffer];
                    m_MeshDisplay.secondary.offset = tex.offset + ui.m_Input.Offsets[tex.buffer] +
                        ui.m_Input.Drawcall.vertexOffset * m_MeshDisplay.position.stride;
                    m_MeshDisplay.secondary.stride = ui.m_Input.Strides[tex.buffer];
                }
                else if (ui.m_Stage != MeshDataStage.VSIn && ui.m_Data != null && ui.m_Data.PostVS.buf != ResourceId.Null)
                {
                    m_MeshDisplay.secondary.buf = ui.m_Data.PostVS.buf;
                    m_MeshDisplay.secondary.offset = ui.m_Data.PostVS.offset + tex.offset;
                    m_MeshDisplay.secondary.stride = ui.m_Data.PostVS.stride;
                }
            }

            UI_UpdateAllColumns();
        }

        private void camGuess_PropChanged()
        {
            m_MeshDisplay.ortho = matrixType.SelectedIndex == 1;

            float fov = 90.0f;
            float.TryParse(fovGuess.Text, out fov);

            m_MeshDisplay.fov = fov;
            fovGuess.Text = m_MeshDisplay.fov.ToString("G");

            m_MeshDisplay.aspect = 1.0f;

            // take a guess for the aspect ratio, for if the user hasn't overridden it
            ResourceId depth = m_Core.CurPipelineState.GetDepthTarget();
            ResourceId[] targets = m_Core.CurPipelineState.GetOutputTargets();

            if (depth != ResourceId.Null || (targets != null && targets.Length > 0))
            {
                foreach (var t in m_Core.CurTextures)
                {
                    if (depth != ResourceId.Null && t.ID == depth)
                    {
                        m_MeshDisplay.aspect = (float)t.width / (float)t.height;
                        break;
                    }
                    if (depth == ResourceId.Null && targets != null && targets.Length > 0 && t.ID == targets[0])
                    {
                        m_MeshDisplay.aspect = (float)t.width / (float)t.height;
                        break;
                    }
                }
            }

            if (aspectGuess.Text.Length > 0 && float.TryParse(aspectGuess.Text, out m_MeshDisplay.aspect))
                aspectGuess.Text = m_MeshDisplay.aspect.ToString("G");
            else
                aspectGuess.Text = "";

            // use estimates from post vs data (calculated from vertex position data) if the user
            // hasn't overridden the values
            m_MeshDisplay.position.nearPlane = 0.1f;

            if (m_VSOut.m_Data != null && m_VSOut.m_Data.PostVS.buf != ResourceId.Null)
                m_MeshDisplay.position.nearPlane = m_VSOut.m_Data.PostVS.nearPlane;

            if (nearGuess.Text.Length > 0 && float.TryParse(nearGuess.Text, out m_MeshDisplay.position.nearPlane))
                nearGuess.Text = m_MeshDisplay.position.nearPlane.ToString("G");
            else
                nearGuess.Text = "";

            m_MeshDisplay.position.farPlane = 100.0f;

            if (m_VSOut.m_Data != null && m_VSOut.m_Data.PostVS.buf != ResourceId.Null)
                m_MeshDisplay.position.farPlane = m_VSOut.m_Data.PostVS.farPlane;

            if (farGuess.Text.Length > 0 && float.TryParse(farGuess.Text, out m_MeshDisplay.position.farPlane))
                farGuess.Text = m_MeshDisplay.position.farPlane.ToString("G");
            else
                farGuess.Text = "";

            render.Invalidate();
        }

        private void camGuess_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r' || e.KeyChar == '\n')
            {
                camGuess_PropChanged();
                e.Handled = true;
            }
        }

        private void matrixType_SelectedIndexChanged(object sender, EventArgs e)
        {
            camGuess_PropChanged();
        }

        private void drawRange_SelectedIndexChanged(object sender, EventArgs e)
        {
            m_MeshDisplay.thisDrawOnly = (drawRange.SelectedIndex == 0);

            render.Invalidate();
        }

        private void solidShading_SelectedIndexChanged(object sender, EventArgs e)
        {
            m_MeshDisplay.solidShadeMode = (SolidShadeMode)solidShading.SelectedIndex;

            if (solidShading.SelectedIndex == 0 && !wireframeDraw.Checked)
                wireframeDraw.Checked = true;

            UI_UpdateAllColumns();

            render.Invalidate();
        }

        private void wireframeDraw_CheckedChanged(object sender, EventArgs e)
        {
            if (solidShading.SelectedIndex == 0 && !wireframeDraw.Checked)
            {
                wireframeDraw.Checked = true;
                return;
            }

            m_MeshDisplay.wireframeDraw = wireframeDraw.Checked;

            render.Invalidate();
        }

        private void instanceIdx_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                instanceIdxToolitem.Text = instanceIdx.Text;
                rightclickMenu.Close();
            }
        }

        private void instanceIdxToolitem_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!m_Core.LogLoaded) return;

            if (e.KeyChar == '\r' || e.KeyChar == '\n')
            {
                instanceIdx.Text = instanceIdxToolitem.Text;
                uint inst = 0;
                if (uint.TryParse(instanceIdxToolitem.Text, out inst))
                {
                    if (inst != m_MeshDisplay.curInstance && inst >= 0 && m_Core.CurDrawcall != null && inst < m_Core.CurDrawcall.numInstances)
                    {
                        m_MeshDisplay.curInstance = inst;


                        OnEventSelected(m_Core.CurFrame, m_Core.CurEvent);
                    }
                }
            }
        }

        private int m_ContextColumn = 0;

        private void resetSelectedColumnsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            UI_AutoFetchRenderComponents(m_ContextUIState.m_Stage, true);
            UI_AutoFetchRenderComponents(m_ContextUIState.m_Stage, false);

            UI_UpdateMeshRenderComponents();
            render.Invalidate();
        }

        private void selectColumnAsPositionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_PosElement[(int)m_ContextUIState.m_Stage] = m_ContextColumn;

            UI_UpdateMeshRenderComponents();
            render.Invalidate();
        }

        private void selectColumnAsSecondaryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_SecondElement[(int)m_ContextUIState.m_Stage] = m_ContextColumn;
            m_SecondShowAlpha[(int)m_ContextUIState.m_Stage] = false;

            UI_UpdateMeshRenderComponents();
            render.Invalidate();
        }

        private void selectAlphaAsSecondaryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_SecondElement[(int)m_ContextUIState.m_Stage] = m_ContextColumn;
            m_SecondShowAlpha[(int)m_ContextUIState.m_Stage] = true;

            UI_UpdateMeshRenderComponents();
            render.Invalidate();
        }

        private void bufferView_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (m_Core.LogLoaded && MeshView)
            {
                m_ContextUIState = GetUIState(sender);

                if (e.Button == MouseButtons.Right &&
                    m_ContextUIState.m_Input != null &&
                    m_ContextUIState.m_Input.BufferFormats != null)
                {
                    selectColumnAsPositionToolStripMenuItem.Visible = true;
                    selectAlphaAsSecondaryToolStripMenuItem.Visible = true;

                    m_ContextColumn = 0;
                    int colidx = 2; // skip VTX and IDX columns

                    for (int el = 0; el < m_ContextUIState.m_Input.BufferFormats.Length; el++)
                    {
                        for (int i = 0; i < m_ContextUIState.m_Input.BufferFormats[el].format.compCount; i++)
                        {
                            if (colidx == e.ColumnIndex)
                            {
                                m_ContextColumn = el;
                                selectAlphaAsSecondaryToolStripMenuItem.Visible = (m_ContextUIState.m_Input.BufferFormats[el].format.compCount >= 4);
                            }

                            colidx++;
                        }
                    }

                    columnContextMenu.Show(Cursor.Position);
                }
            }
        }

        private void bufferView_MouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (m_Core.LogLoaded && e.RowIndex >= 0)
            {
                m_ContextUIState = GetUIState(sender);

                if (e.Button == MouseButtons.Right)
                {
                    openFormat.Visible = !MeshView;

                    debugVertex.Visible = MeshView &&
                        m_Core.LogLoaded &&
                        sender == vsInBufferView &&
                        vsInBufferView.SelectedRows.Count == 1;
                    setInstanceToolStripMenuItem.Enabled = (m_Core.CurDrawcall != null && m_Core.CurDrawcall.numInstances > 1);

                    rightclickMenu.Show(Cursor.Position);
                }
            }
        }

        private void rightclickMenu_Closed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            uint inst = 0;
            if (uint.TryParse(instanceIdx.Text, out inst))
            {
                if (inst != m_MeshDisplay.curInstance && inst >= 0 && m_Core.CurDrawcall != null && inst < m_Core.CurDrawcall.numInstances)
                {
                    m_MeshDisplay.curInstance = inst;

                    OnEventSelected(m_Core.CurFrame, m_Core.CurEvent);
                }
            }
        }

        private void syncViewsToolItem_Click(object sender, EventArgs e)
        {
            syncViews.Checked = syncViewsToolItem.Checked;
        }

        private void syncViews_Click(object sender, EventArgs e)
        {
            syncViewsToolItem.Checked = syncViews.Checked;
        }

        private void UpdateRowOffset()
        {
            ScrollToRow(vsInBufferView, RowOffset);
            ScrollToRow(vsOutBufferView, RowOffset);
            ScrollToRow(gsOutBufferView, RowOffset);
        }

        private void rowOffset_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\n' || e.KeyChar == '\r')
            {
                rowOffset.Text = RowOffset.ToString();

                UpdateRowOffset();
            }
        }

        private void byteOffset_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\n' || e.KeyChar == '\r')
            {
                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    var contents = RT_FetchBufferContents(MeshDataStage.VSIn, r, m_VSIn.m_Input);

                    this.BeginInvoke(new Action(() =>
                    {
                        if (m_VSIn.m_Input != null)
                        {
                            UI_SetRowsData(MeshDataStage.VSIn, contents, 0);
                        }
                    }));
                });
            }
        }

        private void offsetCancel_Click(object sender, EventArgs e)
        {
            rowOffset.Text = "0";

            UpdateRowOffset();
        }

        private void openFormat_Click(object sender, EventArgs e)
        {
            ShowFormatSpecifier();
        }

        public void ProcessBufferFormat(string formatText)
        {
            ResourceId id = GetUIState(MeshDataStage.VSIn).m_Input.Buffers[0];
            bool isBuffer = true;

            if (id == ResourceId.Null)
            {
                isBuffer = false;
                id = GetUIState(MeshDataStage.VSIn).m_Input.Buffers[1];
            }

            ViewRawBuffer(isBuffer, id, formatText);
        }

        private void ShowFormatSpecifier()
        {
			if (m_FormatSpecifier == null)
			{
				m_FormatSpecifier = new BufferFormatSpecifier(this, m_FormatText);

				var dock = Helpers.WrapDockContent(dockPanel, m_FormatSpecifier, m_FormatSpecifier.Text);
				dock.CloseButton = false;
				dock.CloseButtonVisible = false;
			}

            (m_FormatSpecifier.Parent as DockContent).Show(dockPanel, DockState.DockBottom);
        }

        private void debugVertex_Click(object sender, EventArgs e)
        {
            if (!m_Core.LogLoaded) return;

            ShaderReflection shaderDetails = null;
            ShaderDebugTrace trace = null;

            shaderDetails = m_Core.CurPipelineState.GetShaderReflection(ShaderStageType.Vertex);

            UIState ui = GetUIState(MeshDataStage.VSIn);

            if (ui.m_GridView.SelectedRows.Count == 0)
            {
                MessageBox.Show("You must select a vertex to debug", "No Vertex Selected",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int row = ui.m_GridView.SelectedRows[0].Index;

            if (row >= ui.m_Rows.Length || ui.m_Rows[row].Length <= 1) return;

            UInt32 idx = (UInt32)ui.m_Rows[row][1];

            var draw = m_Core.CurDrawcall;

            m_Core.Renderer.Invoke((ReplayRenderer r) =>
            {
                trace = r.DebugVertex((UInt32)row, m_MeshDisplay.curInstance, idx, draw.instanceOffset, draw.vertexOffset);
            });

            this.BeginInvoke(new Action(() =>
            {
                string debugContext = String.Format("Vertex {0}", row);
                if (draw.numInstances > 1)
                    debugContext += String.Format(", Instance {0}", m_MeshDisplay.curInstance);

                ShaderViewer s = new ShaderViewer(m_Core, shaderDetails, ShaderStageType.Vertex, trace, debugContext);

                s.Show(this.DockPanel);
            }));
        }

        private bool selectNoRecurse = false;

        private void bufferView_SelectionChanged(object sender, EventArgs e)
        {
            if (selectNoRecurse) return;

            selectNoRecurse = true;

            if (syncViews.Checked)
            {
                if (vsInBufferView.Focused && vsInBufferView.SelectedRows.Count > 0)
                {
                    vsOutBufferView.ClearSelection();
                    if (vsInBufferView.SelectedRows.Count == 1 && vsInBufferView.SelectedRows[0].Index < vsOutBufferView.Rows.Count)
                        vsOutBufferView.Rows[vsInBufferView.SelectedRows[0].Index].Selected = true;

                    gsOutBufferView.ClearSelection();
                    if (vsInBufferView.SelectedRows.Count == 1 && vsInBufferView.SelectedRows[0].Index < gsOutBufferView.Rows.Count)
                        gsOutBufferView.Rows[vsInBufferView.SelectedRows[0].Index].Selected = true;
                }
                if (vsOutBufferView.Focused && vsOutBufferView.SelectedRows.Count > 0)
                {
                    vsInBufferView.ClearSelection();
                    if (vsOutBufferView.SelectedRows.Count == 1 && vsOutBufferView.SelectedRows[0].Index < vsInBufferView.Rows.Count)
                        vsInBufferView.Rows[vsOutBufferView.SelectedRows[0].Index].Selected = true;

                    gsOutBufferView.ClearSelection();
                    if (vsOutBufferView.SelectedRows.Count == 1 && vsOutBufferView.SelectedRows[0].Index < gsOutBufferView.Rows.Count)
                        gsOutBufferView.Rows[vsOutBufferView.SelectedRows[0].Index].Selected = true;
                }
                if (gsOutBufferView.Focused && gsOutBufferView.SelectedRows.Count > 0)
                {
                    vsInBufferView.ClearSelection();
                    if (gsOutBufferView.SelectedRows.Count == 1 && gsOutBufferView.SelectedRows[0].Index < vsInBufferView.Rows.Count)
                        vsInBufferView.Rows[gsOutBufferView.SelectedRows[0].Index].Selected = true;

                    vsOutBufferView.ClearSelection();
                    if (gsOutBufferView.SelectedRows.Count == 1 && gsOutBufferView.SelectedRows[0].Index < vsOutBufferView.Rows.Count)
                        vsOutBufferView.Rows[gsOutBufferView.SelectedRows[0].Index].Selected = true;
                }
            }

            if (vsInBufferView.Focused && m_Core.LogLoaded)
            {
                debugVertex.Enabled = debugVertexToolItem.Enabled = true;
            }
            else
            {
                debugVertex.Enabled = debugVertexToolItem.Enabled = false;
            }

            if (!MeshView)
            {
                debugVertex.Enabled = debugVertexToolItem.Visible = debugSep.Visible = false;
            }

            UpdateHighlightVerts(GetUIState(m_MeshDisplay.type));

            selectNoRecurse = false;
        }

        private void bufferView_Scroll(object sender, ScrollEventArgs e)
        {
            if (syncViews.Checked)
            {
                if (vsInBufferView.Focused)
                {
                    if (vsOutBufferView.RowCount > 0 && vsOutBufferView.RowCount > vsInBufferView.FirstDisplayedScrollingRowIndex)
                        vsOutBufferView.FirstDisplayedScrollingRowIndex = vsInBufferView.FirstDisplayedScrollingRowIndex;
                    if (gsOutBufferView.RowCount > 0 && gsOutBufferView.RowCount > vsInBufferView.FirstDisplayedScrollingRowIndex)
                        gsOutBufferView.FirstDisplayedScrollingRowIndex = vsInBufferView.FirstDisplayedScrollingRowIndex;
                }
                if (vsOutBufferView.Focused)
                {
                    if (vsInBufferView.RowCount > 0 && vsInBufferView.RowCount > vsOutBufferView.FirstDisplayedScrollingRowIndex)
                        vsInBufferView.FirstDisplayedScrollingRowIndex = vsOutBufferView.FirstDisplayedScrollingRowIndex;
                    if (gsOutBufferView.RowCount > 0 && gsOutBufferView.RowCount > vsOutBufferView.FirstDisplayedScrollingRowIndex)
                        gsOutBufferView.FirstDisplayedScrollingRowIndex = vsOutBufferView.FirstDisplayedScrollingRowIndex;
                }
                if (gsOutBufferView.Focused)
                {
                    if (vsOutBufferView.RowCount > 0 && vsOutBufferView.RowCount > gsOutBufferView.FirstDisplayedScrollingRowIndex)
                        vsOutBufferView.FirstDisplayedScrollingRowIndex = gsOutBufferView.FirstDisplayedScrollingRowIndex;
                    if (vsInBufferView.RowCount > 0 && vsInBufferView.RowCount > gsOutBufferView.FirstDisplayedScrollingRowIndex)
                        vsInBufferView.FirstDisplayedScrollingRowIndex = gsOutBufferView.FirstDisplayedScrollingRowIndex;
                }
            }
        }

        #endregion

        #region Vertex Highlighting

        private void highlightVerts_CheckedChanged(object sender, EventArgs e)
        {
            UpdateHighlightVerts(GetUIState(m_MeshDisplay.type));
        }

        private void ClearHighlightVerts()
        {
            m_MeshDisplay.highlightVert = ~0U;
            render.Invalidate();
        }

        private void UpdateHighlightVerts(UIState ui)
        {
            if (ui == null || ui.m_RawData == null) return;
            if (ui.m_GridView.SelectedRows.Count == 0) return;
            if (!MeshView) return;

            if(highlightVerts.Checked)
                m_MeshDisplay.highlightVert = (uint)ui.m_GridView.SelectedRows[0].Index;
            else
                m_MeshDisplay.highlightVert = ~0U;

            render.Invalidate();
        }

        #endregion

        private void bufferView_EnterLeave(object sender, EventArgs e)
        {
            if (vsInBufferView.Focused && m_Core.LogLoaded)
            {
                debugVertex.Enabled = debugVertexToolItem.Enabled = true;
            }
            else
            {
                debugVertex.Enabled = debugVertexToolItem.Enabled = false;
            }
        }
    }
}
