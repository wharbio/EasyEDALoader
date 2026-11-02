using DXP;
using PCB;
using SCH;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class EasyEDALoaderModule : ServerModule
    {
        private bool noGUIMode;
        private readonly EasyEdaCommandBridge commandBridge;
        private readonly SynchronizationContext bridgeSynchronizationContext;
        private int loaderDialogOpen;
        private static readonly List<CommandProc> registeredCommandProcs = new List<CommandProc>();

        public EasyEDALoaderModule(IClient argClient)
          : base(argClient, "EasyEDA-Loader")
        {
            noGUIMode = argClient.ProductInfo().SupportsUIFeature("NoGUI", false);
            bridgeSynchronizationContext = SynchronizationContext.Current;
            commandBridge = new EasyEdaCommandBridge();
            commandBridge.CommandReceived += HandleBridgeCommand;
            commandBridge.Start();
            Trace("Module constructed.");
        }

        protected override IServerDocument NewDocumentInstance(string argKind, string argFileName) => (IServerDocument)null;

        protected override void InitializeCommands()
        {
            Trace("InitializeCommands.");
            RegisterCommand("EasyEDARun", new CommandProc(Run));
            RegisterCommand("EasyEDA-Loader:EasyEDARun", new CommandProc(Run));
            RegisterCommand("EasyEDAReproject3D", new CommandProc(ReprojectActiveFootprint3D));
            RegisterCommand("EasyEDA-Loader:EasyEDAReproject3D", new CommandProc(ReprojectActiveFootprint3D));
            RegisterCommand("EasyEDAAlign3DModel", new CommandProc(AlignActiveFootprint3DModel));
            RegisterCommand("EasyEDA-Loader:EasyEDAAlign3DModel", new CommandProc(AlignActiveFootprint3DModel));
            RegisterCommand("EasyEDACreateCustomPadFromSelected", new CommandProc(CreateCustomPadFromSelected));
            RegisterCommand("EasyEDA-Loader:EasyEDACreateCustomPadFromSelected", new CommandProc(CreateCustomPadFromSelected));
            RegisterCommand("EasyEDASwitchTopSignalLayer", new CommandProc(SwitchTopSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchTopSignalLayer", new CommandProc(SwitchTopSignalLayer));
            RegisterCommand("EasyEDASwitchBottomSignalLayer", new CommandProc(SwitchBottomSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchBottomSignalLayer", new CommandProc(SwitchBottomSignalLayer));
            RegisterCommand("EasyEDASwitchNextSignalLayer", new CommandProc(SwitchNextSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchNextSignalLayer", new CommandProc(SwitchNextSignalLayer));
            RegisterCommand("EasyEDASwitchPreviousSignalLayer", new CommandProc(SwitchPreviousSignalLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchPreviousSignalLayer", new CommandProc(SwitchPreviousSignalLayer));
            RegisterCommand("EasyEDASwitchToSelectedPrimitiveLayer", new CommandProc(SwitchToSelectedPrimitiveLayer));
            RegisterCommand("EasyEDA-Loader:EasyEDASwitchToSelectedPrimitiveLayer", new CommandProc(SwitchToSelectedPrimitiveLayer));
            RegisterCommand("EasyEDADuplicateLayout", new CommandProc(DuplicateLayout));
            RegisterCommand("EasyEDA-Loader:EasyEDADuplicateLayout", new CommandProc(DuplicateLayout));
            RegisterCommand("EasyEDAExportShapeAll", new CommandProc(ExportShapeAll));
            RegisterCommand("EasyEDA-Loader:EasyEDAExportShapeAll", new CommandProc(ExportShapeAll));
            RegisterCommand("EasyEDAExportShapeSelected", new CommandProc(ExportShapeSelected));
            RegisterCommand("EasyEDA-Loader:EasyEDAExportShapeSelected", new CommandProc(ExportShapeSelected));
            RegisterCommand("EasyEDAExportShapeAllWithPads", new CommandProc(ExportShapeAllWithPads));
            RegisterCommand("EasyEDA-Loader:EasyEDAExportShapeAllWithPads", new CommandProc(ExportShapeAllWithPads));
            RegisterCommand("EasyEDAExportShapeSelectedWithPads", new CommandProc(ExportShapeSelectedWithPads));
            RegisterCommand("EasyEDA-Loader:EasyEDAExportShapeSelectedWithPads", new CommandProc(ExportShapeSelectedWithPads));
            RegisterCommand("EasyEDAExportShapeSelectedLibraries", new CommandProc(ExportShapeSelectedLibraries));
            RegisterCommand("EasyEDA-Loader:EasyEDAExportShapeSelectedLibraries", new CommandProc(ExportShapeSelectedLibraries));
            RegisterCommand("EasyEDAOpenSymbolLibrary", new CommandProc(OpenSymbolLibrary));
            RegisterCommand("EasyEDA-Loader:EasyEDAOpenSymbolLibrary", new CommandProc(OpenSymbolLibrary));
            RegisterCommand("EasyEDAOpenFootprintLibrary", new CommandProc(OpenFootprintLibrary));
            RegisterCommand("EasyEDA-Loader:EasyEDAOpenFootprintLibrary", new CommandProc(OpenFootprintLibrary));
            RegisterCommand("EasyEDAImportJlcCamArchive", new CommandProc(ImportJlcCamArchive));
            RegisterCommand("EasyEDA-Loader:EasyEDAImportJlcCamArchive", new CommandProc(ImportJlcCamArchive));
            RegisterCommand("EasyEDAImportJlcCamFolder", new CommandProc(ImportJlcCamFolder));
            RegisterCommand("EasyEDA-Loader:EasyEDAImportJlcCamFolder", new CommandProc(ImportJlcCamFolder));
            RegisterCommand("EasyEDAAddEdgeRails", new CommandProc(AddEdgeRails));
            RegisterCommand("EasyEDA-Loader:EasyEDAAddEdgeRails", new CommandProc(AddEdgeRails));
            RegisterCommand("EasyEDAAddTentedContourVias", new CommandProc(AddTentedContourVias));
            RegisterCommand("EasyEDA-Loader:EasyEDAAddTentedContourVias", new CommandProc(AddTentedContourVias));
            RegisterCommand("EasyEDAConvertSelectedRegionToPolygon", new CommandProc(ConvertSelectedRegionToPolygon));
            RegisterCommand("EasyEDA-Loader:EasyEDAConvertSelectedRegionToPolygon", new CommandProc(ConvertSelectedRegionToPolygon));
        }

        private void RegisterCommand(string argCommandId, CommandProc commandProc)
        {
            CommandProc wrappedProc = (IServerDocumentView view, ref string parameters) =>
            {
                try
                {
                    Trace($"Command invoked: {argCommandId}");
                    commandProc(view, ref parameters);
                }
                catch (Exception ex)
                {
                    Trace($"Command failed: {ex}");
                    if (noGUIMode)
                    {
                        throw;
                    }
                    else
                    {
                        int num = (int)MessageBox.Show(ex.Message, "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                    }
                }
            };

            registeredCommandProcs.Add(wrappedProc);
            ((DXP.CommandLauncher)CommandLauncher).RegisterCommand(argCommandId, wrappedProc, null);
            Trace($"Registered command: {argCommandId}");
        }

        internal static void Trace(string message)
        {
            string line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            foreach (string logPath in GetTracePaths())
            {
                try
                {
                    File.AppendAllText(logPath, line);
                    return;
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<string> GetTracePaths()
        {
            string assemblyLocation = typeof(EasyEDALoaderModule).Assembly.Location;
            if (!string.IsNullOrEmpty(assemblyLocation))
                yield return Path.Combine(Path.GetDirectoryName(assemblyLocation), "EasyEDA-Loader.log");

            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApplicationData))
                yield return Path.Combine(localApplicationData, "Altium", "EasyEDA-Loader.log");

            yield return Path.Combine(Path.GetTempPath(), "EasyEDA-Loader.log");
        }

        private static EESCH.SchematicPropertySet BuildSchematicPropertySet(
            SymbolData symbolData,
            FootprintData footprintData,
            EasyedaApi.ProductInfo productInfo,
            string designator,
            string partName,
            string description,
            string footprintName,
            string package,
            string pcbLibraryPath,
            string mounting)
        {
            SymbolParameters symbolParameters = symbolData?.Head?.Parameters;
            string manufacturer = FirstNonEmpty(
                symbolParameters?.Manufacturer,
                GetProductParameter(productInfo, "Manufacturer"),
                GetProductParameter(productInfo, "MFR"),
                GetProductParameter(productInfo, "Mfr."));

            return new EESCH.SchematicPropertySet
            {
                Manufacturer = manufacturer,
                ValueType = EESCH.SelectRuleValueType(designator, partName, description, package),
                Footprint = CleanPropertyValue(footprintName),
                FootprintLibrary = CleanPropertyValue(pcbLibraryPath),
                Package = "",
                Mounting = ""
            };
        }

        private static string SelectPartNumber(ComponentSelection selection, ComponentInfo component, SymbolData symbolData, EasyedaApi.ProductInfo productInfo)
        {
            return SymbolImportRules.SelectDesignItemId(
                manufacturerPart: FirstNonEmpty(
                    symbolData?.Head?.Parameters?.ManufacturerPart,
                    GetProductParameter(productInfo, "Manufacturer Part"),
                    GetProductParameter(productInfo, "ManufacturerPart"),
                    GetProductParameter(productInfo, "MPN"),
                    GetProductParameter(productInfo, "Mfr. Part")),
                symbolName: symbolData?.Head?.Parameters?.Name,
                componentTitle: component?.Title,
                searchResultName: selection?.PartInfo?.Name,
                searchPart: selection?.PartInfo?.Part,
                lcscNumber: component?.Lcsc?.Number,
                szlcscNumber: component?.Szlcsc?.Number);
        }

        private static string SelectFootprintDescription(ComponentInfo component, EasyedaApi.ProductInfo productInfo, string partNumber, string package, string mounting)
        {
            return FootprintMetadataSelector.SelectDescription(
                productInfo?.Description,
                component?.Description,
                component?.PackageDetail?.Title,
                package,
                partNumber,
                mounting,
                productInfo?.Parameters,
                BuildFootprintDescriptionGeometry(component?.PackageDetail?.Footprint));
        }

        private static double SelectFootprintHeightMm(EasyedaApi.ProductInfo productInfo)
        {
            double height = productInfo?.Size?.Z ?? 0;
            return height > 0 ? height : 0;
        }

        private static FootprintDescriptionGeometry BuildFootprintDescriptionGeometry(FootprintData footprintData)
        {
            if (footprintData == null)
                return null;

            var padCenters = new List<Tuple<double, double>>();
            if (footprintData.Shapes != null)
            {
                foreach (var shape in footprintData.Shapes)
                {
                    if (shape is EeFootprintPad pad && IsNumberedElectricalPad(pad))
                        padCenters.Add(Tuple.Create(pad.CenterX, pad.CenterY));
                }
            }

            return new FootprintDescriptionGeometry
            {
                PositionCount = padCenters.Count,
                PitchMm = EstimatePitchMm(padCenters),
                BodyWidthMm = footprintData.BoundingBox?.Width ?? 0,
                BodyHeightMm = footprintData.BoundingBox?.Height ?? 0
            };
        }

        private static bool IsNumberedElectricalPad(EeFootprintPad pad)
        {
            if (pad == null || string.IsNullOrWhiteSpace(pad.Number))
                return false;

            return int.TryParse(pad.Number.Trim(), out _);
        }

        private static double EstimatePitchMm(List<Tuple<double, double>> padCenters)
        {
            if (padCenters == null || padCenters.Count < 2)
                return 0;

            double minX = padCenters[0].Item1;
            double maxX = padCenters[0].Item1;
            double minY = padCenters[0].Item2;
            double maxY = padCenters[0].Item2;
            foreach (var center in padCenters)
            {
                minX = Math.Min(minX, center.Item1);
                maxX = Math.Max(maxX, center.Item1);
                minY = Math.Min(minY, center.Item2);
                maxY = Math.Max(maxY, center.Item2);
            }

            bool useX = (maxX - minX) >= (maxY - minY);
            var coordinates = new List<double>();
            foreach (var center in padCenters)
                coordinates.Add(useX ? center.Item1 : center.Item2);

            coordinates.Sort();
            double pitch = 0;
            for (int i = 1; i < coordinates.Count; i++)
            {
                double delta = Math.Abs(coordinates[i] - coordinates[i - 1]);
                if (delta <= 0.001)
                    continue;

                if (pitch <= 0 || delta < pitch)
                    pitch = delta;
            }

            return pitch;
        }

        private static string InferMounting(FootprintData footprintData)
        {
            if (footprintData?.Shapes == null || footprintData.Layers == null)
                return "";

            bool hasSmd = false;
            bool hasThroughHole = false;
            foreach (var shape in footprintData.Shapes)
            {
                if (shape is EeFootprintPad pad)
                {
                    string layerName = footprintData.Layers.GetLayer(pad.Layer)?.Name;
                    if (string.Equals(layerName, "TopLayer", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layerName, "BottomLayer", StringComparison.OrdinalIgnoreCase))
                        hasSmd = true;
                    else if (string.Equals(layerName, "Multi-Layer", StringComparison.OrdinalIgnoreCase))
                        hasThroughHole = true;
                }
                else if (shape is EeFootprintHole)
                {
                    hasThroughHole = true;
                }
            }

            if (hasSmd && hasThroughHole)
                return "hybrid";
            if (hasSmd)
                return "SMT";
            if (hasThroughHole)
                return "through-hole";

            return "";
        }

        private static string GetProductParameter(EasyedaApi.ProductInfo productInfo, string parameterName)
        {
            if (productInfo?.Parameters == null)
                return "";

            foreach (var kvp in productInfo.Parameters)
            {
                if (string.Equals(kvp.Key, parameterName, StringComparison.OrdinalIgnoreCase))
                    return CleanPropertyValue(kvp.Value);
            }

            return "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                string cleaned = CleanPropertyValue(value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    return cleaned;
            }

            return "";
        }

        private static string CleanPropertyValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-" || value.Trim() == "*")
                return "";

            return value.Trim();
        }

        private void Run(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("Run entered.");
            if (IsLoaderDialogOpen())
            {
                Trace("Run ignored because dialog is already open.");
                return;
            }

            Interlocked.Exchange(ref loaderDialogOpen, 1);
            try
            {
                Dialog dialog = new Dialog((selections, progress) => ImportSelectedComponents(selections, progress));
                DialogResult result = dialog.ShowDialog();
                Trace("Dialog closed with result: " + result);
            }
            finally
            {
                Interlocked.Exchange(ref loaderDialogOpen, 0);
            }
        }

        private void ReprojectActiveFootprint3D(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("ReprojectActiveFootprint3D entered.");
            IPCB_Group component = GetActivePcbLibComponentOrThrow();
            IServerDocument document = GetCurrentDocument();
            int removedCount = 0;
            int projectionCount = 0;
            BeginPcbLibraryEdit(document);
            AltiumApi.GlobalVars.PCBServer.PreProcess();
            try
            {
                projectionCount = EEPCB.ReprojectComponentBodySilhouette(component, out removedCount);
            }
            finally
            {
                AltiumApi.GlobalVars.PCBServer.PostProcess();
                EndPcbLibraryEdit(document);
            }

            MarkCurrentDocumentModified();
            EEPCB.GetPcbGroupBoard(component)?.ViewManager_FullUpdate();
            Trace($"ReprojectActiveFootprint3D completed. Removed={removedCount} Projection={projectionCount}");
        }

        private void AlignActiveFootprint3DModel(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("AlignActiveFootprint3DModel entered.");
            IPCB_Group component = GetActivePcbLibComponentOrThrow();
            IServerDocument document = GetCurrentDocument();
            int alignedCount = 0;
            BeginPcbLibraryEdit(document);
            AltiumApi.GlobalVars.PCBServer.PreProcess();
            try
            {
                alignedCount = EEPCB.AlignComponentBodiesToPads(component);
            }
            finally
            {
                AltiumApi.GlobalVars.PCBServer.PostProcess();
                EndPcbLibraryEdit(document);
            }

            MarkCurrentDocumentModified();
            EEPCB.GetPcbGroupBoard(component)?.ViewManager_FullUpdate();
            Trace($"AlignActiveFootprint3DModel completed. Aligned={alignedCount}");
        }

        private void CreateCustomPadFromSelected(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("CreateCustomPadFromSelected entered.");
            IPCB_Group component = GetActivePcbLibComponentOrThrow();
            IServerDocument document = GetCurrentDocument();
            int replacedCount = 0;
            BeginPcbLibraryEdit(document);
            AltiumApi.GlobalVars.PCBServer.PreProcess();
            try
            {
                replacedCount = EEPCB.CreateCustomPadFromSelected(component);
            }
            finally
            {
                AltiumApi.GlobalVars.PCBServer.PostProcess();
                EndPcbLibraryEdit(document);
            }

            MarkCurrentDocumentModified();
            EEPCB.GetPcbGroupBoard(component)?.ViewManager_FullUpdate();
            Trace($"CreateCustomPadFromSelected completed. Replaced={replacedCount}");
        }

        private void DuplicateLayout(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("DuplicateLayout entered.");
            if (IsLoaderDialogOpen())
            {
                Trace("DuplicateLayout ignored because dialog is already open.");
                return;
            }

            if (!LayoutDuplicationCapture.TryCaptureLayoutDuplicationSession(argContext, out LayoutDuplicationSession session, out string error))
                throw new InvalidOperationException(error);

            Interlocked.Exchange(ref loaderDialogOpen, 1);
            try
            {
                var dialog = new LayoutDuplicatorDialog(session)
                {
                    ShowActivated = true,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
                };

                IntPtr ownerHandle = Process.GetCurrentProcess().MainWindowHandle;
                if (ownerHandle != IntPtr.Zero)
                    new System.Windows.Interop.WindowInteropHelper(dialog).Owner = ownerHandle;

                bool? result = dialog.ShowDialog();
                Trace("DuplicateLayout dialog closed with result: " + result);
                if (result == true)
                    MarkCurrentDocumentModified();
            }
            finally
            {
                Interlocked.Exchange(ref loaderDialogOpen, 0);
            }
        }

        private void ExportShapeAll(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            ExportShape(argContext, ShapeExportScope.AllComponents);
        }

        private void ExportShapeSelected(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            ExportShape(argContext, ShapeExportScope.SelectedComponent);
        }

        private void ExportShapeAllWithPads(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            ExportShape(argContext, ShapeExportScope.AllComponents, includePads: true);
        }

        private void ExportShapeSelectedWithPads(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            ExportShape(argContext, ShapeExportScope.SelectedComponent, includePads: true);
        }

        private void OpenSymbolLibrary(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("OpenSymbolLibrary entered.");
            LibraryNavigation.OpenSelectedSchematicComponentLibrary(argContext);
        }

        private void OpenFootprintLibrary(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("OpenFootprintLibrary entered.");
            LibraryNavigation.OpenSelectedPcbComponentLibrary(argContext);
        }

        private void ImportJlcCamArchive(IServerDocumentView argContext, ref string argParameters)
        {
            if (IsLoaderDialogOpen()) return;
            IPCB_Board board = EEPCB.GetCurrentPcbBoard(argContext);
            if (board == null) throw new InvalidOperationException("Open a PCB document before importing JLCCAM.");
            using (var dialog = new OpenFileDialog { Title = "Import JLCCAM archive", Filter = "JLC production archive (*.rar)|*.rar", CheckFileExists = true, InitialDirectory = JlcCamSettings.LoadArchiveFolder() })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                JlcCamSettings.SaveArchiveFolder(Path.GetDirectoryName(dialog.FileName));
                RunJlcCamDialog(board, () => JlcCamSource.OpenArchive(dialog.FileName));
            }
        }

        private void ImportJlcCamFolder(IServerDocumentView argContext, ref string argParameters)
        {
            if (IsLoaderDialogOpen()) return;
            IPCB_Board board = EEPCB.GetCurrentPcbBoard(argContext);
            if (board == null) throw new InvalidOperationException("Open a PCB document before importing JLCCAM.");
            using (var dialog = new FolderBrowserDialog { Description = "Select extracted JLCCAM production folder", SelectedPath = JlcCamSettings.LoadFolder(), ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                JlcCamSettings.SaveFolder(dialog.SelectedPath);
                RunJlcCamDialog(board, () => JlcCamSource.OpenFolder(dialog.SelectedPath));
            }
        }

        private void RunJlcCamDialog(IPCB_Board board, Func<JlcCamAnalysisSession> open)
        {
            Interlocked.Exchange(ref loaderDialogOpen, 1);
            JlcCamAnalysisSession session = null;
            try
            {
                session = JlcCamAnalyzer.Analyze(open());
                var dialog = new JlcCamImportDialog(session, board) { ShowActivated = true, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
                IntPtr ownerHandle = Process.GetCurrentProcess().MainWindowHandle;
                if (ownerHandle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = ownerHandle;
                bool? dialogResult = dialog.ShowDialog();
                if (dialogResult == true)
                    MarkCurrentDocumentModified();
            }
            catch { session?.Dispose(); throw; }
            finally { Interlocked.Exchange(ref loaderDialogOpen, 0); }
        }

        private void ConvertSelectedRegionToPolygon(IServerDocumentView argContext, ref string argParameters)
        {
            if (IsLoaderDialogOpen()) return;
            IPCB_Board board = EEPCB.GetCurrentPcbBoard(argContext);
            if (board == null || !string.Equals(Path.GetExtension(board.GetState_FileName()), ".PcbDoc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Open a PCB document before converting regions to polygons.");
            RegionPolygonConverter.ConvertSelected(board);
            MarkCurrentDocumentModified();
        }

        private void AddTentedContourVias(IServerDocumentView argContext, ref string argParameters)
        {
            if (IsLoaderDialogOpen()) return;
            IPCB_Board board = EEPCB.GetCurrentPcbBoard(argContext);
            if (board == null || !string.Equals(Path.GetExtension(board.GetState_FileName()), ".PcbDoc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Open a PCB document before adding contour vias.");
            Interlocked.Exchange(ref loaderDialogOpen, 1);
            try
            {
                var dialog = new ContourViaDialog(board);
                IntPtr ownerHandle = Process.GetCurrentProcess().MainWindowHandle;
                if (ownerHandle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = ownerHandle;
                if (dialog.ShowDialog() == true) MarkCurrentDocumentModified();
            }
            finally { Interlocked.Exchange(ref loaderDialogOpen, 0); }
        }

        private void AddEdgeRails(IServerDocumentView argContext, ref string argParameters)
        {
            if (IsLoaderDialogOpen()) return;
            IPCB_Board board = EEPCB.GetCurrentPcbBoard(argContext);
            if (board == null) throw new InvalidOperationException("Open a PCB document before adding edge rails.");
            if (!EdgeRailsPcbReader.TryRead(board, out EdgeRailBounds bounds, out EdgeRailContour contour, out double cornerR) || bounds.IsEmpty)
                throw new InvalidOperationException("Could not read the board outline. Draw a closed board outline before adding edge rails.");
            RunEdgeRailsDialog(board, bounds, contour, cornerR);
        }

        private void RunEdgeRailsDialog(IPCB_Board board, EdgeRailBounds bounds, EdgeRailContour contour, double cornerRMm)
        {
            Interlocked.Exchange(ref loaderDialogOpen, 1);
            try
            {
                var dialog = new EdgeRailsDialog(board, bounds, contour, cornerRMm) { ShowActivated = true, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
                IntPtr ownerHandle = Process.GetCurrentProcess().MainWindowHandle;
                if (ownerHandle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = ownerHandle;
                if (dialog.ShowDialog() == true) MarkCurrentDocumentModified();
            }
            finally { Interlocked.Exchange(ref loaderDialogOpen, 0); }
        }

        private void ExportShapeSelectedLibraries(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            string[] libraryFiles = SelectShapeExportLibraries();
            if (libraryFiles.Length == 0)
                return;

            string targetFolder = SelectShapeExportFolder();
            if (string.IsNullOrWhiteSpace(targetFolder))
                return;

            ExportSelectedShapeLibraries(libraryFiles, targetFolder);
        }

        private void ExportShape(IServerDocumentView argContext, ShapeExportScope scope, bool includePads = false)
        {
            bool diagnosticsEnabled = ShapeExportSettings.LoadDiagnosticsEnabled();
            if (diagnosticsEnabled)
                Trace("ExportShape entered. Scope=" + scope + ", IncludePads=" + includePads);
            string folder = SelectShapeExportFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                if (diagnosticsEnabled)
                    Trace("ExportShape cancelled by user.");
                return;
            }

            if (diagnosticsEnabled)
                Trace("ExportShape diagnostics enabled.");
            ShapeExportResult result;
            using (var progressForm = new ShapeExportProgressForm())
            {
                progressForm.Show();
                progressForm.Report(new ShapeExportProgress
                {
                    Message = "Preparing export...",
                    Detail = folder
                });

                try
                {
                    string sourceLibraryPath = includePads ? GetCurrentDocument()?.GetFileName() : null;
                    result = PcbShapeSvgExporter.Export(scope, folder, argContext, progressForm.Report, diagnosticsEnabled, () => progressForm.IsCancellationRequested, includePads, sourceLibraryPath);
                }
                catch (OperationCanceledException)
                {
                    if (diagnosticsEnabled)
                        Trace("ExportShape cancelled during export.");
                    return;
                }

                progressForm.Report(new ShapeExportProgress
                {
                    Message = "Export complete",
                    Detail = $"{result.FileCount} SVG file(s), {result.PrimitiveCount} primitive(s)",
                    Percent = 100
                });
            }

            string message = $"Exported {result.FileCount} SVG file(s) from {result.ComponentCount} component(s), {result.PrimitiveCount} primitive(s).";
            if (diagnosticsEnabled && result.Warnings.Count > 0)
                Trace("ExportShape warnings: " + string.Join(" | ", result.Warnings));
            if (result.Errors.Count > 0)
                ShowShapeExportErrors(result.Errors);
            if (result.Warnings.Count > 0)
                ShowShapeExportWarnings(result.Warnings, result.DiagnosticsPath);
            if (diagnosticsEnabled)
                Trace("ExportShape completed. " + message.Replace(Environment.NewLine, " | "));
        }

        private static void ShowShapeExportWarnings(IReadOnlyList<string> warnings, string diagnosticsPath)
        {
            string report = string.Join(Environment.NewLine, warnings);
            string summary = "SVG export completed with warnings. Select text or copy the complete report.";
            if (!string.IsNullOrWhiteSpace(diagnosticsPath))
                summary += Environment.NewLine + "Debug file: " + diagnosticsPath;
            using (var dialog = new ShapeExportReportForm(
                "EasyEDA SVG Export Warnings",
                summary,
                report))
            {
                dialog.ShowDialog();
            }
        }

        private void ExportSelectedShapeLibraries(IReadOnlyList<string> libraryFiles, string targetFolder)
        {
            bool diagnosticsEnabled = ShapeExportSettings.LoadDiagnosticsEnabled();
            var errors = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool cancelled = false;

            using (var progressForm = new ShapeExportProgressForm())
            {
                progressForm.Show();
                for (int libraryIndex = 0; libraryIndex < libraryFiles.Count; libraryIndex++)
                {
                    if (progressForm.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    string libraryPath = libraryFiles[libraryIndex];
                    string libraryName = Path.GetFileName(libraryPath);
                    IPCB_Library pcbLibrary = null;
                    bool loadedByExport = false;
                    try
                    {
                        progressForm.Report(new ShapeExportProgress
                        {
                            Message = "Loading " + libraryName,
                            Detail = (libraryIndex + 1) + " of " + libraryFiles.Count,
                            Percent = libraryIndex * 100.0 / libraryFiles.Count
                        });

                        pcbLibrary = AltiumApi.GlobalVars.PCBServer.GetPCBLibraryByPath(libraryPath);
                        if (pcbLibrary == null)
                        {
                            pcbLibrary = AltiumApi.GlobalVars.PCBServer.LoadPCBLibraryFromFile(libraryPath);
                            loadedByExport = pcbLibrary != null;
                        }

                        if (pcbLibrary == null)
                            throw new InvalidOperationException("Altium could not load the PCB library.");

                        int currentLibraryIndex = libraryIndex;
                        Action<ShapeExportProgress> reportLibraryProgress = progress =>
                        {
                            double localPercent = progress?.Percent ?? 0.0;
                            progressForm.Report(new ShapeExportProgress
                            {
                                Message = progress?.Message ?? ("Exporting " + libraryName),
                                Detail = libraryName + " | " + (progress?.Detail ?? ""),
                                Percent = (currentLibraryIndex + (localPercent / 100.0)) * 100.0 / libraryFiles.Count
                            });
                        };

                        ShapeExportResult result = PcbShapeSvgExporter.ExportLibrary(
                            pcbLibrary,
                            targetFolder,
                            reportLibraryProgress,
                            diagnosticsEnabled,
                            () => progressForm.IsCancellationRequested,
                            usedNames,
                            libraryPath);

                        if (diagnosticsEnabled && result.Warnings.Count > 0)
                            Trace(libraryName + " shape export warnings: " + string.Join(" | ", result.Warnings));
                        foreach (string error in result.Errors)
                            errors.Add(libraryName + " / " + error);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(libraryName + ": " + ex.Message);
                        Trace("Shape library export failed for " + libraryPath + ": " + ex);
                        progressForm.Report(new ShapeExportProgress
                        {
                            Message = "Failed " + libraryName,
                            Detail = ex.Message,
                            Percent = (libraryIndex + 1) * 100.0 / libraryFiles.Count
                        });
                    }
                    finally
                    {
                        if (loadedByExport && pcbLibrary != null)
                        try
                        {
                            AltiumApi.GlobalVars.PCBServer.DestroyPCBLibrary(ref pcbLibrary);
                            Application.DoEvents();
                        }
                        catch (Exception ex)
                        {
                            errors.Add(libraryName + ": could not unload library: " + ex.Message);
                            Trace("Shape library unload failed for " + libraryPath + ": " + ex);
                        }
                    }

                    if (cancelled)
                        break;
                }

                if (!cancelled)
                {
                    progressForm.Report(new ShapeExportProgress
                    {
                        Message = errors.Count == 0 ? "Export complete" : "Export completed with errors",
                        Detail = targetFolder,
                        Percent = 100
                    });
                }
            }

            if (errors.Count > 0)
                ShowShapeExportErrors(errors);
        }

        private static void ShowShapeExportErrors(IReadOnlyList<string> errors)
        {
            string message = "Shape export failed for " + errors.Count + " operation(s):"
                + Environment.NewLine
                + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, errors);

            using (var dialog = new ShapeExportErrorForm(message))
                dialog.ShowDialog();
        }

        private string[] SelectShapeExportLibraries()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select PCB libraries";
                dialog.Filter = "Altium PCB libraries (*.PcbLib)|*.PcbLib|All files (*.*)|*.*";
                dialog.DefaultExt = "PcbLib";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.RestoreDirectory = true;

                string lastFolder = ShapeExportSettings.LoadLastLibraryFolder();
                if (!string.IsNullOrWhiteSpace(lastFolder))
                    dialog.InitialDirectory = lastFolder;

                if (dialog.ShowDialog() != DialogResult.OK)
                    return Array.Empty<string>();

                string[] files = dialog.FileNames
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (files.Length > 0)
                    ShapeExportSettings.SaveLastLibraryFolder(Path.GetDirectoryName(files[0]));

                return files;
            }
        }

        private string SelectShapeExportFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select SVG export folder";
                dialog.ShowNewFolderButton = true;
                string lastFolder = ShapeExportSettings.LoadLastFolder();
                if (!string.IsNullOrWhiteSpace(lastFolder))
                    dialog.SelectedPath = lastFolder;

                if (dialog.ShowDialog() != DialogResult.OK)
                    return "";

                ShapeExportSettings.SaveLastFolder(dialog.SelectedPath);
                return dialog.SelectedPath;
            }
        }

        private static bool SameFilePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private void SwitchTopSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchTopSignalLayer entered.");
            if (!EEPCB.SwitchToTopSignalLayer())
                throw new InvalidOperationException("Could not switch to the top signal layer. Open a PCB document and try again.");
        }

        private void SwitchBottomSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchBottomSignalLayer entered.");
            if (!EEPCB.SwitchToBottomSignalLayer())
                throw new InvalidOperationException("Could not switch to the bottom signal layer. Open a PCB document and try again.");
        }

        private void SwitchNextSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchNextSignalLayer entered.");
            if (!EEPCB.SwitchToNextSignalLayer())
                throw new InvalidOperationException("Could not switch to the next displayed signal layer. Open a PCB document with displayed signal layers and try again.");
        }

        private void SwitchPreviousSignalLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchPreviousSignalLayer entered.");
            if (!EEPCB.SwitchToPreviousSignalLayer())
                throw new InvalidOperationException("Could not switch to the previous displayed signal layer. Open a PCB document with displayed signal layers and try again.");
        }

        private void SwitchToSelectedPrimitiveLayer(
          IServerDocumentView argContext,
          ref string argParameters)
        {
            Trace("SwitchToSelectedPrimitiveLayer entered.");
            if (!EEPCB.SwitchToSelectedPrimitiveLayer())
                throw new InvalidOperationException("Could not switch to the selected primitive layer. Open a PCB document, select a PCB primitive with a layer, and try again.");
        }

        private EasyEdaCommandBridge.CommandResponse HandleBridgeCommand(EasyEdaCommandBridge.CommandRequest request)
        {
            string command = request?.Command ?? string.Empty;
            if (IsLoaderDialogOpen())
            {
                return EasyEdaCommandBridge.CommandResponse.Error(
                    "loader-dialog-open",
                    "EasyEDALoader window is open. Close it before running Ulanzi commands.",
                    command);
            }

            if (bridgeSynchronizationContext != null &&
                SynchronizationContext.Current != bridgeSynchronizationContext)
            {
                EasyEdaCommandBridge.CommandResponse response = null;
                Exception exception = null;
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bridgeSynchronizationContext.Post(
                    _ =>
                    {
                        try
                        {
                            response = HandleBridgeCommandOnAltiumThread(request);
                        }
                        catch (Exception ex)
                        {
                            exception = ex;
                        }
                        finally
                        {
                            completed.TrySetResult(true);
                        }
                    },
                    null);

                TimeSpan commandTimeout =
                    string.Equals(command, EasyEdaCommandBridge.CommandExportComponentAssembly, StringComparison.Ordinal) ||
                    string.Equals(command, EasyEdaCommandBridge.CommandExportBoardAssembly, StringComparison.Ordinal) ||
                    string.Equals(command, EasyEdaCommandBridge.CommandExportBoard3D, StringComparison.Ordinal)
                        ? TimeSpan.FromMinutes(5)
                        : TimeSpan.FromSeconds(30);

                if (!completed.Task.Wait(commandTimeout))
                {
                    return EasyEdaCommandBridge.CommandResponse.Error(
                        "command-timeout",
                        "Timed out waiting for Altium to execute EasyEDALoader command.",
                        command);
                }

                if (exception != null)
                    throw exception;

                return response;
            }

            return HandleBridgeCommandOnAltiumThread(request);
        }

        private bool IsLoaderDialogOpen()
        {
            return Interlocked.CompareExchange(ref loaderDialogOpen, 0, 0) != 0;
        }

        private EasyEdaCommandBridge.CommandResponse HandleBridgeCommandOnAltiumThread(EasyEdaCommandBridge.CommandRequest request)
        {
            string command = request?.Command ?? string.Empty;
            string parameters = string.Empty;
            IServerDocumentView context = null;

            switch (command)
            {
                case EasyEdaCommandBridge.CommandOpenLoader:
                    Run(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandReproject3D:
                    ReprojectActiveFootprint3D(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandAlign3DModel:
                    AlignActiveFootprint3DModel(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerTop:
                    SwitchTopSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerBottom:
                    SwitchBottomSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerNext:
                    SwitchNextSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerPrevious:
                    SwitchPreviousSignalLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandLayerSelectedPrimitive:
                    SwitchToSelectedPrimitiveLayer(context, ref parameters);
                    break;
                case EasyEdaCommandBridge.CommandExportComponentAssembly:
                    return ExportComponentAssemblyForBridge(request);
                case EasyEdaCommandBridge.CommandExportBoardAssembly:
                    return ExportBoardAssemblyForBridge(request);
                case EasyEdaCommandBridge.CommandExportBoard3D:
                    return ExportBoard3DForBridge(request);
                default:
                    return EasyEdaCommandBridge.CommandResponse.Error(
                        "invalid-command",
                        "Unknown EasyEDALoader bridge command.",
                        command);
            }

            return EasyEdaCommandBridge.CommandResponse.Ok(command);
        }

        private static EasyEdaCommandBridge.CommandResponse ExportBoard3DForBridge(
            EasyEdaCommandBridge.CommandRequest request)
        {
            string outputPath = request.GetString("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("output_path is required.");
            outputPath = Path.GetFullPath(outputPath);
            if (!string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output_path must end in .png.");

            string side = request.GetString("side", "top").Trim().ToLowerInvariant();
            if (side != "top" && side != "bottom")
                throw new ArgumentException("side must be 'top' or 'bottom'.");

            int dpi = request.GetInt32("dpi", 600);
            if (dpi != 75 && dpi != 150 && dpi != 300 && dpi != 600)
                throw new ArgumentException("dpi must be 75, 150, 300, or 600.");

            IPCB_Board board = EEPCB.GetCurrentPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document before exporting a 3D image.");

            var options = new Pcb3DImageExportOptions
            {
                Side = side,
                Dpi = dpi,
                UseSystemColors = request.GetBoolean("use_system_colors", true),
                WorkspaceColor = request.GetInt32("workspace_color", -1),
                BoardColor = request.GetInt32("board_color", -1),
                SolderMaskColor = request.GetInt32("solder_mask_color", -1),
                SilkColor = request.GetInt32("silk_color", -1),
                CopperColor = request.GetInt32("copper_color", -1)
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            Pcb3DImageExportResult result = Pcb3DImageExporter.Export(board, outputPath, options);
            return EasyEdaCommandBridge.CommandResponse.Ok(EasyEdaCommandBridge.CommandExportBoard3D)
                .WithData("output_path", outputPath)
                .WithData("side", side)
                .WithData("dpi", dpi)
                .WithData("generator", result.GeneratorName)
                .WithData("camera_look_at_x", result.CameraLookAtX)
                .WithData("camera_look_at_y", result.CameraLookAtY)
                .WithData("camera_view_x", result.CameraViewX)
                .WithData("camera_view_y", result.CameraViewY)
                .WithData("camera_units_per_inch", 10000000)
                .WithData("camera_mirror_x", result.CameraMirrorX)
                .WithData("document_modified", false)
                .WithData("document_saved", false);
        }

        private static EasyEdaCommandBridge.CommandResponse ExportComponentAssemblyForBridge(
            EasyEdaCommandBridge.CommandRequest request)
        {
            string component = request.GetString("component");
            string outputPath = request.GetString("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = request.GetString("svg_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("output_path is required.");

            outputPath = Path.GetFullPath(outputPath);
            if (!string.Equals(Path.GetExtension(outputPath), ".svg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output_path must end in .svg.");
            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            IPCB_Board board = EEPCB.GetCurrentPcbBoard();
            PcbShapeSvgExportResult result;
            if (board != null)
            {
                if (string.IsNullOrWhiteSpace(component))
                    throw new ArgumentException("component is required when a PCB document is active.");
                result = PcbShapeSvgExportService.ExportComponent(board, component, outputPath);
            }
            else
            {
                IPCB_Library pcbLibrary = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
                if (pcbLibrary == null)
                    throw new InvalidOperationException("Open a PCB document or PCB library before exporting component assembly artwork.");
                result = PcbShapeSvgExportService.ExportCurrentLibraryFootprint(pcbLibrary, outputPath);
            }

            if (result.FileCount != 1)
            {
                string detail = result.Errors.Count > 0
                    ? string.Join(" | ", result.Errors)
                    : "No Mechanical 2 assembly primitives were found.";
                throw new InvalidOperationException(detail);
            }

            return EasyEdaCommandBridge.CommandResponse.Ok(EasyEdaCommandBridge.CommandExportComponentAssembly)
                .WithData("component", component)
                .WithData("output_path", outputPath)
                .WithData("primitive_count", result.PrimitiveCount)
                .WithData("pad_count", result.PadCount)
                .WithData("pads_included", true)
                .WithData("document_modified", false)
                .WithData("document_saved", false);
        }

        private static EasyEdaCommandBridge.CommandResponse ExportBoardAssemblyForBridge(
            EasyEdaCommandBridge.CommandRequest request)
        {
            string outputPath = request.GetString("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("output_path is required.");
            outputPath = Path.GetFullPath(outputPath);
            if (!string.Equals(Path.GetExtension(outputPath), ".svg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output_path must end in .svg.");

            string side = request.GetString("side", "top").Trim().ToLowerInvariant();
            if (side != "top" && side != "bottom")
                throw new ArgumentException("side must be 'top' or 'bottom'.");

            IPCB_Board board = EEPCB.GetCurrentPcbBoard();
            if (board == null)
                throw new InvalidOperationException("Open a PCB document before exporting assembly artwork.");
            PcbShapeSvgExportResult result = PcbShapeSvgExportService.ExportBoardAssembly(
                board,
                outputPath,
                side == "bottom",
                request.GetBoolean("mirror_bottom", true));

            return EasyEdaCommandBridge.CommandResponse.Ok(EasyEdaCommandBridge.CommandExportBoardAssembly)
                .WithData("side", side)
                .WithData("output_path", outputPath)
                .WithData("component_count", result.ComponentCount)
                .WithData("primitive_count", result.PrimitiveCount)
                .WithData("pad_count", result.PadCount)
                .WithData("pads_included", true)
                .WithData("document_modified", false)
                .WithData("document_saved", false);
        }

        private static IPCB_Group GetActivePcbLibComponentOrThrow()
        {
            IPCB_Group component = EEPCB.GetCurrentPcbLibComponent();
            if (component == null)
                throw new InvalidOperationException("Open a PCB library and select a footprint before running this command.");

            return component;
        }

        private void MarkCurrentDocumentModified()
        {
            try
            {
                GetCurrentDocument()?.SetModified(true);
            }
            catch
            {
            }
        }

        private void ShowInfo(string message)
        {
            if (noGUIMode)
                return;

            MessageBox.Show(message, "EasyEDA Loader", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool ImportSelectedComponents(
            IReadOnlyList<ComponentSelection> selections,
            Action<ImportProgressEvent> progress)
        {
            if (selections == null || selections.Count == 0)
                return false;

            var currentDoc = GetCurrentDocument();

            var ctx = new CancellationTokenSource();
            var api = new EasyedaApi();

            IServerDocument tempPcbDocument = null;
            var importLog = new ImportLog();
            IServerDocument tempSchDocument = null;
            IPCB_Library tempPcbLib = null;
            IPCB_Library activePcbLib = null;
            ISch_Lib tempSchLib = null;
            ISch_Lib activeSchLib = null;
            string tempPcbLibraryPath = "";
            bool hadErrors = false;

            ReportImportProgress(progress, "Preparing target libraries...", 25);

            if (selections.Any(selection => selection.ImportTarget == ComponentImportTarget.TemporaryLibraries))
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string libraryPath = Path.Combine(documentsPath, "AltiumEE");
                Directory.CreateDirectory(libraryPath);
                tempPcbLibraryPath = Path.Combine(libraryPath, "EasyEDA.pcblib");
                string schLibraryPath = Path.Combine(libraryPath, "EasyEDA.schlib");

                ReportImportProgress(progress, "Opening temporary PCB library...", 27);
                tempPcbDocument = AltiumApi.GlobalVars.Client.OpenDocument("PcbLib", tempPcbLibraryPath);
                AltiumApi.GlobalVars.Client.ShowDocument(tempPcbDocument);
                tempPcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();

                ReportImportProgress(progress, "Opening temporary schematic library...", 29);
                tempSchDocument = AltiumApi.GlobalVars.Client.OpenDocument("SchLib", schLibraryPath);
                AltiumApi.GlobalVars.Client.ShowDocument(tempSchDocument);
                tempSchLib = EESCH.GetCurrentSchLibrary();
            }

            if (selections.Any(selection => selection.ImportTarget == ComponentImportTarget.ActivePcbLibrary && selection.IncludeFootprint))
            {
                activePcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
                if (activePcbLib == null)
                {
                    ReportImportProgress(progress, "Open and activate a PCB library before adding a footprint.", 30, false, true);
                    return false;
                }
            }

            if (selections.Any(selection => selection.ImportTarget == ComponentImportTarget.ActiveSchLibrary && selection.IncludeSymbol))
            {
                activeSchLib = GetActiveSchLibrary();
                if (activeSchLib == null)
                {
                    ReportImportProgress(progress, "Open and activate a schematic library before adding a symbol.", 30, false, true);
                    return false;
                }
            }

            // Process each selected component
            for (int selectionIndex = 0; selectionIndex < selections.Count; selectionIndex++)
            {
                var selection = selections[selectionIndex];
                double componentStartProgress = 30.0 + (selectionIndex * 60.0 / selections.Count);
                double componentEndProgress = 30.0 + ((selectionIndex + 1) * 60.0 / selections.Count);
                try
                {
                    var root = selection.Root;
                    var ee_footprint = root.Component.PackageDetail?.Footprint;
                    var ee_symbol = root.Component.GetPrimarySymbol();
                    string package = FirstNonEmpty(ee_footprint?.Head?.Parameters?.Package, selection.PartInfo?.Name, selection.PartInfo?.Part);
                    EasyedaApi.ProductInfo productInfo = selection.PartInfo?.Info;
                    string partNumber = SelectPartNumber(selection, root.Component, ee_symbol, productInfo);
                    string partName = FirstNonEmpty(partNumber, ee_symbol?.Head?.Parameters?.Name, root.Component.Title, selection.PartInfo?.Name, selection.PartInfo?.Part);
                    string mounting = InferMounting(ee_footprint);
                    EeFootprint3dModel model = selection.Include3dModel ? ee_footprint?.GetModel() : null;

                    ReportImportProgress(
                        progress,
                        $"Processing {selectionIndex + 1}/{selections.Count}: {partName}",
                        componentStartProgress);

                    // Prefetch model if we can.
                    string modelTraceIdentifier = FirstNonEmpty(partNumber, partName, model?.Uuid);
                    Task<byte[]> modelTask = model != null
                        ? ModelImportTrace.MeasureAsync("model_download_cache_read", modelTraceIdentifier, () => ModelCache.GetStepModelAsync(api, model.Uuid, ctx.Token))
                        : null;
                    Task<byte[]> rawModelTask = model != null
                        ? ModelImportTrace.MeasureAsync("raw_obj_download_cache_read", modelTraceIdentifier, () => ModelCache.GetRawObjModelAsync(api, model.Uuid, ctx.Token))
                        : null;

                    // Get product info (use cached from search if available)
                    string footprintName = FootprintMetadataSelector.SelectName(package, partNumber);
                    string footprintDescription = SelectFootprintDescription(root.Component, productInfo, partNumber, package, mounting);
                    double footprintHeight = SelectFootprintHeightMm(productInfo);
                    ReportImportProgress(
                        progress,
                        $"Metadata selected: package='{package}', part='{partNumber}', footprint='{footprintName}', description='{footprintDescription}'",
                        componentStartProgress + 2.0);

                    IPCB_Library targetPcbLib = null;
                    IServerDocument targetPcbDocument = null;
                    ISch_Lib targetSchLib = null;
                    IServerDocument targetSchDocument = null;
                    string pcbLibraryPath = "";

                    if (selection.ImportTarget == ComponentImportTarget.TemporaryLibraries)
                    {
                        targetPcbLib = tempPcbLib;
                        targetPcbDocument = tempPcbDocument;
                        targetSchLib = tempSchLib;
                        targetSchDocument = tempSchDocument;
                        pcbLibraryPath = tempPcbLibraryPath;
                    }
                    else if (selection.ImportTarget == ComponentImportTarget.ActivePcbLibrary)
                    {
                        targetPcbLib = activePcbLib;
                        targetPcbDocument = currentDoc;
                    }
                    else if (selection.ImportTarget == ComponentImportTarget.ActiveSchLibrary)
                    {
                        targetSchLib = activeSchLib;
                        targetSchDocument = currentDoc;
                    }

                        // Create PCB footprint if requested
                        if (selection.IncludeFootprint)
                        {
                            if (ee_footprint == null)
                                throw new InvalidOperationException("The selected component does not include a footprint.");
                            if (targetPcbLib == null)
                                throw new InvalidOperationException("No target PCB library is active.");

                            ReportImportProgress(progress, $"Creating PCB footprint: {footprintName}", componentStartProgress + 5.0);

                            if (targetPcbDocument != null)
                                AltiumApi.GlobalVars.Client.ShowDocument(targetPcbDocument);

                            var libComp = targetPcbLib.GetComponentByName(footprintName);
                            bool createdFootprint = false;
                            if (libComp == null)
                            {
                                libComp = EEPCB.CreateFootprintInLib(footprintName, footprintDescription, footprintHeight);
                                createdFootprint = libComp != null;
                                Trace($"PCB footprint create requested='{footprintName}' actual='{EEPCB.GetComponentPattern(libComp)}' description='{footprintDescription}'");
                            }
                            else
                            {
                                EEPCB.SetFootprintMetadata(libComp, footprintDescription, footprintHeight);
                                EEPCB.SetComponentBodyIdentifiers(libComp, partNumber);
                                Trace($"PCB footprint update requested='{footprintName}' actual='{EEPCB.GetComponentPattern(libComp)}' description='{footprintDescription}'");
                            }

                            if (createdFootprint)
                            {
                                var footprintContext = new EeFootprintContext
                                {
                                    Box = ee_footprint.BoundingBox,
                                    Layers = ee_footprint.Layers,
                                    OriginX = ee_footprint.Head?.X,
                                    OriginY = ee_footprint.Head?.Y,
                                    CancelToken = ctx.Token,
                                    Exception = (Exception ex) =>
                                    {
                                        importLog.Error($"Footprint {footprintName} ({partNumber})", ex);
                                        ReportImportProgress(progress, "Footprint warning: " + ex.Message, null, true, true);
                                        return true;
                                    },
                                    ModelTask = modelTask,
                                    RawModelTask = rawModelTask,
                                    RemoveWatermark = selection.RemoveWatermark,
                                    CleanText = selection.CleanText,
                                    ImportLcscMechanicalLayers = selection.ImportLcscMechanicalLayers,
                                    ImportAdditionalLayers = selection.ImportAdditionalLayers,
                                    PartNumber = partNumber,
                                    CachePartNumber = selection.PartInfo?.Part,
                                    Description = footprintDescription,
                                    HeightMm = footprintHeight,
                                    ModelOffset = productInfo?.Offset,
                                };
                                ReportImportProgress(progress, $"Adding footprint primitives: {footprintName}", componentStartProgress + 12.0);
                                ee_footprint.AddToComponent(libComp, footprintContext);
                                ReportImportProgress(progress, $"PCB footprint complete: {footprintName}", componentStartProgress + 25.0);
                            }
                        }

                        // Create schematic symbol
                        if (selection.IncludeSymbol)
                        {
                            if (ee_symbol == null)
                                throw new InvalidOperationException("The selected component does not include a schematic symbol.");
                            if (targetSchLib == null)
                                throw new InvalidOperationException("No target schematic library is active.");

                            ReportImportProgress(progress, $"Creating schematic symbol: {partName}", componentStartProgress + 32.0);

                            if (targetSchDocument != null)
                                AltiumApi.GlobalVars.Client.ShowDocument(targetSchDocument);

                            string description = SymbolImportRules.SelectSymbolDescription(
                                productInfo?.Description,
                                root.Component?.Description,
                                root.Component?.PackageDetail?.Title,
                                package,
                                partNumber,
                                mounting,
                                productInfo?.Parameters,
                                BuildFootprintDescriptionGeometry(ee_footprint));
                            string designator = EESCH.SelectRuleDesignator(ee_symbol.Head?.Parameters?.Pre, partName, description, package);

                            var existingComponent = targetSchLib.GetState_SchComponentByLibRef(partName);
                            if (existingComponent == null)
                            {
                                var component = EESCH.CreateComponent(partName, description, designator);
                                if (component != null)
                                {
                                    BeginSchematicLibraryEdit(targetSchDocument);
                                    try
                                    {
                                        bool isConnector = SymbolImportRules.IsConnectorDesignator(designator);
                                        targetSchLib.SetState_Current_SchComponent(component);
                                        SymbolDrawing.CreateMultiPartComponent(targetSchLib, component, pcbLibraryPath, footprintName, root.Component.GetSymbolParts(), isConnector);

                                        EESCH.ApplyGostPropertySet(component, BuildSchematicPropertySet(ee_symbol, ee_footprint, productInfo, designator, partName, description, footprintName, package, pcbLibraryPath, mounting));
                                    }
                                    finally
                                    {
                                        EndSchematicLibraryEdit(targetSchDocument);
                                    }
                                    targetSchLib.SetState_Current_SchComponent(component);
                                    targetSchLib.GraphicallyInvalidate();
                                    targetSchDocument?.SetModified(true);
                                    ReportImportProgress(progress, $"Schematic symbol complete: {partName}", componentStartProgress + 50.0);
                                }
                            }
                        }

                        ReportImportProgress(progress, $"Finished {partName}", componentEndProgress);
                    }
                    catch (Exception ex)
                    {
                        hadErrors = true;
                        importLog.Error($"Component {selection.PartInfo?.Name}", ex);
                        ReportImportProgress(progress, $"Failed to process component {selection.PartInfo.Name}: {ex.Message}", componentEndProgress, false, true);
                    }
                }

            ReportImportProgress(progress, "Applying import save policy...", 92);
            ImportLibrarySavePolicy.EnsureAutomaticLibrarySaveIsDisabled();
            Trace("Imported libraries left unsaved by import policy.");

            // Return to the original document we started in
            if (currentDoc != null)
            {
                ReportImportProgress(progress, "Returning to original document...", 98);
                AltiumApi.GlobalVars.Client.ShowDocument(currentDoc);
            }

            ctx.Dispose();

            if (importLog.ErrorCount > 0)
            {
                string detail = importLog.WriteFailed
                    ? "Some diagnostics could not be written. Review the log above."
                    : "Details: " + importLog.LogPath;
                ReportImportProgress(progress, $"{importLog.ErrorCount} import warning(s) or error(s). {detail}", 100, false, true);
            }

            if (hadErrors)
            {
                ReportImportProgress(progress, "Import finished with errors. Review the log above.", 100, false, true);
                return false;
            }

            ReportImportProgress(progress, importLog.ErrorCount == 0 ? "Import finished." : "Import finished with warnings.", 100);
            return true;
        }

        private static void ReportImportProgress(
            Action<ImportProgressEvent> progress,
            string message,
            double? percent = null,
            bool isIndeterminate = false,
            bool isError = false)
        {
            Trace(message);
            progress?.Invoke(new ImportProgressEvent
            {
                Message = message,
                Percent = percent,
                IsIndeterminate = isIndeterminate || !percent.HasValue,
                IsError = isError
            });
        }

        private static void BeginSchematicLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PreProcess(document, "");
        }

        private static void BeginPcbLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PreProcess(document, "");
        }

        private static void EndSchematicLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PostProcess(document, "");
        }

        private static void EndPcbLibraryEdit(IServerDocument document)
        {
            if (document == null)
                return;

            AltiumApi.GlobalVars.Client.GetProcessControl()?.PostProcess(document, "");
        }

        private static ISch_Lib GetActiveSchLibrary()
        {
            try
            {
                var schDoc = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument();
                if (schDoc == null || schDoc.GetState_ObjectId() != SCH.TObjectId.eSchLib)
                    return null;

                return schDoc as ISch_Lib;
            }
            catch
            {
                return null;
            }
        }

        private static IServerDocument GetCurrentDocument()
        {
            try
            {
                return AltiumApi.GlobalVars.Client.GetCurrentView()?.GetOwnerDocument();
            }
            catch
            {
                return null;
            }
        }
    }
}
