using PCB;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace EasyEDA_Loader
{
    internal class EEPCB
    {
        public const double CourtyardMarginMm = 0.25;
        private const double MechanicalLineWidthMm = 0.1;
        private const double OverlayLineWidthMm = 0.2;
        private const double AssemblyTextSizeMm = 1.5;
        private const string AssemblyTextFontName = "ARIAL";
        private const double AssemblyTextAverageCharWidthFactor = 0.45;
        private const double AssemblyTextVisibleHeightFactor = 0.675;
        private const double AssemblyTextGapMm = 0.2;
        private const double AssemblyTextKeepoutMm = 0.05;
        private const double AssemblyTextSearchStepMm = 0.025;

        public class LayerMapException : Exception
        {
            public LayerMapException(string message) : base(message)
            {

            }
        }
        public static IPCB_LibComponent CreateFootprintInLib(string name, string description, double heightMm = 0)
        {
            var pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib == null) return null;
            var footprint = pcbLib.CreateNewComponent();
            pcbLib.SetState_CurrentComponent(footprint);
            var uid = pcbLib.GetUniqueCompName(name);
            footprint.SetState_Pattern(uid);
            SetFootprintMetadata(footprint, description, heightMm);
            AltiumApi.GlobalVars.PCBServer.PostProcess();
            return footprint;
        }

        public static void SetFootprintMetadata(IPCB_LibComponent footprint, string description, double heightMm = 0)
        {
            if (footprint == null)
                return;

            if (!string.IsNullOrWhiteSpace(description))
                footprint.SetState_Description(description);

            if (heightMm > 0)
                footprint.SetState_Height(AltiumApi.MmToCoord(heightMm));
        }

        public static string GetComponentPattern(object footprint)
        {
            if (footprint == null)
                return "";

            return TryInvokeResult(footprint, "GetState_Pattern") as string
                ?? TryInvokeResult(footprint, "GetState_Name") as string
                ?? "";
        }

        public static IPCB_Group GetCurrentPcbLibComponent()
        {
            var pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib == null)
                return null;

            IPCB_Group currentComponent = CoercePcbLibComponent(
                pcbLib,
                TryInvokeResult(pcbLib, "GetState_CurrentComponent")
                    ?? TryInvokeResult(pcbLib, "Internal_GetState_CurrentComponent"));
            if (currentComponent != null)
                return currentComponent;

            return FindCurrentPcbLibComponentFallback(pcbLib);
        }

        private static IPCB_Group FindCurrentPcbLibComponentFallback(IPCB_Library pcbLib)
        {
            if (pcbLib == null)
                return null;

            IPCB_Board activeBoard = TryInvokeResult(pcbLib, "GetState_Board") as IPCB_Board
                ?? TryInvokeResult(pcbLib, "Internal_GetState_Board") as IPCB_Board;
            IPCB_Group firstComponent = null;
            IPCB_Group onlyComponent = null;
            int componentCount = 0;

            foreach (IPCB_Group component in EnumeratePcbLibComponents(pcbLib))
            {
                componentCount++;
                if (firstComponent == null)
                    firstComponent = component;
                onlyComponent = component;

                if (ComponentUsesBoard(component, activeBoard))
                {
                    SetCurrentPcbLibComponent(pcbLib, component);
                    return component;
                }
            }

            if (componentCount == 1)
            {
                SetCurrentPcbLibComponent(pcbLib, onlyComponent);
                return onlyComponent;
            }

            if (TryConvertToInt(TryInvokeResult(pcbLib, "ComponentCount"), out int count) && count == 1 && firstComponent != null)
            {
                SetCurrentPcbLibComponent(pcbLib, firstComponent);
                return firstComponent;
            }

            return null;
        }

        private static IPCB_Group CoercePcbLibComponent(IPCB_Library pcbLib, object component)
        {
            if (component == null)
                return null;

            if (component is IPCB_Group group)
                return group;

            string pattern = TryInvokeResult(component, "GetState_Pattern") as string;
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = TryInvokeResult(component, "GetState_Name") as string;

            if (string.IsNullOrWhiteSpace(pattern))
                return null;

            return TryInvokeResult(pcbLib, "GetComponentByName", pattern) as IPCB_Group
                ?? TryInvokeResult(pcbLib, "Internal_GetComponentByName", pattern) as IPCB_Group;
        }

        private static void SetCurrentPcbLibComponent(IPCB_Library pcbLib, IPCB_Group component)
        {
            if (pcbLib == null || component == null)
                return;

            TryInvoke(pcbLib, "SetState_CurrentComponent", component);
        }

        private static IEnumerable<IPCB_Group> EnumeratePcbLibComponents(IPCB_Library pcbLib)
        {
            object iterator = null;
            try
            {
                iterator = TryInvokeResult(pcbLib, "LibraryIterator_Create")
                    ?? TryInvokeResult(pcbLib, "Internal_LibraryIterator_Create");
                if (iterator == null)
                    yield break;

                TryInvoke(iterator, "SetState_FilterAll");
                object component = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (component != null)
                {
                    IPCB_Group libComponent = CoercePcbLibComponent(pcbLib, component);
                    if (libComponent != null)
                        yield return libComponent;

                    component = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(pcbLib, "LibraryIterator_Destroy", iterator);
            }
        }

        private static bool ComponentUsesBoard(IPCB_Group component, IPCB_Board activeBoard)
        {
            if (component == null || activeBoard == null)
                return false;

            IPCB_Board componentBoard = GetComponentBoard(component);
            if (componentBoard == null)
                return false;

            if (ReferenceEquals(componentBoard, activeBoard))
                return true;

            return TryGetBoardId(componentBoard, out int componentBoardId)
                && TryGetBoardId(activeBoard, out int activeBoardId)
                && componentBoardId == activeBoardId;
        }

        private static IPCB_Board GetComponentBoard(object component)
        {
            if (component == null)
                return null;

            try
            {
                if (component is IPCB_LibComponent libComponent)
                    return libComponent.GetState_Board();
            }
            catch
            {
            }

            return TryInvokeResult(component, "GetState_Board") as IPCB_Board
                ?? TryInvokeResult(component, "Internal_GetState_Board") as IPCB_Board;
        }

        private static IPCB_Board GetCurrentPcbLibraryBoard()
        {
            IPCB_Library pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib == null)
                return null;

            return TryInvokeResult(pcbLib, "GetState_Board") as IPCB_Board
                ?? TryInvokeResult(pcbLib, "Internal_GetState_Board") as IPCB_Board;
        }

        public static bool SwitchToTopSignalLayer()
        {
            return SwitchToFirstDisplayedSignalLayer();
        }

        public static bool SwitchToBottomSignalLayer()
        {
            return SwitchToLastDisplayedSignalLayer();
        }

        public static bool SwitchToNextSignalLayer()
        {
            return SwitchToAdjacentSignalLayer(true);
        }

        public static bool SwitchToPreviousSignalLayer()
        {
            return SwitchToAdjacentSignalLayer(false);
        }

        public static bool SwitchToSelectedPrimitiveLayer()
        {
            IPCB_Board board = GetCurrentPcbBoard();
            if (board == null)
                return false;

            return FindSelectedPrimitiveLayer(board, out IV7_Layer layer)
                && SwitchToSignalLayer(board, layer);
        }

        public static IPCB_Board GetCurrentPcbBoard(DXP.IServerDocumentView commandView = null)
        {
            return GetPcbBoardFromView(commandView)
                ?? TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "GetCurrentPCBBoard") as IPCB_Board
                ?? TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_GetCurrentPCBBoard") as IPCB_Board
                ?? GetPcbBoardFromCurrentView();
        }

        private static IPCB_Board GetPcbBoardFromCurrentView()
        {
            object viewObject = TryInvokeResult(AltiumApi.GlobalVars.Client, "GetCurrentView")
                ?? TryInvokeResult(AltiumApi.GlobalVars.Client, "Internal_GetCurrentView");
            return GetPcbBoardFromView(viewObject as DXP.IServerDocumentView);
        }

        private static IPCB_Board GetPcbBoardFromView(DXP.IServerDocumentView commandView)
        {
            if (commandView == null)
                return null;

            object document = TryInvokeResult(commandView, "Internal_GetOwnerDocument");
            string path = Convert.ToString(TryInvokeResult(document, "GetFileName"));
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_GetPCBBoardByPath", path) as IPCB_Board
                ?? TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_LoadPCBBoardByPath", path) as IPCB_Board;
        }

        private static bool SwitchToFirstDisplayedSignalLayer()
        {
            IPCB_Board board = GetCurrentPcbBoard();
            if (board == null)
                return false;

            List<IV7_Layer> layers = GetDisplayedSignalLayers(board);
            if (layers.Count > 0)
                return SwitchToSignalLayer(board, layers[0]);

            return SwitchToLayerStackSignalLayer(board, true);
        }

        private static bool SwitchToLastDisplayedSignalLayer()
        {
            IPCB_Board board = GetCurrentPcbBoard();
            if (board == null)
                return false;

            List<IV7_Layer> layers = GetDisplayedSignalLayers(board);
            if (layers.Count > 0)
                return SwitchToSignalLayer(board, layers[layers.Count - 1]);

            return SwitchToLayerStackSignalLayer(board, false);
        }

        private static bool SwitchToAdjacentSignalLayer(bool forward)
        {
            IPCB_Board board = GetCurrentPcbBoard();
            if (board == null)
                return false;

            List<IV7_Layer> layers = GetDisplayedSignalLayers(board);
            if (layers.Count == 0)
                return false;

            IV7_Layer currentLayer = TryInvokeResult(board, "Internal_GetState_CurrentLayerV7") as IV7_Layer;
            int currentIndex = IndexOfLayer(layers, currentLayer);
            int targetIndex;
            if (currentIndex < 0)
                targetIndex = forward ? 0 : layers.Count - 1;
            else if (forward)
                targetIndex = (currentIndex + 1) % layers.Count;
            else
                targetIndex = (currentIndex + layers.Count - 1) % layers.Count;

            return SwitchToSignalLayer(board, layers[targetIndex]);
        }

        private static object GetLayerStack(IPCB_Board board)
        {
            if (board == null)
                return null;

            return TryInvokeResult(board, "Internal_GetState_LayerStack_V7")
                ?? TryInvokeResult(board, "Internal_GetState_LayerStack");
        }

        private static bool SwitchToLayerStackSignalLayer(IPCB_Board board, bool top)
        {
            object layerStack = GetLayerStack(board);
            IV7_Layer layer = top
                ? TryInvokeResult(layerStack, "Internal_GetState_TopSignalLayer") as IV7_Layer
                : TryInvokeResult(layerStack, "Internal_GetState_BottomSignalLayer") as IV7_Layer;

            return SwitchToSignalLayer(board, layer);
        }

        private static List<IV7_Layer> GetDisplayedSignalLayers(IPCB_Board board)
        {
            var layers = new List<IV7_Layer>();
            if (board == null)
                return layers;

            object iterator = TryInvokeResult(board, "Internal_SignalLayerIterator");
            if (iterator == null)
                return layers;

            TryInvoke(iterator, "SetBeforeFirst");
            if (!TryConvertToBool(TryInvokeResult(iterator, "First"), out bool hasLayer) || !hasLayer)
                return layers;

            do
            {
                IV7_Layer layer = TryInvokeResult(iterator, "Internal_Layer") as IV7_Layer;
                if (layer != null && IsLayerDisplayed(board, layer))
                    layers.Add(layer);
            }
            while (TryConvertToBool(TryInvokeResult(iterator, "Next"), out hasLayer) && hasLayer);

            return layers;
        }

        private static bool IsLayerDisplayed(IPCB_Board board, IV7_Layer layer)
        {
            if (board == null || layer == null)
                return false;

            return TryConvertToBool(TryInvokeResult(board, "GetState_LayerIsDisplayed", layer), out bool displayed)
                && displayed;
        }

        private static bool FindSelectedPrimitiveLayer(IPCB_Board board, out IV7_Layer layer)
        {
            layer = null;
            foreach (object primitive in GetSelectedPrimitiveObjects(board))
            {
                if (TryGetPrimitiveLayer(primitive, out layer, out int layerNumber) && layer != null)
                    return true;
            }

            return false;
        }

        private static List<object> GetSelectedPrimitiveObjects(IPCB_Board board)
        {
            var result = new List<object>();
            var seen = new HashSet<object>();
            if (board == null)
                return result;

            int selectedCount = GetSelectedObjectCount(board);
            for (int index = 0; index < selectedCount; index++)
                AddSelectedPrimitiveObject(result, seen, TryInvokeResult(board, "Internal_GetState_SelectecObject", index));

            for (int index = 1; index <= selectedCount; index++)
                AddSelectedPrimitiveObject(result, seen, TryInvokeResult(board, "Internal_GetState_SelectecObject", index));

            if (result.Count > 0)
                return result;

            foreach (object primitive in EnumerateBoardPrimitives(board))
            {
                if (TryConvertToBool(TryInvokeResult(primitive, "GetState_Selected"), out bool selected) && selected)
                    AddSelectedPrimitiveObject(result, seen, primitive);
            }

            return result;
        }

        private static void AddSelectedPrimitiveObject(List<object> result, HashSet<object> seen, object primitive)
        {
            if (!(primitive is IPCB_Primitive))
                return;

            if (seen.Add(primitive))
                result.Add(primitive);
        }

        private static int IndexOfLayer(List<IV7_Layer> layers, IV7_Layer target)
        {
            int targetNumber = LayerNumber(target);
            if (targetNumber < 0)
                return -1;

            for (int i = 0; i < layers.Count; i++)
            {
                if (LayerNumber(layers[i]) == targetNumber)
                    return i;
            }

            return -1;
        }

        private static int LayerNumber(IV7_Layer layer)
        {
            if (layer == null)
                return -1;

            try
            {
                return new V7_Layer(layer).Number();
            }
            catch
            {
                return -1;
            }
        }

        private static bool SwitchToSignalLayer(IPCB_Board board, IV7_Layer layer)
        {
            if (board == null || layer == null)
                return false;

            TryInvoke(board, "SetState_CurrentLayerV7", layer);
            TryInvoke(board, "ViewManager_UpdateLayerTabs");
            LaunchPcbCommand("PCB:Zoom", "Action=Redraw");
            return true;
        }

        private static void AddDistinctBoard(List<IPCB_Board> boards, IPCB_Board board)
        {
            if (boards == null || board == null)
                return;

            foreach (IPCB_Board existingBoard in boards)
            {
                if (ReferenceEquals(existingBoard, board))
                    return;

                if (TryGetBoardId(existingBoard, out int existingBoardId)
                    && TryGetBoardId(board, out int boardId)
                    && existingBoardId == boardId)
                    return;
            }

            boards.Add(board);
        }

        public static IPCB_Board GetPcbGroupBoard(IPCB_Group component)
        {
            return GetComponentBoard(component);
        }

        private static bool TryGetBoardId(IPCB_Board board, out int boardId)
        {
            boardId = 0;
            if (board == null)
                return false;

            return TryConvertToInt(TryInvokeResult(board, "GetState_BoardID"), out boardId)
                && boardId != 0;
        }

        public static void AddToPCB(IPCB_Group c, object obj)
        {
            if (c == null || obj == null)
                return;

            GetComponentBoard(c)?.AddPCBObject(obj);
            c.AddPCBObject(obj);
        }

        private static void AddToPcbLibComponent(IPCB_Group c, object obj)
        {
            if (c == null || obj == null)
                return;

            c.AddPCBObject(obj);
        }

        public static TLayerConstant EELayerToAltium(string layer)
        {
            return EELayerToAltium(layer, true);
        }

        public static TLayerConstant EELayerToAltium(string layer, bool importLcscMechanicalLayers)
        {
            if (TryEELayerToAltium(layer, importLcscMechanicalLayers, out TLayerConstant altiumLayer))
                return altiumLayer;

            throw new LayerMapException($"Skipped layer {layer}");
        }

        public static bool TryEELayerToAltium(string layer, bool importLcscMechanicalLayers, out TLayerConstant altiumLayer, bool importAdditionalLayers = false)
        {
            layer = FootprintLayerMap.NormalizeLayerName(layer, importLcscMechanicalLayers, importAdditionalLayers);
            if (layer == null)
            {
                altiumLayer = default(TLayerConstant);
                return false;
            }

            switch (layer)
            {
                case "TopLayer": altiumLayer = TLayerConstant.eTopLayer; return true;
                case "BottomLayer": altiumLayer = TLayerConstant.eBottomLayer; return true;
                case "TopSilkLayer": altiumLayer = TLayerConstant.eTopOverlay; return true;
                case "BottomSilkLayer": altiumLayer = TLayerConstant.eBottomOverlay; return true;
                case "TopPasteMaskLayer": altiumLayer = TLayerConstant.eTopPaste; return true;
                case "BottomPasteMaskLayer": altiumLayer = TLayerConstant.eBottomPaste; return true;
                case "TopSolderMaskLayer": altiumLayer = TLayerConstant.eTopSolder; return true;
                case "BottomSolderMaskLayer": altiumLayer = TLayerConstant.eBottomSolder; return true;
                case "BoardOutline": altiumLayer = TLayerConstant.eMechanical3; return true;
                case "Multi-Layer": altiumLayer = TLayerConstant.eMultiLayer; return true;
                case "TopAssembly": altiumLayer = TLayerConstant.eMechanical2; return true;
                case "Mechanical": altiumLayer = TLayerConstant.eMechanical2; return true;
                case "3DModel": altiumLayer = TLayerConstant.eMechanical1; return true;
                default: throw new LayerMapException($"Invalid layer {layer}");
            }
        }

        public static double NormalizeLineWidth(TLayerConstant layer, double width)
        {
            if (layer == TLayerConstant.eTopOverlay || layer == TLayerConstant.eBottomOverlay)
                return Math.Max(OverlayLineWidthMm, NormalizeImperialWidth(width));

            if (layer == TLayerConstant.eMechanical2 || layer == TLayerConstant.eMechanical3)
                return Math.Max(MechanicalLineWidthMm, NormalizeImperialWidth(width));

            return width;
        }

        private static double NormalizeImperialWidth(double width)
        {
            if (width <= 0)
                return width;

            double imperialMultiple = width / 0.254;
            double roundedMultiple = Math.Round(imperialMultiple);
            if (roundedMultiple >= 1 && Math.Abs(imperialMultiple - roundedMultiple) < 0.05)
                return 0.2 * roundedMultiple;

            return width;
        }

        public static IPCB_Track CreateLine(IPCB_Group c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
        {
            var track = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eTrackObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Track;
            if (track == null) return null;
            width = NormalizeLineWidth(layer, width);
            track.SetState_Width(AltiumApi.MmToCoord(width));
            track.SetState_V7Layer(new V7_Layer(layer));
            track.SetState_X1(AltiumApi.MmToCoord(x1) + c.GetState_XLocation());
            track.SetState_X2(AltiumApi.MmToCoord(x2) + c.GetState_XLocation());
            track.SetState_Y1(AltiumApi.MmToCoord(y1) + c.GetState_YLocation());
            track.SetState_Y2(AltiumApi.MmToCoord(y2) + c.GetState_YLocation());
            return track;
        }

        public static IPCB_Arc CreateArc(IPCB_Group c, TLayerConstant layer, double x, double y, double rad, double width, double startAngle, double endAngle)
        {
            var circle = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eArcObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Arc;
            if (circle == null) return null;
            width = NormalizeLineWidth(layer, width);
            circle.SetState_CenterX(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            circle.SetState_CenterY(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            circle.SetState_Radius(AltiumApi.MmToCoord(rad));
            circle.SetState_LineWidth(AltiumApi.MmToCoord(width));
            circle.SetState_StartAngle(startAngle);
            circle.SetState_EndAngle(endAngle);
            circle.SetState_V7Layer(new V7_Layer(layer));
            return circle;
        }

        public static IPCB_Pad4 CreatePTH(IPCB_Group c, TLayerConstant layer, TExtendedHoleType holeType, TShape padShape, double x, double y, double height, double width, double holeSize, string name, bool plated, double rotation)
        {
            var pth = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.ePadObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Pad4;
            if (pth == null) return null;
            pth.SetState_Mode(TPadMode.ePadMode_Simple);
            pth.SetState_Name(name);
            pth.SetState_HoleType(holeType);
            pth.SetState_HoleSize(AltiumApi.MmToCoord(holeSize));
            pth.SetState_Rotation(rotation);
            pth.SetState_Plated(plated);
            pth.SetState_TopShape(padShape);
            pth.SetState_TopXSize(AltiumApi.MmToCoord(width));
            pth.SetState_TopYSize(AltiumApi.MmToCoord(height));
            pth.SetState_V7Layer(new V7_Layer(layer));
            pth.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            pth.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            return pth;
        }

        public static IPCB_Via CreateVia(IPCB_Group c, TLayerConstant layerStart, TLayerConstant layerEnd, double x, double y, double size, double holeSize)
        {
            var via = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eViaObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Via;
            if (via == null) return null;
            via.SetState_HighLayer(new V7_Layer(layerStart));
            via.SetState_LowLayer(new V7_Layer(layerEnd));
            via.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            via.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            via.SetState_HoleSize(AltiumApi.MmToCoord(holeSize));
            via.SetState_Size(AltiumApi.MmToCoord(size));
            return via;
        }

        public static IPCB_Text3 CreateText(IPCB_Group c, TLayerConstant layer, string text, double x, double y, double width, double size, double rotation)
        {
            var textObject = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eTextObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Text3;
            if (textObject == null) return null;
            bool isAssemblyText = IsAssemblyText(layer, text);
            if (isAssemblyText)
            {
                size = AssemblyTextSizeMm;
                width = MechanicalLineWidthMm;
            }

            width = NormalizeLineWidth(layer, width);
            textObject.SetState_V7Layer(new V7_Layer(layer));
            textObject.SetState_XLocation(AltiumApi.MmToCoord(x) + c.GetState_XLocation());
            textObject.SetState_YLocation(AltiumApi.MmToCoord(y) + c.GetState_YLocation());
            textObject.SetState_Text(text);
            textObject.SetState_Size(AltiumApi.MmToCoord(size));
            textObject.SetState_Width(AltiumApi.MmToCoord(width));
            textObject.SetState_Rotation(rotation);
            if (isAssemblyText)
                ApplyAssemblyTextStyle(textObject);

            return textObject;
        }

        private static bool IsAssemblyText(TLayerConstant layer, string text)
        {
            if (layer != TLayerConstant.eMechanical2)
                return false;

            string value = (text ?? string.Empty).Trim();
            return string.Equals(value, ".Designator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, ".Comment", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyAssemblyTextStyle(IPCB_Text3 textObject)
        {
            textObject.SetState_UseTTFonts(true);
            textObject.SetState_FontName(AssemblyTextFontName);
            textObject.SetState_Bold(false);
            textObject.SetState_Italic(false);
        }

        public static void AddRectangle(IPCB_Group c, TLayerConstant layer, double x1, double y1, double x2, double y2, double width)
        {
            AddToPCB(c, CreateLine(c, layer, x1, y1, x2, y1, width));
            AddToPCB(c, CreateLine(c, layer, x2, y1, x2, y2, width));
            AddToPCB(c, CreateLine(c, layer, x2, y2, x1, y2, width));
            AddToPCB(c, CreateLine(c, layer, x1, y2, x1, y1, width));
        }

        public static int Add3dBodyProjection(IPCB_Group c, IReadOnlyList<StepSilhouettePrimitive> primitives, bool addToBoardView = false)
        {
            if (c == null || primitives == null)
                return 0;

            int count = 0;
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                object pcbPrimitive = null;
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    pcbPrimitive = CreateLine(c, TLayerConstant.eMechanical2, primitive.X1, primitive.Y1, primitive.X2, primitive.Y2, MechanicalLineWidthMm);
                }
                else if (primitive.Kind == StepSilhouettePrimitiveKind.Arc)
                {
                    pcbPrimitive = CreateArc(c, TLayerConstant.eMechanical2, primitive.CenterX, primitive.CenterY, primitive.Radius, MechanicalLineWidthMm, primitive.StartAngle, primitive.EndAngle);
                }

                if (pcbPrimitive == null)
                    continue;

                AddProjectionPrimitive(c, pcbPrimitive, addToBoardView);
                count++;
            }

            return count;
        }

        private static void AddProjectionPrimitive(IPCB_Group c, object primitive, bool addToBoardView)
        {
            if (addToBoardView)
                AddToPCB(c, primitive);
            else
                AddToPcbLibComponent(c, primitive);
        }

        public static void AddCourtyard(IPCB_Group c, double width, double height)
        {
            if (width <= 0 || height <= 0)
                return;

            double halfWidth = width / 2.0 + CourtyardMarginMm;
            double halfHeight = height / 2.0 + CourtyardMarginMm;
            AddRectangle(c, TLayerConstant.eMechanical3, -halfWidth, -halfHeight, halfWidth, halfHeight, MechanicalLineWidthMm);

            double centerMark = Math.Min(1.0, Math.Max(0.5, Math.Min(halfWidth, halfHeight) / 3.0));
            AddToPCB(c, CreateLine(c, TLayerConstant.eMechanical3, -centerMark, 0, centerMark, 0, MechanicalLineWidthMm));
            AddToPCB(c, CreateLine(c, TLayerConstant.eMechanical3, 0, -centerMark, 0, centerMark, MechanicalLineWidthMm));
        }

        public static void AddAssemblyTexts(IPCB_Group c, bool hasDesignator, bool hasComment, double bodyHeight, IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = null, bool addToBoardView = false)
        {
            ProjectionTextLocations locations = ChooseProjectionTextLocations(projectionPrimitives);
            if (!hasDesignator)
                AddProjectionPrimitive(c, CreateText(c, TLayerConstant.eMechanical2, ".Designator", locations.DesignatorX, locations.DesignatorY, MechanicalLineWidthMm, AssemblyTextSizeMm, 0), addToBoardView);
            if (!hasComment)
                AddProjectionPrimitive(c, CreateText(c, TLayerConstant.eMechanical2, ".Comment", locations.CommentX, locations.CommentY, MechanicalLineWidthMm, AssemblyTextSizeMm, 0), addToBoardView);
        }

        public static int ClearMechanical2Projection(IPCB_Group component)
        {
            if (component == null)
                return 0;

            SyncPcbLibComponentFromBoard(component);

            var primitivesToRemove = new List<object>();
            var seenPrimitives = new HashSet<object>();
            int filteredGroupCandidateCount = 0;
            int groupCandidateCount = 0;
            int directCandidateCount = 0;
            int filteredBoardCandidateCount = 0;
            int boardCandidateCount = 0;
            foreach (object primitive in EnumerateFilteredComponentProjectionPrimitives(component))
            {
                if (AddProjectionCleanupCandidate(primitive, primitivesToRemove, seenPrimitives))
                    filteredGroupCandidateCount++;
            }

            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (AddProjectionCleanupCandidate(primitive, primitivesToRemove, seenPrimitives))
                    groupCandidateCount++;
            }

            foreach (object primitive in EnumerateComponentProjectionPrimitivesByObjectId(component))
            {
                if (AddProjectionCleanupCandidate(primitive, primitivesToRemove, seenPrimitives))
                    directCandidateCount++;
            }

            var boards = new List<IPCB_Board>();
            AddDistinctBoard(boards, GetComponentBoard(component));
            AddDistinctBoard(boards, GetCurrentPcbLibraryBoard());

            int editorDeletedCount = ClearMechanical2ByEditorCommand(component, boards);
            if (editorDeletedCount > 0)
            {
                EasyEDALoaderModule.Trace($"ClearMechanical2Projection editor command deleted selected Mechanical 2 objects: selected={editorDeletedCount}");
                return editorDeletedCount;
            }

            foreach (IPCB_Board board in boards)
            {
                foreach (object primitive in EnumerateFilteredBoardProjectionPrimitives(board))
                {
                    if (AddProjectionCleanupCandidate(primitive, primitivesToRemove, seenPrimitives))
                        filteredBoardCandidateCount++;
                }
            }

            foreach (IPCB_Board board in boards)
            {
                foreach (object primitive in EnumerateBoardPrimitives(board))
                {
                    if (AddProjectionCleanupCandidate(primitive, primitivesToRemove, seenPrimitives))
                        boardCandidateCount++;
                }
            }

            int removedCount = primitivesToRemove.Count;
            RemoveProjectionCleanupCandidates(component, boards, primitivesToRemove);

            EasyEDALoaderModule.Trace($"ClearMechanical2Projection candidates: filteredGroup={filteredGroupCandidateCount}, group={groupCandidateCount}, direct={directCandidateCount}, filteredBoard={filteredBoardCandidateCount}, board={boardCandidateCount}, total={removedCount}");
            return removedCount;
        }

        public static int ReprojectComponentBodySilhouette(IPCB_Group component)
        {
            return ReprojectComponentBodySilhouette(component, out _);
        }

        public static int ReprojectComponentBodySilhouette(IPCB_Group component, out int removedCount)
        {
            removedCount = 0;
            if (component == null)
                return 0;

            bool exportedBody = false;
            var allProjectionPrimitives = new List<StepSilhouettePrimitive>();
            foreach (IPCB_ComponentBody body in EnumerateComponentBodies(component))
            {
                if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds bodyBounds))
                    continue;

                byte[] stepData = TryExportComponentBodyStep(body);
                if (stepData == null || stepData.Length == 0)
                    continue;

                exportedBody = true;
                TryGetComponentBodyModelState(body, out double rotX, out double rotY, out double rotZ);
                IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = StepSilhouetteProjection.Generate(
                    stepData,
                    new StepSilhouettePlacement
                    {
                        TargetBounds = bodyBounds,
                        RotX = rotX,
                        RotY = rotY,
                        RotZ = rotZ,
                        Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()
                    });

                allProjectionPrimitives.AddRange(projectionPrimitives);
            }

            if (allProjectionPrimitives.Count == 0)
            {
                if (exportedBody)
                    throw new InvalidOperationException("OCCT HLR produced no Mechanical 2 projection primitives for the active footprint 3D body.");

                throw new InvalidOperationException("The active footprint does not contain an exportable 3D body to reproject.");
            }

            bool modifying = BeginPcbPrimitiveModify(component);
            bool changed = false;
            try
            {
                removedCount = ClearMechanical2Projection(component);
                int projectionCount = Add3dBodyProjection(component, allProjectionPrimitives);
                AddAssemblyTexts(component, false, false, 0, allProjectionPrimitives);
                SyncPcbLibComponentToBoard(component);
                changed = removedCount > 0 || projectionCount > 0;
                return projectionCount;
            }
            finally
            {
                EndPcbPrimitiveModify(component, modifying, changed);
            }
        }

        public static int AlignComponentBodiesToPads(IPCB_Group component)
        {
            if (component == null)
                return 0;

            StepSilhouetteBounds padBounds = MeasurePadBounds(component);
            if (padBounds == null)
                throw new InvalidOperationException("The active footprint does not contain pads to align against.");

            int alignedCount = 0;
            foreach (IPCB_ComponentBody body in EnumerateComponentBodies(component))
            {
                if (CenterComponentBodyMm(component, body, padBounds.CenterX, padBounds.CenterY))
                    alignedCount++;
            }

            return alignedCount;
        }

        public static int CreateCustomPadFromSelected(IPCB_Group component)
        {
            if (component == null)
                throw new InvalidOperationException("Open a PCB library and select a footprint before creating a custom pad.");

            IPCB_Board board = GetComponentBoard(component) ?? GetCurrentPcbLibraryBoard();
            if (board == null)
                throw new InvalidOperationException("Could not resolve the active PCB library board.");

            List<object> selectedPrimitives = GetSelectedObjects(board);
            if (selectedPrimitives.Count == 0)
                throw new InvalidOperationException("Select pads, tracks, fills, arcs, regions, or polygons before creating a custom pad.");

            List<SelectedCustomPadSource> sources = BuildCustomPadSources(selectedPrimitives);
            if (sources.Count == 0)
                throw new InvalidOperationException("The current selection does not contain supported pad or copper geometry.");

            int targetLayerNumber = sources[0].LayerNumber;
            IV7_Layer targetLayer = sources[0].Layer;
            foreach (SelectedCustomPadSource source in sources)
            {
                if (source.LayerNumber != targetLayerNumber)
                    throw new InvalidOperationException("Create Custom Pad from Selected requires all selected geometry to be on one PCB layer.");

                if (targetLayer == null && source.Layer != null)
                    targetLayer = source.Layer;
            }

            if (targetLayer == null)
                targetLayer = CreateLayerFromNumber(targetLayerNumber);
            if (targetLayer == null)
                throw new InvalidOperationException("Could not resolve the selected geometry layer.");

            if (!CreateCustomPadWithEditorConversion(component, board, sources, targetLayer))
                throw new InvalidOperationException("Could not join the selected geometry into a custom pad outline.");

            RefreshPcbLibraryAfterPrimitiveRemoval(
                AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary(),
                new[] { board },
                GetComponentPattern(component));

            return sources.Count;
        }

        private static bool CreateCustomPadWithEditorConversion(
            IPCB_Group component,
            IPCB_Board board,
            IReadOnlyList<SelectedCustomPadSource> sources,
            IV7_Layer targetLayer)
        {
            IPCB_Pad anchorPad = SelectCustomPadAnchorPad(sources);
            if (anchorPad == null)
                throw new InvalidOperationException("Create Custom Pad from Selected requires at least one selected pad to use as the custom pad anchor.");

            var temporaryOutline = new List<object>();
            int outlineCount = AddCustomPadConversionOutline(component, board, sources, targetLayer, temporaryOutline);
            if (outlineCount <= 0)
                return false;

            SelectOnlyCustomPadConversionObjects(board, anchorPad, temporaryOutline);
            if (!LaunchPcbCommand("PCB:CustomPadShape", "Action=Convert|Object=Track"))
                return false;

            if (FindConvertedCustomPad(component, anchorPad, targetLayer) == null)
            {
                DeleteCustomPadConversionObjects(board, temporaryOutline);
                return false;
            }

            var objectsToDelete = new List<object>();
            foreach (SelectedCustomPadSource source in sources)
            {
                if (!ReferenceEquals(source.Primitive, anchorPad))
                    AddDistinctObject(objectsToDelete, source.Primitive);
            }

            foreach (object temporaryObject in temporaryOutline)
                AddDistinctObject(objectsToDelete, temporaryObject);

            DeleteCustomPadConversionObjects(board, objectsToDelete);
            TryInvoke(board, "ViewManager_FullUpdate");
            LaunchPcbCommand("PCB:Zoom", "Action=Redraw");
            return true;
        }

        private static IPCB_Pad FindConvertedCustomPad(IPCB_Group component, IPCB_Pad anchorPad, IV7_Layer targetLayer)
        {
            if (anchorPad == null)
                return null;

            string anchorName = anchorPad.GetState_Name();
            if (IsCustomPadShapeOnLayer(anchorPad, targetLayer))
                return anchorPad;

            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (!(primitive is IPCB_Pad pad))
                    continue;

                if (!string.Equals(pad.GetState_Name(), anchorName, StringComparison.Ordinal))
                    continue;

                if (IsCustomPadShapeOnLayer(pad, targetLayer))
                    return pad;
            }

            return null;
        }

        private static bool IsCustomPadShapeOnLayer(IPCB_Pad pad, IV7_Layer targetLayer)
        {
            if (pad == null || targetLayer == null)
                return false;

            if (TryConvertToInt(TryInvokeResult(pad, "Internal_GetState_ShapeOnLayer", targetLayer), out int shape) &&
                shape == (int)TShape.eCustomShape)
                return true;

            if (TryConvertToBool(TryInvokeResult(pad, "HasCustomShapes"), out bool hasCustomShapes) && hasCustomShapes)
                return true;

            if (pad is IPCB_CustomPadShape customPadShape)
                return customPadShape.Internal_GetProperty_CustomShape(targetLayer) != null;

            return false;
        }

        private static IPCB_Pad SelectCustomPadAnchorPad(IReadOnlyList<SelectedCustomPadSource> sources)
        {
            IPCB_Pad result = null;
            long resultArea = -1;
            foreach (SelectedCustomPadSource source in sources)
            {
                if (!(source.Primitive is IPCB_Pad pad))
                    continue;

                if (!TryGetPadBounds(source, out CustomPadRect bounds))
                    continue;

                long area = (long)(bounds.Right - bounds.Left) * (bounds.Top - bounds.Bottom);
                if (area > resultArea)
                {
                    result = pad;
                    resultArea = area;
                }
            }

            return result;
        }

        private static int AddCustomPadConversionOutline(
            IPCB_Group component,
            IPCB_Board board,
            IReadOnlyList<SelectedCustomPadSource> sources,
            IV7_Layer targetLayer,
            List<object> temporaryOutline)
        {
            List<SelectedCustomPadSource> padSources = new List<SelectedCustomPadSource>();
            foreach (SelectedCustomPadSource source in sources)
            {
                if (source.Primitive is IPCB_Pad)
                    padSources.Add(source);
            }

            if (padSources.Count == 2 &&
                TryAddSteppedPadCustomPadContour(component, board, padSources[0], padSources[1], targetLayer, temporaryOutline, out int steppedCount))
                return steppedCount;

            if (!TryGetSourceBounds(sources, out CustomPadRect bounds))
                return 0;

            int radius = 0;
            foreach (SelectedCustomPadSource source in sources)
            {
                if (TryGetPadCornerRadius(source, out int sourceRadius))
                    radius = Math.Max(radius, sourceRadius);
            }

            radius = Math.Min(radius, Math.Min(bounds.Right - bounds.Left, bounds.Top - bounds.Bottom) / 2);
            return AddRoundedRectCustomPadContour(component, board, targetLayer, bounds, radius, temporaryOutline);
        }

        private static bool TryAddSteppedPadCustomPadContour(
            IPCB_Group component,
            IPCB_Board board,
            SelectedCustomPadSource first,
            SelectedCustomPadSource second,
            IV7_Layer targetLayer,
            List<object> temporaryOutline,
            out int outlineCount)
        {
            outlineCount = 0;
            if (!TryGetPadBounds(first, out CustomPadRect firstBounds) ||
                !TryGetPadBounds(second, out CustomPadRect secondBounds))
                return false;

            long firstArea = (long)(firstBounds.Right - firstBounds.Left) * (firstBounds.Top - firstBounds.Bottom);
            long secondArea = (long)(secondBounds.Right - secondBounds.Left) * (secondBounds.Top - secondBounds.Bottom);
            CustomPadRect main = firstArea >= secondArea ? firstBounds : secondBounds;
            CustomPadRect extension = firstArea >= secondArea ? secondBounds : firstBounds;

            int tolerance = Math.Max(1, AltiumApi.MmToCoord(0.002));
            if (!(extension.Right > main.Right + tolerance &&
                extension.Left < main.Right + tolerance &&
                extension.Top < main.Top - tolerance &&
                extension.Bottom > main.Bottom + tolerance))
                return false;

            int mainRadius = main.Radius;
            int extensionRadius = extension.Radius;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, main.Left + mainRadius, main.Top, main.Right - mainRadius, main.Top, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourArc(component, board, targetLayer, main.Right - mainRadius, main.Top - mainRadius, mainRadius, 0, 90, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, main.Right, main.Top - mainRadius, main.Right, extension.Top, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, main.Right, extension.Top, extension.Right - extensionRadius, extension.Top, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourArc(component, board, targetLayer, extension.Right - extensionRadius, extension.Top - extensionRadius, extensionRadius, 0, 90, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, extension.Right, extension.Top - extensionRadius, extension.Right, extension.Bottom + extensionRadius, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourArc(component, board, targetLayer, extension.Right - extensionRadius, extension.Bottom + extensionRadius, extensionRadius, 270, 0, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, extension.Right - extensionRadius, extension.Bottom, main.Right, extension.Bottom, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, main.Right, extension.Bottom, main.Right, main.Bottom + mainRadius, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourArc(component, board, targetLayer, main.Right - mainRadius, main.Bottom + mainRadius, mainRadius, 270, 0, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, main.Right - mainRadius, main.Bottom, main.Left + mainRadius, main.Bottom, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourArc(component, board, targetLayer, main.Left + mainRadius, main.Bottom + mainRadius, mainRadius, 180, 270, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourTrack(component, board, targetLayer, main.Left, main.Bottom + mainRadius, main.Left, main.Top - mainRadius, temporaryOutline) ? 1 : 0;
            outlineCount += AddCustomPadContourArc(component, board, targetLayer, main.Left + mainRadius, main.Top - mainRadius, mainRadius, 90, 180, temporaryOutline) ? 1 : 0;
            return outlineCount > 0;
        }

        private static int AddRoundedRectCustomPadContour(
            IPCB_Group component,
            IPCB_Board board,
            IV7_Layer targetLayer,
            CustomPadRect bounds,
            int radius,
            List<object> temporaryOutline)
        {
            int count = 0;
            count += AddCustomPadContourTrack(component, board, targetLayer, bounds.Left + radius, bounds.Top, bounds.Right - radius, bounds.Top, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourArc(component, board, targetLayer, bounds.Right - radius, bounds.Top - radius, radius, 0, 90, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourTrack(component, board, targetLayer, bounds.Right, bounds.Top - radius, bounds.Right, bounds.Bottom + radius, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourArc(component, board, targetLayer, bounds.Right - radius, bounds.Bottom + radius, radius, 270, 0, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourTrack(component, board, targetLayer, bounds.Right - radius, bounds.Bottom, bounds.Left + radius, bounds.Bottom, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourArc(component, board, targetLayer, bounds.Left + radius, bounds.Bottom + radius, radius, 180, 270, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourTrack(component, board, targetLayer, bounds.Left, bounds.Bottom + radius, bounds.Left, bounds.Top - radius, temporaryOutline) ? 1 : 0;
            count += AddCustomPadContourArc(component, board, targetLayer, bounds.Left + radius, bounds.Top - radius, radius, 90, 180, temporaryOutline) ? 1 : 0;
            return count;
        }

        private static bool AddCustomPadContourTrack(
            IPCB_Group component,
            IPCB_Board board,
            IV7_Layer targetLayer,
            int x1,
            int y1,
            int x2,
            int y2,
            List<object> temporaryOutline)
        {
            if (x1 == x2 && y1 == y2)
                return false;

            var track = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(
                TObjectId.eTrackObject,
                TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Track;
            if (track == null)
                return false;

            track.SetState_V7Layer(targetLayer);
            track.SetState_X1(x1);
            track.SetState_Y1(y1);
            track.SetState_X2(x2);
            track.SetState_Y2(y2);
            track.SetState_Width(Math.Max(1, AltiumApi.MmToCoord(0.001)));
            AddToPCB(component, track);
            temporaryOutline.Add(track);
            return true;
        }

        private static bool AddCustomPadContourArc(
            IPCB_Group component,
            IPCB_Board board,
            IV7_Layer targetLayer,
            int xCenter,
            int yCenter,
            int radius,
            double startAngle,
            double endAngle,
            List<object> temporaryOutline)
        {
            if (radius <= 0)
                return false;

            var arc = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(
                TObjectId.eArcObject,
                TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Arc;
            if (arc == null)
                return false;

            arc.SetState_V7Layer(targetLayer);
            arc.SetState_CenterX(xCenter);
            arc.SetState_CenterY(yCenter);
            arc.SetState_Radius(radius);
            arc.SetState_StartAngle(startAngle);
            arc.SetState_EndAngle(endAngle);
            arc.SetState_LineWidth(Math.Max(1, AltiumApi.MmToCoord(0.001)));
            AddToPCB(component, arc);
            temporaryOutline.Add(arc);
            return true;
        }

        private static void SelectOnlyCustomPadConversionObjects(IPCB_Board board, IPCB_Pad anchorPad, IReadOnlyList<object> outlineObjects)
        {
            LaunchPcbCommand("PCB:DeSelect", "Scope=All");
            TryInvoke(board, "SelectedObjects_Clear");
            SetPrimitiveSelected(board, anchorPad, true);
            foreach (object outlineObject in outlineObjects)
                SetPrimitiveSelected(board, outlineObject, true);
        }

        private static void DeleteCustomPadConversionObjects(IPCB_Board board, IReadOnlyList<object> objectsToDelete)
        {
            if (objectsToDelete == null || objectsToDelete.Count == 0)
                return;

            LaunchPcbCommand("PCB:DeSelect", "Scope=All");
            TryInvoke(board, "SelectedObjects_Clear");
            foreach (object objectToDelete in objectsToDelete)
                SetPrimitiveSelected(board, objectToDelete, true);

            LaunchPcbCommand("PCB:DeleteObjects", "Object=SELECTED");
            LaunchPcbCommand("PCB:DeSelect", "Scope=All");
            TryInvoke(board, "SelectedObjects_Clear");
        }

        private static void SetPrimitiveSelected(IPCB_Board board, object primitive, bool selected)
        {
            if (primitive is IPCB_Primitive pcbPrimitive)
                pcbPrimitive.SetState_Selected(selected);

            if (selected && board != null)
                TryInvoke(board, "SelectedObjects_Add", primitive);
        }

        private static void AddDistinctObject(List<object> objects, object value)
        {
            if (value == null)
                return;

            foreach (object existing in objects)
            {
                if (ReferenceEquals(existing, value))
                    return;
            }

            objects.Add(value);
        }

        private static bool TryGetSourceBounds(IReadOnlyList<SelectedCustomPadSource> sources, out CustomPadRect bounds)
        {
            bounds = null;
            foreach (SelectedCustomPadSource source in sources)
            {
                if (!TryGetSourceBounds(source, out CustomPadRect sourceBounds))
                    continue;

                if (bounds == null)
                {
                    bounds = sourceBounds;
                    continue;
                }

                bounds.Left = Math.Min(bounds.Left, sourceBounds.Left);
                bounds.Right = Math.Max(bounds.Right, sourceBounds.Right);
                bounds.Bottom = Math.Min(bounds.Bottom, sourceBounds.Bottom);
                bounds.Top = Math.Max(bounds.Top, sourceBounds.Top);
                bounds.Radius = Math.Max(bounds.Radius, sourceBounds.Radius);
            }

            return bounds != null && bounds.Right > bounds.Left && bounds.Top > bounds.Bottom;
        }

        private static bool TryGetSourceBounds(SelectedCustomPadSource source, out CustomPadRect bounds)
        {
            if (source?.Primitive is IPCB_Pad)
                return TryGetPadBounds(source, out bounds);

            if (TryGetPrimitiveBoundsCoord(source?.Primitive, out int left, out int bottom, out int right, out int top))
            {
                bounds = new CustomPadRect
                {
                    Left = left,
                    Bottom = bottom,
                    Right = right,
                    Top = top
                };
                return true;
            }

            bounds = null;
            return false;
        }

        private static bool TryGetPadBounds(SelectedCustomPadSource source, out CustomPadRect bounds)
        {
            bounds = null;
            if (!(source?.Primitive is IPCB_Pad pad) || source.Layer == null)
                return false;

            int x = pad.GetState_XLocation();
            int y = pad.GetState_YLocation();
            int width = pad.GetState_XSizeOnLayer(source.Layer);
            int height = pad.GetState_YSizeOnLayer(source.Layer);
            if (width <= 0 || height <= 0)
                return false;

            int radius = 0;
            TryGetPadCornerRadius(source, out radius);
            bounds = new CustomPadRect
            {
                Left = x - width / 2,
                Right = x + width / 2,
                Bottom = y - height / 2,
                Top = y + height / 2,
                Radius = radius
            };
            return true;
        }

        private static bool TryGetPadCornerRadius(SelectedCustomPadSource source, out int radius)
        {
            radius = 0;
            if (!(source?.Primitive is IPCB_Pad pad) || source.Layer == null)
                return false;

            int width = pad.GetState_XSizeOnLayer(source.Layer);
            int height = pad.GetState_YSizeOnLayer(source.Layer);
            if (width <= 0 || height <= 0)
                return false;

            if (pad is IPCB_Pad2 pad2)
            {
                radius = pad2.GetState_CornerRadiusOnLayer(source.Layer);
                if (radius > 0)
                    return true;
            }

            int fallbackRadius = GetFallbackPadCornerRadius(width, height);
            int shapeValue = pad.Internal_GetState_ShapeOnLayer(source.Layer);
            if (shapeValue == (int)TShape.eRounded)
            {
                radius = Math.Min(width, height) / 2;
                return radius > 0;
            }

            if (shapeValue == (int)TShape.eRoundRectShape || shapeValue == (int)TShape.eRoundedRectangular)
            {
                radius = fallbackRadius;
                return radius > 0;
            }

            if (pad is IPCB_Pad4 pad4 && (pad4.HasCustomRoundedRectangle() || pad4.HasCornerRadiusChamfer()))
            {
                radius = fallbackRadius;
                return radius > 0;
            }

            return false;
        }

        private static int GetFallbackPadCornerRadius(int width, int height)
        {
            return Math.Max(1, Math.Min(width, height) / 4);
        }

        private static bool TryGetPrimitiveBoundsCoord(object primitive, out int left, out int bottom, out int right, out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;

            object rect = TryInvokeResult(primitive, "Internal_BoundingRectangle");
            if (rect == null)
                return false;

            if (!TryGetRectCoord(rect, "GetLeft", out left) ||
                !TryGetRectCoord(rect, "GetBottom", out bottom) ||
                !TryGetRectCoord(rect, "GetRight", out right) ||
                !TryGetRectCoord(rect, "GetTop", out top))
                return false;

            return right > left && top > bottom;
        }

        private static List<object> GetSelectedObjects(IPCB_Board board)
        {
            var result = new List<object>();
            var seen = new HashSet<object>();
            if (board == null)
                return result;

            int selectedCount = GetSelectedObjectCount(board);
            for (int index = 0; index < selectedCount; index++)
                AddSelectedObject(result, seen, TryInvokeResult(board, "Internal_GetState_SelectecObject", index));

            for (int index = 1; index <= selectedCount; index++)
                AddSelectedObject(result, seen, TryInvokeResult(board, "Internal_GetState_SelectecObject", index));

            if (result.Count > 0)
                return result;

            foreach (object primitive in EnumerateBoardPrimitives(board))
            {
                if (TryConvertToBool(TryInvokeResult(primitive, "GetState_Selected"), out bool selected) && selected)
                    AddSelectedObject(result, seen, primitive);
            }

            return result;
        }

        private static void AddSelectedObject(List<object> result, HashSet<object> seen, object primitive)
        {
            if (!IsSupportedCustomPadSourcePrimitive(primitive))
                return;

            if (seen.Add(primitive))
                result.Add(primitive);
        }

        private static bool IsSupportedCustomPadSourcePrimitive(object primitive)
        {
            return primitive is IPCB_Pad
                || primitive is IPCB_Track
                || primitive is IPCB_Fill
                || primitive is IPCB_Arc
                || primitive is IPCB_Region
                || primitive is IPCB_Polygon;
        }

        private static List<SelectedCustomPadSource> BuildCustomPadSources(IReadOnlyList<object> selectedPrimitives)
        {
            var sources = new List<SelectedCustomPadSource>();
            foreach (object primitive in selectedPrimitives)
            {
                if (!TryGetPrimitiveLayer(primitive, out IV7_Layer layer, out int layerNumber))
                    continue;

                int contourLayerNumber = GetContourMakerLayerNumber(layer, layerNumber);
                sources.Add(new SelectedCustomPadSource
                {
                    Primitive = primitive,
                    Layer = layer,
                    LayerNumber = layerNumber,
                    ContourLayerNumber = contourLayerNumber
                });
            }

            return sources;
        }

        private static object CreateJoinedCustomPadPolygon(IReadOnlyList<SelectedCustomPadSource> sources)
        {
            object contourMaker = TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_PCBContourMaker");
            object contourUtilities = TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_PCBContourUtilities");
            object resultPolygon = TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_PCBGeometricPolygonFactory");
            if (contourMaker == null || contourUtilities == null || resultPolygon == null)
            {
                EasyEDALoaderModule.Trace(
                    $"CreateCustomPadFromSelected contour setup failed: maker={contourMaker != null} utilities={contourUtilities != null} resultPolygon={resultPolygon != null}");
                return null;
            }

            ConfigureCustomPadContourMaker(contourMaker, sources);

            object polygonList = TryInvokeResult(contourUtilities, "Internal_CreateInterfaceList");
            if (!(polygonList is DXP.IInterfaceList interfaceList))
            {
                EasyEDALoaderModule.Trace(
                    $"CreateCustomPadFromSelected contour list failed: type={polygonList?.GetType().FullName ?? "null"}");
                return null;
            }

            foreach (SelectedCustomPadSource source in sources)
            {
                object polygon = TryInvokeResult(contourMaker, "Internal_MakeContour", source.Primitive, 0, source.ContourLayerNumber);
                int contourCount = GetGeometricPolygonContourCount(polygon);
                if (polygon == null || contourCount == 0)
                {
                    EasyEDALoaderModule.Trace(
                        $"CreateCustomPadFromSelected contour source skipped: type={source.Primitive?.GetType().FullName ?? "null"} layer={source.LayerNumber} contourLayer={source.ContourLayerNumber} polygonNull={polygon == null} contours={contourCount}");
                    continue;
                }

                interfaceList.Add(polygon);
            }

            if (interfaceList.GetCount() == 0)
            {
                EasyEDALoaderModule.Trace($"CreateCustomPadFromSelected contour result empty: sources={sources.Count}");
                return null;
            }

            if (interfaceList.GetCount() == 1)
                return interfaceList.Get(0);

            TryInvoke(contourUtilities, "UnionBatchSet", polygonList, resultPolygon);
            return resultPolygon;
        }

        private static void ConfigureCustomPadContourMaker(object contourMaker, IReadOnlyList<SelectedCustomPadSource> sources)
        {
            if (contourMaker == null || sources == null || sources.Count == 0)
                return;

            bool hasRoundedPadCorners = false;
            foreach (SelectedCustomPadSource source in sources)
            {
                if (SourceHasRoundedPadCorners(source))
                {
                    hasRoundedPadCorners = true;
                    break;
                }
            }

            if (!hasRoundedPadCorners)
                return;

            if (contourMaker is IPCB_ContourMaker maker)
                maker.SetState_ArcResolution(64);
            else
                TryInvoke(contourMaker, "SetState_ArcResolution", 64);
        }

        private static bool SourceHasRoundedPadCorners(SelectedCustomPadSource source)
        {
            if (!(source?.Primitive is IPCB_Pad pad))
                return false;

            if (IsRoundedPadShape(TryInvokeResult(pad, "Internal_GetState_TopShape")) ||
                IsRoundedPadShape(TryInvokeResult(pad, "Internal_GetState_MidShape")) ||
                IsRoundedPadShape(TryInvokeResult(pad, "Internal_GetState_BotShape")) ||
                IsRoundedPadShape(TryInvokeResult(pad, "Internal_GetState_ShapeOnLayer", source.Layer)))
                return true;

            return TryConvertToBool(TryInvokeResult(pad, "HasCornerRadiusChamfer"), out bool hasCornerRadiusChamfer) &&
                hasCornerRadiusChamfer;
        }

        private static bool IsRoundedPadShape(object rawShape)
        {
            if (!TryConvertToInt(rawShape, out int shape))
                return false;

            return shape == (int)TShape.eRounded ||
                shape == (int)TShape.eRoundRectShape ||
                shape == (int)TShape.eRoundedRectangular;
        }

        private static IPCB_Region CreateCustomPadRegion(IV7_Layer targetLayer, object joinedPolygon)
        {
            var region = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(
                TObjectId.eRegionObject,
                TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Region;
            if (region == null)
                throw new InvalidOperationException("Could not create an Altium region for the custom pad shape.");

            region.SetState_Kind(TRegionKind.eRegionKind_Copper);
            region.SetState_V7Layer(targetLayer);
            region.SetGeometricPolygon(joinedPolygon);
            return region;
        }

        private static IPCB_Pad4 CreateCustomPad(
            IPCB_Group component,
            IV7_Layer targetLayer,
            IPCB_Region customShape,
            CustomPadSourceProperties properties,
            int left,
            int bottom,
            int right,
            int top)
        {
            var pad = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(
                TObjectId.ePadObject,
                TDimensionKind.eNoDimension,
                TObjectCreationMode.eCreate_Default) as IPCB_Pad4;
            if (pad == null)
                throw new InvalidOperationException("Could not create an Altium pad.");

            int width = Math.Max(1, right - left);
            int height = Math.Max(1, top - bottom);
            pad.SetState_Mode(TPadMode.ePadMode_Simple);
            pad.SetState_Name(properties.Name);
            pad.SetState_HoleType(properties.HoleType);
            pad.SetState_HoleSize(properties.HoleSize);
            pad.SetState_HoleWidth(properties.HoleWidth);
            pad.SetState_HoleRotation(properties.HoleRotation);
            pad.SetState_Plated(properties.Plated);
            pad.SetState_Rotation(0);
            pad.SetState_TopXSize(width);
            pad.SetState_TopYSize(height);
            pad.SetState_MidXSize(width);
            pad.SetState_MidYSize(height);
            pad.SetState_BotXSize(width);
            pad.SetState_BotYSize(height);
            pad.SetState_V7Layer(targetLayer);
            pad.SetState_XLocation(properties.XLocation);
            pad.SetState_YLocation(properties.YLocation);
            if (properties.Net != null)
                TryInvoke(pad, "SetState_Net", properties.Net);

            ApplyCustomPadShape(pad, targetLayer, customShape);
            return pad;
        }

        internal static void SetPolygonPadShape(IPCB_Pad4 pad, TLayerConstant layer, ShapeContour contour)
        {
            if (contour.Points.Count < 3 || contour.Points.Exists(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                throw new FormatException("Polygon pad must contain at least three finite points.");

            // Custom regions use pad-local coordinates. Reuse the existing linking
            // path without adding the footprint origin to those coordinates.
            var layers = layer == TLayerConstant.eMultiLayer
                ? new[] { TLayerConstant.eTopLayer, TLayerConstant.eMidLayer1, TLayerConstant.eBottomLayer }
                : new[] { layer };
            foreach (var shapeLayer in layers)
                ApplyCustomPadShape(pad, new V7_Layer(shapeLayer), CreateSolidRegion(null, shapeLayer, new[] { contour }));
        }

        private static void ApplyCustomPadShape(IPCB_Pad4 pad, IV7_Layer targetLayer, IPCB_Region customShape)
        {
            if (pad == null || targetLayer == null || customShape == null)
                throw new InvalidOperationException("Could not create the custom pad shape.");

            var v7Layer = new V7_Layer(targetLayer);
            if (v7Layer.IsTopLayer())
                pad.SetState_TopShape(TShape.eCustomShape);
            else if (v7Layer.IsBottomLayer())
                pad.SetState_BotShape(TShape.eCustomShape);
            else
                pad.SetState_MidShape(TShape.eCustomShape);

            pad.SetState_StackShapeOnLayer(targetLayer, (int)TShape.eCustomShape);

            if (!(pad is IPCB_CustomPadShape customPadShape))
                throw new InvalidOperationException("This Altium SDK build did not expose custom pad shape support.");

            customPadShape.SetProperty_CustomShapeKind(targetLayer, (int)TShapeSubKind.eNoKind);
            customPadShape.SetProperty_CustomShape(targetLayer, customShape);
            pad.LinkCustomShape(customShape);
            pad.UpdatePadStructureOnLayer(targetLayer);
            pad.InvalidateSizeShape();
            pad.InvalidateCache();
            pad.ValidateSizeShape();
            pad.GraphicallyInvalidate();
        }

        private static CustomPadSourceProperties SelectCustomPadSourceProperties(
            IReadOnlyList<SelectedCustomPadSource> sources,
            int left,
            int bottom,
            int right,
            int top)
        {
            var result = new CustomPadSourceProperties
            {
                Name = "CUSTOM",
                HoleType = TExtendedHoleType.eRoundHole,
                HoleSize = 0,
                HoleWidth = 0,
                HoleRotation = 0,
                Plated = false,
                XLocation = left + (right - left) / 2,
                YLocation = bottom + (top - bottom) / 2
            };

            IPCB_Pad firstPad = null;
            IPCB_Pad holePad = null;
            foreach (SelectedCustomPadSource source in sources)
            {
                if (result.Net == null)
                    result.Net = TryInvokeResult(source.Primitive, "Internal_GetState_Net");

                if (!(source.Primitive is IPCB_Pad pad))
                    continue;

                if (firstPad == null)
                {
                    firstPad = pad;
                    string name = pad.GetState_Name();
                    if (!string.IsNullOrWhiteSpace(name))
                        result.Name = name;
                }

                int holeSize = pad.GetState_HoleSize();
                int holeWidth = pad.GetState_HoleWidth();
                if (holeSize <= 0 && holeWidth <= 0)
                    continue;

                if (holePad != null)
                    throw new InvalidOperationException("Create Custom Pad from Selected supports at most one selected through-hole pad.");

                holePad = pad;
                result.HoleSize = holeSize;
                result.HoleWidth = holeWidth;
                result.HoleRotation = pad.GetState_HoleRotation();
                result.Plated = pad.GetState_Plated();
                result.XLocation = pad.GetState_XLocation();
                result.YLocation = pad.GetState_YLocation();
                if (TryConvertToInt(TryInvokeResult(pad, "Internal_GetState_HoleType"), out int holeType))
                    result.HoleType = (TExtendedHoleType)holeType;
            }

            return result;
        }

        private static void RemoveSelectedCustomPadSourcePrimitives(
            IPCB_Group component,
            IPCB_Board board,
            IReadOnlyList<SelectedCustomPadSource> sources)
        {
            if (sources == null || sources.Count == 0)
                return;

            var boards = new List<IPCB_Board>();
            AddDistinctBoard(boards, board);
            AddDistinctBoard(boards, GetComponentBoard(component));
            AddDistinctBoard(boards, GetCurrentPcbLibraryBoard());

            foreach (SelectedCustomPadSource source in sources)
            {
                TryInvoke(component, "RemovePCBObject", source.Primitive);
                foreach (IPCB_Board removeBoard in boards)
                    TryInvoke(removeBoard, "RemovePCBObject", source.Primitive);
            }
        }

        private static bool TryGetPrimitiveLayer(object primitive, out IV7_Layer layer, out int layerNumber)
        {
            layer = TryInvokeResult(primitive, "Internal_GetState_V7Layer") as IV7_Layer;
            if (layer != null)
            {
                try
                {
                    layerNumber = new V7_Layer(layer).Number();
                    return true;
                }
                catch
                {
                }
            }

            if (TryGetPrimitiveLayerNumber(primitive, out layerNumber))
            {
                layer = CreateLayerFromNumber(layerNumber);
                return true;
            }

            layerNumber = 0;
            return false;
        }

        private static int GetContourMakerLayerNumber(IV7_Layer layer, int fallbackLayerNumber)
        {
            if (layer != null)
            {
                try
                {
                    return (int)new V7_Layer(layer).SafeV6Layer();
                }
                catch
                {
                }
            }

            return fallbackLayerNumber;
        }

        private static IV7_Layer CreateLayerFromNumber(int layerNumber)
        {
            try
            {
                return new V7_Layer((TLayerConstant)layerNumber);
            }
            catch
            {
                return null;
            }
        }

        private static int GetGeometricPolygonContourCount(object polygon)
        {
            if (polygon == null)
                return 0;

            if (TryConvertToInt(TryInvokeResult(polygon, "GetState_Count"), out int count))
                return count;

            return 0;
        }

        private static bool TryGetGeometricPolygonBounds(object polygon, out int left, out int bottom, out int right, out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;

            bool hasPoint = false;
            int contourCount = GetGeometricPolygonContourCount(polygon);
            for (int contourIndex = 0; contourIndex < contourCount; contourIndex++)
            {
                object contour = TryInvokeResult(polygon, "Internal_GetState_Contour", contourIndex);
                if (contour == null)
                    continue;

                if (!TryConvertToInt(TryInvokeResult(contour, "GetState_Count"), out int pointCount))
                    continue;

                for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                {
                    if (!TryConvertToInt(TryInvokeResult(contour, "GetState_PointX", pointIndex), out int x) ||
                        !TryConvertToInt(TryInvokeResult(contour, "GetState_PointY", pointIndex), out int y))
                        continue;

                    if (!hasPoint)
                    {
                        left = right = x;
                        bottom = top = y;
                        hasPoint = true;
                    }
                    else
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        bottom = Math.Min(bottom, y);
                        top = Math.Max(top, y);
                    }
                }
            }

            return hasPoint && right > left && top > bottom;
        }

        private static ProjectionTextLocations ChooseProjectionTextLocations(IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives)
        {
            double designatorHeight = AssemblyTextSizeMm;
            double designatorAnchorWidth = ProjectionTextWidth(".De");
            double commentWidth = ProjectionTextWidth(".Comment");
            StepSilhouetteBounds bounds = MeasureProjectionPrimitives(projectionPrimitives);

            if (bounds == null)
            {
                double centerX = 0;
                double centerY = 0;
                return new ProjectionTextLocations
                {
                    DesignatorX = centerX - designatorAnchorWidth / 2.0,
                    DesignatorY = centerY - designatorHeight / 2.0,
                    CommentX = centerX - commentWidth / 2.0,
                    CommentY = centerY - designatorHeight - AssemblyTextGapMm - ProjectionTrueTypeVisibleHeight()
                };
            }

            double projectionCenterX = bounds.CenterX;
            double projectionCenterY = bounds.CenterY;
            if (TryChooseProjectionDesignatorCenter(projectionPrimitives, projectionCenterX, projectionCenterY, bounds.Bottom, bounds.Top, designatorHeight, out double designatorY))
            {
                return new ProjectionTextLocations
                {
                    DesignatorX = projectionCenterX - designatorAnchorWidth / 2.0,
                    DesignatorY = designatorY,
                    CommentX = projectionCenterX - commentWidth / 2.0,
                    CommentY = bounds.Bottom - AssemblyTextGapMm - ProjectionTrueTypeVisibleHeight()
                };
            }

            return new ProjectionTextLocations
            {
                DesignatorX = projectionCenterX - designatorAnchorWidth / 2.0,
                DesignatorY = bounds.Top + AssemblyTextGapMm,
                CommentX = projectionCenterX - commentWidth / 2.0,
                CommentY = bounds.Bottom - AssemblyTextGapMm - ProjectionTrueTypeVisibleHeight()
            };
        }

        private static bool TryChooseProjectionDesignatorCenter(
            IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives,
            double centerX,
            double centerY,
            double bottom,
            double top,
            double textHeight,
            out double designatorY)
        {
            designatorY = centerY - textHeight / 2.0;
            double minCenterY = bottom + textHeight / 2.0;
            double maxCenterY = top - textHeight / 2.0;
            if (maxCenterY < minCenterY)
                return false;

            double maxOffset = Math.Max(Math.Abs(centerY - minCenterY), Math.Abs(maxCenterY - centerY));
            double offset = 0;
            while (offset <= maxOffset + 0.0000001)
            {
                double candidateCenterY = centerY - offset;
                if (candidateCenterY >= minCenterY && candidateCenterY <= maxCenterY &&
                    ProjectionDesignatorAnchorIsClear(projectionPrimitives, centerX, candidateCenterY, textHeight))
                {
                    designatorY = candidateCenterY - textHeight / 2.0;
                    return true;
                }

                if (offset > 0)
                {
                    candidateCenterY = centerY + offset;
                    if (candidateCenterY >= minCenterY && candidateCenterY <= maxCenterY &&
                        ProjectionDesignatorAnchorIsClear(projectionPrimitives, centerX, candidateCenterY, textHeight))
                    {
                        designatorY = candidateCenterY - textHeight / 2.0;
                        return true;
                    }
                }

                offset += AssemblyTextSearchStepMm;
            }

            return false;
        }

        private static bool ProjectionDesignatorAnchorIsClear(
            IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives,
            double centerX,
            double centerY,
            double textHeight)
        {
            double anchorWidth = ProjectionTextWidth(".De");
            double left = centerX - anchorWidth / 2.0;
            double right = centerX + anchorWidth / 2.0;
            double textY = centerY - textHeight / 2.0;
            double bottom = textY;
            double top = textY + ProjectionTrueTypeVisibleHeight();
            return !ProjectionPrimitivesIntersectRect(projectionPrimitives, left, bottom, right, top);
        }

        private static bool ProjectionPrimitivesIntersectRect(
            IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives,
            double left,
            double bottom,
            double right,
            double top)
        {
            if (projectionPrimitives == null)
                return false;

            left -= AssemblyTextKeepoutMm;
            bottom -= AssemblyTextKeepoutMm;
            right += AssemblyTextKeepoutMm;
            top += AssemblyTextKeepoutMm;

            foreach (StepSilhouettePrimitive primitive in projectionPrimitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    if (SegmentIntersectsRect(primitive.X1, primitive.Y1, primitive.X2, primitive.Y2, left, bottom, right, top))
                        return true;
                }
                else
                {
                    StepSilhouetteBounds arcBounds = BoundsForArc(primitive.CenterX, primitive.CenterY, primitive.Radius, primitive.StartAngle, primitive.EndAngle);
                    if (!(arcBounds.Right < left || arcBounds.Left > right || arcBounds.Top < bottom || arcBounds.Bottom > top))
                        return true;
                }
            }

            return false;
        }

        private static StepSilhouetteBounds MeasureProjectionPrimitives(IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives)
        {
            if (projectionPrimitives == null || projectionPrimitives.Count == 0)
                return null;

            StepSilhouetteBounds bounds = null;
            foreach (StepSilhouettePrimitive primitive in projectionPrimitives)
            {
                StepSilhouetteBounds primitiveBounds = BoundsForPrimitive(primitive);
                if (primitiveBounds == null)
                    continue;

                if (bounds == null)
                {
                    bounds = primitiveBounds;
                    continue;
                }

                bounds.Left = Math.Min(bounds.Left, primitiveBounds.Left);
                bounds.Bottom = Math.Min(bounds.Bottom, primitiveBounds.Bottom);
                bounds.Right = Math.Max(bounds.Right, primitiveBounds.Right);
                bounds.Top = Math.Max(bounds.Top, primitiveBounds.Top);
            }

            return bounds;
        }

        private static StepSilhouetteBounds BoundsForPrimitive(StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
            {
                return new StepSilhouetteBounds
                {
                    Left = Math.Min(primitive.X1, primitive.X2),
                    Bottom = Math.Min(primitive.Y1, primitive.Y2),
                    Right = Math.Max(primitive.X1, primitive.X2),
                    Top = Math.Max(primitive.Y1, primitive.Y2)
                };
            }

            return BoundsForArc(primitive.CenterX, primitive.CenterY, primitive.Radius, primitive.StartAngle, primitive.EndAngle);
        }

        private static StepSilhouetteBounds BoundsForArc(double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            var bounds = new StepSilhouetteBounds
            {
                Left = double.PositiveInfinity,
                Bottom = double.PositiveInfinity,
                Right = double.NegativeInfinity,
                Top = double.NegativeInfinity
            };

            AddArcBoundsPoint(bounds, centerX, centerY, radius, startAngle);
            AddArcBoundsPoint(bounds, centerX, centerY, radius, endAngle);
            foreach (double cardinalAngle in new[] { 0.0, 90.0, 180.0, 270.0, 360.0, 450.0, 540.0, 630.0, 720.0 })
            {
                if (AngleInArcSweep(cardinalAngle, startAngle, endAngle))
                    AddArcBoundsPoint(bounds, centerX, centerY, radius, cardinalAngle);
            }

            return bounds;
        }

        private static void AddArcBoundsPoint(StepSilhouetteBounds bounds, double centerX, double centerY, double radius, double angle)
        {
            double radians = angle * Math.PI / 180.0;
            double x = centerX + radius * Math.Cos(radians);
            double y = centerY + radius * Math.Sin(radians);
            bounds.Left = Math.Min(bounds.Left, x);
            bounds.Bottom = Math.Min(bounds.Bottom, y);
            bounds.Right = Math.Max(bounds.Right, x);
            bounds.Top = Math.Max(bounds.Top, y);
        }

        private static bool AngleInArcSweep(double testAngle, double startAngle, double endAngle)
        {
            while (testAngle < startAngle)
                testAngle += 360.0;
            return testAngle >= startAngle && testAngle <= endAngle;
        }

        private static bool SegmentIntersectsRect(double x1, double y1, double x2, double y2, double left, double bottom, double right, double top)
        {
            if (Math.Max(x1, x2) < left || Math.Min(x1, x2) > right || Math.Max(y1, y2) < bottom || Math.Min(y1, y2) > top)
                return false;

            if (PointInsideRect(x1, y1, left, bottom, right, top) || PointInsideRect(x2, y2, left, bottom, right, top))
                return true;

            return SegmentsIntersect(x1, y1, x2, y2, left, bottom, right, bottom)
                || SegmentsIntersect(x1, y1, x2, y2, right, bottom, right, top)
                || SegmentsIntersect(x1, y1, x2, y2, right, top, left, top)
                || SegmentsIntersect(x1, y1, x2, y2, left, top, left, bottom);
        }

        private static bool PointInsideRect(double x, double y, double left, double bottom, double right, double top)
        {
            return x >= left && x <= right && y >= bottom && y <= top;
        }

        private static bool SegmentsIntersect(double a1X, double a1Y, double a2X, double a2Y, double b1X, double b1Y, double b2X, double b2Y)
        {
            double d1 = SegmentDirection(b1X, b1Y, b2X, b2Y, a1X, a1Y);
            double d2 = SegmentDirection(b1X, b1Y, b2X, b2Y, a2X, a2Y);
            double d3 = SegmentDirection(a1X, a1Y, a2X, a2Y, b1X, b1Y);
            double d4 = SegmentDirection(a1X, a1Y, a2X, a2Y, b2X, b2Y);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;

            return Math.Abs(d1) < 0.000001 && PointOnSegment(b1X, b1Y, b2X, b2Y, a1X, a1Y)
                || Math.Abs(d2) < 0.000001 && PointOnSegment(b1X, b1Y, b2X, b2Y, a2X, a2Y)
                || Math.Abs(d3) < 0.000001 && PointOnSegment(a1X, a1Y, a2X, a2Y, b1X, b1Y)
                || Math.Abs(d4) < 0.000001 && PointOnSegment(a1X, a1Y, a2X, a2Y, b2X, b2Y);
        }

        private static double SegmentDirection(double aX, double aY, double bX, double bY, double cX, double cY)
        {
            return (cX - aX) * (bY - aY) - (bX - aX) * (cY - aY);
        }

        private static bool PointOnSegment(double aX, double aY, double bX, double bY, double cX, double cY)
        {
            return Math.Abs(SegmentDirection(aX, aY, bX, bY, cX, cY)) < 0.000001
                && cX >= Math.Min(aX, bX)
                && cX <= Math.Max(aX, bX)
                && cY >= Math.Min(aY, bY)
                && cY <= Math.Max(aY, bY);
        }

        private static double ProjectionTextWidth(string text)
        {
            return (text ?? string.Empty).Length * AssemblyTextSizeMm * AssemblyTextAverageCharWidthFactor;
        }

        private static double ProjectionTrueTypeVisibleHeight()
        {
            return AssemblyTextSizeMm * AssemblyTextVisibleHeightFactor;
        }

        private sealed class ProjectionTextLocations
        {
            public double DesignatorX { get; set; }
            public double DesignatorY { get; set; }
            public double CommentX { get; set; }
            public double CommentY { get; set; }
        }

        internal static IPCB_Region CreateSolidRegion(IPCB_Group component, TLayerConstant layer, IReadOnlyList<ShapeContour> contours)
        {
            var polygon = AltiumApi.GlobalVars.PCBServer.Internal_PCBGeometricPolygonFactory() as IPCB_GeometricPolygon;
            if (polygon == null)
                throw new InvalidOperationException("Could not create a region polygon.");
            int originX = component?.GetState_XLocation() ?? 0;
            int originY = component?.GetState_YLocation() ?? 0;
            foreach (var source in contours)
            {
                if (source.Points.Count < 3) continue;
                var contour = AltiumApi.GlobalVars.PCBServer.PCBContourFactory();
                foreach (var point in source.Points)
                    contour.AddPoint(AltiumApi.MmToCoord(point.X) + originX, AltiumApi.MmToCoord(point.Y) + originY);
                polygon.Internal_AddContourIsHole(contour, source.IsHole);
            }
            var region = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eRegionObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_Region;
            if (region == null)
                throw new InvalidOperationException("Could not create a footprint region.");
            region.SetState_Kind(TRegionKind.eRegionKind_Copper);
            region.SetState_V7Layer(new V7_Layer(layer));
            region.SetGeometricPolygon(polygon);
            return region;
        }

        public static IPCB_ComponentBody CreateComponentBody(IPCB_Group c, string fileName, double rx, double ry, double rz, double x, double y, double z, string identifier = null, double overallHeightMm = 0)
        {
            var stepModel = AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(TObjectId.eComponentBodyObject, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default) as IPCB_ComponentBody;
            if (stepModel == null) return null;
            var model = stepModel.ModelFactory_FromFilename(fileName, false);
            if (model == null) return null;
            model.SetState(rx, ry, rz, AltiumApi.MmToCoord(z));
            stepModel.SetModel(model);
            stepModel.SetState_FromModel();
            model.SetState(rx, ry, rz, AltiumApi.MmToCoord(z));
            stepModel.SetModel(model);
            SetImportedComponentBodyLayer(stepModel);
            string modelIdentifier = !string.IsNullOrWhiteSpace(identifier)
                ? identifier
                : Path.GetFileNameWithoutExtension(fileName);
            SetComponentBodyIdentifier(stepModel, modelIdentifier);
            SetComponentBodyHeights(stepModel, z, overallHeightMm);
            if (TryGetComponentBodyBoundsMm(c, stepModel, out StepSilhouetteBounds currentBounds))
            {
                FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(currentBounds, x, y);
                stepModel.MoveByXY(AltiumApi.MmToCoord(move.XMm), AltiumApi.MmToCoord(move.YMm));
            }
            else
            {
                // Some Altium builds expose body bounds only after insertion; use the component origin as a fallback.
                int originX = c.GetState_XLocation();
                int originY = c.GetState_YLocation();
                stepModel.MoveByXY(AltiumApi.MmToCoord(x) + originX, AltiumApi.MmToCoord(y) + originY);
            }
            return stepModel;
        }

        public static void SetComponentBodyIdentifier(IPCB_ComponentBody body, string identifier)
        {
            if (body == null || string.IsNullOrWhiteSpace(identifier))
                return;

            body.SetState_Identifier(identifier);
            body.GraphicallyInvalidate();
        }

        public static void SetComponentBodyIdentifiers(IPCB_LibComponent component, string identifier)
        {
            if (component == null || string.IsNullOrWhiteSpace(identifier))
                return;

            foreach (IPCB_ComponentBody body in EnumerateComponentBodies(component))
                SetComponentBodyIdentifier(body, identifier);
        }

        private static IEnumerable<object> EnumerateComponentPrimitives(IPCB_Group component)
        {
            if (component == null)
                yield break;

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(component, "GroupIterator_Create")
                    ?? TryInvokeResult(component, "Internal_GroupIterator_Create");
                if (iterator == null)
                    yield break;

                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;

                    primitive = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(component, "GroupIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<object> EnumerateFilteredComponentProjectionPrimitives(IPCB_Group component)
        {
            if (component == null)
                yield break;

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(component, "GroupIterator_Create")
                    ?? TryInvokeResult(component, "Internal_GroupIterator_Create");
                if (iterator == null)
                    yield break;

                ApplyProjectionCleanupFilters(iterator);
                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;

                    primitive = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(component, "GroupIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<object> EnumerateComponentProjectionPrimitivesByObjectId(IPCB_Group component)
        {
            if (component == null)
                yield break;

            int[] objectIds =
            {
                (int)TObjectId.eTrackObject,
                (int)TObjectId.eArcObject,
                (int)TObjectId.eTextObject,
                (int)TObjectId.eFillObject,
                (int)TObjectId.eRegionObject
            };

            foreach (int objectId in objectIds)
            {
                int emptyRun = 0;
                for (int index = 0; index < 10000 && emptyRun < 100; index++)
                {
                    object primitive = TryInvokeResult(component, "Internal_GetPrimitiveAt", index, objectId);
                    if (primitive == null && index == 0)
                        primitive = TryInvokeResult(component, "Internal_GetPrimitiveAt", 1, objectId);

                    if (primitive == null)
                    {
                        emptyRun++;
                        continue;
                    }

                    emptyRun = 0;
                    yield return primitive;
                }
            }
        }

        private static IEnumerable<object> EnumerateBoardPrimitives(IPCB_Board board)
        {
            if (board == null)
                yield break;

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(board, "BoardIterator_Create")
                    ?? TryInvokeResult(board, "Internal_BoardIterator_Create");
                if (iterator == null)
                    yield break;

                TryInvoke(iterator, "SetState_FilterAll");
                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;

                    primitive = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(board, "BoardIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<object> EnumerateFilteredBoardProjectionPrimitives(IPCB_Board board)
        {
            if (board == null)
                yield break;

            object iterator = null;
            try
            {
                iterator = TryInvokeResult(board, "BoardIterator_Create")
                    ?? TryInvokeResult(board, "Internal_BoardIterator_Create");
                if (iterator == null)
                    yield break;

                ApplyProjectionCleanupFilters(iterator);
                object primitive = TryInvokeResult(iterator, "FirstPCBObject")
                    ?? TryInvokeResult(iterator, "Internal_FirstPCBObject");
                while (primitive != null)
                {
                    yield return primitive;

                    primitive = TryInvokeResult(iterator, "NextPCBObject")
                        ?? TryInvokeResult(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                if (iterator != null)
                    TryInvoke(board, "BoardIterator_Destroy", iterator);
            }
        }

        private static void ApplyProjectionCleanupFilters(object iterator)
        {
            DXP.ITransportSet objectSet = CreateTransportSet(
                (int)TObjectId.eTrackObject,
                (int)TObjectId.eArcObject,
                (int)TObjectId.eTextObject,
                (int)TObjectId.eFillObject,
                (int)TObjectId.eRegionObject);

            TryInvoke(iterator, "AddFilter_ObjectSet", objectSet);

            object pcbLayerSet = CreatePcbLayerSet(TLayerConstant.eMechanical2);
            if (pcbLayerSet != null)
                TryInvoke(iterator, "AddFilter_IPCB_LayerSet", pcbLayerSet);
            else
            {
                DXP.ITransportSet layerSet = CreateTransportSet(
                    new V7_Layer(TLayerConstant.eMechanical2).Number());
                TryInvoke(iterator, "AddFilter_LayerSet", layerSet);
            }
        }

        private static object CreatePcbLayerSet(TLayerConstant layer)
        {
            try
            {
                object layerSetUtils = TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "LayerSet")
                    ?? TryInvokeResult(AltiumApi.GlobalVars.PCBServer, "Internal_LayerSet");
                if (layerSetUtils == null)
                    return null;

                return TryInvokeResult(layerSetUtils, "Factory", new V7_Layer(layer))
                    ?? TryInvokeResult(layerSetUtils, "Internal_Factory", new V7_Layer(layer));
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("CreatePcbLayerSet failed: " + ex.Message);
                return null;
            }
        }

        private static DXP.ITransportSet CreateTransportSet(params int[] values)
        {
            var genericSet = new DXP.GenericSet();
            int[] mask = genericSet.Mask;
            foreach (int value in values)
            {
                if (value < 0)
                    continue;

                int index = value / 32;
                if (index >= mask.Length)
                    continue;

                int bit = value % 32;
                mask[index] |= unchecked((int)(1u << bit));
            }

            return new DXP.TransportSet(genericSet);
        }

        private static bool AddProjectionCleanupCandidate(object primitive, List<object> primitivesToRemove, HashSet<object> seenPrimitives)
        {
            if (!IsProjectionCleanupPrimitive(primitive) || !IsPrimitiveOnLayer(primitive, TLayerConstant.eMechanical2))
                return false;

            if (!seenPrimitives.Add(primitive))
                return false;

            primitivesToRemove.Add(primitive);
            return true;
        }

        private static int ClearMechanical2ByEditorCommand(IPCB_Group component, IReadOnlyList<IPCB_Board> boards)
        {
            if (boards == null || boards.Count == 0)
                return 0;

            IPCB_Board board = boards[0];
            if (board == null)
                return 0;

            try
            {
                IPCB_Library pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
                if (pcbLib != null && component != null)
                {
                    TryInvoke(pcbLib, "SetState_CurrentComponent", component);
                    TryInvoke(board, "ViewManager_FullUpdate");
                }

                TryInvoke(board, "SelectedObjects_Clear");
                LaunchPcbCommand("PCB:DeSelect", "Scope=All");
                TryInvoke(board, "SetState_CurrentLayerV7", new V7_Layer(TLayerConstant.eMechanical2));

                if (!LaunchPcbCommand("PCB:Select", "Scope=Layer"))
                    return 0;

                int selectedCount = GetSelectedObjectCount(board);
                if (selectedCount <= 0)
                {
                    EasyEDALoaderModule.Trace("ClearMechanical2ByEditorCommand selected no Mechanical 2 objects.");
                    return 0;
                }

                if (!LaunchPcbCommand("PCB:DeleteObjects", "Object=FOCUSED"))
                    return 0;

                LaunchPcbCommand("PCB:DeSelect", "Scope=All");
                LaunchPcbCommand("PCB:Zoom", "Action=Redraw");
                RefreshPcbLibraryAfterPrimitiveRemoval(pcbLib, boards, null);
                return selectedCount;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("ClearMechanical2ByEditorCommand failed: " + ex.Message);
                return 0;
            }
        }

        private static bool LaunchPcbCommand(string commandName, string parameters)
        {
            try
            {
                string commandParameters = parameters ?? string.Empty;
                DXP.Utils.RunCommand(commandName, ref commandParameters);
                EasyEDALoaderModule.Trace($"LaunchPcbCommand sent by DXP.Utils.RunCommand: {commandName} {parameters}");
                return true;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"LaunchPcbCommand RunCommand failed: {commandName} {parameters}: {ex.Message}");
            }

            try
            {
                object viewObject = TryInvokeResult(AltiumApi.GlobalVars.Client, "GetCurrentView")
                    ?? TryInvokeResult(AltiumApi.GlobalVars.Client, "Internal_GetCurrentView");
                string commandParameters = parameters ?? string.Empty;
                if (viewObject is DXP.IServerDocumentView serverView)
                {
                    DXP.Utils.MessageRouterSendCommandToModule(commandName, ref commandParameters, serverView);
                    EasyEDALoaderModule.Trace($"LaunchPcbCommand sent by message router: {commandName} {parameters}");
                    return true;
                }

                object launcherObject = TryInvokeResult(AltiumApi.GlobalVars.Client, "GetCommandLauncher")
                    ?? TryInvokeResult(AltiumApi.GlobalVars.Client, "Internal_GetCommandLauncher");
                if (!(launcherObject is DXP.ICommandLauncher launcher))
                {
                    EasyEDALoaderModule.Trace($"LaunchPcbCommand failed: no message router view or command launcher for {commandName} {parameters}");
                    return false;
                }

                launcher.LaunchCommand(commandName, ref commandParameters, viewObject);
                EasyEDALoaderModule.Trace($"LaunchPcbCommand sent by command launcher: {commandName} {parameters}");
                return true;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"LaunchPcbCommand failed: {commandName} {parameters}: {ex.Message}");
                return false;
            }
        }

        private static int GetSelectedObjectCount(IPCB_Board board)
        {
            if (board == null)
                return 0;

            if (TryConvertToInt(TryInvokeResult(board, "SelectedObjectsCount"), out int selectedCount))
                return selectedCount;

            if (TryConvertToInt(TryInvokeResult(board, "GetState_SelectecObjectCount"), out selectedCount))
                return selectedCount;

            return 0;
        }

        private static void RemoveProjectionCleanupCandidates(IPCB_Group component, IReadOnlyList<IPCB_Board> boards, List<object> primitivesToRemove)
        {
            if (component == null || primitivesToRemove == null || primitivesToRemove.Count == 0)
                return;

            IPCB_Library pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            string componentName = GetComponentPattern(component);

            while (primitivesToRemove.Count > 0)
            {
                object primitive = primitivesToRemove[0];
                primitivesToRemove.RemoveAt(0);
                TryInvoke(component, "RemovePCBObject", primitive);
                if (boards != null)
                {
                    foreach (IPCB_Board board in boards)
                        TryInvoke(board, "RemovePCBObject", primitive);
                }

                primitive = null;
            }

            RefreshPcbLibraryAfterPrimitiveRemoval(pcbLib, boards, componentName);
        }

        private static void RefreshPcbLibraryAfterPrimitiveRemoval(IPCB_Library pcbLib, IReadOnlyList<IPCB_Board> boards, string componentName)
        {
            if (boards != null)
            {
                foreach (IPCB_Board board in boards)
                {
                    if (board == null)
                        continue;

                    TryInvoke(board, "ViewManager_FullUpdate");
                    TryInvoke(board, "GraphicalView_ZoomRedraw");
                    TryInvoke(board, "Update_PCBGraphicalView", true, true);
                    TryInvoke(board, "Navigate_RedrawChangedObjectsInBoard");
                }
            }

            if (pcbLib == null)
                return;

            if (!string.IsNullOrWhiteSpace(componentName))
                TryInvoke(pcbLib, "SetBoardToComponentByName", componentName);

            TryInvoke(pcbLib, "RefreshView");
        }

        private static void SyncPcbLibComponentFromBoard(IPCB_Group component)
        {
            if (component == null)
                return;

            TryInvoke(component, "TransferAllPrimitivesBackFromBoard");
        }

        private static void SyncPcbLibComponentToBoard(IPCB_Group component)
        {
            if (component == null)
                return;

            TryInvoke(component, "TransferAllPrimitivesOntoBoard");

            var boards = new List<IPCB_Board>();
            AddDistinctBoard(boards, GetComponentBoard(component));
            AddDistinctBoard(boards, GetCurrentPcbLibraryBoard());
            RefreshPcbLibraryAfterPrimitiveRemoval(
                AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary(),
                boards,
                GetComponentPattern(component));
        }

        private static bool BeginPcbPrimitiveModify(object primitive)
        {
            if (primitive == null)
                return false;

            try
            {
                TryInvoke(primitive, "BeginModify");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EndPcbPrimitiveModify(object primitive, bool modifying, bool changed)
        {
            if (primitive == null || !modifying)
                return;

            try
            {
                TryInvoke(primitive, changed ? "EndModify" : "CancelModify");
            }
            catch
            {
            }
        }

        private static bool IsProjectionCleanupPrimitive(object primitive)
        {
            return primitive is IPCB_Track
                || primitive is IPCB_Arc
                || primitive is IPCB_Text
                || primitive is IPCB_Fill
                || primitive is IPCB_Region;
        }

        private static IEnumerable<IPCB_ComponentBody> EnumerateComponentBodies(IPCB_Group component)
        {
            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (primitive is IPCB_ComponentBody body)
                    yield return body;
            }
        }

        private static byte[] TryExportComponentBodyStep(IPCB_ComponentBody body)
        {
            if (body == null)
                return null;

            string tempPath = Path.Combine(Path.GetTempPath(), "EasyEDA-Reproject-" + Guid.NewGuid().ToString("N") + ".step");
            try
            {
                try
                {
                    if (body.SaveModelToFile(tempPath) && File.Exists(tempPath))
                        return File.ReadAllBytes(tempPath);
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace("SaveModelToFile failed for 3D body projection, trying model fallback: " + ex.Message);
                }

                object modelObject = TryInvokeResult(body, "Internal_GetModel");
                if (modelObject is IPCB_Model model)
                {
                    try
                    {
                        model.ExportToStep(tempPath);
                        if (File.Exists(tempPath))
                            return File.ReadAllBytes(tempPath);
                    }
                    catch
                    {
                    }

                    string modelPath = model.GetFileName();
                    if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
                        return File.ReadAllBytes(modelPath);
                }
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Failed to export 3D body model for projection: " + ex);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }

            return null;
        }

        private static void TryGetComponentBodyModelState(IPCB_ComponentBody body, out double rotX, out double rotY, out double rotZ)
        {
            rotX = 0;
            rotY = 0;
            rotZ = 0;
            if (body == null)
                return;

            object modelObject = TryInvokeResult(body, "Internal_GetModel");
            if (!(modelObject is IPCB_Model model))
                return;

            int dz = 0;
            try
            {
                model.GetState(out rotX, out rotY, out rotZ, out dz);
            }
            catch
            {
                rotX = 0;
                rotY = 0;
                rotZ = model.GetRotation();
            }
        }

        private static StepSilhouetteBounds MeasurePadBounds(IPCB_Group component)
        {
            StepSilhouetteBounds bounds = null;
            foreach (object primitive in EnumerateComponentPrimitives(component))
            {
                if (!(primitive is IPCB_Pad))
                    continue;

                if (!TryGetPrimitiveBoundsMm(component, primitive, out StepSilhouetteBounds padBounds))
                    continue;

                if (bounds == null)
                {
                    bounds = padBounds;
                    continue;
                }

                bounds.Left = Math.Min(bounds.Left, padBounds.Left);
                bounds.Bottom = Math.Min(bounds.Bottom, padBounds.Bottom);
                bounds.Right = Math.Max(bounds.Right, padBounds.Right);
                bounds.Top = Math.Max(bounds.Top, padBounds.Top);
            }

            return bounds;
        }

        public static void SetComponentBodyHeights(IPCB_ComponentBody body, double standoffHeightMm, double overallHeightMm)
        {
            if (body == null)
                return;

            if (standoffHeightMm > 0)
                body.SetStandoffHeight(AltiumApi.MmToCoord(standoffHeightMm));

            if (overallHeightMm > 0)
                body.SetOverallHeight(AltiumApi.MmToCoord(overallHeightMm));
        }

        public static bool CenterComponentBodyMm(IPCB_Group component, IPCB_ComponentBody body, double targetCenterX, double targetCenterY)
        {
            if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds currentBounds))
                return false;

            FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(currentBounds, targetCenterX, targetCenterY);
            if (Math.Abs(move.XMm) <= 0.000001 && Math.Abs(move.YMm) <= 0.000001)
                return true;

            bool changed = false;
            body.BeginModify();
            try
            {
                changed = TranslateComponentBodyModelOriginMm(body, move.XMm, move.YMm);
                if (changed)
                    body.GraphicallyInvalidate();
                return changed;
            }
            finally
            {
                if (changed)
                    body.EndModify();
                else
                    body.CancelModify();
            }
        }

        private static bool TranslateComponentBodyModelOriginMm(IPCB_ComponentBody body, double xMm, double yMm)
        {
            if (body == null)
                return false;

            object modelObject = TryInvokeResult(body, "Internal_GetModel");
            if (!(modelObject is IPCB_Model model))
                return false;

            int originX = 0;
            int originY = 0;
            object origin = TryInvokeResult(model, "Internal_GetOrigin");
            if (origin != null)
            {
                TryGetCoordPointValue(origin, "GetX", "X", out originX);
                TryGetCoordPointValue(origin, "GetY", "Y", out originY);
            }

            var newOrigin = new CoordPoint
            {
                X = originX + AltiumApi.MmToCoord(xMm),
                Y = originY + AltiumApi.MmToCoord(yMm)
            };

            model.SetOrigin(newOrigin);
            body.SetModel(model);
            body.SetState_FromModel();
            SetImportedComponentBodyLayer(body);
            return true;
        }

        private static bool TryGetCoordPointValue(object point, string methodName, string propertyName, out int value)
        {
            value = 0;
            object raw = TryInvokeResult(point, methodName);
            if (raw == null)
            {
                PropertyInfo property = point.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                raw = property?.GetValue(point);
            }

            if (raw == null)
                return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static StepSilhouetteBounds GetComponentBodyBoundsMm(IPCB_Group component, IPCB_ComponentBody body, double fallbackCenterX, double fallbackCenterY, double fallbackWidth, double fallbackHeight)
        {
            StepSilhouetteBounds fallback = new StepSilhouetteBounds
            {
                Left = fallbackCenterX - fallbackWidth / 2.0,
                Bottom = fallbackCenterY - fallbackHeight / 2.0,
                Right = fallbackCenterX + fallbackWidth / 2.0,
                Top = fallbackCenterY + fallbackHeight / 2.0
            };

            if (!TryGetComponentBodyBoundsMm(component, body, out StepSilhouetteBounds bounds))
                return fallback;

            return bounds;
        }

        private static bool TryGetComponentBodyBoundsMm(IPCB_Group component, IPCB_ComponentBody body, out StepSilhouetteBounds bounds)
        {
            return TryGetPrimitiveBoundsMm(component, body, out bounds);
        }

        private static bool TryGetPrimitiveBoundsMm(IPCB_Group component, object primitive, out StepSilhouetteBounds bounds)
        {
            bounds = null;
            if (component == null || primitive == null)
                return false;

            object rect = TryInvokeResult(primitive, "Internal_BoundingRectangle");
            if (rect == null)
                return false;

            if (!TryGetRectCoord(rect, "GetLeft", out int left)
                || !TryGetRectCoord(rect, "GetRight", out int right)
                || !TryGetRectCoord(rect, "GetBottom", out int bottom)
                || !TryGetRectCoord(rect, "GetTop", out int top))
                return false;

            // Pads, artwork and model targets all use the component's local frame.
            // A library component can be located away from the PCB board origin.
            int originX = component.GetState_XLocation();
            int originY = component.GetState_YLocation();

            bounds = new StepSilhouetteBounds
            {
                Left = AltiumApi.CoordToMm(left - originX),
                Bottom = AltiumApi.CoordToMm(bottom - originY),
                Right = AltiumApi.CoordToMm(right - originX),
                Top = AltiumApi.CoordToMm(top - originY)
            };

            if (bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom)
                return false;

            return true;
        }

        private static bool IsPrimitiveOnLayer(object primitive, TLayerConstant layer)
        {
            if (!TryGetPrimitiveLayerNumber(primitive, out int primitiveLayer))
                return false;

            int expectedLayer = (int)layer;
            if (primitiveLayer == expectedLayer)
                return true;

            try
            {
                return primitiveLayer == new V7_Layer(layer).Number();
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPrimitiveLayerNumber(object primitive, out int layer)
        {
            layer = 0;
            if (primitive == null)
                return false;

            object raw = TryInvokeResult(primitive, "GetState_Layer")
                ?? TryInvokeResult(primitive, "Internal_GetState_Layer");
            if (TryConvertToInt(raw, out layer))
                return true;

            object v7Layer = TryInvokeResult(primitive, "Internal_GetState_V7Layer");
            if (v7Layer is IV7_Layer layerObject)
            {
                try
                {
                    layer = new V7_Layer(layerObject).Number();
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryGetRectCoord(object rect, string methodName, out int value)
        {
            value = 0;
            object raw = TryInvokeResult(rect, methodName);
            if (raw == null)
                return false;

            return TryConvertToInt(raw, out value);
        }

        private static bool TryConvertToInt(object raw, out int value)
        {
            value = 0;
            if (raw == null)
                return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertToBool(object raw, out bool value)
        {
            value = false;
            if (raw == null)
                return false;

            try
            {
                value = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TrySetLayer(object target, TLayerConstant layer)
        {
            TryInvoke(target, "SetState_V7Layer", new V7_Layer(layer));
            TryInvoke(target, "SetState_Layer", new V7_Layer(layer));
            TryInvoke(target, "SetState_Layer", layer);
        }

        public static void SetImportedComponentBodyLayer(IPCB_ComponentBody body)
        {
            if (body == null)
                return;

            V7_Layer mechanicalLayer = V7_Layer.MechanicalLayer(1);
            TrySetLayer(body, mechanicalLayer);
        }

        private static void TrySetLayer(object target, V7_Layer layer)
        {
            if (target == null || layer == null)
                return;

            TryInvoke(target, "SetState_V7Layer", layer);
        }

        private static void TryInvoke(object target, string methodName, params object[] args)
        {
            TryInvokeResult(target, methodName, args);
        }

        private static object TryInvokeResult(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            object directResult = TryInvokePcbServer(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbContourMaker(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbContourUtilities(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbGeometricPolygon(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbContour(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbLayerSetUtils(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbLayerStack(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbLayerIterator(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbLibrary(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbBoard(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbGroup(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbComponent(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbComponentBody(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbModel(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbPrimitive(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbCoordObject(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            directResult = TryInvokePcbAbstractIterator(target, methodName, args);
            if (directResult != Missing.Value)
                return directResult;

            foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName || method.GetParameters().Length != args.Length)
                    continue;

                try
                {
                    return method.Invoke(target, args);
                }
                catch
                {
                }
            }

            return null;
        }

        private static object TryInvokePcbServer(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_ServerInterface pcbServer))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetCurrentPCBBoard" when args.Length == 0:
                        return pcbServer.Internal_GetCurrentPCBBoard();
                    case "Internal_GetPCBBoardByPath" when args.Length == 1 && args[0] is string boardPath:
                        return pcbServer.Internal_GetPCBBoardByPath(boardPath);
                    case "Internal_LoadPCBBoardByPath" when args.Length == 1 && args[0] is string loadBoardPath:
                        return pcbServer.Internal_LoadPCBBoardByPath(loadBoardPath);
                    case "LayerSet" when args.Length == 0:
                        return pcbServer.LayerSet();
                    case "Internal_LayerSet" when args.Length == 0:
                        return pcbServer.Internal_LayerSet();
                    case "Internal_PCBContourMaker" when args.Length == 0:
                        return pcbServer.Internal_PCBContourMaker();
                    case "Internal_PCBContourUtilities" when args.Length == 0:
                        return pcbServer.Internal_PCBContourUtilities();
                    case "Internal_PCBGeometricPolygonFactory" when args.Length == 0:
                        return pcbServer.Internal_PCBGeometricPolygonFactory();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbContourMaker(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_ContourMaker contourMaker))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_MakeContour" when args.Length == 3 &&
                        TryConvertToInt(args[1], out int expansion) &&
                        TryConvertToInt(args[2], out int layer):
                        return contourMaker.Internal_MakeContour(args[0], expansion, layer);
                    case "SetState_ArcResolution" when args.Length == 1 && TryConvertToInt(args[0], out int arcResolution):
                        contourMaker.SetState_ArcResolution(arcResolution);
                        return null;
                    case "GetState_ArcResolution" when args.Length == 0:
                        return contourMaker.GetState_ArcResolution();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbContourUtilities(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_ContourUtilities contourUtilities))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_CreateInterfaceList" when args.Length == 0:
                        return contourUtilities.Internal_CreateInterfaceList();
                    case "UnionBatchSet" when args.Length == 2:
                        contourUtilities.UnionBatchSet(args[0], args[1]);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbGeometricPolygon(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_GeometricPolygon geometricPolygon))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "GetState_Count" when args.Length == 0:
                        return geometricPolygon.GetState_Count();
                    case "Internal_GetState_Contour" when args.Length == 1 && TryConvertToInt(args[0], out int contourIndex):
                        return geometricPolygon.Internal_GetState_Contour(contourIndex);
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbContour(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Contour contour))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "GetState_Count" when args.Length == 0:
                        return contour.GetState_Count();
                    case "GetState_PointX" when args.Length == 1 && TryConvertToInt(args[0], out int pointXIndex):
                        return contour.GetState_PointX(pointXIndex);
                    case "GetState_PointY" when args.Length == 1 && TryConvertToInt(args[0], out int pointYIndex):
                        return contour.GetState_PointY(pointYIndex);
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbLayerSetUtils(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_LayerSetUtils layerSetUtils))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Factory" when args.Length == 1 && args[0] is V7_LayerBase layer:
                        return layerSetUtils.Factory(layer);
                    case "Internal_Factory" when args.Length == 1 && args[0] is IV7_Layer internalLayer:
                        return layerSetUtils.Internal_Factory(internalLayer);
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbLayerStack(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_LayerStack layerStack))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetState_TopSignalLayer" when args.Length == 0:
                        return layerStack.Internal_GetState_TopSignalLayer();
                    case "Internal_GetState_BottomSignalLayer" when args.Length == 0:
                        return layerStack.Internal_GetState_BottomSignalLayer();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbLayerIterator(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_LayerIterator layerIterator))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "SetBeforeFirst" when args.Length == 0:
                        layerIterator.SetBeforeFirst();
                        return null;
                    case "First" when args.Length == 0:
                        return layerIterator.First();
                    case "Next" when args.Length == 0:
                        return layerIterator.Next();
                    case "Internal_Layer" when args.Length == 0:
                        return layerIterator.Internal_Layer();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbLibrary(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Library pcbLib))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetState_CurrentComponent" when args.Length == 0:
                        return pcbLib.Internal_GetState_CurrentComponent();
                    case "Internal_GetState_Board" when args.Length == 0:
                        return pcbLib.Internal_GetState_Board();
                    case "Internal_GetComponentByName" when args.Length == 1:
                        return pcbLib.Internal_GetComponentByName(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                    case "Internal_LibraryIterator_Create" when args.Length == 0:
                        return pcbLib.Internal_LibraryIterator_Create();
                    case "ComponentCount" when args.Length == 0:
                        return pcbLib.ComponentCount();
                    case "SetState_CurrentComponent" when args.Length == 1:
                        pcbLib.SetState_CurrentComponent(args[0]);
                        return null;
                    case "SetBoardToComponentByName" when args.Length == 1:
                        return pcbLib.SetBoardToComponentByName(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                    case "RefreshView" when args.Length == 0:
                        pcbLib.RefreshView();
                        return null;
                    case "Navigate_FirstComponent" when args.Length == 0:
                        pcbLib.Navigate_FirstComponent();
                        return null;
                    case "LibraryIterator_Destroy" when args.Length == 1:
                        object iterator = args[0];
                        pcbLib.LibraryIterator_Destroy(ref iterator);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbBoard(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Board board))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_BoardIterator_Create" when args.Length == 0:
                        return board.Internal_BoardIterator_Create();
                    case "BoardIterator_Destroy" when args.Length == 1:
                        object iterator = args[0];
                        board.BoardIterator_Destroy(ref iterator);
                        return null;
                    case "RemovePCBObject" when args.Length == 1:
                        board.RemovePCBObject(args[0]);
                        return null;
                    case "AddPCBObject" when args.Length == 1:
                        board.AddPCBObject(args[0]);
                        return null;
                    case "SelectedObjects_Clear" when args.Length == 0:
                        board.SelectedObjects_Clear();
                        return null;
                    case "SelectedObjects_Add" when args.Length == 1:
                        board.SelectedObjects_Add(args[0]);
                        return null;
                    case "SelectedObjectsCount" when args.Length == 0:
                        return board.SelectedObjectsCount();
                    case "GetState_SelectecObjectCount" when args.Length == 0:
                        return board.GetState_SelectecObjectCount();
                    case "Internal_GetState_SelectecObject" when args.Length == 1:
                        return board.Internal_GetState_SelectecObject(Convert.ToInt32(args[0], CultureInfo.InvariantCulture));
                    case "Internal_GetState_CurrentLayerV7" when args.Length == 0:
                        return board.Internal_GetState_CurrentLayerV7();
                    case "Internal_GetState_LayerStack" when args.Length == 0:
                        return board.Internal_GetState_LayerStack();
                    case "Internal_GetState_LayerStack_V7" when args.Length == 0:
                        return board.Internal_GetState_LayerStack_V7();
                    case "Internal_SignalLayerIterator" when args.Length == 0:
                        return board.Internal_SignalLayerIterator();
                    case "GetState_LayerIsDisplayed" when args.Length == 1 && args[0] is IV7_Layer displayLayer:
                        return board.GetState_LayerIsDisplayed(displayLayer);
                    case "SetState_CurrentLayerV7" when args.Length == 1 && args[0] is IV7_Layer currentLayer:
                        board.SetState_CurrentLayerV7(currentLayer);
                        return null;
                    case "ViewManager_FullUpdate" when args.Length == 0:
                        board.ViewManager_FullUpdate();
                        return null;
                    case "ViewManager_UpdateLayerTabs" when args.Length == 0:
                        board.ViewManager_UpdateLayerTabs();
                        return null;
                    case "GraphicalView_ZoomRedraw" when args.Length == 0:
                        board.GraphicalView_ZoomRedraw();
                        return null;
                    case "Update_PCBGraphicalView" when args.Length == 2:
                        board.Update_PCBGraphicalView(
                            Convert.ToBoolean(args[0], CultureInfo.InvariantCulture),
                            Convert.ToBoolean(args[1], CultureInfo.InvariantCulture));
                        return null;
                    case "Navigate_RedrawChangedObjectsInBoard" when args.Length == 0:
                        board.Navigate_RedrawChangedObjectsInBoard();
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbComponent(object target, string methodName, object[] args)
        {
            try
            {
                if (target is IPCB_LibComponent libComponent)
                {
                    switch (methodName)
                    {
                        case "GetState_Pattern" when args.Length == 0:
                            return libComponent.GetState_Pattern();
                        case "SetState_Pattern" when args.Length == 1:
                            libComponent.SetState_Pattern(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                            return null;
                        case "TransferAllPrimitivesBackFromBoard" when args.Length == 0:
                            libComponent.TransferAllPrimitivesBackFromBoard();
                            return null;
                        case "TransferAllPrimitivesOntoBoard" when args.Length == 0:
                            libComponent.TransferAllPrimitivesOntoBoard();
                            return null;
                    }
                }

                if (target is IPCB_Component component)
                {
                    switch (methodName)
                    {
                        case "GetState_Pattern" when args.Length == 0:
                            return component.GetState_Pattern();
                        case "Internal_GetState_Name" when args.Length == 0:
                            return component.Internal_GetState_Name();
                        case "SetState_Pattern" when args.Length == 1:
                            component.SetState_Pattern(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                            return null;
                    }
                }
            }
            catch
            {
                return null;
            }

            return Missing.Value;
        }

        private static object TryInvokePcbComponentBody(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_ComponentBody body))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetModel" when args.Length == 0:
                        return body.Internal_GetModel();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbModel(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Model model))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GetOrigin" when args.Length == 0:
                        return model.Internal_GetOrigin();
                    case "GetFileName" when args.Length == 0:
                        return model.GetFileName();
                    case "ExportToStep" when args.Length == 1:
                        model.ExportToStep(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbGroup(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Group group))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_GroupIterator_Create" when args.Length == 0:
                        return group.Internal_GroupIterator_Create();
                    case "Internal_GetPrimitiveAt" when args.Length == 2:
                        return group.Internal_GetPrimitiveAt(
                            Convert.ToInt32(args[0], CultureInfo.InvariantCulture),
                            Convert.ToInt32(args[1], CultureInfo.InvariantCulture));
                    case "GroupIterator_Destroy" when args.Length == 1:
                        object iterator = args[0];
                        group.GroupIterator_Destroy(ref iterator);
                        return null;
                    case "RemovePCBObject" when args.Length == 1:
                        group.RemovePCBObject(args[0]);
                        return null;
                    case "AddPCBObject" when args.Length == 1:
                        group.AddPCBObject(args[0]);
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbPrimitive(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_Primitive primitive))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "Internal_BoundingRectangle" when args.Length == 0:
                        return primitive.Internal_BoundingRectangle();
                    case "Internal_GetState_Layer" when args.Length == 0:
                        return primitive.Internal_GetState_Layer();
                    case "Internal_GetState_V7Layer" when args.Length == 0:
                        return primitive.Internal_GetState_V7Layer();
                    case "SetState_V7Layer" when args.Length == 1 && args[0] is IV7_Layer v7Layer:
                        primitive.SetState_V7Layer(v7Layer);
                        return null;
                    case "SetState_Layer" when args.Length == 1 && TryConvertToInt(args[0], out int layer):
                        primitive.SetState_Layer(layer);
                        return null;
                    case "BeginModify" when args.Length == 0:
                        primitive.BeginModify();
                        return null;
                    case "EndModify" when args.Length == 0:
                        primitive.EndModify();
                        return null;
                    case "CancelModify" when args.Length == 0:
                        primitive.CancelModify();
                        return null;
                    case "GraphicallyInvalidate" when args.Length == 0:
                        primitive.GraphicallyInvalidate();
                        return null;
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private static object TryInvokePcbCoordObject(object target, string methodName, object[] args)
        {
            try
            {
                if (target is ICoordRect rect)
                {
                    switch (methodName)
                    {
                        case "GetLeft" when args.Length == 0:
                            return rect.GetLeft();
                        case "GetRight" when args.Length == 0:
                            return rect.GetRight();
                        case "GetBottom" when args.Length == 0:
                            return rect.GetBottom();
                        case "GetTop" when args.Length == 0:
                            return rect.GetTop();
                    }
                }

                if (target is ICoordPoint point)
                {
                    switch (methodName)
                    {
                        case "GetX" when args.Length == 0:
                            return point.GetX();
                        case "GetY" when args.Length == 0:
                            return point.GetY();
                    }
                }
            }
            catch
            {
                return null;
            }

            return Missing.Value;
        }

        private static object TryInvokePcbAbstractIterator(object target, string methodName, object[] args)
        {
            if (!(target is IPCB_AbstractIterator iterator))
                return Missing.Value;

            try
            {
                switch (methodName)
                {
                    case "AddFilter_ObjectSet" when args.Length == 1 && args[0] is DXP.ITransportSet objectSet:
                        iterator.AddFilter_ObjectSet(objectSet);
                        return null;
                    case "AddFilter_LayerSet" when args.Length == 1 && args[0] is DXP.ITransportSet layerSet:
                        iterator.AddFilter_LayerSet(layerSet);
                        return null;
                    case "AddFilter_IPCB_LayerSet" when args.Length == 1:
                        iterator.AddFilter_IPCB_LayerSet(args[0]);
                        return null;
                    case "SetState_FilterAll" when args.Length == 0:
                        iterator.SetState_FilterAll();
                        return null;
                    case "Internal_FirstPCBObject" when args.Length == 0:
                        return iterator.Internal_FirstPCBObject();
                    case "Internal_NextPCBObject" when args.Length == 0:
                        return iterator.Internal_NextPCBObject();
                    default:
                        return Missing.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        private sealed class SelectedCustomPadSource
        {
            public object Primitive { get; set; }
            public IV7_Layer Layer { get; set; }
            public int LayerNumber { get; set; }
            public int ContourLayerNumber { get; set; }
        }

        private sealed class CustomPadRect
        {
            public int Left { get; set; }
            public int Bottom { get; set; }
            public int Right { get; set; }
            public int Top { get; set; }
            public int Radius { get; set; }
        }

        private sealed class CustomPadSourceProperties
        {
            public string Name { get; set; }
            public TExtendedHoleType HoleType { get; set; }
            public int HoleSize { get; set; }
            public int HoleWidth { get; set; }
            public double HoleRotation { get; set; }
            public bool Plated { get; set; }
            public int XLocation { get; set; }
            public int YLocation { get; set; }
            public object Net { get; set; }
        }
    }
}
