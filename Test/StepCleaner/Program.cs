using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SkiaSharp;
using StepF3DRenderLib;

namespace StepCleaner.Tests
{
    internal static class Program
    {
        private const int ProjectionDifferenceTolerance = 6;
        private const int AllowedDetectionRegionPaddingPixels = 16;
        private const double MaxOutsideDetectionRegionChangeRatio = 0.01;
        private const int VerificationProjectionImageSizePixels = 1000;
        private const int VerificationProjectionPaddingPixels = 50;
        private const int FlatnessEdgeThreshold = 28;
        private const double MinOriginalRegionEdgeRatioForFlatness = 0.08;
        private const double MaxCleanedRegionEdgeRatio = 0.035;
        private const double MaxRetainedRegionEdgeRatio = 0.45;
        private const string DefaultMeasurePartNumber = "C5338332";
        private const int OcctHlrBenchmarkMinimumExpectedLines = 290;
        private const int OcctHlrBenchmarkMinimumExpectedArcs = 20;

        private static int Main(string[] args)
        {
            if (args.Length > 0)
                return RunCommand(args);

            try
            {
                Stopwatch fullTestStopwatch = Stopwatch.StartNew();
                string dataRoot = FindDataRoot();
                string originalDirectory = Path.Combine(dataRoot, "Original");
                string cleanDirectory = Path.Combine(dataRoot, "Clean");
                string validatedDirectory = Path.Combine(dataRoot, "Validated");
                string markedDirectory = Path.Combine(dataRoot, "Marked");
                string projectionDirectory = Path.Combine(dataRoot, "Projection");
                string originalCleanCompareProjectionDirectory = Path.Combine(dataRoot, "OriginalCleanCompareProjection");
                string cleanProjectionDirectory = Path.Combine(dataRoot, "CleanProjection");
                string validatedProjectionDirectory = Path.Combine(dataRoot, "ValidatedProjection");
                string failedProjectionReportPath = Path.Combine(dataRoot, "FailedProjectionReport.md");
                string failedProjectionReportDirectory = Path.Combine(dataRoot, "FailedProjectionReport");
                string detectionDirectory = Path.Combine(cleanDirectory, "Detection");

                Directory.CreateDirectory(cleanDirectory);

                var originalFiles = GetStepFiles(originalDirectory);
                var validatedFiles = GetStepFiles(validatedDirectory);
                var validatedByName = validatedFiles.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                var originalBaseNames = new HashSet<string>(
                    originalFiles.Select(file => Path.GetFileNameWithoutExtension(file)),
                    StringComparer.OrdinalIgnoreCase);
                var generatedCleanByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var detectionViewNamesByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var postCleanFaultFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var failures = new List<string>();
                var visualFailures = new List<ProjectionVisualFailure>();
                var projectionTimings = new ProjectionVerificationTimings();
                var detectionCache = new FullTestDetectionCache();

                if (originalFiles.Count == 0)
                    failures.Add("No STEP files were found in Original.");

                if (validatedFiles.Count == 0)
                    failures.Add("No STEP files were found in Validated.");

                VerifyCleanupIgnoresMarkedOptions(originalFiles, failures);

                int cleanupParallelism = GetFullRegressionCleanupParallelism();
                Console.WriteLine("full_regression_cleanup_parallelism=" + cleanupParallelism.ToString(CultureInfo.InvariantCulture));
                IReadOnlyList<FullRegressionCleanResult> cleanResults =
                    CleanOriginalFilesForFullRegression(originalFiles, cleanDirectory, cleanupParallelism);
                foreach (FullRegressionCleanResult cleanResult in cleanResults)
                {
                    string fileName = Path.GetFileName(cleanResult.OriginalFile);
                    if (cleanResult.Exception != null)
                    {
                        failures.Add(
                            "Failed to clean " +
                            fileName +
                            ": " +
                            cleanResult.Exception.Message);
                        continue;
                    }

                    Console.WriteLine(
                        "Cleaned " +
                        fileName +
                        " clean_ms=" +
                        cleanResult.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                    if (cleanResult.ElapsedMilliseconds > 30000)
                        PrintTopCleanerTiming(cleanResult.Report, "  ");
                    generatedCleanByName[fileName] = cleanResult.OutputFile;
                    detectionCache.SetReport(cleanResult.OriginalFile, cleanResult.Report?.DetectionReport);
                    if (!validatedByName.TryGetValue(fileName, out string validatedFile))
                    {
                        failures.Add(
                            "Clean output is missing from Validated, so it is treated as not fully cleaned. " +
                            "Please view the generated clean model before accepting it: " +
                            cleanResult.OutputFile);
                        continue;
                    }
                }

                VerifyDetectionDebugImages(
                    originalFiles,
                    originalBaseNames,
                    projectionDirectory,
                    markedDirectory,
                    detectionDirectory,
                    detectionCache,
                    regenerateImages: false,
                    failures);

                foreach (string note in GetCleanupNotes())
                    Console.WriteLine("Cleanup note: " + note);

                foreach (string validatedFile in validatedFiles)
                {
                    string fileName = Path.GetFileName(validatedFile);
                    if (!generatedCleanByName.ContainsKey(fileName))
                        failures.Add("Validated file has no matching Original model or generated Clean output: " + fileName);
                }

                var verificationProjectionOptions = CreateVerificationProjectionOptions();
                projectionTimings.Measure(
                    "clean_projection_render_ms",
                    () => ProjectDirectoryIfNeeded(cleanDirectory, cleanProjectionDirectory, verificationProjectionOptions));

                VerifyPostCleanProjections(
                    originalFiles,
                    generatedCleanByName,
                    originalCleanCompareProjectionDirectory,
                    cleanProjectionDirectory,
                    verificationProjectionOptions,
                    detectionViewNamesByFileName,
                    detectionCache,
                    projectionTimings,
                    postCleanFaultFileNames,
                    failures,
                    visualFailures);

                CompareCleanAndValidatedProjections(
                    generatedCleanByName,
                    validatedByName,
                    validatedDirectory,
                    cleanProjectionDirectory,
                    validatedProjectionDirectory,
                    verificationProjectionOptions,
                    detectionViewNamesByFileName,
                    projectionTimings,
                    postCleanFaultFileNames,
                    failures,
                    visualFailures);

                projectionTimings.Measure(
                    "failed_projection_report_ms",
                    () => WriteFailedProjectionReport(
                        failedProjectionReportPath,
                        failedProjectionReportDirectory,
                        visualFailures));
                if (visualFailures.Count > 0)
                    Console.WriteLine("Failed projection report: " + failedProjectionReportPath);
                projectionTimings.WriteToConsole();

                if (failures.Count > 0)
                {
                    Console.Error.WriteLine("STEP cleaner regression test failed.");
                    foreach (string failure in failures)
                        Console.Error.WriteLine("  " + failure);

                    fullTestStopwatch.Stop();
                    Console.WriteLine("full_test_wall_ms=" + fullTestStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                    return 1;
                }

                Console.WriteLine(
                    "STEP cleaner regression test passed. Cleaned " +
                    originalFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " original file(s), compared " +
                    validatedFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " validated file(s).");
                fullTestStopwatch.Stop();
                Console.WriteLine("full_test_wall_ms=" + fullTestStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP cleaner regression test failed: " + ex.Message);
                return 1;
            }
        }

        private static int RunCommand(string[] args)
        {
            if (IsOption(args[0], "--metadata"))
                return RunMetadataTests();

            if (IsOption(args[0], "--symbol-rules"))
                return RunSymbolRuleTests();

            if (IsOption(args[0], "--import-save-policy"))
                return RunImportSavePolicyTests();

            if (IsOption(args[0], "--async-import"))
                return RunAsyncImportTests();

            if (IsOption(args[0], "--footprint-placement"))
                return RunFootprintPlacementTests();

            if (IsOption(args[0], "--footprint-layers"))
                return RunFootprintLayerTests();

            if (IsOption(args[0], "--pcblib-actions"))
                return RunPcbLibActionTests();

            if (IsOption(args[0], "--ulanzi-plugin"))
                return RunUlanziPluginTests();

            if (IsOption(args[0], "--model-cache"))
                return RunModelCacheTests();

            if (IsOption(args[0], "--measure-model-import"))
                return RunModelImportMeasurement(args);

            if (IsOption(args[0], "--silhouette-cleanup"))
                return RunSilhouetteCleanupTests();

            if (IsOption(args[0], "--occt-hlr-smoke"))
                return OcctHiddenLineProjectionSmokeTests.Run();

            if (IsOption(args[0], "--occt-hlr-benchmark"))
                return RunOcctHlrBenchmark(args);

            if (IsOption(args[0], "--occt-overlap-unit"))
                return OcctOverlapCleanupTests.Run();

            if (IsOption(args[0], "--occt-stage-report"))
                return OcctSilhouetteStageReport.Run(args);

            if (IsOption(args[0], "--f3d-buffer-smoke"))
                return RunF3DBufferSmoke(args);

            if (IsOption(args[0], "--f3d-preview-smoke"))
                return RunF3DPreviewSmoke(args);

            if (IsOption(args[0], "--f3d-no-ambient-occlusion"))
                return RunF3DNoAmbientOcclusionTests();

            if (IsOption(args[0], "--clean-text"))
                return RunCleanTextTests();

            if (IsOption(args[0], "--xt60-lceda"))
                return RunXt60LcedaWatermarkTests();

            if (IsOption(args[0], "--projection-edge-mode"))
                return RunProjectionEdgeModeTests();

            if (IsOption(args[0], "--raw-silhouette-edge-projection"))
                return RunRawSilhouetteEdgeProjectionTests();
            if (IsOption(args[0], "--visible-raw-silhouette-edge-projection"))
                return RunVisibleRawSilhouetteEdgeProjectionTests();
            if (IsOption(args[0], "--vector-projection-contract"))
                return RunVectorProjectionContractTests(args);
            if (IsOption(args[0], "--edge-preview"))
                return SaveEdgePreview(args);

            if (IsOption(args[0], "--watermark-template-library"))
                return RunWatermarkTemplateLibraryTests();

            if (IsOption(args[0], "--text-logo-detection"))
                return RunTextLogoDetectionTests();

            if (IsOption(args[0], "--marked-detection-parity"))
                return RunMarkedVectorDetectionParityTests(false);

            if (IsOption(args[0], "--marked-detection-parity-clean-text"))
                return RunMarkedVectorDetectionParityTests(true);

            if (IsOption(args[0], "--marked-vector-detection-parity"))
                return RunMarkedVectorDetectionParityTests(false);

            if (IsOption(args[0], "--marked-vector-detection-parity-clean-text"))
                return RunMarkedVectorDetectionParityTests(true);

            if (IsOption(args[0], "--vector-text-detector-smoke"))
                return RunVectorTextDetectorSmokeTests();

            if (IsOption(args[0], "--vector-text-detector-dual-pass-contract"))
                return RunVectorTextDetectorDualPassContract();

            if (IsOption(args[0], "--vector-watermark-projection-parallelism-contract"))
                return RunVectorWatermarkProjectionParallelismContract();

            if (IsOption(args[0], "--vector-detection-primitive-membership-contract"))
                return RunVectorDetectionPrimitiveMembershipContract();

            if (IsOption(args[0], "--vector-detection-dump"))
                return RunVectorDetectionDump(args);

            if (IsOption(args[0], "--residual-vector-provenance-dump"))
                return RunResidualVectorProvenanceDump(args);

            if (IsOption(args[0], "--vector-prism-topology-rewrite-contract"))
                return RunVectorPrismTopologyRewriteContractTests();

            if (IsOption(args[0], "--step-entity-append-contract"))
                return RunStepEntityAppendContractTests();

            if (IsOption(args[0], "--clean-report-dump"))
                return RunCleanReportDump(args);

            if (IsOption(args[0], "--stepcleaner-profile"))
                return RunStepCleanerProfile(args);

            if (IsOption(args[0], "--stepcleaner-speed-contract"))
                return RunStepCleanerSpeedContract(args);

            if (IsOption(args[0], "--full-regression-parallelism-contract"))
                return RunFullRegressionParallelismContract();

            if (IsOption(args[0], "--detection-debug-cache-coverage-contract"))
                return RunDetectionDebugCacheCoverageContract();

            if (IsOption(args[0], "--vector-detection-report-contract"))
                return RunVectorDetectionReportContractTests();

            if (IsOption(args[0], "--vector-detection-quality-contract"))
                return RunVectorDetectionQualityContractTests();

            if (IsOption(args[0], "--vector-prism-cleanup-contract"))
                return RunVectorPrismCleanupContractTests();

            if (IsOption(args[0], "--vector-prism-retained-bound-contract"))
                return RunVectorPrismRetainedBoundContractTests();

            if (IsOption(args[0], "--detection-box-cleanup-contract"))
                return RunDetectionBoxCleanupContractTests();

            if (IsOption(args[0], "--residual-edge-cleanup-contract"))
                return RunResidualEdgeCleanupContractTests();

            if (IsOption(args[0], "--text-logo-cleanup-promotion"))
                return RunTextLogoCleanupPromotionTests();

            if (IsOption(args[0], "--text-logo-negative-classifier"))
                return RunTextLogoNegativeClassifierTests();

            if (IsOption(args[0], "--text-logo-verifier"))
                return RunTextLogoVerifierTests();

            if (IsOption(args[0], "--text-logo-visual-residuals"))
                return RunTextLogoVisualResidualTests(args);

            if (IsOption(args[0], "--text-logo-full-topology-removal-contract"))
                return RunTextLogoFullTopologyRemovalContractTests();

            if (IsOption(args[0], "--removed-geometry"))
                return RunRemovedGeometryExportTests();

            if (IsOption(args[0], "--removed-geometry-roi-locality"))
                return RunRemovedGeometryRoiLocalityTests();

            if (IsOption(args[0], "--removed-geometry-non-watermark-containment-contract"))
                return RunRemovedGeometryNonWatermarkContainmentContractTests();

            if (IsOption(args[0], "--non-watermark-hole-preservation-contract"))
                return RunNonWatermarkHolePreservationContractTests();

            if (IsOption(args[0], "--detector-blind-residual-topology-contract"))
                return RunDetectorBlindResidualTopologyContractTests();

            if (IsOption(args[0], "--reported-cleanup-regressions-contract"))
                return RunReportedCleanupRegressionContractTests(args);

            if (IsOption(args[0], "--regenerate-detection-debug-images"))
                return RunRegenerateDetectionDebugImages();

            if (IsOption(args[0], "--silhouette"))
                return SaveSilhouetteProjectionImage(args);

            if (IsOption(args[0], "--silhouette-dump"))
                return SaveSilhouettePrimitiveDump(args);

            Console.Error.WriteLine("Unknown command: " + args[0]);
            Console.Error.WriteLine("Usage: StepCleaner.Tests --metadata");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --symbol-rules");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --import-save-policy");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --async-import");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --footprint-placement");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --footprint-layers");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --pcblib-actions");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --ulanzi-plugin");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --model-cache");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --measure-model-import [part-number] [--repeat count] [--clean-text]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette-cleanup");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-smoke");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-benchmark <input.step> [--repeat count]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-overlap-unit");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-stage-report [output-dir]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-buffer-smoke <input.step>");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-preview-smoke <input.step> [output.png]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-no-ambient-occlusion");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --clean-text");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --xt60-lceda");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --projection-edge-mode");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --raw-silhouette-edge-projection");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-projection-contract [output-dir]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --watermark-template-library");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-detection");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --marked-detection-parity");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --marked-detection-parity-clean-text");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-text-detector-smoke");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-text-detector-dual-pass-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-watermark-projection-parallelism-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-detection-primitive-membership-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --residual-vector-provenance-dump <input.step> <view>");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-prism-topology-rewrite-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --step-entity-append-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --detection-box-cleanup-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --residual-edge-cleanup-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-cleanup-promotion");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-negative-classifier");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-verifier");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-visual-residuals [fixture-name]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-full-topology-removal-contract");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --removed-geometry");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --removed-geometry-roi-locality");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette <input.step> <output.png> [--rotx deg] [--roty deg] [--rotz deg] [--rotation2d deg] [--size pixels] [--padding pixels] [--no-grid] [--no-axes]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette-dump <input.step> <output.csv>");
            return 2;
        }

        private static int RunModelCacheTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();

            AssertEqual(
                "model-uuid__watermark",
                CleanStepCacheKeys.GetCleanModeKey("model-uuid", false),
                "watermark-only clean cache key should use the existing suffix",
                failures);

            AssertEqual(
                "model-uuid__watermark_text",
                CleanStepCacheKeys.GetCleanModeKey("model-uuid", true),
                "clean-text cache key should use the existing suffix",
                failures);

            var expectedKeys = new[]
            {
                "model-uuid__watermark",
                "model-uuid__watermark_text"
            };
            var actualKeys = CleanStepCacheKeys.GetCleanModeKeys("model-uuid").ToArray();
            for (int i = 0; i < expectedKeys.Length; i++)
            {
                string actual = i < actualKeys.Length ? actualKeys[i] : "";
                AssertEqual(expectedKeys[i], actual, "regenerate should target every cleaned STEP cache variant", failures);
            }
            if (actualKeys.Length != expectedKeys.Length)
            {
                failures.Add(
                    "regenerate should target exactly " +
                    expectedKeys.Length.ToString(CultureInfo.InvariantCulture) +
                    " cleaned STEP cache variants, got " +
                    actualKeys.Length.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            string footprint3dModel = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintShapes", "EeFootprint3dModel.cs"));
            string modelCache = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "ModelCache.cs"));
            string easyEdaLoader = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs"));
            string dialogWindow = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "DialogWindow.cs"));
            string dialogWindowXaml = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "DialogWindow.xaml"));
            string canvasZoomPanHelper = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "CanvasZoomPanHelper.cs"));
            string standaloneProject = File.ReadAllText(Path.Combine(repoRoot, "Standalone", "Standalone.csproj"));
            string standaloneMainWindow = File.ReadAllText(Path.Combine(repoRoot, "Standalone", "MainWindow.xaml.cs"));
            string stepWatermarkCleaner = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepWatermarkCleaner.cs"));
            string stepWatermarkCleanVerifier = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepWatermarkCleanVerifier.cs"));
            string stepProjectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            string stepSilhouetteProjection = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepSilhouetteProjection.cs"));
            string occtHiddenLineExtractor = File.ReadAllText(Path.Combine(repoRoot, "StepOcctHlr", "OcctHiddenLineExtractor.cs"));
            string buildAndInstallScript = File.ReadAllText(Path.Combine(repoRoot, "BuildAndInstall-Altium.ps1"));
            string stepF3DRenderProgramPath = Path.Combine(repoRoot, "StepF3DRender", "Program.cs");
            string stepF3DRenderProgram = File.Exists(stepF3DRenderProgramPath)
                ? File.ReadAllText(stepF3DRenderProgramPath)
                : string.Empty;
            string stepF3DRenderLibPath = Path.Combine(repoRoot, "StepF3DRenderLib", "F3DProjectionRenderer.cs");
            string stepF3DRenderLib = File.Exists(stepF3DRenderLibPath)
                ? File.ReadAllText(stepF3DRenderLibPath)
                : string.Empty;
            string easyEdaProject = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.csproj"));
            string stepCleanerProject = File.ReadAllText(Path.Combine(repoRoot, "StepCleaner", "StepCleaner.csproj"));
            string stepCleanerTestsProject = File.ReadAllText(Path.Combine(repoRoot, "Test", "StepCleaner", "StepCleaner.Tests.csproj"));
            string stepCleanerProgram = File.ReadAllText(Path.Combine(repoRoot, "Test", "StepCleaner", "Program.cs"));
            AssertContains(
                footprint3dModel,
                "ModelCache.GetCleanStepModelWithStatusAsync",
                "footprint import should reuse the cleaned STEP model cache instead of cleaning every import",
                failures);
            AssertContains(
                modelCache,
                "public sealed class ModelCacheResult",
                "model cache should expose whether clean STEP data came from cache",
                failures);
            AssertContains(
                footprint3dModel,
                "GetCleanStepModelWithStatusAsync",
                "footprint import should trace clean STEP cache hit/miss status",
                failures);
            AssertContains(
                dialogWindow,
                "GetCleanStepModelWithStatusAsync",
                "preview clean STEP generation should share the same cache-status path",
                failures);
            AssertContains(
                modelCache,
                "GetSearchProductInfoAsync",
                "EasyEDA product search results should be cached under the local loader cache",
                failures);
            AssertContains(
                modelCache,
                "GetComponentJsonAsync",
                "EasyEDA component JSON should be cached under the local loader cache",
                failures);
            AssertContains(
                modelCache,
                "GetJsonObjectFromStringAsync",
                "EasyEDA component JSON should cache the raw server response instead of reserializing converter-backed objects",
                failures);
            AssertContains(
                modelCache,
                "IsUsableComponentRoot",
                "EasyEDA component JSON cache should reject stale files that no longer deserialize into preview-ready component data",
                failures);
            AssertContains(
                modelCache,
                "api.GetComponentJsonStringAsync(lcscId, cancellationToken)",
                "EasyEDA component JSON cache should download raw JSON so converter WriteJson methods cannot break preview loading",
                failures);
            AssertContains(
                File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "API", "EasyedaApi.cs")),
                "GetComponentJsonStringAsync",
                "EasyEDA API should expose raw component JSON for cache writes",
                failures);
            AssertContains(
                modelCache,
                "GetPngImageAsync",
                "EasyEDA thumbnail image data should be cached under the local loader cache",
                failures);
            AssertContains(
                modelCache,
                "GetProjectionPreviewPngAsync",
                "bottom model projection preview PNGs should be cached by original model and render size",
                failures);
            AssertContains(
                modelCache,
                "GetModelCacheRoot",
                "model cache should expose the cached 3D model root folder for Explorer",
                failures);
            AssertContains(
                modelCache,
                "DeleteSelectedComponentCache",
                "the dialog should be able to remove all cache files for the selected component",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetSearchProductInfoAsync",
                "search should use cached EasyEDA product data instead of always posting to the remote server",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetComponentJsonAsync",
                "preview and import should use cached EasyEDA component JSON instead of always fetching remotely",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetPngImageAsync",
                "thumbnail preview should use cached EasyEDA image bytes",
                failures);
            AssertContains(
                dialogWindow,
                "GetSelectedComponentCacheKey",
                "dialog clean/projection cache actions should be scoped to the selected component row",
                failures);
            AssertContains(
                dialogWindow,
                "WarmSelectedComponentCacheAsync",
                "form load and row selection preview should warm missing selected-component cache elements",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetRawObjModelAsync(Api, modelInfo.Uuid",
                "selected-component cache warm-up should store the raw OBJ model cache when missing",
                failures);
            AssertContains(
                dialogWindow,
                "ModelZInfoCache.GetOrCreateAsync",
                "selected-component cache warm-up should store the derived raw OBJ ZInfo cache when missing",
                failures);
            AssertContains(
                dialogWindow,
                "await interactivePreviewTask.ConfigureAwait(false)",
                "selected-component clean STEP warm-up should wait for the active preview to avoid cold-cache clean races",
                failures);
            AssertContains(
                dialogWindow,
                "CleanStepCacheKeys.GetCleanModeKeys(selectedComponentCacheKey)",
                "selected-component cache warm-up should store every clean STEP cache variant when missing",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetSearchProductInfoAsync(Api, partViewModel.PartInfo.Part",
                "selected-component cache warm-up should store search metadata when restored session skipped a live search",
                failures);
            AssertContains(
                footprint3dModel,
                "CleanStepModelFastWithReport",
                "footprint import clean-cache misses should skip projection verification and use the fast cleaner report path",
                failures);
            AssertContains(
                dialogWindow,
                "CleanStepModelFastWithReport",
                "preview clean STEP cache misses should skip projection verification and use the fast cleaner report path",
                failures);
            AssertContains(
                footprint3dModel,
                "ModelCache.GetComponentModelCacheKey(ctx.CachePartNumber, GetSafeCacheFileName())",
                "footprint import should scope clean STEP cache outputs to the selected component plus model",
                failures);
            AssertContains(
                easyEdaLoader,
                "CachePartNumber = selection.PartInfo?.Part",
                "footprint import should pass the stable selected LCSC row part number into cache keys",
                failures);
            AssertDoesNotContain(
                footprint3dModel,
                "? StepWatermarkCleanVerifier.CleanOrThrow",
                "footprint import should not call watermark cleanup directly inside the import branch",
                failures);
            AssertContains(
                easyEdaLoader,
                "ModelImportTrace.MeasureAsync(\"model_download_cache_read\"",
                "model STEP download/cache read should be timed from the normal import prefetch path",
                failures);
            AssertContains(
                easyEdaLoader,
                "ModelImportTrace.MeasureAsync(\"raw_obj_download_cache_read\"",
                "raw OBJ download/cache read should be timed from the normal import prefetch path",
                failures);
            AssertContains(
                footprint3dModel,
                "ModelImportTrace.Measure(\"watermark_clean_cache\"",
                "watermark clean/cache phase should be timed during 3D model import",
                failures);
            AssertContains(
                footprint3dModel,
                "ModelImportTrace.MeasureAsync(\"raw_obj_z_info\"",
                "raw OBJ Z info parse/cache phase should be timed during 3D model import",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "ModelImportTrace.Measure(\"occt_hlr_projection\"",
                "OCCT HLR helper projection phase should be timed",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "ModelImportTrace.Measure(\"projection_optimization\"",
                "projection parse/place/optimization phase should be timed",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "UseStandardInputForStepData",
                "OCCT silhouette projection should avoid writing a temp STEP file when the caller has bytes",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "ReadOcctProjectionJsonFromString",
                "OCCT silhouette projection should parse helper JSON from stdout without a temp JSON file",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "BuildPrimitiveSpatialBuckets",
                "OCCT overlap cleanup should use spatial buckets instead of scanning every primitive for every sample",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "Dictionary<long, List<int>> spatialBuckets",
                "OCCT overlap cleanup spatial buckets should use numeric keys instead of allocating strings for every sample",
                failures);
            AssertContains(
                occtHiddenLineExtractor,
                "TryReadFirstTwoShapeReferences",
                "OCCT HLR edge parsing should avoid regex work in the BREP edge hot loop",
                failures);
            AssertContains(
                occtHiddenLineExtractor,
                "IsIdentityModelRotation(options)",
                "OCCT HLR helper should skip BRepBuilderAPI_Transform when model rotation is identity",
                failures);
            AssertContains(
                dialogWindow,
                "CreatePreviewSession(",
                "interactive colored STEP preview should create an in-process libf3d preview session",
                failures);
            AssertContains(
                dialogWindow,
                "CreatePreviewSession(previewStepData",
                "interactive colored STEP preview should create a single active libf3d preview session from the selected clean/original STEP bytes",
                failures);
            AssertContains(
                dialogWindow,
                "CreatePreviewSession(previewStepData, cameraSnapshot)",
                "interactive colored STEP preview should restore the F3D camera when toggling clean/original STEP data",
                failures);
            AssertContains(
                dialogWindow,
                "DrainF3DPreviewInteractions()",
                "interactive colored STEP preview should apply queued wheel/mouse input before capturing camera for clean/original toggles",
                failures);
            AssertContains(
                dialogWindow,
                "if (_currentModel != null)",
                "clean option changes should refresh only the current 3D preview instead of reloading the whole component",
                failures);
            AssertContains(
                dialogWindow,
                "CoalesceF3DPreviewInteraction",
                "interactive colored STEP preview should coalesce drag mouse moves before rendering",
                failures);
            AssertContains(
                dialogWindow,
                "ScheduleF3DPreviewRender",
                "interactive colored STEP preview should throttle rendering without canceling every mouse move",
                failures);
            AssertContains(
                dialogWindow,
                "int maxEdge = isInteractive ? 960 : 1920",
                "interactive colored STEP preview should keep enough resolution while rotating and use high-resolution idle frames",
                failures);
            AssertContains(
                dialogWindow,
                "RequestF3DPreviewIdleRender",
                "interactive 3D preview should replace the low-resolution interaction frame with a high-resolution idle render",
                failures);
            AssertContains(
                dialogWindow,
                "isInteractive && _f3dPreviewDragStart == null",
                "interactive 3D preview should schedule the high-resolution refresh after drag or wheel input settles",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Width=\"1600\"",
                "dialog should be wide enough for a 25 percent 3D preview column",
                failures);
            AssertContains(
                dialogWindowXaml,
                "<ColumnDefinition Width=\"37.5*\"/>",
                "dialog should allocate 37.5 percent of the main content width to the interactive 3D preview",
                failures);
            AssertContains(
                dialogWindowXaml,
                "x:Name=\"modelProjectionViewport\"",
                "middle-column projection preview should render inside a full-column viewport",
                failures);
            AssertContains(
                dialogWindowXaml,
                "x:Name=\"f3dPreviewStatusTextBlock\"",
                "interactive STEP preview should show a visible status or failure message when rendering cannot start",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Background=\"#FAFAFA\"",
                "middle-column projection preview should use a light viewport background so unfilled sides do not appear as black gutters",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "Background=\"Black\"",
                "symbol and footprint preview surfaces should not be black because EasyEDA primitives can render dark-on-dark and appear blank",
                failures);
            AssertContains(
                dialogWindowXaml,
                "modelProjectionImage",
                "middle-column projection preview should keep the projection image control",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Width=\"{Binding ActualWidth, ElementName=modelProjectionViewport}\"",
                "middle-column projection preview should display at the exact column viewport width",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Height=\"{Binding ActualHeight, ElementName=modelProjectionViewport}\"",
                "middle-column projection preview should display at the exact row viewport height",
                failures);
            AssertContains(
                dialogWindowXaml,
                "VerticalAlignment=\"Top\"",
                "middle-column projection preview should preserve its rendered height instead of vertically centering a shifted crop",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Stretch=\"Uniform\"",
                "middle-column projection and interactive 3D previews should fit their columns without crop-induced shifting",
                failures);
            AssertContains(
                dialogWindow,
                "GetModelProjectionPreviewImageSizePixels",
                "middle-column projection preview should render the source bitmap at the column width",
                failures);
            AssertContains(
                dialogWindow,
                "modelProjectionViewport.ActualWidth",
                "middle-column projection preview should size projection rendering from the current column width",
                failures);
            AssertContains(
                dialogWindow,
                "GetModelProjectionPreviewImageSizePixels(out int imageWidthPixels, out int imageHeightPixels)",
                "middle-column projection preview should render a rectangular bitmap matching the preview viewport",
                failures);
            AssertContains(
                dialogWindow,
                "modelProjectionViewport.ActualHeight",
                "middle-column projection preview should size projection rendering from the current row height",
                failures);
            AssertContains(
                dialogWindow,
                "ImageWidthPixels = imageWidthPixels",
                "middle-column projection preview should pass the column width to the projection renderer",
                failures);
            AssertContains(
                dialogWindow,
                "ImageHeightPixels = imageHeightPixels",
                "middle-column projection preview should pass the row height to the projection renderer",
                failures);
            AssertContains(
                dialogWindow,
                "SetF3DPreviewStatus(\"3D preview failed: \" + ex.Message",
                "interactive STEP preview failures should be visible in the dialog instead of trace-only",
                failures);
            AssertContains(
                dialogWindow,
                "Clean STEP preview failed; showing original STEP",
                "interactive STEP preview should fall back to the original STEP when clean STEP generation fails",
                failures);
            AssertContains(
                dialogWindow,
                "byte[] previewStepData = stepData;",
                "interactive STEP preview should keep original STEP data available as the fallback preview model",
                failures);
            AssertContains(
                dialogWindow,
                "SetF3DPreviewStatus(\"3D preview render failed: \" + ex.Message",
                "interactive STEP render failures should be visible in the dialog instead of trace-only",
                failures);
            AssertContains(
                dialogWindow,
                "DrawSymbolPreviewSafely",
                "symbol preview drawing failures should not abort footprint and 3D preview loading",
                failures);
            AssertContains(
                dialogWindow,
                "DrawFootprintPreviewSafely",
                "footprint preview drawing failures should not abort 3D preview loading",
                failures);
            AssertContains(
                dialogWindow,
                "LoadPreviewComponentJsonAsync",
                "preview loading should retry component JSON by the row part number and display name before giving up",
                failures);
            AssertContains(
                dialogWindow,
                "GetPreviewPartCandidates",
                "preview loading should derive stable fallback identifiers from the selected result row",
                failures);
            AssertContains(
                dialogWindow,
                "ShowMissingPreviewData",
                "preview loading should show visible pane messages when EasyEDA returns no drawable component data",
                failures);
            AssertContains(
                dialogWindow,
                "No component preview data was returned",
                "blank component JSON responses should be visible in the preview panes",
                failures);
            AssertContains(
                dialogWindow,
                "No footprint preview data was returned",
                "parts without footprint payloads should show a visible footprint preview message",
                failures);
            AssertContains(
                dialogWindow,
                "No 3D model preview data was returned",
                "parts without usable 3D payloads should show a visible original STEP preview message",
                failures);
            AssertContains(
                dialogWindow,
                "if (symbolCanvas.Children.Count == 0)",
                "symbol preview should show a visible message when a non-null symbol payload draws no primitives",
                failures);
            AssertContains(
                dialogWindow,
                "if (footprintCanvas.Children.Count == 0)",
                "footprint preview should show a visible message when a non-null footprint payload draws no primitives",
                failures);
            AssertContains(
                dialogWindow,
                "BitmapSource bitmapSource = CreateF3DBitmapSource(renderedImage);",
                "interactive STEP preview should inspect renderer bitmap creation before clearing failure status",
                failures);
            AssertContains(
                dialogWindow,
                "3D preview failed: renderer returned no image.",
                "interactive STEP preview should show a visible message when F3D returns an empty image",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "public int ImageWidthPixels",
                "STEP projection options should support non-square preview render width",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "public int ImageHeightPixels",
                "STEP projection options should support non-square preview render height",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "Stretch=\"UniformToFill\"",
                "projection and interactive 3D previews should not crop their bitmaps while fitting to the column",
                failures);
            AssertContains(
                dialogWindowXaml,
                "x:Name=\"f3dModelViewport\"",
                "interactive 3D preview should render against the full preview column viewport",
                failures);
            AssertContains(
                dialogWindowXaml,
                "RenderOptions.BitmapScalingMode=\"HighQuality\"",
                "interactive 3D preview should use high-quality WPF bitmap scaling",
                failures);
            AssertContains(
                dialogWindow,
                "f3dModelViewport.ActualWidth",
                "interactive 3D preview should size F3D renders from the full preview column viewport width",
                failures);
            AssertContains(
                dialogWindow,
                "Task<byte[]> stepDataTask = ModelCache.GetStepModelAsync",
                "3D model preview should start a shared STEP load as soon as the model UUID is known",
                failures);
            AssertContains(
                dialogWindow,
                "interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask",
                "interactive 3D preview should start from the shared STEP task independently of other preview data",
                failures);
            AssertContains(
                dialogWindow,
                "ShowModelProjectionPreviewAsync(_currentModel,",
                "2D model projection should render from an explicit original-model projection path",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetProjectionPreviewPngAsync",
                "2D model projection should cache rendered preview PNGs",
                failures);
            AssertContains(
                dialogWindow,
                "await originalStepDataTask.ConfigureAwait(false)",
                "2D model projection cache misses should reuse the already-started original STEP task",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetStepModelAsync(Api, modelInfo.Uuid",
                "2D model projection should always read the original STEP model, independent of clean STEP preview state",
                failures);
            AssertAppearsBefore(
                dialogWindow,
                "interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask",
                "DrawFootprintPreviewSafely",
                "interactive 3D preview should begin before footprint drawing runs through the safe UI preview path",
                failures);
            AssertAppearsBefore(
                dialogWindow,
                "interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask",
                "ShowModelProjectionPreviewAsync(_currentModel,",
                "interactive 3D preview should begin before 2D projection rendering",
                failures);
            AssertContains(
                dialogWindowXaml,
                "removeCacheButton",
                "dialog should expose a Remove cache button near model cache actions",
                failures);
            AssertContains(
                dialogWindowXaml,
                "openStepCacheButton",
                "dialog should expose an Open STEP Cache button near model cache actions",
                failures);
            AssertContains(
                dialogWindow,
                "RemoveCacheButton_Click",
                "Remove cache button should be wired to selected-component cache cleanup",
                failures);
            AssertContains(
                dialogWindow,
                "OpenStepCacheButton_Click",
                "Open STEP Cache button should be wired to an Explorer folder opener",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetModelCacheRoot()",
                "Open STEP Cache should open the cached 3D model folder",
                failures);
            AssertContains(
                dialogWindow,
                "previewCts = new CancellationTokenSource();\n\n                await LoadPreviewAsync(partViewModel, previewCts.Token);\n\n                CompleteCriticalOperation(\"Selected component cache removed.\", true);",
                "Remove cache should reload fresh selected-component data into cache and preview after deletion",
                failures);
            AssertContains(
                dialogWindow,
                "IsCurrentPreviewForSelectedComponent",
                "model cache buttons should only act when the loaded preview belongs to the selected grid row",
                failures);
            AssertContains(
                dialogWindow,
                "HasSelectedComponentForCache(out _)",
                "Remove cache should be enabled for the selected part even when preview/model loading failed",
                failures);
            AssertContains(
                dialogWindow,
                "HasSelectedComponentForCache(out PartInfoViewModel partViewModel)",
                "Remove cache should act on the selected part instead of requiring a completed preview",
                failures);
            AssertContains(
                dialogWindowXaml,
                "toggleOperationLogButton",
                "operation log panel should expose a button to hide and show the log",
                failures);
            AssertContains(
                dialogWindow,
                "ToggleOperationLogButton_Click",
                "operation log hide/show button should be wired to code-behind",
                failures);
            AssertContains(
                dialogWindow,
                "operationLogTextBox.Visibility",
                "operation log hide/show button should collapse the log text box",
                failures);
            AssertContains(
                standaloneProject,
                "<TargetFramework>net8.0-windows</TargetFramework>",
                "standalone app should target the same Windows runtime family as the shared loader project",
                failures);
            AssertContains(
                standaloneMainWindow,
                "GetZInfoFromOrigin(ctx)",
                "standalone app should use the current raw OBJ Z-info API instead of the removed Z-offset helper",
                failures);
            AssertContains(
                standaloneMainWindow,
                "ModelCache.GetSearchProductInfoAsync",
                "standalone search should use the same cached EasyEDA product lookup as the dialog",
                failures);
            AssertContains(
                standaloneMainWindow,
                "ModelCache.GetComponentJsonAsync",
                "standalone preview loading should use cached component JSON instead of direct live API calls",
                failures);
            AssertContains(
                standaloneMainWindow,
                "LoadSelectedPartAsync",
                "standalone should load preview for the selected search result instead of requiring row double-click",
                failures);
            AssertContains(
                standaloneMainWindow,
                "_previewLoadVersion",
                "standalone preview loads should ignore stale async work from previous row selections",
                failures);
            AssertContains(
                standaloneMainWindow,
                "Interlocked.Increment(ref _previewLoadVersion)",
                "standalone preview loads should version each row load before awaiting remote/cache data",
                failures);
            AssertContains(
                standaloneMainWindow,
                "SearchBox.SelectedItem is EasyedaApi.PartInfo",
                "standalone Load button should prefer the selected row part over raw search text like gd32f103",
                failures);
            AssertContains(
                standaloneMainWindow,
                "DrawSymbolPreviewSafely",
                "standalone should isolate symbol drawing failures from footprint and 3D preview loading",
                failures);
            AssertContains(
                standaloneMainWindow,
                "DrawFootprintPreviewSafely",
                "standalone should isolate footprint drawing failures from 3D preview loading",
                failures);
            AssertContains(
                standaloneMainWindow,
                "No component preview data was returned",
                "standalone should show an in-pane message instead of crashing on missing component JSON",
                failures);
            AssertContains(
                canvasZoomPanHelper,
                "GetElementBounds",
                "canvas zoom helper should calculate primitive geometry bounds instead of relying only on ActualWidth",
                failures);
            AssertContains(
                canvasZoomPanHelper,
                "if (child is Line line)",
                "canvas zoom helper should include WPF Line coordinates when fitting symbol and footprint previews",
                failures);
            AssertContains(
                canvasZoomPanHelper,
                "shape.RenderedGeometry.Bounds",
                "canvas zoom helper should include Path and Shape rendered geometry bounds when fitting previews",
                failures);
            AssertContains(
                dialogWindow,
                "RenderInteractivePreviewImage",
                "interactive colored STEP preview should render one active image instead of rendering original and clean windows every frame",
                failures);
            AssertContains(
                dialogWindow,
                "QueueF3DPreviewRender",
                "interactive colored STEP preview should queue renders for the active preview image",
                failures);
            AssertContains(
                dialogWindowXaml,
                "f3dModelImage",
                "interactive colored STEP preview should host the selected original/clean model in one WPF Image",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "f3dOriginalModelImage",
                "interactive colored STEP preview should not keep a second original-model preview image",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "f3dCleanModelImage",
                "interactive colored STEP preview should not keep a second clean-model preview image",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "AIS_ColoredShape",
                "interactive colored STEP preview should not use the managed OCCT color probe with wrong DF56 colors",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "SetParent(",
                "interactive colored STEP preview should not reparent external f3d.exe windows",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "WaitForMainWindowHandleAsync",
                "interactive colored STEP preview should not wait for an external f3d.exe main window",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "RegisterRawInputDevices",
                "dual STEP preview sync should not use global raw-input mirroring",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "MirrorF3DRawMouseInput",
                "dual STEP preview sync should not mirror Win32 mouse messages between external windows",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "F3DPreviewCameraState",
                "F3D library helper should expose a shared preview camera state",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "CreatePreviewSession",
                "F3D library helper should expose a persistent in-process preview session",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "CreatePreviewSession(byte[] stepData)",
                "F3D library helper should expose a single-model preview session for faster dialog previews",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "RenderInteractivePreviewImage",
                "F3D library helper should render a single active preview image without a paired clean/original engine",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "GetCameraSnapshot",
                "F3D library helper should expose absolute camera capture for clean/original preview toggles",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "ApplyCameraSnapshot",
                "F3D library helper should restore an absolute camera capture on a new preview session",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "OrthographicZoomFactor",
                "F3D library helper should preserve orthographic zoom because F3D stores it as parallel scale, not view angle",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "PreviewMouseWheelZoomFactor",
                "F3D library helper should track wheel zoom factor for camera restore in orthographic mode",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "_pendingCameraSnapshot",
                "F3D library helper should defer camera restore until the preview window has its final render size",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "ApplyPendingCameraSnapshot",
                "F3D library helper should apply saved zoom after PreparePreviewWindows sets the sized viewport",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_camera_azimuth",
                "F3D library helper should apply preview orbit through libf3d camera APIs",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_camera_pan",
                "F3D library helper should apply preview pan through libf3d camera APIs",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_camera_zoom",
                "F3D library helper should apply preview zoom through libf3d camera APIs",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_engine_get_interactor",
                "interactive colored STEP preview should use F3D's native interactor for mouse behavior",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_interactor_trigger_mouse_position",
                "interactive colored STEP preview should forward mouse movement to F3D's native interactor",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_interactor_trigger_mouse_wheel",
                "interactive colored STEP preview should forward mouse wheel input to F3D's native interactor",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3D",
                "colored projection PNG rendering should keep F3D as the color-correct renderer until native XCAFPrs_AISObject support exists",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3DLibraryBatch",
                "colored projection PNG rendering should try the single-load F3D library batch helper before f3d-console fallback",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_scene_add",
                "F3D library helper should load the STEP scene through f3d_c_api instead of f3d-console",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_scene_add_buffer",
                "F3D shared renderer should load STEP bytes directly through libf3d without a temp STEP file",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "F3DProjectionRenderer.RenderRawImages",
                "internal colored projection rendering should call the shared F3D renderer library instead of a helper process",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "--six-sides-stdout",
                "internal colored projection rendering should not send raw images through stdout",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "rawBase64",
                "internal colored projection rendering should not base64-expand raw image buffers",
                failures);
            AssertContains(
                stepF3DRenderProgram,
                "F3DProjectionRenderer.RenderPngFilesFromFile",
                "StepF3DRender executable should be a tiny CLI wrapper over the shared renderer",
                failures);
            AssertContains(
                easyEdaProject,
                "StepF3DRenderLib.csproj",
                "EasyEDA-Loader should reference the shared F3D renderer library for in-process projections",
                failures);
            AssertContains(
                stepCleanerProject,
                "StepF3DRenderLib.csproj",
                "StepCleaner should reference the shared F3D renderer library used by linked projection code",
                failures);
            AssertContains(
                stepCleanerTestsProject,
                "StepF3DRenderLib.csproj",
                "StepCleaner tests should reference the shared F3D renderer library used by linked projection code",
                failures);
            AssertContains(
                stepF3DRenderProgram,
                "--six-sides",
                "F3D library helper should expose a six-side render command",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "model.scivis.array_name",
                "F3D library helper should preserve the STEP Colors scalar array",
                failures);
            AssertContains(
                stepF3DRenderProgram,
                "--views",
                "F3D library helper should render selected side subsets from one STEP load",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3DLibraryBatch(inputPath, selectedDetectionViews",
                "highlighted detection projections should try the single-load F3D library batch helper",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "views.Count != Views.Length",
                "F3D library batch renderer should not be limited to exactly six views",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "SkipGeometryModelForExternalRender",
                "verification projection rendering should be able to skip STEP geometry parsing when external rendering succeeds",
                failures);
            AssertContains(
                stepCleanerProgram,
                "FullTest" + "DetectionCache",
                "full StepCleaner regression should reuse original-model detection reports between debug image generation and post-clean verification",
                failures);
            AssertContains(
                stepCleanerProgram,
                "GetDetection" + "Regions(",
                "full StepCleaner regression should reuse projected detection regions for original-vs-clean and clean-vs-validated comparisons",
                failures);
            AssertContains(
                stepCleanerProgram,
                "detection_debug_project" + "_file_ms",
                "detection debug image generation should expose detailed timing for the remaining bottleneck",
                failures);
            AssertContains(
                stepCleanerProgram,
                "FilesEqualByLength" + "AndBytes",
                "clean-vs-validated projection comparison should skip PNG decode when files are byte-identical",
                failures);
            AssertContains(
                stepCleanerProgram,
                "CopyBitmapPixels" + "ToInt32Rows",
                "projection comparison should avoid per-pixel SKBitmap.GetPixel calls in hot loops",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "ProjectFileImages(",
                "internal projection rendering should expose an in-memory raw image API",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "ProjectDetectionFileImages(",
                "internal detection projection rendering should expose an in-memory highlighted raw image API",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3DLibraryBatchToRawImages",
                "F3D library batch rendering should avoid saving/loading image files or PNG encoding for internal callers",
                failures);
            AssertContains(
                stepCleanerProgram,
                "Generate(cleanedStep,",
                "measurement OCCT HLR projection should use cleaned STEP bytes instead of requiring a saved STEP file",
                failures);
            AssertContains(
                footprint3dModel,
                "StepSilhouetteProjection.Generate(",
                "footprint import OCCT HLR projection should use in-memory STEP bytes after the Altium body file is written",
                failures);
            AssertContains(
                stepWatermarkCleanVerifier,
                "ProjectFileImages(",
                "watermark verification should compare raw in-memory projection images before writing report artifacts",
                failures);
            AssertContains(
                stepCleanerProgram,
                "full_test_wall_ms",
                "full StepCleaner regression timing should print total wall time for before/after comparisons",
                failures);
            AssertContains(
                buildAndInstallScript,
                "StepF3DRender\\StepF3DRender.csproj",
                "Altium install script should build the F3D library render helper",
                failures);
            AssertContains(
                buildAndInstallScript,
                "StepF3DRender.exe",
                "Altium install script should copy the F3D library render helper",
                failures);
            AssertContains(
                buildAndInstallScript,
                "STEPCLEANER_F3D_LIB",
                "Altium install script should honor the configured native F3D library path",
                failures);
            AssertContains(
                buildAndInstallScript,
                "f3d_c_api.dll",
                "Altium install script should package the native libf3d C API for internal previews",
                failures);
            AssertContains(
                buildAndInstallScript,
                "F3D\\bin",
                "Altium install script should install native F3D runtime DLLs under the extension F3D bin folder",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Installed F3D native library",
                "Altium install script should report the installed native F3D library path",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Install-F3DCompatibleMsvcRuntime",
                "Altium install script should update Altium's app-local MSVCP140.dll when it is too old for in-process F3D",
                failures);
            AssertDoesNotContain(
                buildAndInstallScript,
                "[string]$AltiumProfile = \"",
                "Altium install script should deduce the profile automatically instead of hard-coding a default parameter",
                failures);
            AssertDoesNotContain(
                buildAndInstallScript,
                "[string]$AltiumExe = \"",
                "Altium install script should deduce the executable automatically instead of hard-coding a default parameter",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Resolve-AltiumProfile",
                "Altium install script should resolve the installed Altium profile automatically",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Resolve-AltiumExecutable",
                "Altium install script should resolve the installed Altium executable automatically",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Resolve-AltiumInstallation",
                "Altium install script should correlate the profile and executable before installing",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Get-AltiumRegistryInstallations",
                "Altium install script should discover Altium installs from Windows uninstall registry entries",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Get-AltiumProfileCandidates $uniqueId",
                "Altium install script should correlate ProgramData profiles to the installer GUID",
                failures);
            AssertContains(
                buildAndInstallScript,
                "*_Security",
                "Altium install script should ignore Altium profile security folders",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Multiple Altium installations were detected",
                "Altium install script should fail clearly instead of guessing when multiple installs match",
                failures);
            AssertContains(
                buildAndInstallScript,
                "EasyEDA-Loader-MsvcBackup",
                "Altium install script should back up Altium's MSVCP140.dll before installing the F3D-compatible runtime",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Updated Altium MSVCP140.dll for in-process F3D",
                "Altium install script should report MSVC runtime updates needed by in-process F3D",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "Path.Combine(baseDirectory, \"F3D\", \"bin\", \"f3d_c_api.dll\")",
                "F3D renderer should load the native library bundled by the Altium install script",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "typeof(F3DProjectionRenderer).Assembly.Location",
                "F3D renderer should locate the bundled native library beside StepF3DRenderLib when Altium is the host process",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs",
                "F3D renderer should isolate native dependency resolution from Altium's process directory",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "Loaded MSVCP140.dll=",
                "F3D renderer should diagnose Altium's already-loaded MSVCP140.dll when native initialization fails",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "--scalar-coloring",
                "colored projection PNG rendering should preserve F3D scalar-coloring",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "--coloring-array=Colors",
                "colored projection PNG rendering should use the STEP Colors array",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "--coloring-component=-2",
                "colored projection PNG rendering should use F3D RGB scalar components",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "AIS_ColoredShape",
                "colored projection PNG rendering should not use the managed OCCT color probe with wrong DF56 colors",
                failures);
            AssertContains(
                stepCleanerProgram,
                "DefaultMeasure" + "PartNumber = \"C5338332\"",
                "standalone model measurement should default to the C5338332 example",
                failures);
            AssertContains(
                stepCleanerProgram,
                "RunModelImport" + "Measurement(args)",
                "test harness should expose a standalone model import measurement command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "GetComponent" + "JsonPath(partNumber)",
                "measurement command should cache component JSON lookup for repeatable measurements",
                failures);
            AssertContains(
                stepCleanerProgram,
                "--measure-" + "model-import",
                "usage should document the standalone model import measurement command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "--occt-hlr-" + "benchmark",
                "usage should document the OCCT HLR benchmark command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "RunOcctHlr" + "Benchmark(args)",
                "test harness should expose a standalone OCCT HLR benchmark command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "CleanStepModelFastWithReport",
                "measurement command should exercise the same fast watermark clean-cache miss path as import",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "public sealed class StepWatermarkCleanerTiming",
                "watermark cleaner should expose detailed detection/edit timing entries",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "RemoveReferencesFromCommaList(definition, styledItemIds)",
                "watermark cleaner should remove styled-item references in bulk instead of one regex scan per removed style",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "BuildPlanarHostCandidatesByAxis",
                "watermark cleaner should cache planar host candidates per solid instead of rebuilding them for each watermark candidate",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "ownerInfo.PlanarHostCandidatesByAxis",
                "host-plane selection should use prebuilt planar host candidates from SolidInfo",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "facePointsByFace",
                "watermark cleaner should cache face point ids while building face components",
                failures);
            AssertContains(
                stepWatermarkCleanVerifier,
                "CleanOrThrowWithReport",
                "verifier should expose clean report timings for cache-miss measurement",
                failures);
            AssertContains(
                stepCleanerProgram,
                "watermark_clean_detail_",
                "measurement command should print detailed watermark detection/edit timings on clean cache misses",
                failures);
            AssertContains(
                stepCleanerProgram,
                "CleanOrThrowWithReport",
                "measurement command should capture cleaner report timings from the verifier miss path",
                failures);
            AssertContains(
                stepCleanerProgram,
                "ModelZInfo" + "Cache.GetOrCreateAsync",
                "measurement command should exercise cached raw OBJ Z info parsing",
                failures);
            AssertContains(
                stepCleanerProgram,
                "StepSilhouette" + "Projection.Generate(cleanedStep",
                "measurement command should project from cleaned STEP bytes instead of reloading a saved file",
                failures);

            string zInfoCacheDirectory = Path.Combine(Path.GetTempPath(), "EasyEDALoaderZInfo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(zInfoCacheDirectory);
            try
            {
                string zInfoCachePath = Path.Combine(zInfoCacheDirectory, "model.zinfo");
                int rawObjLoadCount = 0;
                Func<byte[]> rawObjLoader = () =>
                {
                    rawObjLoadCount++;
                    return Encoding.UTF8.GetBytes(
                        "v 0 0 -1.25" + Environment.NewLine +
                        "v 1 0 2.75" + Environment.NewLine);
                };

                ModelZInfo firstZInfo = ModelZInfoCache.GetOrCreate(zInfoCachePath, rawObjLoader);
                ModelZInfo secondZInfo = ModelZInfoCache.GetOrCreate(
                    zInfoCachePath,
                    () =>
                    {
                        rawObjLoadCount++;
                        return Encoding.UTF8.GetBytes(
                            "v 0 0 -9" + Environment.NewLine +
                            "v 1 0 9" + Environment.NewLine);
                    });

                AssertNear(1.25, firstZInfo.OffsetFromOrigin, 0.00001, "raw OBJ Z info should keep the lowest vertex offset", failures);
                AssertNear(4.0, firstZInfo.Height, 0.00001, "raw OBJ Z info should keep model height", failures);
                AssertNear(firstZInfo.OffsetFromOrigin, secondZInfo.OffsetFromOrigin, 0.00001, "cached Z info should preserve the parsed offset", failures);
                AssertNear(firstZInfo.Height, secondZInfo.Height, 0.00001, "cached Z info should preserve the parsed height", failures);
                AssertEqual("1", rawObjLoadCount.ToString(CultureInfo.InvariantCulture), "cached Z info should avoid reparsing raw OBJ on repeated imports", failures);

                byte[] positiveOriginObj = Encoding.UTF8.GetBytes("v 0 0 0.20\nv 1 0 2.20\n");
                ModelZInfo positiveZInfo = ModelZInfoCache.ParseRawObj(positiveOriginObj);
                AssertNear(-0.20, positiveZInfo.OffsetFromOrigin, 0.00001, "positive lowest vertex needs a downward Z correction", failures);
                AssertNear(2.0, positiveZInfo.Height, 0.00001, "signed Z correction must preserve model height", failures);
                AssertNear(0.0, 0.20 + positiveZInfo.OffsetFromOrigin, 0.00001, "corrected model bottom must lie at the footprint plane", failures);

                string legacyZInfoCachePath = Path.Combine(zInfoCacheDirectory, "legacy.zinfo");
                File.WriteAllLines(legacyZInfoCachePath, new[] { "0.20", "2.0" });
                int legacyRawObjLoadCount = 0;
                ModelZInfo migratedZInfo = ModelZInfoCache.GetOrCreate(legacyZInfoCachePath, () =>
                {
                    legacyRawObjLoadCount++;
                    return positiveOriginObj;
                });
                ModelZInfo migratedCacheHit = ModelZInfoCache.GetOrCreate(legacyZInfoCachePath, () =>
                {
                    legacyRawObjLoadCount++;
                    return positiveOriginObj;
                });
                AssertNear(-0.20, migratedZInfo.OffsetFromOrigin, 0.00001, "legacy unsigned Z caches must be recalculated", failures);
                AssertNear(-0.20, migratedCacheHit.OffsetFromOrigin, 0.00001, "signed Z correction must survive a cache hit", failures);
                AssertEqual("1", legacyRawObjLoadCount.ToString(CultureInfo.InvariantCulture), "legacy Z cache migration should load the OBJ only once", failures);
            }
            finally
            {
                try
                {
                    Directory.Delete(zInfoCacheDirectory, true);
                }
                catch
                {
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Model cache regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Model cache regression test passed.");
            return 0;
        }

        private static int RunXt60LcedaWatermarkTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("XT60 LCEDA regression test failed.");
                Console.Error.WriteLine("  Missing original fixture: " + inputPath);
                return 1;
            }

            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            byte[] cleanedStep = Encoding.Latin1.GetBytes(report.CleanedStep);
            string detectedViews = string.Join(
                ",",
                report.DetectionReport.Regions
                    .Select(region => region.ViewName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase));

            AssertEqual(
                "not equal",
                cleanedStep.SequenceEqual(originalStep) ? "equal" : "not equal",
                "XT60 bottom LCEDA cleanup should change the STEP output",
                failures);
            AssertContains(
                detectedViews,
                "z_minus",
                "XT60 bottom LCEDA detection should report a z_minus cleanup region",
                failures);
            if (report.DetectionReport.HostLoopCount <= 0)
                failures.Add("XT60 bottom LCEDA cleanup should detect host-face watermark loops.");
            string cleanerDiagnostics = string.Join(Environment.NewLine, report.Diagnostics);
            AssertContains(
                cleanerDiagnostics,
                "Automatic host-face watermark loops: 5",
                "XT60 bottom LCEDA cleanup should use the automatic host-loop detector",
                failures);
            AssertContains(
                cleanerDiagnostics,
                "Removed host-face inner loops: 5",
                "XT60 bottom LCEDA cleanup should remove only the five LCEDA inner loops",
                failures);
            int automaticCleanupVolumeCount = GetDiagnosticInt(report.Diagnostics, "Automatic cleanup volumes");
            if (automaticCleanupVolumeCount <= 0)
            {
                failures.Add(
                    "XT60 bottom LCEDA cleanup should use at least one bounded cleanup volume, got " +
                    automaticCleanupVolumeCount.ToString(CultureInfo.InvariantCulture) +
                    ". Diagnostics: " +
                    cleanerDiagnostics);
            }
            AssertContains(
                cleanerDiagnostics,
                "Edited geometry outside cleanup volumes: 0",
                "XT60 bottom LCEDA cleanup must not edit geometry outside its bounded cleanup volume",
                failures);
            var residualTopology = StepWatermarkCleaner.FindResidualCleanupTopology(
                Encoding.Latin1.GetString(originalStep),
                report.CleanedStep,
                report.DetectionReport,
                new StepWatermarkCleanerOptions());
            if (residualTopology.Failures.Count > 0)
            {
                foreach (string failure in residualTopology.Failures)
                    failures.Add("XT60 residual topology: " + failure);
            }
            AssertDoesNotContain(
                report.CleanedStep,
                "#1469 = ADVANCED_FACE",
                "XT60 cleanup should remove coplanar standalone LCEDA residue face #1469",
                failures);
            AssertDoesNotContain(
                report.CleanedStep,
                "#7432 = ADVANCED_FACE",
                "XT60 cleanup should remove coplanar standalone LCEDA residue face #7432",
                failures);

            string verifierDirectory = Path.Combine(dataRoot, "Clean", "XT60Verifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            bool verifierThrew = false;
            try
            {
                StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                    originalStep,
                    "CONN-TH_XT60PB-M.step",
                    verifierDirectory);
            }
            catch (StepWatermarkCleanFailedException)
            {
                verifierThrew = true;
            }

            if (report.DetectionReport.Regions.Count == 0 && !verifierThrew)
                failures.Add("XT60 verifier silently passed even though automatic detection reported zero cleanup regions.");

            if (report.DetectionReport.Regions.Count > 0 && verifierThrew)
                failures.Add("XT60 verifier failed after automatic detection reported cleanup regions.");

            VerifyXt60OriginalStillFailsResidualTopology(dataRoot, originalStep, report.DetectionReport, failures);
            VerifyNoRegionCleanupFailsWithReport(dataRoot, failures);
            VerifyCandidateOnlyCleanupFailsWithReport(dataRoot, failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("XT60 LCEDA regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("XT60 LCEDA regression test passed.");
            return 0;
        }

        private static void VerifyXt60OriginalStillFailsResidualTopology(
            string dataRoot,
            byte[] originalStep,
            StepWatermarkDetectionReport detectionReport,
            List<string> failures)
        {
            string verifierDirectory = Path.Combine(dataRoot, "Clean", "XT60OriginalVerifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                "VerifyPostCleanOutput",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte[]),
                    typeof(string),
                    typeof(StepWatermarkDetectionReport),
                    typeof(string)
                },
                null);
            if (verifyMethod == null)
            {
                failures.Add("Could not find verifier post-clean method for XT60 residual-topology regression test.");
                return;
            }

            object verification = verifyMethod.Invoke(
                null,
                new object[]
                {
                    originalStep,
                    originalStep,
                    "xt60-original",
                    detectionReport,
                    verifierDirectory
                });
            bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
            string reportPath = (string)verification.GetType().GetProperty("ReportPath").GetValue(verification);
            var verificationFailures = ((IEnumerable<string>)verification.GetType().GetProperty("Failures").GetValue(verification)).ToList();
            if (passed)
                failures.Add("Verifier should fail when XT60 LCEDA residual geometry remains in the cleaned output.");

            if (!verificationFailures.Any(failure =>
                failure.Contains("Residual cleanup face", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("remains on host face", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("XT60 residual-topology verifier did not report surviving LCEDA topology.");
            }

            if (!File.Exists(reportPath))
            {
                failures.Add("Verifier XT60 residual-topology failure report is missing: " + reportPath);
                return;
            }
        }

        private static void VerifyNoRegionCleanupFailsWithReport(string dataRoot, List<string> failures)
        {
            string verifierDirectory = Path.Combine(dataRoot, "Clean", "NoRegionVerifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var emptyDetectionReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = Array.Empty<int>(),
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = Array.Empty<StepWatermarkRegionDetection>(),
                Diagnostics = Array.Empty<string>()
            };
            var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                "VerifyPostCleanOutput",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte[]),
                    typeof(string),
                    typeof(StepWatermarkDetectionReport),
                    typeof(string)
                },
                null);
            if (verifyMethod == null)
            {
                failures.Add("Could not find verifier post-clean method for no-region regression test.");
                return;
            }

            object verification = verifyMethod.Invoke(
                null,
                new object[]
                {
                    originalStep,
                    originalStep,
                    "no-region",
                    emptyDetectionReport,
                    verifierDirectory
                });
            bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
            string reportPath = (string)verification.GetType().GetProperty("ReportPath").GetValue(verification);
            if (passed)
                failures.Add("Verifier should fail when no detected cleanup regions are available.");

            if (!File.Exists(reportPath))
            {
                failures.Add("Verifier no-region failure report is missing: " + reportPath);
                return;
            }

            string reportText = File.ReadAllText(reportPath);
            AssertContains(
                reportText,
                "Projection verification failures without comparison images",
                "Verifier no-region failure report should describe non-visual failures",
                failures);
            AssertDoesNotContain(
                reportText,
                "No failed projections.",
                "Verifier no-region failure report should not claim there were no failed projections",
                failures);
        }

        private static void VerifyCandidateOnlyCleanupFailsWithReport(string dataRoot, List<string> failures)
        {
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var candidateOnlyDetectionReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = Array.Empty<int>(),
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = new[]
                {
                    new StepWatermarkRegionDetection
                    {
                        EntityId = 1,
                        Kind = "solid-candidate",
                        ViewName = "z_plus"
                    }
                },
                Diagnostics = Array.Empty<string>()
            };

            string verifierDirectory = Path.Combine(dataRoot, "Clean", "CandidateOnlyVerifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                "VerifyPostCleanOutput",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte[]),
                    typeof(string),
                    typeof(StepWatermarkDetectionReport),
                    typeof(string)
                },
                null);
            if (verifyMethod == null)
            {
                failures.Add("Could not find verifier post-clean method for candidate-only regression test.");
                return;
            }

            object verification = verifyMethod.Invoke(
                null,
                new object[]
                {
                    originalStep,
                    originalStep,
                    "candidate-only",
                    candidateOnlyDetectionReport,
                    verifierDirectory
                });
            bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
            var verificationFailures = ((IEnumerable<string>)verification.GetType().GetProperty("Failures").GetValue(verification)).ToList();
            if (passed)
                failures.Add("Verifier should fail when only candidate detection overlays exist and no cleanup geometry was edited.");

            if (!verificationFailures.Any(failure =>
                failure.Contains("no verified cleanup regions", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("cannot prove", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("Candidate-only verifier failure should explain that no verified cleanup regions were available.");
            }
        }

        private static int RunRemovedGeometryExportTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "BUZ-SMD_4P-L7.5-W7.5-H2.5.step");
            string outputDirectory = Path.Combine(dataRoot, "RemovedGeometry");
            string outputPath = Path.Combine(outputDirectory, "BUZ-SMD_4P-L7.5-W7.5-H2.5.removed.step");
            string cleanPath = Path.Combine(dataRoot, "Clean", "BUZ-SMD_4P-L7.5-W7.5-H2.5.removed-test-clean.step");
            string automaticRemovedPath = Path.Combine(dataRoot, "RemovedGeometry", "BUZ-SMD_4P-L7.5-W7.5-H2.5.removed-test-clean.removed.step");
            string cleanProjectionDirectory = Path.Combine(dataRoot, "RemovedGeometryProjection");
            string verifierDirectory = Path.Combine(dataRoot, "Clean", "BUZSideLogoVerifier");

            Directory.CreateDirectory(outputDirectory);
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            if (string.IsNullOrWhiteSpace(report.RemovedGeometryStep))
            {
                failures.Add("Removed-geometry export should produce a non-empty diagnostic STEP file.");
            }
            else
            {
                File.WriteAllBytes(outputPath, Encoding.Latin1.GetBytes(report.RemovedGeometryStep));
                if (!File.Exists(outputPath))
                    failures.Add("Removed-geometry STEP file was not written: " + outputPath);
                else if (new FileInfo(outputPath).Length <= 0)
                    failures.Add("Removed-geometry STEP file is empty: " + outputPath);
            }

            File.WriteAllBytes(cleanPath, Encoding.Latin1.GetBytes(report.CleanedStep));
            if (File.Exists(automaticRemovedPath))
                File.Delete(automaticRemovedPath);

            RunStepCleanerCleanup(inputPath, cleanPath, failures);

            if (!File.Exists(automaticRemovedPath))
            {
                failures.Add("Normal cleanup should write a removed-geometry STEP next to the generated clean output: " + automaticRemovedPath);
            }
            else if (new FileInfo(automaticRemovedPath).Length <= 0)
            {
                failures.Add("Normal cleanup removed-geometry STEP is empty: " + automaticRemovedPath);
            }

            var projectionOptions = CreateVerificationProjectionOptions();
            projectionOptions.ViewNames.Clear();
            projectionOptions.ViewNames.Add("x_plus");
            StepProjectionRenderer.ProjectFile(cleanPath, cleanProjectionDirectory, projectionOptions);
            string cleanProjectionPath = Path.Combine(
                cleanProjectionDirectory,
                Path.GetFileNameWithoutExtension(cleanPath) + "__x_plus.png");
            var sideRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalStep,
                    Path.GetFileName(inputPath),
                    report.DetectionReport,
                    projectionOptions)
                .Where(region => string.Equals(region.ViewName, "x_plus", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(region => region.RectangleWidth * region.RectangleHeight)
                .ToList();
            if (sideRegions.Count == 0)
            {
                failures.Add("BUZ side logo detection should produce an x_plus cleanup region.");
            }
            else
            {
                using (var cleanProjection = SKBitmap.Decode(cleanProjectionPath))
                {
                    int brightPixels = sideRegions.Sum(region => CountBrightPixels(cleanProjection, region, threshold: 115));
                    brightPixels += CountBrightPixels(cleanProjection, 250, 380, 430, 520, threshold: 115);
                    double logoEdgeRatio = MeasureRegionEdgeRatio(cleanProjection, 250, 380, 430, 520);
                    if (brightPixels > 50)
                    {
                        failures.Add(
                            "BUZ side logo cleanup should remove bright logo pixels from x_plus; remaining bright pixels=" +
                            brightPixels.ToString(CultureInfo.InvariantCulture) +
                            ".");
                    }

                    if (logoEdgeRatio > 0.002)
                    {
                        failures.Add(
                            "BUZ side logo cleanup should flatten logo outline geometry on x_plus; edge ratio=" +
                            logoEdgeRatio.ToString("G4", CultureInfo.InvariantCulture) +
                            ".");
                    }
                }
            }

            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);

                StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                    originalStep,
                    Path.GetFileName(inputPath),
                    verifierDirectory);
            }
            catch (StepWatermarkCleanFailedException ex)
            {
                failures.Add("BUZ side logo cleanup should pass post-clean verification, but failed: " + string.Join("; ", ex.Failures));
            }

            VerifyRemovedGeometryDoesNotExportLargeNonWatermarkModels(dataRoot, failures);
            VerifyTextLogoPromotedRemovedGeometryIsNotEmpty(dataRoot, failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Removed-geometry export test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Removed-geometry export test passed.");
            Console.WriteLine("Removed geometry: " + outputPath);
            return 0;
        }

        private static int RunTextLogoCleanupPromotionTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtureNames = new[]
            {
                "CONN-TH_MR30PW-M30-G-Y.step",
                "USB-A-TH_FUS264-FDSW3K.step",
                "USB-B-TH_USB-B10-BRW.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "CONN-TH_XT60PB-M.step"
            };

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing text/logo cleanup promotion fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                var verifiedReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(report.DetectionReport);
                int templateTextLogoDetectionCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo detections");
                int templateTextLogoCandidateCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo cleanup candidates");
                int templateTextLogoCleanupRegionCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo cleanup regions");

                if (verifiedReport.Regions.Count <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should promote topology-confirmed text/logo projection detections to verified cleanup regions. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (templateTextLogoDetectionCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should run runtime template text/logo projection detection during normal cleanup. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (templateTextLogoCandidateCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should find topology-confirmed text/logo cleanup candidates. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (templateTextLogoCleanupRegionCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should promote template text/logo detections to cleanup regions, got " +
                        templateTextLogoCleanupRegionCount.ToString(CultureInfo.InvariantCulture) +
                        ". Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (BytesEqual(originalStep, Encoding.Latin1.GetBytes(report.CleanedStep)))
                {
                    failures.Add(
                        fixtureName +
                        " should not silently pass cleanup as a no-op after text/logo promotion. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (report.FlattenedFaceCount <= 0 && report.FlattenedPointCount <= 0 && report.RemovedSolidCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should report actual cleanup edits after text/logo promotion. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo cleanup promotion test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo cleanup promotion test passed.");
            return 0;
        }

        private static int RunTextLogoNegativeClassifierTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string fixtureName = "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing text/logo negative classifier fixture: " + inputPath);
            }
            else
            {
                string originalStep = Encoding.Latin1.GetString(File.ReadAllBytes(inputPath));
                var report = StepWatermarkCleaner.CleanWithReport(
                    originalStep,
                    new StepWatermarkCleanerOptions());
                string diagnostics = string.Join(" | ", report.Diagnostics ?? Array.Empty<string>());
                var originalEntities = ParseStepEntityDefinitions(originalStep);
                var cleanedEntities = ParseStepEntityDefinitions(report.CleanedStep);
                var removedEntities = ParseStepEntityDefinitions(report.RemovedGeometryStep ?? string.Empty);
                int[] protectedFaceIds = { 14214, 26383, 34754 };

                foreach (int protectedFaceId in protectedFaceIds)
                {
                    string protectedFace = "#" + protectedFaceId.ToString(CultureInfo.InvariantCulture) + " = ADVANCED_FACE";
                    AssertContains(
                        report.CleanedStep,
                        protectedFace,
                        "CONN-SMD text/logo negative classifier should preserve protected contact face " + protectedFace,
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        protectedFace,
                        "CONN-SMD text/logo negative classifier should not export protected contact face " + protectedFace + " as removed geometry",
                        failures);
                    VerifyProtectedFaceEntityClosurePreserved(
                        protectedFaceId,
                        originalEntities,
                        cleanedEntities,
                        removedEntities,
                        failures);
                }

                int templateTextLogoProtectedRejectCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo protected rejects");
                if (templateTextLogoProtectedRejectCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should explicitly reject protected contact geometry during template text/logo promotion. Diagnostics: " +
                        diagnostics);
                }

                if (report.DetectionReport.Regions.Any(region =>
                    region.EntityId == 14214 ||
                    region.EntityId == 26383 ||
                    region.EntityId == 34754))
                {
                    failures.Add(
                        fixtureName +
                        " detection regions should not reference protected contact faces #14214, #26383, or #34754. Diagnostics: " +
                        diagnostics);
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo negative classifier test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo negative classifier test passed.");
            return 0;
        }

        private static int RunTextLogoVerifierTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string fixtureName = "USB-B-TH_USB-B10-BRW.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing text/logo verifier fixture: " + inputPath);
            }
            else
            {
                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());

                string verifierDirectory = Path.Combine(dataRoot, "Clean", "TextLogoVerifierNoOp");
                try
                {
                    if (Directory.Exists(verifierDirectory))
                        Directory.Delete(verifierDirectory, true);
                }
                catch
                {
                }

                var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                    "VerifyPostCleanOutput",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null,
                    new[]
                    {
                        typeof(byte[]),
                        typeof(byte[]),
                        typeof(string),
                        typeof(StepWatermarkDetectionReport),
                        typeof(string)
                    },
                    null);
                if (verifyMethod == null)
                {
                    failures.Add("Could not find verifier post-clean method for text/logo verifier regression test.");
                }
                else
                {
                    object verification = verifyMethod.Invoke(
                        null,
                        new object[]
                        {
                            originalStep,
                            originalStep,
                            "usb-b-noop",
                            report.DetectionReport,
                            verifierDirectory
                        });
                    bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
                    string reportPath = (string)verification.GetType().GetProperty("ReportPath").GetValue(verification);
                    var verificationFailures = ((IEnumerable<string>)verification.GetType().GetProperty("Failures").GetValue(verification)).ToList();

                    if (passed)
                        failures.Add("Verifier should fail when original STEP is supplied as clean output for a detected text/logo watermark.");

                    if (!verificationFailures.Any(failure =>
                        failure.Contains("retains known watermark visual template", StringComparison.OrdinalIgnoreCase) ||
                        failure.Contains("retains text/logo edge detail", StringComparison.OrdinalIgnoreCase)))
                    {
                        failures.Add("Text/logo verifier failure should report retained visual template or edge detail inside the detected cleanup region.");
                    }

                    if (!File.Exists(reportPath))
                    {
                        failures.Add("Text/logo verifier failure report is missing: " + reportPath);
                    }
                    else
                    {
                        string reportText = File.ReadAllText(reportPath);
                        AssertContains(
                            reportText,
                            "Projection verification failures without comparison images",
                            "Text/logo verifier report should include non-visual failures.",
                            failures);
                    }
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo verifier test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo verifier test passed.");
            return 0;
        }

        private static int RunTextLogoVisualResidualTests(string[] args)
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            List<string> fixtureNames = new[]
            {
                "USB-B-TH_USB-B10-BRW.step",
                "USB-A-TH_FUS264-FDSW3K.step",
                "CONN-TH_MR30PW-M30-G-Y.step",
                "CONN-SMD_DF56_40S_0.3V_51.step",
                "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "CONN-TH_XT60PB-M.step"
            }.ToList();

            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                string filter = args[1];
                fixtureNames = fixtureNames
                    .Where(fixtureName =>
                        string.Equals(fixtureName, filter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(fixtureName), filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (fixtureNames.Count == 0)
                {
                    Console.Error.WriteLine("Text/logo visual residual test fixture was not found: " + filter);
                    return 2;
                }
            }

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing text/logo visual residual fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                byte[] cleanedStep = Encoding.Latin1.GetBytes(report.CleanedStep);
                StepWatermarkVisualResidualResult visual = StepWatermarkVisualOracle.VerifyKnownWatermarkRemoved(
                    originalStep,
                    cleanedStep,
                    fixtureName);

                foreach (string failure in visual.Failures)
                    failures.Add(failure + " Diagnostics: " + string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo visual residual test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo visual residual test passed.");
            return 0;
        }

        private static int RunTextLogoFullTopologyRemovalContractTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtures = new[]
            {
                new { FileName = "USB-C-SMD_TYPE-C-6PIN-2MD-073.step", ResidualView = "z_minus", MinRemovedFaces = 10 },
                new { FileName = "CONN-TH_MR30PB-M30.A.G.Y.step", ResidualView = "y_plus", MinRemovedFaces = 40 },
                new { FileName = "TYPE-C-TH_TYPEC-215-ARP14.step", ResidualView = "x_plus", MinRemovedFaces = 40 },
                new { FileName = "CONN-TH_MR30PW-M30-G-Y.step", ResidualView = "z_plus", MinRemovedFaces = 20 },
                new { FileName = "CONN-SMD_DF56_40S_0.3V_51.step", ResidualView = "z_minus", MinRemovedFaces = 40 }
            };

            foreach (var fixture in fixtures)
            {
                string inputPath = Path.Combine(originalDirectory, fixture.FileName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing full-topology text/logo fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                int activeRemovedFaceCount = GetActiveAdvancedFaceIds(report.RemovedGeometryStep ?? string.Empty).Count();
                if (activeRemovedFaceCount < fixture.MinRemovedFaces)
                {
                    failures.Add(
                        fixture.FileName +
                        " removed-geometry export should include full text/logo topology, including side-wall faces; activeFaces=" +
                        activeRemovedFaceCount.ToString(CultureInfo.InvariantCulture) +
                        " expected>=" +
                        fixture.MinRemovedFaces.ToString(CultureInfo.InvariantCulture) +
                        ". Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                StepVectorWatermarkDetectionInput cleanInput =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                        Encoding.Latin1.GetBytes(report.CleanedStep),
                        Path.GetFileNameWithoutExtension(fixture.FileName) + ".clean",
                        fixture.ResidualView);
                IReadOnlyList<StepVectorWatermarkDetectionRegion> cleanDetections =
                    StepVectorWatermarkProjectionDetector.Detect(
                        cleanInput,
                        new StepTextLogoDetectionOptions { DetectArbitraryText = true });
                var residualTextDetections = cleanDetections
                    .Where(detection => detection.PrimitiveCount >= 8)
                    .Where(detection =>
                        string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(detection.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (residualTextDetections.Count > 0)
                {
                    failures.Add(
                        fixture.FileName +
                        " retains vector text/logo detections on " +
                        fixture.ResidualView +
                        " after cleanup: " +
                        string.Join("; ", residualTextDetections.Select(detection =>
                            (detection.TemplateName ?? string.Empty) +
                            " " +
                            detection.X.ToString(CultureInfo.InvariantCulture) +
                            "," +
                            detection.Y.ToString(CultureInfo.InvariantCulture) +
                            " " +
                            detection.Width.ToString(CultureInfo.InvariantCulture) +
                            "x" +
                            detection.Height.ToString(CultureInfo.InvariantCulture) +
                            " prims=" +
                            detection.PrimitiveCount.ToString(CultureInfo.InvariantCulture))));
                }

                if (string.Equals(fixture.FileName, "USB-C-SMD_TYPE-C-6PIN-2MD-073.step", StringComparison.OrdinalIgnoreCase))
                {
                    VerifyUsbCShadedLetterFacesRemoved(fixture.FileName, fixture.ResidualView, originalStep, report, dataRoot, failures);
                    VerifyUsbCRemovedGeometryProjectsFullText(fixture.FileName, fixture.ResidualView, report, failures);
                }

                if (string.Equals(fixture.FileName, "CONN-SMD_DF56_40S_0.3V_51.step", StringComparison.OrdinalIgnoreCase))
                    VerifyDf56BottomWatermarkTopologyRemoved(fixture.FileName, fixture.ResidualView, originalStep, report, failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo full-topology removal contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo full-topology removal contract passed.");
            return 0;
        }

        private static void VerifyUsbCShadedLetterFacesRemoved(
            string fixtureName,
            string viewName,
            byte[] originalStep,
            StepWatermarkCleanerReport report,
            string dataRoot,
            List<string> failures)
        {
            StepWatermarkRegionDetection detection = report.DetectionReport?.Regions?.FirstOrDefault(region =>
                string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(region.Kind, "visual", StringComparison.OrdinalIgnoreCase) &&
                region.RectangleX.HasValue &&
                region.RectangleY.HasValue &&
                region.RectangleWidth.HasValue &&
                region.RectangleHeight.HasValue);
            if (detection == null)
            {
                failures.Add(fixtureName + " " + viewName + " cleanup visual region is missing.");
                return;
            }

            string projectionDirectory = Path.Combine(dataRoot, "Clean", "TextLogoFullTopologyProjection");
            Directory.CreateDirectory(projectionDirectory);
            string cleanStepPath = Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(fixtureName) + ".clean.step");
            File.WriteAllText(cleanStepPath, report.CleanedStep, Encoding.Latin1);

            var projectionOptions = StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color);
            projectionOptions.ViewNames.Clear();
            projectionOptions.ViewNames.Add(viewName);
            StepProjectionRenderer.ProjectFile(cleanStepPath, projectionDirectory, projectionOptions);
            string cleanProjectionPath = Path.Combine(
                projectionDirectory,
                Path.GetFileNameWithoutExtension(cleanStepPath) + "__" + viewName + ".png");
            using (var cleanProjection = SKBitmap.Decode(cleanProjectionPath))
            {
                int brightPixels = CountBrightPixels(
                    cleanProjection,
                    detection.RectangleX.Value,
                    detection.RectangleY.Value,
                    detection.RectangleX.Value + detection.RectangleWidth.Value - 1,
                    detection.RectangleY.Value + detection.RectangleHeight.Value - 1,
                    threshold: 145);
                if (brightPixels > 60)
                {
                    failures.Add(
                        fixtureName +
                        " retains shaded letter-face pixels on " +
                        viewName +
                        ": brightPixels=" +
                        brightPixels.ToString(CultureInfo.InvariantCulture) +
                        " expected<=60 in original watermark rectangle " +
                        detection.RectangleX.Value.ToString(CultureInfo.InvariantCulture) +
                        "," +
                        detection.RectangleY.Value.ToString(CultureInfo.InvariantCulture) +
                        " " +
                        detection.RectangleWidth.Value.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        detection.RectangleHeight.Value.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }
        }

        private static void VerifyUsbCRemovedGeometryProjectsFullText(
            string fixtureName,
            string viewName,
            StepWatermarkCleanerReport report,
            List<string> failures)
        {
            string removedGeometryStep = report.RemovedGeometryStep ?? string.Empty;
            if (string.IsNullOrWhiteSpace(removedGeometryStep))
            {
                failures.Add(fixtureName + " removed geometry STEP is empty; expected exported LCEDA topology.");
                return;
            }

            if (Regex.IsMatch(removedGeometryStep, @",\s*\)\s*\)", RegexOptions.CultureInvariant))
            {
                failures.Add(fixtureName + " removed geometry contains malformed trailing-comma STEP lists, which can hide exported letters from OCCT/F3D.");
                return;
            }

            StepVectorWatermarkDetectionInput removedInput;
            try
            {
                removedInput = StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    Encoding.Latin1.GetBytes(removedGeometryStep),
                    Path.GetFileNameWithoutExtension(fixtureName) + ".removed",
                    viewName);
            }
            catch (Exception ex)
            {
                failures.Add(fixtureName + " removed geometry must be projectable so the exported letters can be inspected; projection failed: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            var primitiveBounds = removedInput.Primitives
                .Where(primitive => primitive.ImageBounds != null)
                .Select(primitive => primitive.ImageBounds)
                .ToList();
            int primitiveCount = primitiveBounds.Count;
            double left = primitiveBounds.Count == 0 ? 0.0 : primitiveBounds.Min(bounds => bounds.Left);
            double right = primitiveBounds.Count == 0 ? 0.0 : primitiveBounds.Max(bounds => bounds.Right);
            double top = primitiveBounds.Count == 0 ? 0.0 : primitiveBounds.Max(bounds => bounds.Top);
            double bottom = primitiveBounds.Count == 0 ? 0.0 : primitiveBounds.Min(bounds => bounds.Bottom);
            double width = right - left;
            double height = top - bottom;
            if (removedGeometryStep.IndexOf("removed-host-loop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                failures.Add(fixtureName + " removed geometry should contain real removed faces, not synthetic removed-host-loop patch faces that fill letter holes.");
            }

            if (primitiveCount < 80 || width < 500 || height < 120)
            {
                failures.Add(
                    fixtureName +
                    " removed geometry should project a broad LCEDA diagnostic footprint; primitives=" +
                    primitiveCount.ToString(CultureInfo.InvariantCulture) +
                    ", bounds=" +
                    width.ToString("0.0", CultureInfo.InvariantCulture) +
                    "x" +
                    height.ToString("0.0", CultureInfo.InvariantCulture) +
                    ".");
            }

            IReadOnlyDictionary<int, string> removedEntities = ParseStepEntityDefinitions(removedGeometryStep);
            var boundsById = new Dictionary<int, StepBounds3d>();
            int daFaceCount = GetActiveAdvancedFaceIds(removedGeometryStep)
                .Select(faceId => GetStepEntityBounds(faceId, removedEntities, boundsById))
                .Count(bounds =>
                    bounds.HasValue &&
                    StepBoundsProjectionIntersects(bounds, excludedAxis: 2, uMin: 0.55, uMax: 2.05, vMin: 0.65, vMax: 1.75) &&
                    bounds.MinZ >= -1.581 &&
                    bounds.MaxZ <= -1.559);
            if (daFaceCount < 20)
            {
                failures.Add(
                    fixtureName +
                    " removed geometry should keep real D/A wall and hole faces in the LCEDA topology; daFaces=" +
                    daFaceCount.ToString(CultureInfo.InvariantCulture) +
                    " expected>=20.");
            }
        }

        private static void VerifyDf56BottomWatermarkTopologyRemoved(
            string fixtureName,
            string viewName,
            byte[] originalStep,
            StepWatermarkCleanerReport report,
            List<string> failures)
        {
            string modelName = Path.GetFileNameWithoutExtension(fixtureName);
            StepVectorWatermarkDetectionInput input =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    originalStep,
                    modelName,
                    viewName);
            StepVectorWatermarkDetectionRegion detection = StepVectorWatermarkProjectionDetector
                .Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = true })
                .Where(region =>
                    (region.TemplateName ?? string.Empty).IndexOf("LCEDA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(region.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(region.Kind, "text", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(region => region.Score)
                .FirstOrDefault();
            if (detection == null)
            {
                failures.Add(fixtureName + " should have an original " + viewName + " LCEDA detection for bottom-footprint topology verification.");
                return;
            }

            StepProjectionBounds2d roi = ToProjectionBounds(input, detection, paddingPixels: 8);
            IReadOnlyDictionary<int, string> cleanedEntities = ParseStepEntityDefinitions(report.CleanedStep);
            List<int> cleanedBounds = FindRetainedInnerFaceBoundsInsideProjectionRoi(cleanedEntities, roi);
            if (cleanedBounds.Count > 0)
            {
                failures.Add(
                    fixtureName +
                    " retained " +
                    cleanedBounds.Count.ToString(CultureInfo.InvariantCulture) +
                    " inner face bounds inside the " +
                    viewName +
                    " LCEDA cleanup ROI after cleanup: " +
                    string.Join(", ", cleanedBounds.Take(12).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))) +
                    ".");
            }

            List<int> residualBottomFaces = FindActiveAdvancedFacesInsideProjectionRoiAndDepth(
                cleanedEntities,
                roi,
                minZ: -0.002,
                maxZ: 0.012);
            if (residualBottomFaces.Count > 0)
            {
                failures.Add(
                    fixtureName +
                    " retained " +
                    residualBottomFaces.Count.ToString(CultureInfo.InvariantCulture) +
                    " active bottom-side watermark wall/fill faces inside the " +
                    viewName +
                    " LCEDA cleanup ROI after cleanup: " +
                    string.Join(", ", residualBottomFaces.Take(12).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))) +
                    ".");
            }

            StepVectorWatermarkDetectionInput cleanedInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    Encoding.Latin1.GetBytes(report.CleanedStep ?? string.Empty),
                    modelName + ".cleaned",
                    viewName);
            int cleanedPrimitiveFootprintCount = cleanedInput.Primitives
                .Count(primitive => primitive.ImageBounds != null && VectorPrimitiveIntersectsDetection(primitive, detection));
            if (cleanedPrimitiveFootprintCount > 0)
            {
                List<ProjectedStepTopologySource> cleanedTopology = BuildProjectedStepTopologySources(
                    report.CleanedStep ?? string.Empty,
                    viewName);
                var residualSourceSummary = cleanedInput.Primitives
                    .Where(primitive => primitive.ImageBounds != null && VectorPrimitiveIntersectsDetection(primitive, detection))
                    .Select(primitive => MatchResidualPrimitiveSource(primitive, cleanedTopology))
                    .Where(match => match.Source != null)
                    .GroupBy(match => match.Source.Key, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        Source = group.First().Source,
                        Count = group.Count()
                    })
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Source.Key, StringComparer.Ordinal)
                    .Take(12)
                    .Select(group =>
                        "#" + group.Source.FaceId.ToString(CultureInfo.InvariantCulture) +
                        "/#" + group.Source.BoundId.ToString(CultureInfo.InvariantCulture) +
                        "/#" + group.Source.EdgeCurveId.ToString(CultureInfo.InvariantCulture) +
                        "x" + group.Count.ToString(CultureInfo.InvariantCulture))
                    .ToList();
                failures.Add(
                    fixtureName +
                    " retained " +
                    cleanedPrimitiveFootprintCount.ToString(CultureInfo.InvariantCulture) +
                    " projected vector primitives inside the original " +
                    viewName +
                    " LCEDA watermark ROI after cleanup; hidden wall/edge footprint topology should be removed. Sources: " +
                    string.Join(", ", residualSourceSummary) +
                    ".");
            }

            StepVectorWatermarkDetectionInput removedInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    Encoding.Latin1.GetBytes(report.RemovedGeometryStep ?? string.Empty),
                    modelName + ".removed",
                    viewName);
            int removedPrimitiveCount = removedInput.Primitives.Count(primitive => primitive.ImageBounds != null);
            if (removedPrimitiveCount < 180)
            {
                failures.Add(
                    fixtureName +
                    " removed geometry should keep full bottom LCEDA wall topology for inspection; primitives=" +
                    removedPrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                    " expected>=180.");
            }
        }

        private static int RunRemovedGeometryNonWatermarkContainmentContractTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtureNames = new[]
            {
                "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
                "CONN-SMD_DF56_40S_0.3V_51.step",
                "TYPE-C-TH_TYPEC-215-ARP14.step",
                "USB-A-TH_FUS264-FDSW3K.step",
                "USB-A-SMD_USB-212-BCW.step"
            };

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing removed-geometry non-watermark containment fixture: " + inputPath);
                    continue;
                }

                var report = StepWatermarkCleaner.CleanWithReport(
                    File.ReadAllText(inputPath, Encoding.Latin1),
                    new StepWatermarkCleanerOptions());
                string diagnostics = string.Join(" | ", report.Diagnostics ?? Array.Empty<string>());
                if ((report.RemovedGeometryStep ?? string.Empty).Contains("removed-watermark-proxy", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(fixtureName + " removed geometry must export real removed STEP topology, not proxy prism solids.");
                }

                if (diagnostics.Contains("Vector prism contained owner:", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        fixtureName +
                        " removed geometry used contained-prism owner promotion for connector/contact solids. Diagnostics: " +
                        diagnostics);
                }

                if (diagnostics.Contains("Automatic cleanup volume detail: owner=#0", StringComparison.OrdinalIgnoreCase) ||
                    diagnostics.Contains(" host=#0 ", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        fixtureName +
                        " must not create synthetic owner/host #0 cleanup volumes from screen-space text/logo regions; " +
                        "cross-owner loop detections must stay host-bound so unrelated depth geometry is not removed. Diagnostics: " +
                        diagnostics);
                }

                int activeRemovedFaceCount = GetActiveAdvancedFaceIds(report.RemovedGeometryStep ?? string.Empty).Count();
                if (activeRemovedFaceCount <= 0)
                    failures.Add(fixtureName + " should still export removed watermark/text topology after non-watermark filtering.");

                VerifyRemovedGeometryExcludesKnownBroadSourceFaces(
                    fixtureName,
                    report.RemovedGeometryStep ?? string.Empty,
                    activeRemovedFaceCount,
                    failures);

                VerifyNoSourceRegionFaceSweepRemovals(
                    fixtureName,
                    diagnostics,
                    failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Removed-geometry non-watermark containment contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Removed-geometry non-watermark containment contract passed.");
            return 0;
        }

        private static void VerifyRemovedGeometryExcludesKnownBroadSourceFaces(
            string fixtureName,
            string removedGeometryStep,
            int activeRemovedFaceCount,
            List<string> failures)
        {
            var expectedBroadFaceIds = new Dictionary<string, int[]>
            {
                ["CONN-SMD_DF56_40S_0.3V_51.step"] = new[] { 12236, 15340 }
            };
            var expectedMinimumActiveFaces = new Dictionary<string, int>
            {
                ["CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step"] = 20,
                ["CONN-SMD_DF56_40S_0.3V_51.step"] = 20,
                ["TYPE-C-TH_TYPEC-215-ARP14.step"] = 20
            };

            if (expectedBroadFaceIds.TryGetValue(fixtureName, out int[] broadFaceIds))
            {
                foreach (int broadFaceId in broadFaceIds)
                {
                    string facePrefix = "#" + broadFaceId.ToString(CultureInfo.InvariantCulture) + " = ADVANCED_FACE";
                    if (removedGeometryStep.Contains(facePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            fixtureName +
                            " removed geometry should not export broad non-watermark source face #" +
                            broadFaceId.ToString(CultureInfo.InvariantCulture) +
                            "; the cleaner must remove/export only real watermark topology.");
                    }
                }
            }

            if (expectedMinimumActiveFaces.TryGetValue(fixtureName, out int minimumActiveFaces) &&
                activeRemovedFaceCount < minimumActiveFaces)
            {
                failures.Add(
                    fixtureName +
                    " removed geometry should keep enough active topology to visualize removed walls/fill; activeFaces=" +
                    activeRemovedFaceCount.ToString(CultureInfo.InvariantCulture) +
                    " expected>=" +
                    minimumActiveFaces.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static void VerifyNoSourceRegionFaceSweepRemovals(
            string fixtureName,
            string diagnostics,
            List<string> failures)
        {
            var fixturesRequiringPrimitiveMembership = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
                "CONN-SMD_DF56_40S_0.3V_51.step",
                "TYPE-C-TH_TYPEC-215-ARP14.step"
            };
            if (!fixturesRequiringPrimitiveMembership.Contains(fixtureName) ||
                string.IsNullOrWhiteSpace(diagnostics))
            {
                return;
            }

            foreach (Match match in Regex.Matches(
                diagnostics,
                @"Residual source-region sweep:[^|]*containedFaces=(\d+)",
                RegexOptions.IgnoreCase))
            {
                int containedFaces = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (containedFaces <= 0)
                    continue;

                failures.Add(
                    fixtureName +
                    " must not remove faces through residual source-region rectangle sweeps; " +
                    "use detector primitive membership plus host-loop adjacent walls instead. Diagnostic: " +
                    match.Value);
            }
        }

        private static int RunNonWatermarkHolePreservationContractTests()
        {
            var failures = new List<string>();
            var visualFailures = new List<ProjectionVisualFailure>();
            string dataRoot = FindDataRoot();
            string fixtureName = "USB-A-SMD_USB-212-BCW.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing non-watermark hole preservation fixture: " + inputPath);
            }
            else
            {
                VerifyFocusedOriginalVsCleanProjection(
                    dataRoot,
                    fixtureName,
                    "y_plus",
                    failures,
                    visualFailures);
                var report = StepWatermarkCleaner.CleanWithReport(
                    File.ReadAllText(inputPath, Encoding.Latin1),
                    new StepWatermarkCleanerOptions());
                VerifyNoBroadVectorPrismFaceSelection(
                    fixtureName,
                    "y_plus",
                    report,
                    maxSelectedFaces: 80,
                    failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Non-watermark hole preservation contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Non-watermark hole preservation contract passed.");
            return 0;
        }

        private static void VerifyNoBroadVectorPrismFaceSelection(
            string fixtureName,
            string viewName,
            StepWatermarkCleanerReport report,
            int maxSelectedFaces,
            List<string> failures)
        {
            foreach (string diagnostic in report.Diagnostics ?? Array.Empty<string>())
            {
                if (!diagnostic.Contains("Vector prism candidate:", StringComparison.OrdinalIgnoreCase) ||
                    !diagnostic.Contains("view=" + viewName, StringComparison.OrdinalIgnoreCase) ||
                    !diagnostic.Contains("selectedFaces=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int index = diagnostic.IndexOf("selectedFaces=", StringComparison.OrdinalIgnoreCase);
                int start = index + "selectedFaces=".Length;
                int end = start;
                while (end < diagnostic.Length && char.IsDigit(diagnostic[end]))
                    end++;
                if (end <= start)
                    continue;

                if (!int.TryParse(diagnostic.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int selectedFaces))
                    continue;

                if (selectedFaces > maxSelectedFaces)
                {
                    failures.Add(
                        fixtureName +
                        " " +
                        viewName +
                        " selected " +
                        selectedFaces.ToString(CultureInfo.InvariantCulture) +
                        " faces through a vector prism candidate; expected<=" +
                        maxSelectedFaces.ToString(CultureInfo.InvariantCulture) +
                        " to preserve non-watermark holes/surfaces. Diagnostic: " +
                        diagnostic);
                }
            }
        }

        private static int RunDetectorBlindResidualTopologyContractTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            var fixtures = new[]
            {
                new { FileName = "TYPE-C-TH_TYPEC-215-ARP14.step", ViewName = "x_plus", MaxCleanEdgeRatio = 0.020 },
                new { FileName = "CONN-TH_MR30PW-M30-G-Y.step", ViewName = "z_plus", MaxCleanEdgeRatio = 0.010 },
                new { FileName = "CONN-TH_MR30PB-M30.A.G.Y.step", ViewName = "y_plus", MaxCleanEdgeRatio = 0.010 },
                new { FileName = "CONN-SMD_DF56_40S_0.3V_51.step", ViewName = "z_minus", MaxCleanEdgeRatio = 0.010 }
            };

            foreach (var fixture in fixtures)
            {
                string inputPath = Path.Combine(dataRoot, "Original", fixture.FileName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing detector-blind residual topology fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                VerifyCleanVectorDetectorIsBlind(fixture.FileName, fixture.ViewName, report, failures);
                VerifyNoBlockedResidualWatermarkSources(fixture.FileName, fixture.ViewName, report, failures);
                VerifyCleanProjectionEdgeDensityInsideDetection(
                    dataRoot,
                    fixture.FileName,
                    fixture.ViewName,
                    originalStep,
                    report,
                    fixture.MaxCleanEdgeRatio,
                    failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Detector-blind residual topology contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Detector-blind residual topology contract passed.");
            return 0;
        }

        private static void VerifyNoBlockedResidualWatermarkSources(
            string fixtureName,
            string viewName,
            StepWatermarkCleanerReport report,
            List<string> failures)
        {
            string blockedResidualPrefix = "Residual vector rewrite skipped: view=" + viewName;
            var blockedDiagnostics = report.Diagnostics
                .Where(diagnostic => diagnostic.IndexOf(blockedResidualPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(diagnostic => diagnostic.IndexOf("blockedSources=", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (blockedDiagnostics.Count == 0)
                return;

            failures.Add(
                fixtureName +
                " " +
                viewName +
                " still has blocked residual watermark topology sources: " +
                string.Join(" || ", blockedDiagnostics));
        }

        private static int RunReportedCleanupRegressionContractTests(string[] args)
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            var fixtures = new[]
            {
                new { FileName = "CONN-SMD_DF56_40S_0.3V_51.step", ViewName = "z_minus", Reason = "bottom watermark footprint must be removed" },
                new { FileName = "CONN-TH_MR30PW-M30-G-Y.step", ViewName = "z_plus", Reason = "logo must be fully cleaned" },
                new { FileName = "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step", ViewName = "z_plus", Reason = "logo must be cleaned and package text must be preserved" },
                new { FileName = "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step", ViewName = "z_minus", Reason = "bottom LCEDA watermark must be cleaned" },
                new { FileName = "LED-SMD_XL-3838UV2SA06G3.step", ViewName = "y_minus", Reason = "bottom logo/text residual marks must be removed" },
                new { FileName = "USB-C-SMD_TYPE-C-6PIN-2MD-073.step", ViewName = "z_minus", Reason = "bottom LCEDA residual letters must be removed" },
                new { FileName = "USB-A-SMD_USB-212-BCW.step", ViewName = "y_plus", Reason = "left/right non-watermark side faces must be preserved" },
                new { FileName = "USB-A-SMD_USB-212-BCW.step", ViewName = "z_minus", Reason = "bottom holes and non-watermark faces must be preserved" },
                new { FileName = "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step", ViewName = "z_minus", Reason = "non-watermark faces must be preserved" },
                new { FileName = "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step", ViewName = "z_plus", Reason = "non-watermark faces must be preserved" }
            }.ToList();

            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                string filter = args[1];
                fixtures = fixtures
                    .Where(fixture => fixture.FileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (fixtures.Count == 0)
                {
                    Console.Error.WriteLine("Reported cleanup regression fixture was not found: " + filter);
                    return 2;
                }
            }

            string projectionDirectory = Path.Combine(dataRoot, "Clean", "ReportedCleanupRegressionProjection");
            Directory.CreateDirectory(projectionDirectory);
            var projectionOptions = StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color);

            foreach (var fixtureGroup in fixtures.GroupBy(fixture => fixture.FileName, StringComparer.OrdinalIgnoreCase))
            {
                string fixtureFileName = fixtureGroup.Key;
                string originalPath = Path.Combine(dataRoot, "Original", fixtureFileName);
                string validatedPath = Path.Combine(dataRoot, "Validated", fixtureFileName);
                if (!File.Exists(originalPath))
                {
                    failures.Add("Missing reported cleanup regression original fixture: " + originalPath);
                    continue;
                }

                if (!File.Exists(validatedPath))
                {
                    failures.Add("Missing reported cleanup regression validated fixture: " + validatedPath);
                    continue;
                }

                string modelName = Path.GetFileNameWithoutExtension(fixtureFileName);
                string originalCopyPath = Path.Combine(projectionDirectory, modelName + ".original.step");
                string cleanPath = Path.Combine(projectionDirectory, modelName + ".clean.step");
                string validatedCopyPath = Path.Combine(projectionDirectory, modelName + ".validated.step");
                byte[] originalBytes = File.ReadAllBytes(originalPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalBytes),
                    new StepWatermarkCleanerOptions());
                File.Copy(originalPath, originalCopyPath, true);
                File.WriteAllText(cleanPath, report.CleanedStep, Encoding.Latin1);
                File.Copy(validatedPath, validatedCopyPath, true);

                projectionOptions.ViewNames.Clear();
                foreach (string viewName in fixtureGroup.Select(fixture => fixture.ViewName).Distinct(StringComparer.OrdinalIgnoreCase))
                    projectionOptions.ViewNames.Add(viewName);
                StepProjectionRenderer.ProjectFile(originalCopyPath, projectionDirectory, projectionOptions);
                StepProjectionRenderer.ProjectFile(cleanPath, projectionDirectory, projectionOptions);
                StepProjectionRenderer.ProjectFile(validatedCopyPath, projectionDirectory, projectionOptions);

                foreach (var fixture in fixtureGroup)
                {
                    string originalProjectionPath = Path.Combine(projectionDirectory, modelName + ".original__" + fixture.ViewName + ".png");
                    string cleanProjectionPath = Path.Combine(projectionDirectory, modelName + ".clean__" + fixture.ViewName + ".png");
                    string validatedProjectionPath = Path.Combine(projectionDirectory, modelName + ".validated__" + fixture.ViewName + ".png");
                    if (!File.Exists(originalProjectionPath))
                    {
                        failures.Add("Missing original projection for reported cleanup regression: " + originalProjectionPath);
                        continue;
                    }

                    if (!File.Exists(cleanProjectionPath))
                    {
                        failures.Add("Missing clean projection for reported cleanup regression: " + cleanProjectionPath);
                        continue;
                    }

                    if (!File.Exists(validatedProjectionPath))
                    {
                        failures.Add("Missing validated projection for reported cleanup regression: " + validatedProjectionPath);
                        continue;
                    }

                    bool isSot223ZPlus =
                        string.Equals(fixture.FileName, "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fixture.ViewName, "z_plus", StringComparison.OrdinalIgnoreCase);
                    if (!isSot223ZPlus &&
                        !ProjectionPixelsEqual(cleanProjectionPath, validatedProjectionPath))
                    {
                        failures.Add(
                            fixture.FileName +
                            " " +
                            fixture.ViewName +
                            " differs from validated cleanup (" +
                            fixture.Reason +
                            "): " +
                            cleanProjectionPath +
                            " vs " +
                            validatedProjectionPath);
                    }

                    if (isSot223ZPlus)
                    {
                        VerifyProjectionRegionVisiblePixelsRetained(
                            fixture.FileName,
                            fixture.ViewName,
                            "original SOT-223-4P package marking",
                            originalProjectionPath,
                            cleanProjectionPath,
                            x: 730,
                            y: 500,
                            width: 180,
                            height: 620,
                            luminanceThreshold: 96,
                            minRetainedRatio: 0.94,
                            failures);
                        VerifyProjectionRegionPreserved(
                            fixture.FileName,
                            fixture.ViewName,
                            "validated SOT EasyEDA/LCEDA watermark cleanup",
                            validatedProjectionPath,
                            cleanProjectionPath,
                            x: 930,
                            y: 1060,
                            width: 180,
                            height: 350,
                            maxChangedPixels: 180,
                            failures);
                    }
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Reported cleanup regressions contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Reported cleanup regressions contract passed.");
            return 0;
        }

        private static void VerifyFocusedOriginalVsCleanProjection(
            string dataRoot,
            string fixtureName,
            string viewName,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            string projectionDirectory = Path.Combine(dataRoot, "Clean", "Task9FocusedProjection", Path.GetFileNameWithoutExtension(fixtureName));
            Directory.CreateDirectory(projectionDirectory);
            string originalStepPath = Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(fixtureName) + ".original.step");
            string cleanStepPath = Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(fixtureName) + ".clean.step");
            File.WriteAllBytes(originalStepPath, originalStep);
            File.WriteAllText(cleanStepPath, report.CleanedStep, Encoding.Latin1);

            var projectionOptions = StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color);
            projectionOptions.ViewNames.Clear();
            projectionOptions.ViewNames.Add(viewName);
            StepProjectionRenderer.ProjectFile(originalStepPath, projectionDirectory, projectionOptions);
            StepProjectionRenderer.ProjectFile(cleanStepPath, projectionDirectory, projectionOptions);

            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalStep,
                    fixtureName,
                    report.DetectionReport,
                    projectionOptions)
                .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var postCleanFaultFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VerifyPostCleanProjectionImage(
                fixtureName,
                viewName,
                Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(originalStepPath) + "__" + viewName + ".png"),
                Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(cleanStepPath) + "__" + viewName + ".png"),
                detectionRegions,
                postCleanFaultFileNames,
                failures,
                visualFailures);
        }

        private static void VerifyCleanVectorDetectorIsBlind(
            string fixtureName,
            string viewName,
            StepWatermarkCleanerReport report,
            List<string> failures)
        {
            StepVectorWatermarkDetectionInput cleanInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    Encoding.Latin1.GetBytes(report.CleanedStep),
                    Path.GetFileNameWithoutExtension(fixtureName) + ".clean",
                    viewName);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> cleanDetections =
                StepVectorWatermarkProjectionDetector.Detect(
                    cleanInput,
                    new StepTextLogoDetectionOptions { DetectArbitraryText = true });
            var residuals = cleanDetections
                .Where(detection =>
                    string.Equals(detection.Kind, "logo", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(detection.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
                .Where(detection => detection.PrimitiveCount >= 8)
                .ToList();
            if (residuals.Count > 0)
            {
                failures.Add(
                    fixtureName +
                    " " +
                    viewName +
                    " should exercise detector-blind topology, but vector detector still sees residuals: " +
                    string.Join("; ", residuals.Select(detection => detection.Kind + ":" + (detection.TemplateName ?? string.Empty))));
            }
        }

        private static void VerifyCleanProjectionEdgeDensityInsideDetection(
            string dataRoot,
            string fixtureName,
            string viewName,
            byte[] originalStep,
            StepWatermarkCleanerReport report,
            double maxCleanEdgeRatio,
            List<string> failures)
        {
            string projectionDirectory = Path.Combine(dataRoot, "Clean", "Task9DetectorBlindProjection", Path.GetFileNameWithoutExtension(fixtureName));
            Directory.CreateDirectory(projectionDirectory);
            string originalStepPath = Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(fixtureName) + ".original.step");
            string cleanStepPath = Path.Combine(projectionDirectory, Path.GetFileNameWithoutExtension(fixtureName) + ".clean.step");
            File.WriteAllBytes(originalStepPath, originalStep);
            File.WriteAllText(cleanStepPath, report.CleanedStep, Encoding.Latin1);

            var projectionOptions = StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color);
            projectionOptions.ViewNames.Clear();
            projectionOptions.ViewNames.Add(viewName);
            StepProjectionRenderer.ProjectFile(originalStepPath, projectionDirectory, projectionOptions);
            StepProjectionRenderer.ProjectFile(cleanStepPath, projectionDirectory, projectionOptions);

            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalStep,
                    fixtureName,
                    report.DetectionReport,
                    projectionOptions)
                .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                .Where(IsVisualDetectionRegion)
                .ToList();

            string cleanProjectionPath = Path.Combine(
                projectionDirectory,
                Path.GetFileNameWithoutExtension(cleanStepPath) + "__" + viewName + ".png");
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (cleanImage == null)
                {
                    failures.Add(fixtureName + " " + viewName + " clean projection could not be decoded: " + cleanProjectionPath);
                    return;
                }

                bool checkedAnyRegion = false;
                foreach (StepProjectionDetectionRegion region in detectionRegions)
                {
                    int left = Math.Max(0, region.RectangleX);
                    int top = Math.Max(0, region.RectangleY);
                    int right = Math.Min(cleanImage.Width - 1, region.RectangleX + region.RectangleWidth - 1);
                    int bottom = Math.Min(cleanImage.Height - 1, region.RectangleY + region.RectangleHeight - 1);
                    if (right <= left || bottom <= top)
                        continue;

                    double cleanEdgeRatio = MeasureRegionEdgeRatio(cleanImage, left, top, right, bottom);
                    if (cleanEdgeRatio > maxCleanEdgeRatio)
                    {
                        failures.Add(
                            fixtureName +
                            " " +
                            viewName +
                            " still has detector-blind residual topology inside cleanup ROI [" +
                            left.ToString(CultureInfo.InvariantCulture) +
                            "," +
                            top.ToString(CultureInfo.InvariantCulture) +
                            " " +
                            (right - left + 1).ToString(CultureInfo.InvariantCulture) +
                            "x" +
                            (bottom - top + 1).ToString(CultureInfo.InvariantCulture) +
                            "]: clean edge ratio=" +
                            cleanEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                            ", expected<=" +
                            maxCleanEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                            ".");
                    }

                    checkedAnyRegion = true;
                }

                if (!checkedAnyRegion)
                {
                    StepWatermarkVisualScanResult originalVisual = StepWatermarkVisualOracle.DetectKnownWatermarks(
                        originalStep,
                        fixtureName + ".original");
                    foreach (var detection in originalVisual.Detections
                        .Where(detection => string.Equals(detection.ViewName, viewName, StringComparison.OrdinalIgnoreCase)))
                    {
                        int left = Math.Max(0, detection.X);
                        int top = Math.Max(0, detection.Y);
                        int right = Math.Min(cleanImage.Width - 1, detection.X + detection.Width - 1);
                        int bottom = Math.Min(cleanImage.Height - 1, detection.Y + detection.Height - 1);
                        if (right <= left || bottom <= top)
                            continue;

                        double cleanEdgeRatio = MeasureRegionEdgeRatio(cleanImage, left, top, right, bottom);
                        if (cleanEdgeRatio > maxCleanEdgeRatio)
                        {
                            failures.Add(
                                fixtureName +
                                " " +
                                viewName +
                                " still has detector-blind residual topology inside visual ROI [" +
                                left.ToString(CultureInfo.InvariantCulture) +
                                "," +
                                top.ToString(CultureInfo.InvariantCulture) +
                                " " +
                                (right - left + 1).ToString(CultureInfo.InvariantCulture) +
                                "x" +
                                (bottom - top + 1).ToString(CultureInfo.InvariantCulture) +
                                "]: clean edge ratio=" +
                                cleanEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                                ", expected<=" +
                                maxCleanEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                                ".");
                        }

                        checkedAnyRegion = true;
                    }
                }

                if (!checkedAnyRegion)
                {
                    failures.Add(fixtureName + " " + viewName + " has no projected visual cleanup region for detector-blind residual check.");
                }
            }
        }

        private static int RunRemovedGeometryRoiLocalityTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtureNames = new[]
            {
                "USB-B-TH_USB-B10-BRW.step",
                "CONN-TH_MR30PW-M30-G-Y.step"
            };

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing removed-geometry ROI locality fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                VerifyRemovedGeometryFacesStayInsideVisualRois(
                    fixtureName,
                    originalStep,
                    report,
                    failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Removed-geometry ROI locality test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Removed-geometry ROI locality test passed.");
            return 0;
        }

        private static void VerifyRemovedGeometryFacesStayInsideVisualRois(
            string fixtureName,
            byte[] originalStep,
            StepWatermarkCleanerReport report,
            List<string> failures)
        {
            StepWatermarkVisualScanResult originalVisual = StepWatermarkVisualOracle.DetectKnownWatermarks(
                originalStep,
                fixtureName + ".original");
            if (originalVisual.Detections.Count == 0)
            {
                failures.Add(fixtureName + " original model has no visual watermark ROI detections for removed-geometry locality.");
                return;
            }

            if (string.IsNullOrWhiteSpace(report.RemovedGeometryStep))
            {
                failures.Add(fixtureName + " should export removed geometry for detected text/logo watermark cleanup.");
                return;
            }

            List<int> removedFaceIds = GetActiveAdvancedFaceIds(report.RemovedGeometryStep).ToList();
            // Some pruned diagnostics contain a valid STEP wrapper but no active faces.
            if (removedFaceIds.Count == 0)
                return;

            var removedFaceReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = removedFaceIds,
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = removedFaceIds
                    .SelectMany(faceId => StepProjectionRenderer.ViewNames.Select(viewName => new StepWatermarkRegionDetection
                    {
                        EntityId = faceId,
                        Kind = "face",
                        ViewName = viewName
                    }))
                    .ToList(),
                Diagnostics = Array.Empty<string>()
            };
            IReadOnlyList<StepProjectionDetectionRegion> removedFaceRegions = StepProjectionRenderer.ProjectDetectionRegions(
                originalStep,
                fixtureName,
                removedFaceReport,
                StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color));
            var projectedFaceIds = new HashSet<int>(removedFaceRegions.Select(region => region.EntityId));
            if (projectedFaceIds.Count == 0)
                failures.Add(fixtureName + " removed geometry active faces could not be projected onto the original model.");

            foreach (var faceGroup in removedFaceRegions.GroupBy(region => region.EntityId))
            {
                var watermarkViewRegions = faceGroup
                    .Where(removedRegion => originalVisual.Detections.Any(detection =>
                        string.Equals(detection.ViewName, removedRegion.ViewName, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (watermarkViewRegions.Count == 0)
                    continue;

                bool insideWatermark = watermarkViewRegions.Any(removedRegion =>
                    originalVisual.Detections.Any(detection =>
                        string.Equals(detection.ViewName, removedRegion.ViewName, StringComparison.OrdinalIgnoreCase) &&
                        RectangleInside(
                            removedRegion.RectangleX,
                            removedRegion.RectangleY,
                            removedRegion.RectangleWidth,
                            removedRegion.RectangleHeight,
                            detection.X,
                            detection.Y,
                            detection.Width,
                            detection.Height,
                            padding: 6)));
                if (!insideWatermark)
                {
                    string projectedRegions = string.Join(
                        "; ",
                        watermarkViewRegions.Select(region =>
                            region.ViewName +
                            " " +
                            region.RectangleX.ToString(CultureInfo.InvariantCulture) +
                            "," +
                            region.RectangleY.ToString(CultureInfo.InvariantCulture) +
                            " " +
                            region.RectangleWidth.ToString(CultureInfo.InvariantCulture) +
                            "x" +
                            region.RectangleHeight.ToString(CultureInfo.InvariantCulture)));
                    failures.Add(
                        fixtureName +
                        " removed face #" +
                        faceGroup.Key.ToString(CultureInfo.InvariantCulture) +
                        " projects outside, or not fully inside, all known watermark visual ROIs. Face projections: " +
                        projectedRegions +
                        ". Visual ROIs: " +
                        string.Join("; ", originalVisual.Detections.Select(detection => detection.Describe())) +
                        ". Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }
            }
        }

        private static bool RectangleInside(
            int innerX,
            int innerY,
            int innerWidth,
            int innerHeight,
            int outerX,
            int outerY,
            int outerWidth,
            int outerHeight,
            int padding)
        {
            if (innerWidth <= 0 || innerHeight <= 0 || outerWidth <= 0 || outerHeight <= 0)
                return false;

            int innerRight = innerX + innerWidth - 1;
            int innerBottom = innerY + innerHeight - 1;
            int outerRight = outerX + outerWidth - 1;
            int outerBottom = outerY + outerHeight - 1;
            return innerX >= outerX - padding &&
                innerY >= outerY - padding &&
                innerRight <= outerRight + padding &&
                innerBottom <= outerBottom + padding;
        }

        private static void VerifyRemovedGeometryDoesNotExportLargeNonWatermarkModels(string dataRoot, List<string> failures)
        {
            var fixtureNames = new[]
            {
                "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
                "HDMI-SMD_HDMI-001S.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "TYPE-C-TH_TYPEC-215-ARP14.step",
                "USB-A-SMD_USB-212-BCW.step"
            };

            const int maxRemovedGeometryBytes = 1700000;
            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing removed-geometry over-removal fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                if (report.DetectionReport.RemovableSolidCount != 0)
                {
                    failures.Add(
                        Path.GetFileNameWithoutExtension(fixtureName) +
                        " should not classify component solids as removable watermark solids; detected " +
                        report.DetectionReport.RemovableSolidCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int removedByteCount = string.IsNullOrEmpty(report.RemovedGeometryStep)
                    ? 0
                    : Encoding.Latin1.GetByteCount(report.RemovedGeometryStep);

                if (string.Equals(fixtureName, "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step", StringComparison.OrdinalIgnoreCase))
                {
                    AssertContains(
                        report.CleanedStep,
                        "#14214 = ADVANCED_FACE",
                        "CONN-SMD cleanup should preserve original gold contact face #14214",
                        failures);
                    AssertContains(
                        report.CleanedStep,
                        "#26383 = ADVANCED_FACE",
                        "CONN-SMD cleanup should preserve original gold contact face #26383",
                        failures);
                    AssertContains(
                        report.CleanedStep,
                        "#34754 = ADVANCED_FACE",
                        "CONN-SMD cleanup should preserve original gold contact face #34754",
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        "#14214 = ADVANCED_FACE",
                        "CONN-SMD removed geometry should not contain original gold contact face #14214",
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        "#26383 = ADVANCED_FACE",
                        "CONN-SMD removed geometry should not contain original gold contact face #26383",
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        "#34754 = ADVANCED_FACE",
                        "CONN-SMD removed geometry should not contain original gold contact face #34754",
                        failures);
                }

                if (removedByteCount > maxRemovedGeometryBytes)
                {
                    failures.Add(
                        Path.GetFileNameWithoutExtension(fixtureName) +
                        " removed-geometry export is too large for a watermark-only diagnostic: " +
                        removedByteCount.ToString(CultureInfo.InvariantCulture) +
                        " bytes.");
                }
            }
        }

        private static void VerifyTextLogoPromotedRemovedGeometryIsNotEmpty(string dataRoot, List<string> failures)
        {
            string fixtureName = "CONN-TH_MR30PW-M30-G-Y.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing text/logo removed-geometry fixture: " + inputPath);
                return;
            }

            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            if (report.FlattenedFaceCount <= 0 && report.FlattenedPointCount <= 0 && report.RemovedSolidCount <= 0)
            {
                failures.Add(fixtureName + " should perform text/logo cleanup before removed-geometry export.");
                return;
            }

            if (string.IsNullOrEmpty(report.RemovedGeometryStep))
                failures.Add(fixtureName + " should export non-empty removed geometry for promoted text/logo cleanup.");
        }

        private static int CountBrightPixels(SKBitmap image, StepProjectionDetectionRegion region, byte threshold)
        {
            return CountBrightPixels(
                image,
                region.RectangleX,
                region.RectangleY,
                region.RectangleX + region.RectangleWidth - 1,
                region.RectangleY + region.RectangleHeight - 1,
                threshold);
        }

        private static int CountBrightPixels(SKBitmap image, int rectangleLeft, int rectangleTop, int rectangleRight, int rectangleBottom, byte threshold)
        {
            int left = Math.Max(0, rectangleLeft);
            int top = Math.Max(0, rectangleTop);
            int right = Math.Min(image.Width - 1, rectangleRight);
            int bottom = Math.Min(image.Height - 1, rectangleBottom);
            int count = 0;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    SKColor color = image.GetPixel(x, y);
                    if (color.Red >= threshold && color.Green >= threshold && color.Blue >= threshold)
                        count++;
                }
            }

            return count;
        }

        private static void RunStepCleanerCleanup(string inputPath, string outputPath, List<string> failures)
        {
            string repoRoot = FindRepoRoot();
            var processStart = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            processStart.ArgumentList.Add("run");
            processStart.ArgumentList.Add("--project");
            processStart.ArgumentList.Add(Path.Combine(repoRoot, "StepCleaner", "StepCleaner.csproj"));
            processStart.ArgumentList.Add("--");
            processStart.ArgumentList.Add(inputPath);
            processStart.ArgumentList.Add(outputPath);

            using (Process process = Process.Start(processStart))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    failures.Add(
                        "Normal StepCleaner cleanup failed with exit code " +
                        process.ExitCode.ToString(CultureInfo.InvariantCulture) +
                        ". stdout=" +
                        stdout +
                        " stderr=" +
                        stderr);
                }
            }
        }

        private static int RunCleanTextTests()
        {
            var failures = new List<string>();
            var visualFailures = new List<ProjectionVisualFailure>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string cachedOriginalProjectionDirectory = Path.Combine(dataRoot, "OriginalCleanCompareProjection");
            string cleanTextDirectory = Path.Combine(dataRoot, "CleanText");
            string cleanTextProjectionDirectory = Path.Combine(dataRoot, "CleanTextProjection");
            var originalFiles = GetStepFiles(originalDirectory);
            var expectedTextCleanFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "USB-B-TH_USB-B10-BRW.step"
            };

            if (originalFiles.Count == 0)
                failures.Add("No STEP files were found in Original.");

            foreach (string originalFile in originalFiles)
            {
                byte[] originalStep = File.ReadAllBytes(originalFile);
                byte[] watermarkOnly = StepWatermarkCleaner.Clean(
                    originalStep,
                    new StepWatermarkCleanerOptions());
                var textCleanReport = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions
                    {
                        CleanText = true
                    });
                byte[] textCleaned = Encoding.Latin1.GetBytes(textCleanReport.CleanedStep);

                bool changedByTextCleaning = !BytesEqual(watermarkOnly, textCleaned);
                bool shouldChange = expectedTextCleanFileNames.Contains(Path.GetFileName(originalFile));

                if (changedByTextCleaning != shouldChange)
                {
                    failures.Add(
                        Path.GetFileName(originalFile) +
                        (shouldChange
                            ? " should be additionally cleaned by CleanText."
                            : " should not be changed by CleanText.") +
                        " Diagnostics: " +
                        string.Join(" | ", textCleanReport.Diagnostics ?? Array.Empty<string>()));
                }

                if (shouldChange && changedByTextCleaning)
                {
                    int templateTextDetectionCount = GetDiagnosticInt(textCleanReport.Diagnostics, "Template text detections");
                    int templateTextCandidateCount = GetDiagnosticInt(textCleanReport.Diagnostics, "Template text cleanup candidates");
                    int templateTextFaceCount = GetDiagnosticInt(textCleanReport.Diagnostics, "Template text faces");
                    if (templateTextDetectionCount <= 0 || templateTextCandidateCount <= 0)
                    {
                        failures.Add(
                            Path.GetFileName(originalFile) +
                            " CleanText should report template-backed detections and cleanup candidates, got detections=" +
                            templateTextDetectionCount.ToString(CultureInfo.InvariantCulture) +
                            ", candidates=" +
                            templateTextCandidateCount.ToString(CultureInfo.InvariantCulture) +
                            ". Diagnostics: " +
                            string.Join(" | ", textCleanReport.Diagnostics ?? Array.Empty<string>()));
                    }

                    if (templateTextFaceCount <= 0)
                    {
                        failures.Add(
                            Path.GetFileName(originalFile) +
                            " CleanText should report accepted template-backed text faces, got " +
                            templateTextFaceCount.ToString(CultureInfo.InvariantCulture) +
                            ". Diagnostics: " +
                            string.Join(" | ", textCleanReport.Diagnostics ?? Array.Empty<string>()));
                    }

                    Directory.CreateDirectory(cleanTextDirectory);
                    string textCleanOutputPath = Path.Combine(cleanTextDirectory, Path.GetFileName(originalFile));
                    File.WriteAllBytes(textCleanOutputPath, textCleaned);
                    VerifyCleanTextPostProcessProjectionUsesCachedOriginal(
                        originalFile,
                        textCleanOutputPath,
                        textCleanReport.DetectionReport,
                        cachedOriginalProjectionDirectory,
                        cleanTextProjectionDirectory,
                        failures,
                        visualFailures);
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Clean text regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Clean text regression test passed.");
            return 0;
        }

        private static int GetDiagnosticInt(IReadOnlyList<string> diagnostics, string label)
        {
            if (diagnostics == null)
                return 0;

            string prefix = label + ":";
            foreach (string diagnostic in diagnostics)
            {
                if (string.IsNullOrWhiteSpace(diagnostic) ||
                    !diagnostic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = diagnostic.Substring(prefix.Length).Trim();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                    return result;
            }

            return 0;
        }

        private static int RunModelImportMeasurement(string[] args)
        {
            string partNumber = DefaultMeasurePartNumber;
            int repeatCount = 2;
            bool cleanText = false;

            int index = 1;
            if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                partNumber = args[index];
                index++;
            }

            for (; index < args.Length; index++)
            {
                string option = args[index];
                if (IsOption(option, "--repeat"))
                {
                    if (!TryReadIntOption(args, ref index, option, out repeatCount))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--clean-text"))
                {
                    cleanText = true;
                    continue;
                }

                Console.Error.WriteLine("Unknown measurement option: " + option);
                return 2;
            }

            if (string.IsNullOrWhiteSpace(partNumber))
            {
                Console.Error.WriteLine("Part number is required.");
                return 2;
            }

            if (repeatCount < 1)
            {
                Console.Error.WriteLine("--repeat must be at least 1.");
                return 2;
            }

            try
            {
                return RunModelImportMeasurementAsync(partNumber, repeatCount, cleanText)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Model import measurement failed: " + ex.Message);
                return 1;
            }
        }

        private static int RunF3DBufferSmoke(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-buffer-smoke <input.step>");
                return 1;
            }

            string inputPath = args[1];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                var options = new StepProjectionOptions
                {
                    ImageSizePixels = 256,
                    PaddingPixels = 16,
                    WriteMetadata = false,
                    SkipGeometryModelForExternalRender = true
                };
                options.ViewNames.Add("x_plus");

                IReadOnlyList<StepProjectionImage> images = StepProjectionRenderer.ProjectFileImages(
                    File.ReadAllBytes(inputPath),
                    Path.GetFileNameWithoutExtension(inputPath),
                    options);
                if (images.Count != 1)
                {
                    Console.Error.WriteLine("Expected one rendered image, got " + images.Count.ToString(CultureInfo.InvariantCulture) + ".");
                    return 3;
                }

                StepProjectionImage image = images[0];
                Console.WriteLine("f3d_buffer_smoke=PASS");
                Console.WriteLine("view=" + image.ViewName);
                Console.WriteLine("size=" + image.Width.ToString(CultureInfo.InvariantCulture) + "x" + image.Height.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("raw_rgba_bytes=" + image.RgbaBytes.Length.ToString(CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("F3D buffer smoke failed: " + ex.Message);
                return 4;
            }
        }

        private static int RunF3DPreviewSmoke(string[] args)
        {
            if (args.Length < 2 || args.Length > 4)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-preview-smoke <input.step> [output.png] [--cross-thread]");
                return 1;
            }

            string inputPath = args[1];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                byte[] stepData = File.ReadAllBytes(inputPath);
                bool crossThread = args.Any(arg => IsOption(arg, "--cross-thread"));
                string outputPath = args
                    .Skip(2)
                    .FirstOrDefault(arg => !IsOption(arg, "--cross-thread"));
                F3DRenderedImage image = crossThread
                    ? RenderPreviewSmokeCrossThread(stepData)
                    : RenderPreviewSmokeSameThread(stepData);
                if (image == null)
                {
                    Console.Error.WriteLine("Preview render did not return an image.");
                    return 1;
                }

                byte[] rgba = ConvertRawF3DImageToRgba(image);
                int nonWhitePixels = CountNonWhitePixels(rgba);
                Console.WriteLine("f3d_preview_smoke=" + (nonWhitePixels > 0 ? "PASS" : "FAIL"));
                Console.WriteLine("cross_thread=" + crossThread.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("size=" + image.Width.ToString(CultureInfo.InvariantCulture) + "x" + image.Height.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("channels=" + image.ChannelCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("channel_type=" + image.ChannelType.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("channel_type_size=" + image.ChannelTypeSize.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("non_white_pixels=" + nonWhitePixels.ToString(CultureInfo.InvariantCulture));

                if (!string.IsNullOrWhiteSpace(outputPath))
                    SaveRgbaPng(rgba, image.Width, image.Height, outputPath);

                return nonWhitePixels > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("F3D preview smoke failed: " + ex.Message);
                return 1;
            }
        }

        private static int RunProjectionEdgeModeTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] stepData = File.ReadAllBytes(inputPath);

            var colorOptions = new StepProjectionOptions
            {
                ImageSizePixels = 512,
                PaddingPixels = 32,
                RenderMode = StepProjectionRenderMode.Color
            };
            colorOptions.ViewNames.Add("z_minus");

            var edgeOptions = new StepProjectionOptions
            {
                ImageSizePixels = colorOptions.ImageSizePixels,
                PaddingPixels = colorOptions.PaddingPixels,
                RenderMode = StepProjectionRenderMode.Edge
            };
            edgeOptions.ViewNames.Add("z_minus");

            StepProjectionImage colorImage = StepProjectionRenderer.ProjectFileImages(
                stepData,
                Path.GetFileNameWithoutExtension(inputPath),
                colorOptions)[0];
            StepProjectionImage edgeImage = StepProjectionRenderer.ProjectFileImages(
                stepData,
                Path.GetFileNameWithoutExtension(inputPath),
                edgeOptions)[0];

            if (colorImage.Width != edgeImage.Width || colorImage.Height != edgeImage.Height)
            {
                failures.Add(
                    "Edge projection should use the same dimensions as color projection: color=" +
                    colorImage.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    colorImage.Height.ToString(CultureInfo.InvariantCulture) +
                    ", edge=" +
                    edgeImage.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    edgeImage.Height.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            const int minDarkPixels = 32;
            int darkPixels = CountDarkPixels(edgeImage.RgbaBytes, threshold: 48);
            if (darkPixels < minDarkPixels)
            {
                failures.Add(
                    "Edge projection should contain dark edge pixels in the z_minus view: expected at least " +
                    minDarkPixels.ToString(CultureInfo.InvariantCulture) +
                    ", got " +
                    darkPixels.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Projection edge-mode test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Projection edge-mode test passed.");
            Console.WriteLine("edge_dark_pixels=" + darkPixels.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunRawSilhouetteEdgeProjectionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            string silhouetteProjection = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepSilhouetteProjection.cs"));
            if (!projectionRenderer.Contains("StepSilhouetteProjection.GenerateViews", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer edge mode should use optimized OCCT silhouette views.");
            if (projectionRenderer.Contains("StepSilhouetteProjection.GenerateRawViews", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer edge mode must not use raw OCCT silhouette views.");
            if (!silhouetteProjection.Contains("GenerateViews(", StringComparison.Ordinal))
                failures.Add("StepSilhouetteProjection should expose optimized byte-array view generation.");

            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] stepData = File.ReadAllBytes(inputPath);
            StepProjectionImage edgeImage = ProjectSingleTestView(
                stepData,
                "CONN-TH_XT60PB-M.optimized-edge",
                "z_minus",
                StepProjectionRenderMode.Edge);

            int darkPixels = CountDarkPixels(edgeImage.RgbaBytes, 48);
            if (darkPixels < 1000)
                failures.Add("Optimized silhouette edge image should contain visible HLR edge pixels, got " + darkPixels.ToString(CultureInfo.InvariantCulture) + ".");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Optimized silhouette edge projection test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Optimized silhouette edge projection test passed.");
            Console.WriteLine("edge_dark_pixels=" + darkPixels.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunVisibleRawSilhouetteEdgeProjectionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            if (!projectionRenderer.Contains("StepProjectionRenderMode.EdgeVisibleRaw", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer should expose the EdgeVisibleRaw render mode.");
            if (!projectionRenderer.Contains("ProjectFileVectorProjectionImages", StringComparison.Ordinal))
                failures.Add("EdgeVisibleRaw projection should route file-image generation through vector OCCT HLR primitives.");
            if (projectionRenderer.Contains("image = RenderVisibleRawEdgeProjectionImage(drawingModel", StringComparison.Ordinal))
                failures.Add("ProjectFileImages must not route EdgeVisibleRaw through the STEP parser depth-tested raw renderer.");
            if (projectionRenderer.Contains("var edgeImage = RenderVisibleRawEdgeProjectionImage(model", StringComparison.Ordinal))
                failures.Add("RenderProjection must not route EdgeVisibleRaw through the STEP parser depth-tested raw renderer.");
            if (projectionRenderer.Contains("private static RgbaImage RenderVisibleRawEdgeProjectionImage", StringComparison.Ordinal))
                failures.Add("The legacy visible-raw depth-tested renderer should be removed or moved out of the active renderer.");

            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "USB-B-TH_USB-B10-BRW.step");
            byte[] stepData = File.ReadAllBytes(inputPath);
            StepProjectionImage edgeImage = ProjectSingleTestView(
                stepData,
                "USB-B-TH_USB-B10-BRW.visible-raw-edge",
                "x_plus",
                StepProjectionRenderMode.EdgeVisibleRaw);

            int darkPixels = CountDarkPixels(edgeImage.RgbaBytes, 48);
            if (darkPixels < 500)
            {
                failures.Add(
                    "Visible raw vector edge image should render nonblank linework, got " +
                    darkPixels.ToString(CultureInfo.InvariantCulture) +
                    " dark pixels.");
            }
            ValidateVisibleRawUsbXPlusBounds(
                "ProjectFileImages EdgeVisibleRaw",
                GetDarkPixelBounds(edgeImage, 128),
                failures);

            string projectOutputDirectory = Path.Combine(repoRoot, ".codex-temp", "visible-raw-project-file");
            Directory.CreateDirectory(projectOutputDirectory);
            foreach (string oldOutput in Directory.GetFiles(projectOutputDirectory, "USB-B-TH_USB-B10-BRW__x_plus.*"))
                File.Delete(oldOutput);

            var projectOptions = new StepProjectionOptions
            {
                RenderMode = StepProjectionRenderMode.EdgeVisibleRaw,
                ImageSizePixels = 1600
            };
            projectOptions.ViewNames.Add("x_plus");

            StepProjectionReport report = StepProjectionRenderer.ProjectFile(inputPath, projectOutputDirectory, projectOptions);
            string projectPngPath = Directory
                .GetFiles(projectOutputDirectory, "USB-B-TH_USB-B10-BRW__x_plus*.png")
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(projectPngPath) || new FileInfo(projectPngPath).Length == 0)
                failures.Add("ProjectFile should write an EdgeVisibleRaw vector PNG through RenderProjection.");
            if (!string.IsNullOrWhiteSpace(projectPngPath) &&
                (report.OutputFiles == null || !report.OutputFiles.Any(path => string.Equals(path, projectPngPath, StringComparison.OrdinalIgnoreCase))))
            {
                failures.Add("ProjectFile report should include the EdgeVisibleRaw vector PNG output.");
            }
            if (!string.IsNullOrWhiteSpace(projectPngPath) && File.Exists(projectPngPath))
            {
                using (SKBitmap projectBitmap = SKBitmap.Decode(projectPngPath))
                {
                    ValidateVisibleRawUsbXPlusBounds(
                        "ProjectFile EdgeVisibleRaw",
                        GetDarkPixelBounds(projectBitmap, 128),
                        failures);
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Visible raw silhouette edge projection test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Visible raw silhouette edge projection test passed.");
            Console.WriteLine("dark_pixels=" + darkPixels.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunVectorProjectionContractTests(string[] args)
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string dataRoot = FindDataRoot();
            string outputRoot = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.Combine(repoRoot, ".codex-temp", "vector-projection-debug");

            Directory.CreateDirectory(outputRoot);

            ValidateReadableViewMirrorPolicy(repoRoot, failures);

            string[] validationFileNames = new[]
            {
                "CONN-TH_MR30PW-M30-G-Y.step",
                "HDMI-SMD_HDMI-001S.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "USB-B-TH_USB-B10-BRW.step"
            };
            foreach (string requiredFileName in new[]
            {
                "CONN-TH_MR30PW-M30-G-Y.step",
                "HDMI-SMD_HDMI-001S.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "USB-B-TH_USB-B10-BRW.step"
            })
            {
                if (!validationFileNames.Contains(requiredFileName, StringComparer.OrdinalIgnoreCase))
                    failures.Add("Vector projection validation set should include " + requiredFileName + ".");
            }

            foreach (string fileName in validationFileNames)
            {
                string inputPath = Path.Combine(dataRoot, "Original", fileName);
                string modelName = Path.GetFileNameWithoutExtension(fileName);
                string modelOutputDirectory = Path.Combine(outputRoot, modelName);
                string svgDirectory = Path.Combine(modelOutputDirectory, "svg");
                string pngDirectory = Path.Combine(modelOutputDirectory, "png");
                string jsonPath = Path.Combine(modelOutputDirectory, modelName + ".vector.json");
                Directory.CreateDirectory(modelOutputDirectory);
                Directory.CreateDirectory(svgDirectory);
                Directory.CreateDirectory(pngDirectory);

                RunStepOcctVectorProjection(repoRoot, inputPath, jsonPath, svgDirectory, failures);
                if (!File.Exists(jsonPath))
                {
                    failures.Add("Vector projection JSON was not written: " + jsonPath);
                    continue;
                }

                VectorProjectionResultDto result;
                try
                {
                    result = JsonSerializer.Deserialize<VectorProjectionResultDto>(File.ReadAllText(jsonPath));
                }
                catch (Exception ex)
                {
                    failures.Add("Vector projection JSON could not be parsed for " + fileName + ": " + ex.Message);
                    continue;
                }

                ValidateVectorProjectionResult(fileName, result, failures);
                if (result == null || result.Views == null)
                    continue;

                ValidateMarkedRegionVectorAlignment(fileName, dataRoot, result, failures);

                foreach (VectorProjectionViewDto view in result.Views)
                {
                    if (view == null || string.IsNullOrWhiteSpace(view.Name))
                        continue;

                    string pngPath = Path.Combine(pngDirectory, view.Name + ".png");
                    try
                    {
                        SaveVectorProjectionPng(view, pngPath, modelName + " " + view.Name);
                    }
                    catch (Exception ex)
                    {
                        failures.Add("Vector debug PNG failed for " + modelName + " " + view.Name + ": " + ex.Message);
                    }
                }

                ValidateVectorDebugSvg(modelName, svgDirectory, failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector projection contract test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                Console.Error.WriteLine("Debug output: " + outputRoot);
                return 1;
            }

            Console.WriteLine("Vector projection contract test passed.");
            Console.WriteLine("Debug output: " + outputRoot);
            return 0;
        }

        private static void ValidateReadableViewMirrorPolicy(string repoRoot, List<string> failures)
        {
            string programPath = Path.Combine(repoRoot, "StepOcctHlr", "Program.cs");
            string programText = File.ReadAllText(programPath);
            foreach (string viewName in new[] { "x_plus", "x_minus", "y_plus", "y_minus", "z_plus", "z_minus" })
            {
                int viewIndex = programText.IndexOf("\"" + viewName + "\"", StringComparison.OrdinalIgnoreCase);
                if (viewIndex < 0)
                {
                    failures.Add("StepOcctHlr named view policy should define " + viewName + ".");
                    continue;
                }

                int nextViewIndex = programText.IndexOf("else if", viewIndex + viewName.Length, StringComparison.Ordinal);
                if (nextViewIndex < 0)
                    nextViewIndex = programText.IndexOf("else", viewIndex + viewName.Length, StringComparison.Ordinal);
                if (nextViewIndex < 0)
                    nextViewIndex = programText.Length;

                string viewBlock = programText.Substring(viewIndex, nextViewIndex - viewIndex);
                if (viewBlock.IndexOf("options.MirrorX = true", StringComparison.Ordinal) < 0)
                {
                    failures.Add(
                        "StepOcctHlr " +
                        viewName +
                        " should apply the readable vector projection MirrorX policy.");
                }
            }
        }

        private static void ValidateMarkedRegionVectorAlignment(
            string fileName,
            string dataRoot,
            VectorProjectionResultDto result,
            List<string> failures)
        {
            string modelName = Path.GetFileNameWithoutExtension(fileName);
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            if (!Directory.Exists(markedDirectory))
                return;

            foreach (string markerPath in Directory.GetFiles(markedDirectory, modelName + "__*.json"))
            {
                string markerName = Path.GetFileNameWithoutExtension(markerPath);
                string prefix = modelName + "__";
                if (!markerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string viewName = markerName.Substring(prefix.Length);
                string oppositeViewName = OppositeVectorViewName(viewName);
                if (string.IsNullOrEmpty(oppositeViewName))
                    continue;

                VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, viewName, StringComparison.OrdinalIgnoreCase));
                VectorProjectionViewDto oppositeView = result.Views.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, oppositeViewName, StringComparison.OrdinalIgnoreCase));
                if (view == null || oppositeView == null)
                    continue;

                MarkedRegionFile marker;
                try
                {
                    marker = JsonSerializer.Deserialize<MarkedRegionFile>(File.ReadAllText(markerPath));
                }
                catch (Exception ex)
                {
                    failures.Add("Marked region JSON could not be parsed for " + markerName + ": " + ex.Message);
                    continue;
                }

                if (marker == null || marker.Rectangles == null || marker.Rectangles.Count == 0)
                    continue;

                int sameViewSamples = CountVectorSamplesInMarkedRegions(view, marker);
                int oppositeViewSamples = CountVectorSamplesInMarkedRegions(oppositeView, marker);
                if (sameViewSamples <= 0)
                {
                    failures.Add(
                        modelName +
                        " " +
                        viewName +
                        " vector content should align with its marked region, got same-view samples=" +
                        sameViewSamples.ToString(CultureInfo.InvariantCulture) +
                        " (" +
                        oppositeViewName +
                        " samples=" +
                        oppositeViewSamples.ToString(CultureInfo.InvariantCulture) +
                        ").");
                }
            }
        }

        private static string OppositeVectorViewName(string viewName)
        {
            if (string.Equals(viewName, "x_plus", StringComparison.OrdinalIgnoreCase))
                return "x_minus";
            if (string.Equals(viewName, "x_minus", StringComparison.OrdinalIgnoreCase))
                return "x_plus";
            if (string.Equals(viewName, "y_plus", StringComparison.OrdinalIgnoreCase))
                return "y_minus";
            if (string.Equals(viewName, "y_minus", StringComparison.OrdinalIgnoreCase))
                return "y_plus";
            if (string.Equals(viewName, "z_plus", StringComparison.OrdinalIgnoreCase))
                return "z_minus";
            if (string.Equals(viewName, "z_minus", StringComparison.OrdinalIgnoreCase))
                return "z_plus";
            return null;
        }

        private static int CountVectorSamplesInMarkedRegions(
            VectorProjectionViewDto view,
            MarkedRegionFile marker)
        {
            const int imageSize = 1000;
            const int padding = 60;
            VectorProjectionBoundsDto bounds = NormalizeVectorBounds(view.Bounds);
            var rectangles = new List<MarkedRectI>();
            int markerWidth = marker.ImageWidth > 0 ? marker.ImageWidth : 1600;
            int markerHeight = marker.ImageHeight > 0 ? marker.ImageHeight : 1600;
            foreach (MarkedRectangle rectangle in marker.Rectangles)
            {
                rectangles.Add(new MarkedRectI(
                    ScaleMarkedCoordinate(rectangle.X, markerWidth, imageSize),
                    ScaleMarkedCoordinate(rectangle.Y, markerHeight, imageSize),
                    ScaleMarkedCoordinate(rectangle.Width, markerWidth, imageSize),
                    ScaleMarkedCoordinate(rectangle.Height, markerHeight, imageSize)));
            }

            int count = 0;
            foreach (VectorProjectionPrimitiveDto primitive in view.Primitives ?? new List<VectorProjectionPrimitiveDto>())
                count += CountPrimitiveSamplesInMarkedRegions(primitive, bounds, rectangles, imageSize, padding);

            return count;
        }

        private static int CountPrimitiveSamplesInMarkedRegions(
            VectorProjectionPrimitiveDto primitive,
            VectorProjectionBoundsDto bounds,
            List<MarkedRectI> rectangles,
            int imageSize,
            int padding)
        {
            if (primitive == null || rectangles.Count == 0)
                return 0;
            if (!IsMarkedRegionVectorPrimitive(primitive))
                return 0;

            if (string.Equals(primitive.Kind, "line", StringComparison.OrdinalIgnoreCase) &&
                primitive.Points != null &&
                primitive.Points.Length >= 4)
            {
                return CountSegmentSamplesInMarkedRegions(
                    VectorImageX(primitive.Points[0], bounds, imageSize, padding),
                    VectorImageY(primitive.Points[1], bounds, imageSize, padding),
                    VectorImageX(primitive.Points[2], bounds, imageSize, padding),
                    VectorImageY(primitive.Points[3], bounds, imageSize, padding),
                    rectangles);
            }

            if (string.Equals(primitive.Kind, "polyline", StringComparison.OrdinalIgnoreCase) &&
                primitive.Points != null &&
                primitive.Points.Length >= 4)
            {
                int count = 0;
                for (int index = 0; index + 3 < primitive.Points.Length; index += 2)
                {
                    count += CountSegmentSamplesInMarkedRegions(
                        VectorImageX(primitive.Points[index], bounds, imageSize, padding),
                        VectorImageY(primitive.Points[index + 1], bounds, imageSize, padding),
                        VectorImageX(primitive.Points[index + 2], bounds, imageSize, padding),
                        VectorImageY(primitive.Points[index + 3], bounds, imageSize, padding),
                        rectangles);
                }

                return count;
            }

            if (string.Equals(primitive.Kind, "arc", StringComparison.OrdinalIgnoreCase))
                return CountArcSamplesInMarkedRegions(primitive, bounds, rectangles, imageSize, padding);

            return 0;
        }

        private static bool IsMarkedRegionVectorPrimitive(VectorProjectionPrimitiveDto primitive)
        {
            return primitive != null &&
                string.Equals(primitive.Kind, "polyline", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(primitive.OriginalKind) &&
                primitive.OriginalKind.IndexOf("bspline", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CountArcSamplesInMarkedRegions(
            VectorProjectionPrimitiveDto primitive,
            VectorProjectionBoundsDto bounds,
            List<MarkedRectI> rectangles,
            int imageSize,
            int padding)
        {
            double sweep = primitive.EndAngle - primitive.StartAngle;
            while (sweep < 0.0)
                sweep += 360.0;
            if (sweep <= 0.0)
                sweep = 360.0;

            int count = 0;
            int steps = Math.Max(8, (int)Math.Ceiling(sweep / 3.0));
            for (int i = 0; i <= steps; i++)
            {
                double angle = (primitive.StartAngle + sweep * i / steps) * Math.PI / 180.0;
                double x = primitive.CenterX + Math.Cos(angle) * primitive.Radius;
                double y = primitive.CenterY + Math.Sin(angle) * primitive.Radius;
                if (IsPointInMarkedRegions(
                    VectorImageX(x, bounds, imageSize, padding),
                    VectorImageY(y, bounds, imageSize, padding),
                    rectangles))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSegmentSamplesInMarkedRegions(
            float x1,
            float y1,
            float x2,
            float y2,
            List<MarkedRectI> rectangles)
        {
            double length = Math.Sqrt((x2 - x1) * (double)(x2 - x1) + (y2 - y1) * (double)(y2 - y1));
            int steps = Math.Max(1, (int)Math.Ceiling(length / 4.0));
            int count = 0;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                float x = (float)(x1 + (x2 - x1) * t);
                float y = (float)(y1 + (y2 - y1) * t);
                if (IsPointInMarkedRegions(x, y, rectangles))
                    count++;
            }

            return count;
        }

        private static bool IsPointInMarkedRegions(float x, float y, List<MarkedRectI> rectangles)
        {
            foreach (MarkedRectI rectangle in rectangles)
            {
                if (x >= rectangle.X &&
                    x <= rectangle.X + rectangle.Width &&
                    y >= rectangle.Y &&
                    y <= rectangle.Y + rectangle.Height)
                {
                    return true;
                }
            }

            return false;
        }

        private static int ScaleMarkedCoordinate(int value, int sourceSize, int targetSize)
        {
            return (int)Math.Round(value * (double)targetSize / Math.Max(1, sourceSize));
        }

        private static void RunStepOcctVectorProjection(
            string repoRoot,
            string inputPath,
            string jsonPath,
            string svgDirectory,
            List<string> failures)
        {
            var processStart = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            processStart.ArgumentList.Add("run");
            processStart.ArgumentList.Add("--project");
            processStart.ArgumentList.Add(Path.Combine(repoRoot, "StepOcctHlr", "StepOcctHlr.csproj"));
            processStart.ArgumentList.Add("--");
            processStart.ArgumentList.Add(inputPath);
            processStart.ArgumentList.Add(jsonPath);
            processStart.ArgumentList.Add("--vector-views");
            processStart.ArgumentList.Add("x_plus,x_minus,y_plus,y_minus,z_plus,z_minus");
            processStart.ArgumentList.Add("--vector-svg-dir");
            processStart.ArgumentList.Add(svgDirectory);

            using (Process process = Process.Start(processStart))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    failures.Add(
                        "StepOcctHlr vector projection failed for " +
                        Path.GetFileName(inputPath) +
                        " with exit code " +
                        process.ExitCode.ToString(CultureInfo.InvariantCulture) +
                        ". stdout=" +
                        stdout +
                        " stderr=" +
                        stderr);
                }
            }
        }

        private static void ValidateVectorProjectionResult(
            string fileName,
            VectorProjectionResultDto result,
            List<string> failures)
        {
            if (result == null)
            {
                failures.Add(fileName + " vector result should not be null.");
                return;
            }

            if (!result.Success)
                failures.Add(fileName + " vector result should succeed: " + result.Error);

            AssertEqual(
                "occt-hlr-vector-managed",
                result.Engine,
                fileName + " vector result should identify the managed OCCT HLR vector engine",
                failures);

            if (result.Views == null)
            {
                failures.Add(fileName + " vector result should include view results.");
                return;
            }

            if (result.Views.Count != 6)
            {
                failures.Add(
                    fileName +
                    " vector result should include six views, got " +
                    result.Views.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            foreach (VectorProjectionViewDto view in result.Views)
            {
                if (view == null)
                {
                    failures.Add(fileName + " vector result contains a null view.");
                    continue;
                }

                if (!view.Success)
                    failures.Add(fileName + " " + view.Name + " should succeed: " + view.Error);
                if (view.Primitives == null || view.Primitives.Count == 0)
                {
                    failures.Add(fileName + " " + view.Name + " should contain vector primitives.");
                    continue;
                }

                foreach (VectorProjectionPrimitiveDto primitive in view.Primitives)
                {
                    if (primitive == null)
                    {
                        failures.Add(fileName + " " + view.Name + " contains a null primitive.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(primitive.Kind))
                        failures.Add(fileName + " " + view.Name + " primitive is missing Kind.");
                    if (string.IsNullOrWhiteSpace(primitive.Visibility))
                        failures.Add(fileName + " " + view.Name + " primitive is missing Visibility.");
                    if (string.IsNullOrWhiteSpace(primitive.Category))
                        failures.Add(fileName + " " + view.Name + " primitive is missing Category.");
                }
            }

            VectorProjectionViewDto connYPlus = result.Views.FirstOrDefault(view =>
                string.Equals(view.Name, "y_plus", StringComparison.OrdinalIgnoreCase));
            if (fileName.StartsWith("CONN-TH_MR30PW-M30-G-Y", StringComparison.OrdinalIgnoreCase) &&
                connYPlus != null &&
                (connYPlus.Bounds == null || connYPlus.Bounds.Width < 12.0 || connYPlus.Bounds.Height < 4.0))
            {
                failures.Add(
                    "CONN y_plus vector bounds should not collapse; got " +
                    FormatVectorBounds(connYPlus.Bounds) +
                    ".");
            }

            if (fileName.StartsWith("CONN-TH_MR30PW-M30-G-Y", StringComparison.OrdinalIgnoreCase) &&
                connYPlus != null &&
                connYPlus.Bounds != null &&
                (connYPlus.Bounds.Width > 20.0 || connYPlus.Bounds.Height > 12.0))
            {
                failures.Add(
                    "CONN y_plus vector bounds should exclude detached off-body strokes; got " +
                    FormatVectorBounds(connYPlus.Bounds) +
                    ".");
            }

            VectorProjectionViewDto connZMinus = result.Views.FirstOrDefault(view =>
                string.Equals(view.Name, "z_minus", StringComparison.OrdinalIgnoreCase));
            if (fileName.StartsWith("CONN-TH_MR30PW-M30-G-Y", StringComparison.OrdinalIgnoreCase) &&
                connZMinus != null &&
                connZMinus.Bounds != null &&
                (connZMinus.Bounds.Width > 20.0 || connZMinus.Bounds.Height > 18.0))
            {
                failures.Add(
                    "CONN z_minus vector bounds should exclude detached off-body strokes; got " +
                    FormatVectorBounds(connZMinus.Bounds) +
                    ".");
            }

            VectorProjectionViewDto hdmiYPlus = result.Views.FirstOrDefault(view =>
                string.Equals(view.Name, "y_plus", StringComparison.OrdinalIgnoreCase));
            if (fileName.StartsWith("HDMI-SMD_HDMI-001S", StringComparison.OrdinalIgnoreCase) &&
                hdmiYPlus != null &&
                (hdmiYPlus.Primitives == null || hdmiYPlus.Primitives.Count < 40))
            {
                failures.Add(
                    "HDMI y_plus should include logo-bearing vector content, got " +
                    (hdmiYPlus.Primitives == null ? 0 : hdmiYPlus.Primitives.Count).ToString(CultureInfo.InvariantCulture) +
                    " primitives.");
            }

            if (fileName.StartsWith("HDMI-SMD_HDMI-001S", StringComparison.OrdinalIgnoreCase) &&
                hdmiYPlus != null &&
                hdmiYPlus.Primitives != null)
            {
                int explicitCurveFallbackCount = 0;
                int shortLineFragmentCount = 0;
                foreach (VectorProjectionPrimitiveDto primitive in hdmiYPlus.Primitives)
                {
                    if (string.Equals(primitive.Kind, "polyline", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(primitive.OriginalKind) &&
                        primitive.Tolerance > 0.0)
                    {
                        explicitCurveFallbackCount++;
                    }

                    if (IsVectorLine(primitive))
                    {
                        double fragmentLength = Distance2d(
                            primitive.Points[0],
                            primitive.Points[1],
                            primitive.Points[2],
                            primitive.Points[3]);
                        if (fragmentLength < 0.2)
                            shortLineFragmentCount++;
                    }

                    if (!IsVectorLine(primitive) ||
                        !string.Equals(primitive.Category, "outline", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    double length = Distance2d(
                        primitive.Points[0],
                        primitive.Points[1],
                        primitive.Points[2],
                        primitive.Points[3]);
                    if (length > 1.2)
                    {
                        failures.Add(
                            "HDMI y_plus should not contain long diagonal outline parser artifacts, got source " +
                            primitive.SourceIndex.ToString(CultureInfo.InvariantCulture) +
                            " length " +
                            length.ToString("R", CultureInfo.InvariantCulture) +
                            " points " +
                            FormatVectorPoints(primitive.Points) +
                            ".");
                    }
                }

                if (explicitCurveFallbackCount < 20)
                {
                    failures.Add(
                        "HDMI y_plus should preserve logo/curve geometry as explicit curve fallback primitives, got " +
                        explicitCurveFallbackCount.ToString(CultureInfo.InvariantCulture) +
                        " polyline fallback primitives.");
                }

                if (shortLineFragmentCount >= 300)
                {
                    failures.Add(
                        "HDMI y_plus should not be dominated by tiny endpoint line fragments, got " +
                        shortLineFragmentCount.ToString(CultureInfo.InvariantCulture) +
                        " line fragments shorter than 0.2 mm.");
                }
            }

            if (fileName.StartsWith("SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30", StringComparison.OrdinalIgnoreCase))
            {
                AssertVectorViewBounds(result, fileName, "z_plus", 5.5, 8.5, 5.5, 8.5, 12, failures);
                AssertVectorViewBounds(result, fileName, "z_minus", 5.5, 8.5, 5.5, 8.5, 12, failures);
                AssertSplineFallbacksAreSampled(result, fileName, "z_minus", failures);
                AssertSot223ZMinusMarkingIsNotMirrored(result, fileName, failures);
                AssertSot223ZPlusTextMarkingIsNotMirrored(result, fileName, failures);
            }

            if (fileName.StartsWith("USB-B-TH_USB-B10-BRW", StringComparison.OrdinalIgnoreCase))
            {
                AssertVectorViewBounds(result, fileName, "x_plus", 10.0, 22.0, 10.0, 22.0, 40, failures);
                AssertVectorViewBounds(result, fileName, "z_minus", 10.0, 22.0, 10.0, 22.0, 40, failures);
                AssertSplineFallbacksAreSampled(result, fileName, "x_plus", failures);
                AssertUsbB10ZMinusMarkingIsNotMirrored(result, fileName, failures);
                AssertUsbB10XPlusMarkingIsNotMirrored(result, fileName, failures);
            }
        }

        private static void AssertUsbB10ZMinusMarkingIsNotMirrored(
            VectorProjectionResultDto result,
            string fileName,
            List<string> failures)
        {
            VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "z_minus", StringComparison.OrdinalIgnoreCase));
            if (view == null || view.Primitives == null)
                return;

            double centroidX = CentroidXOfPolylineFallbacks(
                view,
                centerXMin: double.NegativeInfinity,
                centerXMax: double.PositiveInfinity,
                centerYMin: -4.8,
                centerYMax: -3.8,
                maxPrimitiveSpan: 1.2,
                out int sampleCount);

            if (sampleCount < 8 || centroidX >= -0.5)
            {
                failures.Add(
                    fileName +
                    " z_minus LCEDA vector fallback cluster should not be mirrored horizontally; centroidX=" +
                    centroidX.ToString("R", CultureInfo.InvariantCulture) +
                    ", sampleCount=" +
                    sampleCount.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static void AssertUsbB10XPlusMarkingIsNotMirrored(
            VectorProjectionResultDto result,
            string fileName,
            List<string> failures)
        {
            VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "x_plus", StringComparison.OrdinalIgnoreCase));
            if (view == null || view.Primitives == null)
                return;

            double centroidX = CentroidXOfPolylineFallbacks(
                view,
                centerXMin: double.NegativeInfinity,
                centerXMax: double.PositiveInfinity,
                centerYMin: -3.1,
                centerYMax: -1.3,
                maxPrimitiveSpan: 1.2,
                out int sampleCount);

            if (sampleCount < 20 || centroidX >= -0.5)
            {
                failures.Add(
                    fileName +
                    " x_plus EasyEDA/logo vector fallback cluster should not be mirrored horizontally; centroidX=" +
                    centroidX.ToString("R", CultureInfo.InvariantCulture) +
                    ", sampleCount=" +
                    sampleCount.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static void AssertSot223ZMinusMarkingIsNotMirrored(
            VectorProjectionResultDto result,
            string fileName,
            List<string> failures)
        {
            VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "z_minus", StringComparison.OrdinalIgnoreCase));
            if (view == null || view.Primitives == null)
                return;

            double pointSkewX = PointSkewXOfPolylineFallbacks(
                view,
                centerXMin: -0.4,
                centerXMax: 0.4,
                centerYMin: -0.7,
                centerYMax: 0.8,
                maxPrimitiveSpan: 0.3,
                out int sampleCount);

            if (sampleCount < 200 || pointSkewX <= 0.00005)
            {
                failures.Add(
                    fileName +
                    " z_minus LCEDA vector fallback cluster should not be mirrored horizontally; pointSkewX=" +
                    pointSkewX.ToString("R", CultureInfo.InvariantCulture) +
                    ", sampleCount=" +
                    sampleCount.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static void AssertSot223ZPlusTextMarkingIsNotMirrored(
            VectorProjectionResultDto result,
            string fileName,
            List<string> failures)
        {
            VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "z_plus", StringComparison.OrdinalIgnoreCase));
            if (view == null || view.Primitives == null)
                return;

            double centroidX = CentroidXOfPolylineFallbacks(
                view,
                centerXMin: -1.5,
                centerXMax: 1.5,
                centerYMin: -2.9,
                centerYMax: -1.5,
                maxPrimitiveSpan: 0.25,
                out int sampleCount);

            if (sampleCount < 40 || centroidX <= 0.5)
            {
                failures.Add(
                    fileName +
                    " z_plus SOT/text vector fallback cluster should not be mirrored horizontally; centroidX=" +
                    centroidX.ToString("R", CultureInfo.InvariantCulture) +
                    ", sampleCount=" +
                    sampleCount.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static double CentroidXOfPolylineFallbacks(
            VectorProjectionViewDto view,
            double centerXMin,
            double centerXMax,
            double centerYMin,
            double centerYMax,
            double maxPrimitiveSpan,
            out int sampleCount)
        {
            sampleCount = 0;
            double xSum = 0.0;
            foreach (VectorProjectionPrimitiveDto primitive in view.Primitives)
            {
                if (!TryGetPolylineFallbackBounds(primitive, out double minX, out double minY, out double maxX, out double maxY))
                    continue;

                double centerX = (minX + maxX) / 2.0;
                double centerY = (minY + maxY) / 2.0;
                if (centerX < centerXMin ||
                    centerX > centerXMax ||
                    centerY < centerYMin ||
                    centerY > centerYMax ||
                    Math.Max(maxX - minX, maxY - minY) > maxPrimitiveSpan)
                {
                    continue;
                }

                xSum += centerX;
                sampleCount++;
            }

            return sampleCount == 0 ? double.NaN : xSum / sampleCount;
        }

        private static double PointSkewXOfPolylineFallbacks(
            VectorProjectionViewDto view,
            double centerXMin,
            double centerXMax,
            double centerYMin,
            double centerYMax,
            double maxPrimitiveSpan,
            out int sampleCount)
        {
            sampleCount = 0;
            double xSum = 0.0;
            double xSquaredSum = 0.0;
            double xCubedSum = 0.0;
            foreach (VectorProjectionPrimitiveDto primitive in view.Primitives)
            {
                if (!TryGetPolylineFallbackBounds(primitive, out double minX, out double minY, out double maxX, out double maxY))
                    continue;

                double centerX = (minX + maxX) / 2.0;
                double centerY = (minY + maxY) / 2.0;
                if (centerX < centerXMin ||
                    centerX > centerXMax ||
                    centerY < centerYMin ||
                    centerY > centerYMax ||
                    Math.Max(maxX - minX, maxY - minY) > maxPrimitiveSpan)
                {
                    continue;
                }

                for (int index = 0; index + 1 < primitive.Points.Length; index += 2)
                {
                    double x = primitive.Points[index];
                    xSum += x;
                    xSquaredSum += x * x;
                    xCubedSum += x * x * x;
                    sampleCount++;
                }
            }

            if (sampleCount == 0)
                return double.NaN;

            double mean = xSum / sampleCount;
            return xCubedSum / sampleCount -
                3.0 * mean * xSquaredSum / sampleCount +
                2.0 * mean * mean * mean;
        }

        private static bool TryGetPolylineFallbackBounds(
            VectorProjectionPrimitiveDto primitive,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            minX = minY = double.PositiveInfinity;
            maxX = maxY = double.NegativeInfinity;
            if (primitive == null ||
                !string.Equals(primitive.Kind, "polyline", StringComparison.OrdinalIgnoreCase) ||
                primitive.Points == null ||
                primitive.Points.Length < 4 ||
                string.IsNullOrWhiteSpace(primitive.OriginalKind) ||
                primitive.OriginalKind.IndexOf("bspline", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            for (int index = 0; index + 1 < primitive.Points.Length; index += 2)
            {
                double x = primitive.Points[index];
                double y = primitive.Points[index + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            return true;
        }

        private static void AssertSplineFallbacksAreSampled(
            VectorProjectionResultDto result,
            string fileName,
            string viewName,
            List<string> failures)
        {
            VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, viewName, StringComparison.OrdinalIgnoreCase));
            if (view == null || view.Primitives == null)
                return;

            int lowResolutionSplineCount = 0;
            foreach (VectorProjectionPrimitiveDto primitive in view.Primitives)
            {
                if (primitive == null ||
                    !string.Equals(primitive.Kind, "polyline", StringComparison.OrdinalIgnoreCase) ||
                    primitive.Points == null ||
                    primitive.Points.Length < 4 ||
                    primitive.OriginalKind == null ||
                    primitive.OriginalKind.IndexOf("bspline", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int pointCount = primitive.Points.Length / 2;
                if (pointCount < 24)
                    lowResolutionSplineCount++;
            }

            if (lowResolutionSplineCount > 0)
            {
                failures.Add(
                    fileName +
                    " " +
                    viewName +
                    " spline fallback primitives should be evaluated curves, not low-resolution control polygons; got " +
                    lowResolutionSplineCount.ToString(CultureInfo.InvariantCulture) +
                    " spline fallback(s) with fewer than 24 points.");
            }
        }

        private static void AssertVectorViewBounds(
            VectorProjectionResultDto result,
            string fileName,
            string viewName,
            double minWidth,
            double maxWidth,
            double minHeight,
            double maxHeight,
            int minPrimitiveCount,
            List<string> failures)
        {
            VectorProjectionViewDto view = result.Views.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, viewName, StringComparison.OrdinalIgnoreCase));
            if (view == null)
            {
                failures.Add(fileName + " should include " + viewName + " vector view.");
                return;
            }

            int primitiveCount = view.Primitives == null ? 0 : view.Primitives.Count;
            if (primitiveCount < minPrimitiveCount)
            {
                failures.Add(
                    fileName +
                    " " +
                    viewName +
                    " should include at least " +
                    minPrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                    " primitives, got " +
                    primitiveCount.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            if (view.Bounds == null ||
                view.Bounds.Width < minWidth ||
                view.Bounds.Width > maxWidth ||
                view.Bounds.Height < minHeight ||
                view.Bounds.Height > maxHeight)
            {
                failures.Add(
                    fileName +
                    " " +
                    viewName +
                    " vector bounds should stay within expected model span, got " +
                    FormatVectorBounds(view.Bounds) +
                    ".");
            }
        }

        private static string FormatVectorBounds(VectorProjectionBoundsDto bounds)
        {
            if (bounds == null)
                return "<null>";

            return
                "left=" + bounds.Left.ToString("R", CultureInfo.InvariantCulture) +
                ", bottom=" + bounds.Bottom.ToString("R", CultureInfo.InvariantCulture) +
                ", right=" + bounds.Right.ToString("R", CultureInfo.InvariantCulture) +
                ", top=" + bounds.Top.ToString("R", CultureInfo.InvariantCulture) +
                ", width=" + bounds.Width.ToString("R", CultureInfo.InvariantCulture) +
                ", height=" + bounds.Height.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool IsVectorLine(VectorProjectionPrimitiveDto primitive)
        {
            return primitive != null &&
                string.Equals(primitive.Kind, "line", StringComparison.OrdinalIgnoreCase) &&
                primitive.Points != null &&
                primitive.Points.Length >= 4;
        }

        private static double Distance2d(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string FormatVectorPoints(double[] points)
        {
            if (points == null)
                return "<null>";

            return "[" + string.Join(",", points.Select(point => point.ToString("R", CultureInfo.InvariantCulture))) + "]";
        }

        private static void ValidateVectorDebugSvg(string modelName, string svgDirectory, List<string> failures)
        {
            bool sawSvgArcCommand = false;
            foreach (string viewName in new[] { "x_plus", "x_minus", "y_plus", "y_minus", "z_plus", "z_minus" })
            {
                string svgPath = Path.Combine(svgDirectory, viewName + ".svg");
                if (!File.Exists(svgPath))
                {
                    failures.Add(modelName + " " + viewName + " debug SVG was not written: " + svgPath);
                    continue;
                }

                string svg = File.ReadAllText(svgPath);
                try
                {
                    XDocument document = XDocument.Parse(svg);
                    if (!string.Equals(document.Root?.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
                        failures.Add(modelName + " " + viewName + " debug SVG should contain an SVG root.");

                    bool containsImage = document
                        .Descendants()
                        .Any(element => string.Equals(element.Name.LocalName, "image", StringComparison.OrdinalIgnoreCase));
                    if (containsImage)
                        failures.Add(modelName + " " + viewName + " debug SVG must not contain image elements.");

                    bool containsForeignObject = document
                        .Descendants()
                        .Any(element => string.Equals(element.Name.LocalName, "foreignObject", StringComparison.OrdinalIgnoreCase));
                    if (containsForeignObject)
                        failures.Add(modelName + " " + viewName + " debug SVG must not contain foreignObject elements.");

                    bool containsRasterReference = document
                        .Descendants()
                        .Attributes()
                        .Any(attribute =>
                        {
                            string localName = attribute.Name.LocalName;
                            if (!string.Equals(localName, "href", StringComparison.OrdinalIgnoreCase))
                                return false;
                            string value = attribute.Value ?? string.Empty;
                            return value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) ||
                                value.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                        });
                    if (containsRasterReference)
                        failures.Add(modelName + " " + viewName + " debug SVG must not reference raster image payloads.");

                    int vectorElementCount = document
                        .Descendants()
                        .Count(element =>
                            string.Equals(element.Name.LocalName, "line", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(element.Name.LocalName, "path", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(element.Name.LocalName, "circle", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(element.Name.LocalName, "polyline", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(element.Name.LocalName, "polygon", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(element.Name.LocalName, "ellipse", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(element.Name.LocalName, "rect", StringComparison.OrdinalIgnoreCase));
                    if (vectorElementCount == 0)
                        failures.Add(modelName + " " + viewName + " debug SVG should contain vector primitives.");

                    bool viewHasArcCommand = document
                        .Descendants()
                        .Any(element =>
                            string.Equals(element.Name.LocalName, "path", StringComparison.OrdinalIgnoreCase) &&
                            element.Attribute("d") != null &&
                            Regex.IsMatch(element.Attribute("d").Value, @"(^|[\s,])A[\s,]", RegexOptions.IgnoreCase));
                    if (viewHasArcCommand)
                        sawSvgArcCommand = true;
                }
                catch (Exception ex)
                {
                    failures.Add(modelName + " " + viewName + " debug SVG should parse as XML: " + ex.Message);
                }

                if (svg.IndexOf("data:image", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    svg.IndexOf("image/png", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    svg.IndexOf(".png", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    failures.Add(modelName + " " + viewName + " debug SVG text must not contain raster image references.");
                }
            }

            if (!modelName.StartsWith("HDMI-SMD_HDMI-001S", StringComparison.OrdinalIgnoreCase) &&
                !sawSvgArcCommand)
            {
                failures.Add(modelName + " debug SVG should emit real SVG arc commands for circular arc primitives.");
            }
        }

        private static void SaveVectorProjectionPng(VectorProjectionViewDto view, string outputPath, string title)
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            const int imageSize = 1000;
            const int padding = 60;
            VectorProjectionBoundsDto bounds = NormalizeVectorBounds(view.Bounds);
            using (var bitmap = new SKBitmap(imageSize, imageSize, SKColorType.Rgba8888, SKAlphaType.Premul))
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint())
            using (var textPaint = new SKPaint())
            using (var font = new SKFont())
            {
                canvas.Clear(SKColors.White);
                paint.Color = SKColors.Black;
                paint.StrokeWidth = 2.0f;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Stroke;

                textPaint.Color = SKColors.Black;
                textPaint.IsAntialias = true;
                font.Size = 18.0f;

                foreach (VectorProjectionPrimitiveDto primitive in view.Primitives ?? new List<VectorProjectionPrimitiveDto>())
                    DrawVectorPrimitive(canvas, paint, primitive, bounds, imageSize, padding);

                canvas.DrawText(title, 14.0f, 28.0f, SKTextAlign.Left, font, textPaint);

                using (SKImage image = SKImage.FromBitmap(bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (Stream stream = File.Create(outputPath))
                    data.SaveTo(stream);
            }
        }

        private static void DrawVectorPrimitive(
            SKCanvas canvas,
            SKPaint paint,
            VectorProjectionPrimitiveDto primitive,
            VectorProjectionBoundsDto bounds,
            int imageSize,
            int padding)
        {
            if (primitive == null)
                return;

            if (string.Equals(primitive.Kind, "line", StringComparison.OrdinalIgnoreCase) &&
                primitive.Points != null &&
                primitive.Points.Length >= 4)
            {
                canvas.DrawLine(
                    VectorImageX(primitive.Points[0], bounds, imageSize, padding),
                    VectorImageY(primitive.Points[1], bounds, imageSize, padding),
                    VectorImageX(primitive.Points[2], bounds, imageSize, padding),
                    VectorImageY(primitive.Points[3], bounds, imageSize, padding),
                    paint);
                return;
            }

            if (string.Equals(primitive.Kind, "arc", StringComparison.OrdinalIgnoreCase))
            {
                using (var path = new SKPath())
                {
                    bool started = false;
                    double sweep = primitive.EndAngle - primitive.StartAngle;
                    while (sweep < 0.0)
                        sweep += 360.0;
                    if (sweep <= 0.0)
                        sweep = 360.0;

                    int steps = Math.Max(8, (int)Math.Ceiling(sweep / 3.0));
                    for (int i = 0; i <= steps; i++)
                    {
                        double angle = (primitive.StartAngle + sweep * i / steps) * Math.PI / 180.0;
                        double x = primitive.CenterX + Math.Cos(angle) * primitive.Radius;
                        double y = primitive.CenterY + Math.Sin(angle) * primitive.Radius;
                        float px = VectorImageX(x, bounds, imageSize, padding);
                        float py = VectorImageY(y, bounds, imageSize, padding);
                        if (!started)
                        {
                            path.MoveTo(px, py);
                            started = true;
                        }
                        else
                        {
                            path.LineTo(px, py);
                        }
                    }

                    if (started)
                        canvas.DrawPath(path, paint);
                }
            }
            else if (string.Equals(primitive.Kind, "polyline", StringComparison.OrdinalIgnoreCase) &&
                primitive.Points != null &&
                primitive.Points.Length >= 4)
            {
                using (var path = new SKPath())
                {
                    path.MoveTo(
                        VectorImageX(primitive.Points[0], bounds, imageSize, padding),
                        VectorImageY(primitive.Points[1], bounds, imageSize, padding));
                    for (int index = 2; index + 1 < primitive.Points.Length; index += 2)
                    {
                        path.LineTo(
                            VectorImageX(primitive.Points[index], bounds, imageSize, padding),
                            VectorImageY(primitive.Points[index + 1], bounds, imageSize, padding));
                    }

                    canvas.DrawPath(path, paint);
                }
            }
        }

        private static VectorProjectionBoundsDto NormalizeVectorBounds(VectorProjectionBoundsDto bounds)
        {
            if (bounds == null || bounds.Width <= 0.0 || bounds.Height <= 0.0)
            {
                return new VectorProjectionBoundsDto
                {
                    Left = -5.0,
                    Bottom = -5.0,
                    Right = 5.0,
                    Top = 5.0,
                    Width = 10.0,
                    Height = 10.0
                };
            }

            return bounds;
        }

        private static float VectorImageX(double x, VectorProjectionBoundsDto bounds, int imageSize, int padding)
        {
            double scale = VectorImageScale(bounds, imageSize, padding);
            double drawingWidth = bounds.Width * scale;
            double offset = (imageSize - drawingWidth) / 2.0;
            return (float)(offset + (x - bounds.Left) * scale);
        }

        private static float VectorImageY(double y, VectorProjectionBoundsDto bounds, int imageSize, int padding)
        {
            double scale = VectorImageScale(bounds, imageSize, padding);
            double drawingHeight = bounds.Height * scale;
            double offset = (imageSize - drawingHeight) / 2.0;
            return (float)(imageSize - offset - (y - bounds.Bottom) * scale);
        }

        private static double VectorImageScale(VectorProjectionBoundsDto bounds, int imageSize, int padding)
        {
            double drawable = Math.Max(1.0, imageSize - padding * 2.0);
            return drawable / Math.Max(bounds.Width, bounds.Height);
        }

        private static int SaveEdgePreview(string[] args)
        {
            if (args.Length < 4 || args.Length > 5)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --edge-preview <input.step> <view> <output.png> [--visible-raw]");
                return 2;
            }

            string inputPath = args[1];
            string viewName = args[2];
            string outputPath = args[3];
            StepProjectionRenderMode renderMode = args.Length == 5 && IsOption(args[4], "--visible-raw")
                ? StepProjectionRenderMode.EdgeVisibleRaw
                : StepProjectionRenderMode.Edge;
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            StepProjectionImage edgeImage = ProjectSingleTestView(
                File.ReadAllBytes(inputPath),
                Path.GetFileNameWithoutExtension(inputPath),
                viewName,
                renderMode);
            edgeImage.SavePng(outputPath);
            Console.WriteLine("Edge preview written: " + Path.GetFullPath(outputPath));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunWatermarkTemplateLibraryTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string projectionDirectory = Path.Combine(dataRoot, "Projection");
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            string sourcesPath = Path.Combine(dataRoot, "WatermarkTemplateSources.json");

            List<StepWatermarkTemplateSource> sources = ReadJsonList<StepWatermarkTemplateSource>(sourcesPath);
            IReadOnlyList<StepWatermarkTemplate> templates = StepWatermarkTemplateExtractor.ExtractFromMarkedData(
                projectionDirectory,
                markedDirectory,
                sources);
            var templatesByName = templates.ToDictionary(template => template.Name, StringComparer.OrdinalIgnoreCase);

            foreach (string requiredName in new[] { "LCEDA", "EasyEDA", "easyeda-logo" })
            {
                if (!templatesByName.TryGetValue(requiredName, out StepWatermarkTemplate template))
                {
                    failures.Add("Extracted watermark template is missing: " + requiredName);
                    continue;
                }

                if (template.Width <= 8 || template.Height <= 8)
                {
                    failures.Add(
                        "Extracted watermark template " +
                        requiredName +
                        " should be larger than 8x8, got " +
                        template.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        template.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int edgePointCount = template.EdgePoints == null ? 0 : template.EdgePoints.Count;
                if (edgePointCount < 24)
                {
                    failures.Add(
                        "Extracted watermark template " +
                        requiredName +
                        " should contain enough edge points, got " +
                        edgePointCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }

            VerifyKnownRuntimeTemplatesMatchExtractedTemplates(templatesByName, failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Watermark template library test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Watermark template library test passed.");
            foreach (StepWatermarkTemplate template in templates.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    "template=" +
                    template.Name +
                    ", size=" +
                    template.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    template.Height.ToString(CultureInfo.InvariantCulture) +
                    ", edge_points=" +
                    template.EdgePoints.Count.ToString(CultureInfo.InvariantCulture));
            }

            return 0;
        }

        private static int RunTextLogoDetectionTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string expectedPath = Path.Combine(dataRoot, "TextLogoDetectionExpected.json");
            List<TextLogoDetectionExpectation> expectations = ReadJsonList<TextLogoDetectionExpectation>(expectedPath);

            foreach (TextLogoDetectionExpectation expectation in expectations)
            {
                string inputPath = Path.Combine(originalDirectory, expectation.FileName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing original fixture for text/logo detection: " + inputPath);
                    continue;
                }

                byte[] stepData = File.ReadAllBytes(inputPath);
                StepVectorWatermarkDetectionInput vectorInput =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    stepData,
                    Path.GetFileNameWithoutExtension(inputPath),
                    expectation.ViewName);
                IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                    StepVectorWatermarkProjectionDetector.Detect(
                        vectorInput,
                        new StepTextLogoDetectionOptions());
                StepVectorWatermarkDetectionRegion requiredDetection = detections
                    .OrderByDescending(detection => detection.Score)
                    .FirstOrDefault(detection => TemplateNameMatchesExpected(detection.TemplateName, expectation.RequiredTemplate));

                if (requiredDetection == null)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " should detect known template " +
                        expectation.RequiredTemplate +
                        ", got [" +
                        string.Join(",", detections.Select(detection => detection.TemplateName).Distinct(StringComparer.OrdinalIgnoreCase)) +
                        "].");
                    continue;
                }

                Console.WriteLine(
                    "detected=" +
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " template=" +
                    requiredDetection.TemplateName +
                    " bounds=" +
                    requiredDetection.X.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    requiredDetection.Y.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    requiredDetection.Width.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    requiredDetection.Height.ToString(CultureInfo.InvariantCulture) +
                    " score=" +
                    requiredDetection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    " chamfer=" +
                    requiredDetection.ChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    " primitives=" +
                    requiredDetection.PrimitiveCount.ToString(CultureInfo.InvariantCulture));

                if (requiredDetection.Width <= 8 || requiredDetection.Height <= 8)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " detection bounds are too small: " +
                        requiredDetection.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        requiredDetection.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                if (requiredDetection.PrimitiveCount < 2)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " detection should have enough vector primitives, got " +
                        requiredDetection.PrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                VerifyTextLogoDetectionExpectation(expectation, requiredDetection, detections, failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo detection test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Text/logo detection test passed.");
            return 0;
        }

        private static bool TemplateNameMatchesExpected(string detectedTemplateName, string expectedTemplateName)
        {
            if (string.IsNullOrWhiteSpace(detectedTemplateName) || string.IsNullOrWhiteSpace(expectedTemplateName))
                return false;

            if (string.Equals(detectedTemplateName, expectedTemplateName, StringComparison.OrdinalIgnoreCase))
                return true;

            return detectedTemplateName
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(template => string.Equals(template.Trim(), expectedTemplateName, StringComparison.OrdinalIgnoreCase));
        }

        private static void VerifyTextLogoDetectionExpectation(
            TextLogoDetectionExpectation expectation,
            StepVectorWatermarkDetectionRegion requiredDetection,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections,
            List<string> failures)
        {
            int boundsTolerance = expectation.BoundsTolerance <= 0 ? 24 : expectation.BoundsTolerance;
            if (expectation.ExpectedWidth > 0 && expectation.ExpectedHeight > 0)
            {
                int expectedCenterX = expectation.ExpectedX + expectation.ExpectedWidth / 2;
                int expectedCenterY = expectation.ExpectedY + expectation.ExpectedHeight / 2;
                int actualCenterX = requiredDetection.X + requiredDetection.Width / 2;
                int actualCenterY = requiredDetection.Y + requiredDetection.Height / 2;
                if (Math.Abs(actualCenterX - expectedCenterX) > boundsTolerance ||
                    Math.Abs(actualCenterY - expectedCenterY) > boundsTolerance ||
                    Math.Abs(requiredDetection.Width - expectation.ExpectedWidth) > boundsTolerance ||
                    Math.Abs(requiredDetection.Height - expectation.ExpectedHeight) > boundsTolerance)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " " +
                        expectation.RequiredTemplate +
                        " vector detection bounds drifted: expected approximately " +
                        FormatBounds(expectation.ExpectedX, expectation.ExpectedY, expectation.ExpectedWidth, expectation.ExpectedHeight) +
                        ", got " +
                        FormatBounds(requiredDetection.X, requiredDetection.Y, requiredDetection.Width, requiredDetection.Height) +
                        ", tolerance=" +
                        boundsTolerance.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }

            if (expectation.MinScore > 0.0 && requiredDetection.Score < expectation.MinScore)
            {
                failures.Add(
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " " +
                    expectation.RequiredTemplate +
                    " vector detection score is too low: expected at least " +
                    expectation.MinScore.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", got " +
                    requiredDetection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    ".");
            }

            if (expectation.MaxChamferDistance > 0.0 && requiredDetection.ChamferDistance > expectation.MaxChamferDistance)
            {
                failures.Add(
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " " +
                    expectation.RequiredTemplate +
                    " vector chamfer distance is too high: expected at most " +
                    expectation.MaxChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", got " +
                    requiredDetection.ChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    ".");
            }

            double maxUnexpectedHighScore = expectation.MaxUnexpectedHighScore <= 0.0
                ? Math.Max(10.0, requiredDetection.Score * 0.50)
                : expectation.MaxUnexpectedHighScore;
            IEnumerable<string> expectedTemplateNames = expectation.ExpectedTemplates == null || expectation.ExpectedTemplates.Count == 0
                ? new[] { expectation.RequiredTemplate }
                : expectation.ExpectedTemplates;
            var expectedTemplates = expectedTemplateNames
                .Where(template => !string.IsNullOrWhiteSpace(template))
                .ToList();

            foreach (StepVectorWatermarkDetectionRegion detection in detections)
            {
                if (expectedTemplates.Any(template => TemplateNameMatchesExpected(detection.TemplateName, template)))
                    continue;
                if (detection.Score < maxUnexpectedHighScore)
                    continue;

                failures.Add(
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " returned unexpected high-score vector template " +
                    detection.TemplateName +
                    " score=" +
                    detection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", threshold=" +
                    maxUnexpectedHighScore.ToString("0.000", CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static StepProjectionImage ProjectSingleTestView(
            byte[] stepData,
            string modelName,
            string viewName,
            StepProjectionRenderMode renderMode)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = 1600,
                PaddingPixels = 80,
                WriteMetadata = false,
                RenderMode = renderMode
            };
            options.ViewNames.Add(viewName);
            return StepProjectionRenderer.ProjectFileImages(stepData, modelName, options)[0];
        }

        private static void VerifyKnownRuntimeTemplatesMatchExtractedTemplates(
            Dictionary<string, StepWatermarkTemplate> extractedTemplatesByName,
            List<string> failures)
        {
            var knownTemplatesByName = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .ToDictionary(template => template.Name, StringComparer.OrdinalIgnoreCase);

            foreach (string requiredName in new[] { "LCEDA", "EasyEDA", "easyeda-logo" })
            {
                if (!knownTemplatesByName.TryGetValue(requiredName, out StepWatermarkTemplate knownTemplate))
                {
                    failures.Add("Committed runtime watermark template is missing: " + requiredName);
                    continue;
                }

                if (!extractedTemplatesByName.TryGetValue(requiredName, out StepWatermarkTemplate extractedTemplate))
                    continue;

                if (knownTemplate.Width <= 8 || knownTemplate.Height <= 8)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " should be larger than 8x8, got " +
                        knownTemplate.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        knownTemplate.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int knownEdgePointCount = knownTemplate.EdgePoints == null ? 0 : knownTemplate.EdgePoints.Count;
                if (knownEdgePointCount < 24)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " should contain enough edge points, got " +
                        knownEdgePointCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                    continue;
                }

                double extractedAspect = extractedTemplate.Width / (double)Math.Max(1, extractedTemplate.Height);
                double knownAspect = knownTemplate.Width / (double)Math.Max(1, knownTemplate.Height);
                double aspectDelta = Math.Abs(extractedAspect - knownAspect) / Math.Max(0.001, extractedAspect);
                if (aspectDelta > 0.08)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " dimensions are not close to the extracted source aspect: extracted=" +
                        extractedTemplate.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        extractedTemplate.Height.ToString(CultureInfo.InvariantCulture) +
                        ", known=" +
                        knownTemplate.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        knownTemplate.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int overlapCount = CountNormalizedTemplateOverlap(extractedTemplate, knownTemplate);
                int minimumOverlap = Math.Max(24, (int)Math.Round(knownEdgePointCount * 0.80, MidpointRounding.AwayFromZero));
                if (overlapCount < minimumOverlap)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " does not overlap enough normalized extracted edge points: overlap=" +
                        overlapCount.ToString(CultureInfo.InvariantCulture) +
                        ", required=" +
                        minimumOverlap.ToString(CultureInfo.InvariantCulture) +
                        ", known_points=" +
                        knownEdgePointCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }
        }

        private static int CountNormalizedTemplateOverlap(
            StepWatermarkTemplate extractedTemplate,
            StepWatermarkTemplate knownTemplate)
        {
            if (extractedTemplate.EdgePoints == null || knownTemplate.EdgePoints == null)
                return 0;

            var extractedNormalized = new HashSet<int>();
            foreach (StepWatermarkTemplatePoint point in extractedTemplate.EdgePoints)
            {
                int x = extractedTemplate.Width <= 1
                    ? 0
                    : (int)Math.Round(point.X * (knownTemplate.Width - 1) / (double)(extractedTemplate.Width - 1), MidpointRounding.AwayFromZero);
                int y = extractedTemplate.Height <= 1
                    ? 0
                    : (int)Math.Round(point.Y * (knownTemplate.Height - 1) / (double)(extractedTemplate.Height - 1), MidpointRounding.AwayFromZero);
                x = Math.Max(0, Math.Min(knownTemplate.Width - 1, x));
                y = Math.Max(0, Math.Min(knownTemplate.Height - 1, y));
                extractedNormalized.Add(y * knownTemplate.Width + x);
            }

            int overlapCount = 0;
            foreach (StepWatermarkTemplatePoint point in knownTemplate.EdgePoints)
            {
                if (extractedNormalized.Contains(point.Y * knownTemplate.Width + point.X))
                    overlapCount++;
            }

            return overlapCount;
        }

        private static int RunMarkedVectorDetectionParityTests(bool cleanText)
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            var failures = new List<string>();

            foreach (string markerPath in Directory.GetFiles(markedDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryParseMarkedModelAndView(markerPath, out string modelName, out string viewName))
                    continue;

                List<MarkedRectI> markedRects = ReadMarkedRectangles(markerPath);
                if (markedRects.Count == 0)
                    continue;

                string stepPath = Path.Combine(originalDirectory, modelName + ".step");
                if (!File.Exists(stepPath))
                {
                    failures.Add("Missing original STEP for marked vector parity: " + stepPath);
                    continue;
                }

                byte[] stepData = File.ReadAllBytes(stepPath);
                StepVectorWatermarkDetectionInput vectorInput =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(stepData, modelName, viewName);
                IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                    StepVectorWatermarkProjectionDetector.Detect(
                        vectorInput,
                        new StepTextLogoDetectionOptions { DetectArbitraryText = cleanText });

                AssertMarkedVectorParity(modelName, viewName, markedRects, detections, failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Marked vector detection parity failed. cleanText=" + cleanText.ToString(CultureInfo.InvariantCulture));
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Marked vector detection parity passed. cleanText=" + cleanText.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunVectorTextDetectorSmokeTests()
        {
            var failures = new List<string>();

            StepVectorWatermarkDetectionInput knownInput = CreateTemplateVectorTextInput("EasyEDA", 90, 120, 180, 2.0);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> knownDetections =
                StepVectorTextDetector.Detect(knownInput, new StepTextLogoDetectionOptions());
            StepVectorWatermarkDetectionRegion known = knownDetections
                .FirstOrDefault(detection => string.Equals(detection.Text, "EasyEDA", StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                failures.Add("Known EasyEDA template text was not detected.");
            }
            else
            {
                if (!string.Equals(known.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    failures.Add("Known text detection kind should be text, got " + known.Kind + ".");
                if (known.OrientationDegrees != 90 || known.TextOrientationDegrees != 90)
                    failures.Add("Known text orientation should be 90 degrees, got orientation=" + known.OrientationDegrees.ToString(CultureInfo.InvariantCulture) + " textOrientation=" + known.TextOrientationDegrees.ToString(CultureInfo.InvariantCulture) + ".");
                if (known.X < 118 || known.Y < 178 || known.Width > 70 || known.Height > 205)
                    failures.Add("Known text bounds are not tight enough: " + FormatBounds(known.X, known.Y, known.Width, known.Height) + ".");
            }

            StepVectorWatermarkDetectionInput arbitraryInput = CreateArbitraryVectorTextInput();
            IReadOnlyList<StepVectorWatermarkDetectionRegion> withoutCleanText =
                StepVectorTextDetector.Detect(arbitraryInput, new StepTextLogoDetectionOptions { DetectArbitraryText = false });
            if (withoutCleanText.Count != 0)
            {
                failures.Add(
                    "Arbitrary text should not be detected when DetectArbitraryText is false: " +
                    string.Join(
                        "; ",
                        withoutCleanText.Select(detection =>
                            detection.TemplateName +
                            "/" +
                            detection.Text +
                            " score=" +
                            detection.Score.ToString("0.000", CultureInfo.InvariantCulture))));
            }

            IReadOnlyList<StepVectorWatermarkDetectionRegion> arbitraryDetections =
                StepVectorTextDetector.Detect(arbitraryInput, new StepTextLogoDetectionOptions { DetectArbitraryText = true });
            StepVectorWatermarkDetectionRegion arbitrary = arbitraryDetections.FirstOrDefault();
            if (arbitrary == null)
            {
                failures.Add("Arbitrary manufacturer-like text was not detected when DetectArbitraryText is true.");
            }
            else
            {
                if (!string.Equals(arbitrary.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    failures.Add("Arbitrary detection kind should be text, got " + arbitrary.Kind + ".");
                if (arbitrary.TextOrientationDegrees != 0)
                    failures.Add("Arbitrary text orientation should be 0 degrees, got " + arbitrary.TextOrientationDegrees.ToString(CultureInfo.InvariantCulture) + ".");
            }

            IReadOnlyList<StepVectorWatermarkDetectionRegion> splitArbitraryDetections =
                StepVectorTextDetector.Detect(CreateSplitArbitraryVectorTextInput(), new StepTextLogoDetectionOptions { DetectArbitraryText = true });
            StepVectorWatermarkDetectionRegion splitArbitrary = splitArbitraryDetections.FirstOrDefault();
            if (splitArbitraryDetections.Count != 1 || splitArbitrary == null)
            {
                failures.Add("Closest arbitrary text fragments should combine to one detection, got " + splitArbitraryDetections.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                if (splitArbitrary.Width < 130)
                    failures.Add("Combined arbitrary text bounds should span both close fragments, got " + FormatBounds(splitArbitrary.X, splitArbitrary.Y, splitArbitrary.Width, splitArbitrary.Height) + ".");
            }

            IReadOnlyList<StepVectorWatermarkDetectionRegion> pinRowDetections =
                StepVectorTextDetector.Detect(CreatePinRowVectorInput(), new StepTextLogoDetectionOptions { DetectArbitraryText = true });
            if (pinRowDetections.Count != 0)
                failures.Add("Regular pin/contact row should be rejected, got " + pinRowDetections.Count.ToString(CultureInfo.InvariantCulture) + " detection(s).");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector text detector smoke tests failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector text detector smoke tests passed.");
            return 0;
        }

        private static int RunVectorTextDetectorDualPassContract()
        {
            var failures = new List<string>();
            AssertDualPassMatchesSeparateTextDetections(
                "known-template",
                CreateTemplateVectorTextInput("EasyEDA", 90, 120, 180, 2.0),
                failures);
            AssertDualPassMatchesSeparateTextDetections(
                "arbitrary-text",
                CreateArbitraryVectorTextInput(),
                failures);
            AssertDualPassMatchesSeparateTextDetections(
                "split-arbitrary-text",
                CreateSplitArbitraryVectorTextInput(),
                failures);
            AssertDualPassMatchesSeparateTextDetections(
                "pin-row-negative",
                CreatePinRowVectorInput(),
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector text detector dual-pass contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector text detector dual-pass contract passed.");
            return 0;
        }

        private static int RunVectorWatermarkProjectionParallelismContract()
        {
            var failures = new List<string>();
            string previous = Environment.GetEnvironmentVariable("EASYEDA_VECTOR_WATERMARK_PROJECTION_PARALLELISM");
            try
            {
                Environment.SetEnvironmentVariable("EASYEDA_VECTOR_WATERMARK_PROJECTION_PARALLELISM", null);
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(0) != 1)
                    failures.Add("No selected views should use one projection worker.");
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(2) != 2)
                    failures.Add("Default vector watermark projection should use one worker per selected view.");
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(8) != 8)
                    failures.Add("Default vector watermark projection should scale to selected view count.");

                Environment.SetEnvironmentVariable("EASYEDA_VECTOR_WATERMARK_PROJECTION_PARALLELISM", "3");
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(6) != 3)
                    failures.Add("Configured vector watermark projection worker count should be honored.");

                Environment.SetEnvironmentVariable("EASYEDA_VECTOR_WATERMARK_PROJECTION_PARALLELISM", "99");
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(6) != 6)
                    failures.Add("Vector watermark projection worker count should be capped to selected view count.");

                Environment.SetEnvironmentVariable("EASYEDA_VECTOR_WATERMARK_PROJECTION_PARALLELISM", "bad");
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(4) != 4)
                    failures.Add("Invalid vector watermark projection worker count should fall back to selected view count.");

                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(6, 1) != 1)
                    failures.Add("Explicit vector watermark projection cap should allow single-worker callers.");
                if (StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(6, 99) != 6)
                    failures.Add("Explicit vector watermark projection cap should not exceed selected view count.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("EASYEDA_VECTOR_WATERMARK_PROJECTION_PARALLELISM", previous);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector watermark projection parallelism contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector watermark projection parallelism contract passed.");
            return 0;
        }

        private static int RunVectorDetectionPrimitiveMembershipContract()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtures = new[]
            {
                new { Model = "TYPE-C-TH_TYPEC-215-ARP14", View = "x_plus" },
                new { Model = "CONN-SMD_DF56_40S_0.3V_51", View = "x_plus" },
                new { Model = "USB-C-SMD_TYPE-C-6PIN-2MD-073", View = "z_minus" }
            };

            foreach (var fixture in fixtures)
            {
                StepVectorWatermarkDetectionInput input = ProjectVectorWatermarkInput(
                    originalDirectory,
                    fixture.Model,
                    fixture.View);
                IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                    StepVectorWatermarkProjectionDetector.Detect(
                        input,
                        new StepTextLogoDetectionOptions { DetectArbitraryText = true });
                StepVectorWatermarkDetectionRegion detection = detections
                    .OrderByDescending(region => string.Equals(region.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(region => region.Score)
                    .FirstOrDefault();
                if (detection == null)
                {
                    failures.Add(fixture.Model + " " + fixture.View + " should produce a vector watermark detection.");
                    continue;
                }

                IReadOnlyList<int> primitiveIndices = detection.PrimitiveSourceIndices ?? Array.Empty<int>();
                if (primitiveIndices.Count == 0)
                {
                    failures.Add(fixture.Model + " " + fixture.View + " detection must expose primitive membership.");
                    continue;
                }

                if (primitiveIndices.Count != primitiveIndices.Distinct().Count())
                    failures.Add(fixture.Model + " " + fixture.View + " primitive membership should be unique.");

                foreach (int primitiveIndex in primitiveIndices)
                {
                    if (primitiveIndex < 0 || primitiveIndex >= input.Primitives.Count)
                    {
                        failures.Add(
                            fixture.Model +
                            " " +
                            fixture.View +
                            " primitive membership contains out-of-range index " +
                            primitiveIndex.ToString(CultureInfo.InvariantCulture) +
                            ".");
                        continue;
                    }

                    StepVectorWatermarkPrimitive primitive = input.Primitives[primitiveIndex];
                    if (primitive.SampledPoints == null || primitive.SampledPoints.Count < 2)
                    {
                        failures.Add(
                            fixture.Model +
                            " " +
                            fixture.View +
                            " primitive membership index " +
                            primitiveIndex.ToString(CultureInfo.InvariantCulture) +
                            " has no sampled model geometry for residual topology matching.");
                    }
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector detection primitive membership contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector detection primitive membership contract passed.");
            return 0;
        }

        private static void AssertDualPassMatchesSeparateTextDetections(
            string label,
            StepVectorWatermarkDetectionInput input,
            List<string> failures)
        {
            IReadOnlyList<StepVectorWatermarkDetectionRegion> expectedKnown =
                StepVectorTextDetector.Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = false });
            IReadOnlyList<StepVectorWatermarkDetectionRegion> expectedClean =
                StepVectorTextDetector.Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = true });

            StepVectorTextDetectionPair actual =
                StepVectorTextDetector.DetectKnownAndCleanText(input);

            AssertSameVectorDetections(label + " known", expectedKnown, actual.KnownTextDetections, failures);
            AssertSameVectorDetections(label + " clean", expectedClean, actual.CleanTextDetections, failures);
        }

        private static void AssertSameVectorDetections(
            string label,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> expected,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> actual,
            List<string> failures)
        {
            List<string> expectedLines = expected.Select(FormatVectorDetectionForComparison).ToList();
            List<string> actualLines = actual.Select(FormatVectorDetectionForComparison).ToList();
            if (expectedLines.SequenceEqual(actualLines, StringComparer.Ordinal))
                return;

            failures.Add(
                label +
                " dual-pass detections differ. expected=[" +
                string.Join("; ", expectedLines) +
                "] actual=[" +
                string.Join("; ", actualLines) +
                "]");
        }

        private static string FormatVectorDetectionForComparison(StepVectorWatermarkDetectionRegion detection)
        {
            if (detection == null)
                return "<null>";

            return string.Join(
                "|",
                detection.Kind ?? string.Empty,
                detection.TemplateName ?? string.Empty,
                detection.Text ?? string.Empty,
                detection.X.ToString(CultureInfo.InvariantCulture),
                detection.Y.ToString(CultureInfo.InvariantCulture),
                detection.Width.ToString(CultureInfo.InvariantCulture),
                detection.Height.ToString(CultureInfo.InvariantCulture),
                detection.OrientationDegrees.ToString(CultureInfo.InvariantCulture),
                detection.LogoOrientationDegrees.ToString(CultureInfo.InvariantCulture),
                detection.TextOrientationDegrees.ToString(CultureInfo.InvariantCulture),
                detection.Score.ToString("0.000", CultureInfo.InvariantCulture),
                detection.ChamferDistance.ToString("0.000", CultureInfo.InvariantCulture),
                detection.PrimitiveCount.ToString(CultureInfo.InvariantCulture));
        }

        private static int RunVectorDetectionDump(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --vector-detection-dump <model> <view> [--clean-text]");
                return 1;
            }

            bool inputIsPath = args[1].EndsWith(".step", StringComparison.OrdinalIgnoreCase) && File.Exists(args[1]);
            string modelName = args[1].EndsWith(".step", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(args[1])
                : args[1];
            string viewName = args[2];
            bool cleanText = args.Any(argument => IsOption(argument, "--clean-text"));
            string stepPath = inputIsPath
                ? args[1]
                : Path.Combine(FindDataRoot(), "Original", modelName + ".step");
            if (!File.Exists(stepPath))
            {
                Console.Error.WriteLine("Missing original STEP: " + stepPath);
                return 1;
            }

            StepVectorWatermarkDetectionInput input =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    File.ReadAllBytes(stepPath),
                    modelName,
                    viewName);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> logo =
                StepVectorLogoDetector.Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = cleanText });
            IReadOnlyList<StepVectorWatermarkDetectionRegion> text =
                StepVectorTextDetector.Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = cleanText });
            IReadOnlyList<StepVectorWatermarkDetectionRegion> combined =
                StepVectorWatermarkProjectionDetector.Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = cleanText });

            Console.WriteLine(
                modelName +
                " " +
                viewName +
                " primitives=" +
                input.Primitives.Count.ToString(CultureInfo.InvariantCulture) +
                " cleanText=" +
                cleanText.ToString(CultureInfo.InvariantCulture));
            DumpVectorRegions("logo", logo);
            DumpVectorRegions("text", text);
            DumpVectorRegions("facade", combined);
            if (args.Any(argument => IsOption(argument, "--primitives")))
                DumpVectorPrimitivesInsideDetections(input, combined);
            return 0;
        }

        private static int RunResidualVectorProvenanceDump(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --residual-vector-provenance-dump <input.step> <view>");
                return 1;
            }

            string stepPath = args[1];
            string viewName = args[2];
            if (!File.Exists(stepPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + stepPath);
                return 1;
            }

            byte[] stepBytes = File.ReadAllBytes(stepPath);
            StepVectorWatermarkDetectionInput input =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    stepBytes,
                    Path.GetFileNameWithoutExtension(stepPath),
                    viewName);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                StepVectorWatermarkProjectionDetector
                    .Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = false })
                    .Where(IsKnownVectorWatermarkDetection)
                    .ToList();
            if (detections.Count == 0)
            {
                Console.WriteLine("residual-detections=0 view=" + viewName);
                return 0;
            }

            List<ProjectedStepTopologySource> topology =
                BuildProjectedStepTopologySources(
                    Encoding.Latin1.GetString(stepBytes),
                    viewName);

            bool failed = false;
            foreach (StepVectorWatermarkDetectionRegion detection in detections)
            {
                var primitiveMatches = input.Primitives
                    .Where(primitive => primitive.ImageBounds != null && VectorPrimitiveIntersectsDetection(primitive, detection))
                    .Select(primitive => MatchResidualPrimitiveSource(primitive, topology))
                    .ToList();
                var sourceCounts = primitiveMatches
                    .Where(match => match.Source != null)
                    .GroupBy(match => match.Source.Key, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        Key = group.Key,
                        Source = group.First().Source,
                        Count = group.Count()
                    })
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .ToList();
                int unknownCount = primitiveMatches.Count(match => match.Source == null);

                Console.WriteLine(
                    "residual-detection view=" +
                    viewName +
                    " template=" +
                    (detection.TemplateName ?? string.Empty) +
                    " primitives=" +
                    detection.PrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                    " matchedPrimitives=" +
                    primitiveMatches.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var group in sourceCounts)
                {
                    Console.WriteLine(
                        "source face=#" +
                        group.Source.FaceId.ToString(CultureInfo.InvariantCulture) +
                        " bound=#" +
                        group.Source.BoundId.ToString(CultureInfo.InvariantCulture) +
                        " edge=#" +
                        group.Source.EdgeCurveId.ToString(CultureInfo.InvariantCulture) +
                        " count=" +
                        group.Count.ToString(CultureInfo.InvariantCulture));
                }

                Console.WriteLine("unknown count=" + unknownCount.ToString(CultureInfo.InvariantCulture));
                if (primitiveMatches.Count > 0 && unknownCount > Math.Max(1, primitiveMatches.Count / 10))
                    failed = true;
            }

            return failed ? 1 : 0;
        }

        private static int RunCleanReportDump(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --clean-report-dump <model.step>");
                return 1;
            }

            string modelName = args[1].EndsWith(".step", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(args[1])
                : args[1];
            string stepPath = Path.Combine(FindDataRoot(), "Original", modelName + ".step");
            if (!File.Exists(stepPath))
            {
                Console.Error.WriteLine("Missing original STEP: " + stepPath);
                return 1;
            }

            StepWatermarkCleanerReport report = StepWatermarkCleaner.CleanWithReport(
                File.ReadAllText(stepPath, Encoding.Latin1),
                new StepWatermarkCleanerOptions());
            Console.WriteLine(
                modelName +
                " removedSolids=" +
                report.RemovedSolidCount.ToString(CultureInfo.InvariantCulture) +
                " flattenedFaces=" +
                report.FlattenedFaceCount.ToString(CultureInfo.InvariantCulture) +
                " flattenedPoints=" +
                report.FlattenedPointCount.ToString(CultureInfo.InvariantCulture) +
                " recoloredFaces=" +
                report.RecoloredFaceCount.ToString(CultureInfo.InvariantCulture) +
                " removedGeometryBytes=" +
                Encoding.Latin1.GetByteCount(report.RemovedGeometryStep ?? string.Empty).ToString(CultureInfo.InvariantCulture));
            foreach (string diagnostic in report.Diagnostics ?? Array.Empty<string>())
                Console.WriteLine(diagnostic);

            if (report.DetectionReport != null)
            {
                Console.WriteLine("Detection report regions: " + report.DetectionReport.Regions.Count.ToString(CultureInfo.InvariantCulture));
                foreach (StepWatermarkRegionDetection region in report.DetectionReport.Regions)
                {
                    Console.WriteLine(
                        "  report-region kind=" +
                        region.Kind +
                        " view=" +
                        region.ViewName +
                        " entity=#" +
                        region.EntityId.ToString(CultureInfo.InvariantCulture));
                }

                var projectionOptions = new StepProjectionOptions
                {
                    ImageSizePixels = VerificationProjectionImageSizePixels,
                    PaddingPixels = VerificationProjectionPaddingPixels,
                    WriteMetadata = false,
                    SkipGeometryModelForExternalRender = true,
                    MaxParallelFiles = 1
                };
                var verifiedReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(report.DetectionReport);
                IReadOnlyList<StepProjectionDetectionRegion> projectedRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    stepPath,
                    verifiedReport,
                    projectionOptions);
                Console.WriteLine("Projected verified regions: " + projectedRegions.Count.ToString(CultureInfo.InvariantCulture));
                foreach (StepProjectionDetectionRegion region in projectedRegions)
                {
                    Console.WriteLine(
                        "  projected kind=" +
                        region.Kind +
                        " view=" +
                        region.ViewName +
                        " rect=[" +
                        region.RectangleX.ToString(CultureInfo.InvariantCulture) +
                        "," +
                        region.RectangleY.ToString(CultureInfo.InvariantCulture) +
                        " " +
                        region.RectangleWidth.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        region.RectangleHeight.ToString(CultureInfo.InvariantCulture) +
                        "]");
                }
            }

            return 0;
        }

        private static bool StepBoundsProjectionIntersects(
            StepBounds3d bounds,
            int excludedAxis,
            double uMin,
            double uMax,
            double vMin,
            double vMax)
        {
            double minU;
            double maxU;
            double minV;
            double maxV;
            if (excludedAxis == 0)
            {
                minU = bounds.MinY;
                maxU = bounds.MaxY;
                minV = bounds.MinZ;
                maxV = bounds.MaxZ;
            }
            else if (excludedAxis == 1)
            {
                minU = bounds.MinX;
                maxU = bounds.MaxX;
                minV = bounds.MinZ;
                maxV = bounds.MaxZ;
            }
            else
            {
                minU = bounds.MinX;
                maxU = bounds.MaxX;
                minV = bounds.MinY;
                maxV = bounds.MaxY;
            }

            return maxU >= uMin &&
                minU <= uMax &&
                maxV >= vMin &&
                minV <= vMax;
        }

        private static int RunStepCleanerProfile(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --stepcleaner-profile <model.step>");
                return 1;
            }

            string stepPath = ResolveOriginalStepPath(args[1]);
            if (!File.Exists(stepPath))
            {
                Console.Error.WriteLine("Missing STEP model: " + stepPath);
                return 1;
            }

            StepCleanerProfileResult profile = ProfileStepCleanerModel(stepPath, printDetails: true);
            return profile == null ? 1 : 0;
        }

        private static int RunStepCleanerSpeedContract(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --stepcleaner-speed-contract <model.step>");
                return 1;
            }

            string stepPath = ResolveOriginalStepPath(args[1]);
            if (!File.Exists(stepPath))
            {
                Console.Error.WriteLine("Missing STEP model: " + stepPath);
                return 1;
            }

            StepCleanerSpeedContractResult result = MeasureStepCleanerSpeedContract(stepPath);
            var failures = new List<string>();
            AddSpeedBudgetFailure(
                failures,
                "optimized_clean_with_report_ms",
                result.CleanWithReportWithoutRemovedGeometryMs,
                budgetMs: 120000);
            AddSpeedBudgetFailure(
                failures,
                "scoped_visual_oracle_ms",
                result.ScopedVisualOracleMs,
                budgetMs: 18000);
            if (result.RemovedGeometryByteCount != 0)
                failures.Add("removed_geometry_bytes=" + result.RemovedGeometryByteCount.ToString(CultureInfo.InvariantCulture) + " expected=0");
            if (result.CleanDetailTimings.ContainsKey("report_build_removed_geometry_step"))
                failures.Add("report_build_removed_geometry_step timing was present in clean-only path");
            if (result.VisualOracleFailureCount > 0)
                failures.Add("scoped_visual_oracle_failures=" + result.VisualOracleFailureCount.ToString(CultureInfo.InvariantCulture));
            if (result.DetectRegionCount <= 0)
                failures.Add("detect_regions=0");
            if (result.ScopedViewCount <= 0)
                failures.Add("scoped_views=0");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("StepCleaner speed contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                PrintStepCleanerSpeedContract(result);
                return 1;
            }

            Console.WriteLine("StepCleaner speed contract passed.");
            PrintStepCleanerSpeedContract(result);
            return 0;
        }

        private static int RunFullRegressionParallelismContract()
        {
            string previous = Environment.GetEnvironmentVariable("STEPCLEANER_TEST_CLEANUP_PARALLELISM");
            try
            {
                Environment.SetEnvironmentVariable("STEPCLEANER_TEST_CLEANUP_PARALLELISM", null);
                int defaultParallelism = GetFullRegressionCleanupParallelism();
                Environment.SetEnvironmentVariable("STEPCLEANER_TEST_CLEANUP_PARALLELISM", "1");
                int singleParallelism = GetFullRegressionCleanupParallelism();
                Environment.SetEnvironmentVariable("STEPCLEANER_TEST_CLEANUP_PARALLELISM", "99");
                int clampedParallelism = GetFullRegressionCleanupParallelism();

                Console.WriteLine("full_regression_cleanup_parallelism_default=" + defaultParallelism.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("full_regression_cleanup_parallelism_override_1=" + singleParallelism.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("full_regression_cleanup_parallelism_override_99=" + clampedParallelism.ToString(CultureInfo.InvariantCulture));

                var failures = new List<string>();
                int expectedDefault = Math.Min(2, Math.Max(1, Environment.ProcessorCount));
                if (defaultParallelism != expectedDefault)
                    failures.Add("default_parallelism=" + defaultParallelism.ToString(CultureInfo.InvariantCulture) + " expected=" + expectedDefault.ToString(CultureInfo.InvariantCulture));
                if (singleParallelism != 1)
                    failures.Add("override_1_parallelism=" + singleParallelism.ToString(CultureInfo.InvariantCulture) + " expected=1");
                if (clampedParallelism < 1 || clampedParallelism > Environment.ProcessorCount)
                    failures.Add("override_99_parallelism=" + clampedParallelism.ToString(CultureInfo.InvariantCulture) + " expected_between=1.." + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));

                if (failures.Count > 0)
                {
                    Console.Error.WriteLine("Full regression parallelism contract failed.");
                    foreach (string failure in failures)
                        Console.Error.WriteLine("  " + failure);
                    return 1;
                }

                Console.WriteLine("Full regression parallelism contract passed.");
                return 0;
            }
            finally
            {
                Environment.SetEnvironmentVariable("STEPCLEANER_TEST_CLEANUP_PARALLELISM", previous);
            }
        }

        private static int RunDetectionDebugCacheCoverageContract()
        {
            string root = Path.Combine(FindRepoRoot(), ".codex-temp", "detection-debug-cache-coverage-contract");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            string originalDirectory = Path.Combine(root, "Original");
            string projectionDirectory = Path.Combine(root, "Projection");
            string markedDirectory = Path.Combine(root, "Marked");
            string detectionDirectory = Path.Combine(root, "Detection");
            Directory.CreateDirectory(originalDirectory);
            Directory.CreateDirectory(projectionDirectory);
            Directory.CreateDirectory(markedDirectory);
            Directory.CreateDirectory(detectionDirectory);

            string originalFile = Path.Combine(originalDirectory, "Synthetic.step");
            var originalFiles = new List<string> { originalFile };
            var originalBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Synthetic" };
            var cache = new FullTestDetectionCache();
            cache.SetReport(
                originalFile,
                new StepWatermarkDetectionReport
                {
                    Regions = new List<StepWatermarkRegionDetection>
                    {
                        new StepWatermarkRegionDetection { ViewName = "x_plus", Kind = "text", RectangleX = 10, RectangleY = 10, RectangleWidth = 20, RectangleHeight = 20 },
                        new StepWatermarkRegionDetection { ViewName = "y_plus", Kind = "text", RectangleX = 20, RectangleY = 20, RectangleWidth = 20, RectangleHeight = 20 }
                    }
                });
            File.WriteAllBytes(Path.Combine(detectionDirectory, "Synthetic__x_plus.png"), Array.Empty<byte>());

            var failures = new List<string>();
            VerifyDetectionDebugImages(
                originalFiles,
                originalBaseNames,
                projectionDirectory,
                markedDirectory,
                detectionDirectory,
                cache,
                regenerateImages: false,
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Detection debug cache coverage contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Detection debug cache coverage contract passed.");
            return 0;
        }

        private static void AddSpeedBudgetFailure(List<string> failures, string name, long actualMs, long budgetMs)
        {
            if (actualMs > budgetMs)
            {
                failures.Add(
                    name +
                    "=" +
                    actualMs.ToString(CultureInfo.InvariantCulture) +
                    " budget_ms=" +
                    budgetMs.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static StepCleanerSpeedContractResult MeasureStepCleanerSpeedContract(string stepPath)
        {
            string modelName = Path.GetFileNameWithoutExtension(stepPath);
            byte[] originalStepBytes = File.ReadAllBytes(stepPath);
            string stepText = Encoding.Latin1.GetString(originalStepBytes);
            var result = new StepCleanerSpeedContractResult
            {
                ModelName = modelName,
                ByteCount = originalStepBytes.Length
            };

            StepWatermarkCleanerReport cleanReport = null;
            result.CleanWithReportWithoutRemovedGeometryMs = MeasureElapsedMilliseconds(() =>
            {
                cleanReport = StepWatermarkCleaner.CleanWithReport(
                    stepText,
                    new StepWatermarkCleanerOptions
                    {
                        BuildRemovedGeometryStep = false
                    });
            });

            if (cleanReport == null)
                return result;

            result.CleanedStepByteCount = Encoding.Latin1.GetByteCount(cleanReport.CleanedStep ?? string.Empty);
            result.RemovedGeometryByteCount = Encoding.Latin1.GetByteCount(cleanReport.RemovedGeometryStep ?? string.Empty);
            result.DetectRegionCount = cleanReport.DetectionReport?.Regions?.Count ?? 0;
            if (cleanReport.Timings != null)
            {
                foreach (StepWatermarkCleanerTiming timing in cleanReport.Timings)
                {
                    if (timing == null || string.IsNullOrWhiteSpace(timing.Name))
                        continue;

                    result.CleanDetailTimings[timing.Name] = timing.ElapsedMilliseconds;
                }
            }

            List<string> scopedViewNames = cleanReport.DetectionReport?.Regions?
                .Select(region => region.ViewName)
                .Where(viewName => !string.IsNullOrWhiteSpace(viewName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            if (scopedViewNames.Count == 0)
                scopedViewNames.AddRange(StepProjectionRenderer.ViewNames);

            result.ScopedViewCount = scopedViewNames.Count;
            result.ScopedViewNames.AddRange(scopedViewNames);

            byte[] cleanStepBytes = Encoding.Latin1.GetBytes(cleanReport.CleanedStep ?? string.Empty);
            IReadOnlyList<StepWatermarkVisualDetection> originalVisualDetections =
                StepWatermarkVisualOracle.CreateOriginalDetections(cleanReport.DetectionReport, scopedViewNames);
            result.ScopedVisualOracleMs = MeasureElapsedMilliseconds(() =>
            {
                StepWatermarkVisualResidualResult visualResult =
                    StepWatermarkVisualOracle.VerifyKnownWatermarkRemoved(
                        originalVisualDetections,
                        cleanStepBytes,
                        modelName + ".speed",
                        scopedViewNames);
                result.VisualOracleFailureCount = visualResult.Failures.Count;
                result.VisualOracleOriginalDetectionCount = visualResult.OriginalDetections.Count;
                result.VisualOracleResidualDetectionCount = visualResult.ResidualDetections.Count;
            });

            return result;
        }

        private static StepCleanerProfileResult ProfileStepCleanerModel(string stepPath, bool printDetails)
        {
            string modelName = Path.GetFileNameWithoutExtension(stepPath);
            byte[] stepBytes = File.ReadAllBytes(stepPath);
            string stepText = Encoding.Latin1.GetString(stepBytes);
            var result = new StepCleanerProfileResult
            {
                ModelName = modelName,
                ByteCount = stepBytes.Length
            };

            result.CleanerDetectOnlyMs = MeasureElapsedMilliseconds(() =>
            {
                StepWatermarkDetectionReport report = StepWatermarkCleaner.Detect(
                    stepBytes,
                    new StepWatermarkCleanerOptions());
                result.DetectRegionCount = report.Regions.Count;
            });

            result.CleanWithoutRemovedGeometryMs = MeasureElapsedMilliseconds(() =>
            {
                byte[] cleanStep = StepWatermarkCleaner.Clean(
                    stepBytes,
                    new StepWatermarkCleanerOptions());
                result.CleanedStepByteCount = cleanStep.Length;
            });

            StepWatermarkCleanerReport cleanReport = null;
            result.CleanWithReportMs = MeasureElapsedMilliseconds(() =>
            {
                cleanReport = StepWatermarkCleaner.CleanWithReport(
                    stepText,
                    new StepWatermarkCleanerOptions());
                result.RemovedGeometryByteCount = Encoding.Latin1.GetByteCount(cleanReport.RemovedGeometryStep ?? string.Empty);
            });

            if (cleanReport?.Timings != null)
            {
                foreach (StepWatermarkCleanerTiming timing in cleanReport.Timings)
                {
                    if (timing == null || string.IsNullOrWhiteSpace(timing.Name))
                        continue;

                    result.CleanDetailTimings[timing.Name] = timing.ElapsedMilliseconds;
                }
            }

            result.VisualOracleAllViewsMs = MeasureElapsedMilliseconds(() =>
            {
                StepWatermarkVisualScanResult visual = StepWatermarkVisualOracle.DetectKnownWatermarks(
                    stepBytes,
                    modelName + ".profile");
                result.VisualDetectionCount = visual.Detections.Count;
            });

            foreach (string viewName in StepProjectionRenderer.ViewNames)
            {
                result.VectorProjectDetectMsByView[viewName] = MeasureElapsedMilliseconds(() =>
                {
                    StepVectorWatermarkDetectionInput input = StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                        stepBytes,
                        modelName + ".profile",
                        viewName);
                    IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                        StepVectorWatermarkProjectionDetector.Detect(
                            input,
                            new StepTextLogoDetectionOptions { DetectArbitraryText = false });
                    result.VectorPrimitiveCountByView[viewName] = input.Primitives.Count;
                    result.VectorDetectionCountByView[viewName] = detections.Count;
                });
            }

            string projectionOutput = Path.Combine(
                FindDataRoot(),
                ".ProfileProjection");
            Directory.CreateDirectory(projectionOutput);
            result.ProjectFileAllViewsMs = MeasureElapsedMilliseconds(() =>
            {
                StepProjectionReport projectionReport = StepProjectionRenderer.ProjectFile(
                    stepPath,
                    projectionOutput,
                    new StepProjectionOptions
                    {
                        ImageSizePixels = VerificationProjectionImageSizePixels,
                        PaddingPixels = VerificationProjectionPaddingPixels,
                        WriteMetadata = false,
                        SkipGeometryModelForExternalRender = true,
                        MaxParallelFiles = 1
                    });
                result.ProjectionOutputCount = projectionReport.OutputFiles.Count;
            });

            if (printDetails)
                PrintStepCleanerProfile(result);
            return result;
        }

        private static long MeasureElapsedMilliseconds(Action action)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }

        private static void PrintStepCleanerProfile(StepCleanerProfileResult profile)
        {
            if (profile == null)
                return;

            Console.WriteLine("profile_model=" + profile.ModelName);
            Console.WriteLine("profile_bytes=" + profile.ByteCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_detect_regions=" + profile.DetectRegionCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_cleaned_step_bytes=" + profile.CleanedStepByteCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_removed_geometry_bytes=" + profile.RemovedGeometryByteCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_visual_detections=" + profile.VisualDetectionCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_projection_outputs=" + profile.ProjectionOutputCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_cleaner_detect_only_ms=" + profile.CleanerDetectOnlyMs.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_cleaner_clean_ms=" + profile.CleanWithoutRemovedGeometryMs.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_cleaner_clean_with_report_ms=" + profile.CleanWithReportMs.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_visual_oracle_all_views_ms=" + profile.VisualOracleAllViewsMs.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("profile_project_file_all_views_ms=" + profile.ProjectFileAllViewsMs.ToString(CultureInfo.InvariantCulture));

            foreach (var kvp in profile.CleanDetailTimings.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    "profile_clean_detail_" +
                    kvp.Key +
                    "_ms=" +
                    kvp.Value.ToString(CultureInfo.InvariantCulture));
            }

            foreach (string viewName in StepProjectionRenderer.ViewNames)
            {
                profile.VectorProjectDetectMsByView.TryGetValue(viewName, out long elapsedMs);
                profile.VectorPrimitiveCountByView.TryGetValue(viewName, out int primitiveCount);
                profile.VectorDetectionCountByView.TryGetValue(viewName, out int detectionCount);
                Console.WriteLine(
                    "profile_vector_project_detect_" +
                    viewName +
                    "_ms=" +
                    elapsedMs.ToString(CultureInfo.InvariantCulture) +
                    " primitives=" +
                    primitiveCount.ToString(CultureInfo.InvariantCulture) +
                    " detections=" +
                    detectionCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void PrintStepCleanerSpeedContract(StepCleanerSpeedContractResult result)
        {
            if (result == null)
                return;

            Console.WriteLine("speed_contract_model=" + result.ModelName);
            Console.WriteLine("speed_contract_bytes=" + result.ByteCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_detect_regions=" + result.DetectRegionCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_scoped_views=" + string.Join(",", result.ScopedViewNames));
            Console.WriteLine("speed_contract_scoped_view_count=" + result.ScopedViewCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_cleaned_step_bytes=" + result.CleanedStepByteCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_removed_geometry_bytes=" + result.RemovedGeometryByteCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_clean_with_report_no_removed_geometry_ms=" + result.CleanWithReportWithoutRemovedGeometryMs.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_scoped_visual_oracle_ms=" + result.ScopedVisualOracleMs.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_visual_original_detections=" + result.VisualOracleOriginalDetectionCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_visual_residual_detections=" + result.VisualOracleResidualDetectionCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("speed_contract_visual_failures=" + result.VisualOracleFailureCount.ToString(CultureInfo.InvariantCulture));

            foreach (var kvp in result.CleanDetailTimings.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    "speed_contract_clean_detail_" +
                    kvp.Key +
                    "_ms=" +
                    kvp.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string ResolveOriginalStepPath(string modelNameOrPath)
        {
            if (!string.IsNullOrWhiteSpace(modelNameOrPath) && File.Exists(modelNameOrPath))
                return Path.GetFullPath(modelNameOrPath);

            string modelName = modelNameOrPath.EndsWith(".step", StringComparison.OrdinalIgnoreCase) ||
                modelNameOrPath.EndsWith(".stp", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(modelNameOrPath)
                    : modelNameOrPath;
            return Path.Combine(FindDataRoot(), "Original", modelName + ".step");
        }

        private static int RunVectorDetectionReportContractTests()
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            var failures = new List<string>();
            var logoDetails = new List<string>();
            int logoCount = 0;
            var logoMarkedKeys = new HashSet<string>(
                new[]
                {
                    "BUZ-SMD_4P-L7.5-W7.5-H2.5__x_plus",
                    "BUZ-TH_D9.0-H5.5-P4.0__z_plus",
                    "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51__z_plus",
                    "CONN-SMD_DF56_40S_0.3V_51__x_plus",
                    "CONN-TH_MR30PB-M30.A.G.Y__y_plus",
                    "CONN-TH_MR30PW-M30-G-Y__z_plus",
                    "HDMI-SMD_HDMI-001S__y_plus",
                    "LED-SMD_XL-3838UV2SA06G3__y_minus",
                    "LQFP-100_L14.0-W14.0-H1.4-LS16.0-P0.50__z_plus",
                    "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30__z_plus",
                    "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50__x_plus",
                    "TYPE-C-TH_TYPEC-215-ARP14__x_plus",
                    "USB-A-SMD_USB-212-BCW__y_plus",
                    "USB-A-TH_FUS264-FDSW3K__x_plus",
                    "USB-B-TH_USB-B10-BRW__x_plus"
                },
                StringComparer.OrdinalIgnoreCase);

            foreach (string markerPath in Directory.GetFiles(markedDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryParseMarkedModelAndView(markerPath, out string modelName, out string viewName))
                    continue;

                string stepPath = Path.Combine(originalDirectory, modelName + ".step");
                if (!File.Exists(stepPath))
                {
                    failures.Add("Missing original STEP: " + stepPath);
                    continue;
                }

                StepVectorWatermarkDetectionInput input =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                        File.ReadAllBytes(stepPath),
                        modelName,
                        viewName);
                string key = modelName + "__" + viewName;
                IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                    StepVectorWatermarkProjectionDetector.Detect(input, new StepTextLogoDetectionOptions());
                if (logoMarkedKeys.Contains(key) && detections.Count > 0)
                    logoCount++;
                if (logoMarkedKeys.Contains(key))
                {
                    logoDetails.Add(
                        modelName +
                        " " +
                        viewName +
                        " detections=" +
                        detections.Count.ToString(CultureInfo.InvariantCulture) +
                        " " +
                        string.Join("; ", detections.Select(detection =>
                            FormatBounds(detection.X, detection.Y, detection.Width, detection.Height) +
                            " score=" +
                            detection.Score.ToString("0.000", CultureInfo.InvariantCulture))));
                }
            }

            if (logoCount != 15)
                failures.Add("Expected 15 detected logo-marked views, got " + logoCount.ToString(CultureInfo.InvariantCulture) + ".");

            string reportCsvPath = Path.Combine(dataRoot, "CleanRunReport", "MarkedVsDetected", "marked-vs-detected.csv");
            if (File.Exists(reportCsvPath))
            {
                string[] reportLines = File.ReadAllLines(reportCsvPath);
                if (reportLines.Length > 1)
                {
                    string[] headerColumns = ParseCsvLine(reportLines[0]).ToArray();
                    int markedRectsIndex = Array.IndexOf(headerColumns, "MarkedRects");
                    int modelIndex = Array.IndexOf(headerColumns, "Model");
                    int viewIndex = Array.IndexOf(headerColumns, "View");
                    int logoBoxesIndex = Array.IndexOf(headerColumns, "LogoBoxes");
                    int textBoxesIndex = Array.IndexOf(headerColumns, "TextBoxes");
                    int cleanTextTextBoxesIndex = Array.IndexOf(headerColumns, "CleanTextTextBoxes");
                    int cleanTextCombinedStatusIndex = Array.IndexOf(headerColumns, "CleanTextCombinedStatus");
                    int cleanTextCombinedBoxesIndex = Array.IndexOf(headerColumns, "CleanTextCombinedBoxes");
                    int markedFileIndex = Array.IndexOf(headerColumns, "MarkedFile");
                    if (markedRectsIndex < 0 ||
                        modelIndex < 0 ||
                        viewIndex < 0 ||
                        logoBoxesIndex < 0 ||
                        textBoxesIndex < 0 ||
                        cleanTextTextBoxesIndex < 0 ||
                        cleanTextCombinedStatusIndex < 0 ||
                        cleanTextCombinedBoxesIndex < 0 ||
                        markedFileIndex < 0)
                    {
                        failures.Add("MarkedVsDetected CSV should include model/view, marked rects, marked file, logo/text boxes, CleanText combined status, and CleanText combined boxes columns.");
                    }
                    else
                    {
                        foreach (string line in reportLines.Skip(1))
                        {
                            string[] columns = ParseCsvLine(line).ToArray();
                            if (columns.Length <= Math.Max(cleanTextCombinedStatusIndex, cleanTextCombinedBoxesIndex))
                                continue;
                            if (!int.TryParse(columns[markedRectsIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int markedRects) ||
                                markedRects <= 0)
                            {
                                continue;
                            }

                            if (!string.Equals(columns[cleanTextCombinedStatusIndex], "matched", StringComparison.OrdinalIgnoreCase))
                                failures.Add("MarkedVsDetected CleanText combined status should be matched for all marked rows, got `" + columns[cleanTextCombinedStatusIndex] + "` in `" + line + "`.");
                            if (string.IsNullOrWhiteSpace(columns[cleanTextCombinedBoxesIndex]))
                                failures.Add("MarkedVsDetected CleanText combined boxes should not be empty in `" + line + "`.");

                            string model = columns[modelIndex];
                            string view = columns[viewIndex];
                            string logoBoxes = columns[logoBoxesIndex];
                            string textBoxes = columns[textBoxesIndex];
                            string cleanTextTextBoxes = columns[cleanTextTextBoxesIndex];
                            if (string.Equals(model, "BUZ-TH_D9.0-H5.5-P4.0", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "z_plus", StringComparison.OrdinalIgnoreCase) &&
                                logoBoxes.Contains("1291:649:109:347", StringComparison.Ordinal))
                            {
                                failures.Add("MarkedVsDetected should not report BUZ-TH_D9.0-H5.5-P4.0 z_plus expanded combined support as a logo box.");
                            }

                            if (string.Equals(model, "CONN-TH_MR30PB-M30.A.G.Y", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "y_plus", StringComparison.OrdinalIgnoreCase) &&
                                textBoxes.Contains("615:544:349:109", StringComparison.Ordinal))
                            {
                                failures.Add("MarkedVsDetected should not report CONN-TH_MR30PB-M30.A.G.Y y_plus watermark-sized support as a text box.");
                            }

                            if (string.Equals(model, "BUZ-SMD_4P-L7.5-W7.5-H2.5", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "x_plus", StringComparison.OrdinalIgnoreCase))
                            {
                                if (string.IsNullOrWhiteSpace(logoBoxes))
                                    failures.Add("MarkedVsDetected should expose a BUZ-SMD x_plus logo part instead of only text/combined boxes.");
                                if (textBoxes.Contains("459:661:416:131", StringComparison.Ordinal))
                                    failures.Add("MarkedVsDetected should not report BUZ-SMD x_plus full watermark support as a text box.");
                            }

                            if (string.Equals(model, "BUZ-SMD_4P-L7.5-W7.5-H2.5", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "z_minus", StringComparison.OrdinalIgnoreCase) &&
                                cleanTextTextBoxes.Contains(";"))
                            {
                                failures.Add("MarkedVsDetected should report BUZ-SMD z_minus clean text as one merged clean-text rectangle.");
                            }

                            if (string.Equals(model, "BUZ-TH_D9.0-H5.5-P4.0", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "z_plus", StringComparison.OrdinalIgnoreCase) &&
                                string.IsNullOrWhiteSpace(logoBoxes) &&
                                string.IsNullOrWhiteSpace(textBoxes))
                            {
                                failures.Add("MarkedVsDetected should expose BUZ-TH z_plus display parts, not only an orange combined rectangle.");
                            }

                            if (string.Equals(model, "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "z_minus", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(logoBoxes))
                            {
                                failures.Add("MarkedVsDetected should not report CONN-SMD_30P z_minus LCEDA text as a logo box.");
                            }

                            if (string.Equals(model, "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "z_plus", StringComparison.OrdinalIgnoreCase) &&
                                CountBoxes(textBoxes) < 2)
                            {
                                failures.Add("MarkedVsDetected should expose both LCEDA and EasyEDA text rows for CONN-SMD_30P z_plus.");
                            }

                            if (string.Equals(model, "CONN-SMD_DF56_40S_0.3V_51", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "x_plus", StringComparison.OrdinalIgnoreCase) &&
                                CountBoxes(textBoxes) < 2)
                            {
                                failures.Add("MarkedVsDetected should expose both LCEDA and EasyEDA text rows for CONN-SMD_DF56_40S_0.3V_51 x_plus.");
                            }

                            if (string.Equals(model, "CONN-TH_MR30PW-M30-G-Y", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(view, "z_plus", StringComparison.OrdinalIgnoreCase) &&
                                CountBoxes(textBoxes) < 2)
                            {
                                failures.Add("MarkedVsDetected should expose both LCEDA and EasyEDA text rows for CONN-TH_MR30PW-M30-G-Y z_plus.");
                            }
                        }
                    }

                    var markedModels = new HashSet<string>(
                        reportLines
                            .Skip(1)
                            .Select(line => ParseCsvLine(line).ToArray())
                            .Where(columns => columns.Length > Math.Max(modelIndex, markedRectsIndex))
                            .Where(columns => int.TryParse(columns[markedRectsIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0)
                            .Select(columns => columns[modelIndex]),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string line in reportLines.Skip(1))
                    {
                        string[] columns = ParseCsvLine(line).ToArray();
                        if (columns.Length <= Math.Max(Math.Max(modelIndex, markedRectsIndex), markedFileIndex))
                            continue;
                        if (!int.TryParse(columns[markedRectsIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int markedRects) ||
                            markedRects != 0)
                        {
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(columns[markedFileIndex]))
                            continue;

                        if (markedModels.Contains(columns[modelIndex]))
                            failures.Add("MarkedVsDetected should not add unmarked projection-side rows for marked model `" + columns[modelIndex] + "`.");
                    }
                }
            }

            string usbCStepPath = Path.Combine(originalDirectory, "USB-C-SMD_TYPE-C-6PIN-2MD-073.step");
            if (File.Exists(usbCStepPath))
            {
                StepVectorWatermarkDetectionInput usbCInput =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                        File.ReadAllBytes(usbCStepPath),
                        "USB-C-SMD_TYPE-C-6PIN-2MD-073",
                        "z_minus");
                IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                    StepVectorTextDetector.Detect(
                        usbCInput,
                        new StepTextLogoDetectionOptions { DetectArbitraryText = true });
                StepVectorWatermarkDetectionRegion lceda = detections.FirstOrDefault(detection =>
                    string.Equals(detection.Text, "LCEDA", StringComparison.OrdinalIgnoreCase));
                if (lceda == null)
                {
                    failures.Add(
                        "USB-C-SMD_TYPE-C-6PIN-2MD-073 z_minus should detect full LCEDA text, got [" +
                        string.Join(
                            "; ",
                            detections.Select(detection =>
                                detection.TemplateName +
                                "/" +
                                detection.Text +
                                " " +
                                FormatBounds(detection.X, detection.Y, detection.Width, detection.Height))) +
                        "].");
                }
            }
            else
            {
                failures.Add("Missing original STEP: " + usbCStepPath);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector detection report contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                foreach (string detail in logoDetails)
                    Console.Error.WriteLine("  " + detail);
                return 1;
            }

            Console.WriteLine("Vector detection report contract passed.");
            return 0;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var columns = new List<string>();
            if (line == null)
                return columns;

            var current = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    columns.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }

            columns.Add(current.ToString());
            return columns;
        }

        private static int CountBoxes(string boxes)
        {
            if (string.IsNullOrWhiteSpace(boxes))
                return 0;

            return boxes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static int RunVectorDetectionQualityContractTests()
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var failures = new List<string>();

            StepVectorWatermarkDetectionRegion buz =
                SingleVectorWatermark(originalDirectory, "BUZ-TH_D9.0-H5.5-P4.0", "z_plus", failures);
            if (buz != null)
            {
                if (buz.Y > 670 || buz.Height < 320)
                    failures.Add("BUZ-TH_D9.0-H5.5-P4.0 z_plus should include the stacked logo/text vector support, got " + FormatBounds(buz.X, buz.Y, buz.Width, buz.Height) + ".");
                if (string.Equals(buz.Kind, "logo", StringComparison.OrdinalIgnoreCase))
                    failures.Add("BUZ-TH_D9.0-H5.5-P4.0 z_plus stacked logo/text support should not be exposed as a logo-only region.");
            }

            AssertVectorTextLabels(originalDirectory, "CONN-TH_MR30PW-M30-G-Y", "z_plus", failures);
            AssertVectorTextLabels(originalDirectory, "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50", "x_plus", failures);
            AssertVectorTextLabels(originalDirectory, "LED-SMD_XL-3838UV2SA06G3", "y_minus", failures);
            AssertVectorLcedaCoverage(originalDirectory, "USB-A-SMD_USB-212-BCW", "y_plus", maxX: 1120, minWidth: 260, failures);
            AssertVectorLcedaCoverage(originalDirectory, "CONN-TH_MR30PW-M30-G-Y", "z_plus", maxX: 780, minWidth: 320, failures);
            AssertVectorLcedaCoverage(originalDirectory, "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50", "x_plus", maxX: 790, minWidth: 280, failures);
            AssertVectorLcedaCoverage(originalDirectory, "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51", "z_plus", maxX: 817, minWidth: 70, maxY: 852, failures);
            AssertVectorLcedaCoverage(originalDirectory, "CONN-SMD_DF56_40S_0.3V_51", "x_plus", maxX: 364, minWidth: 220, maxY: 990, failures);

            StepVectorWatermarkDetectionRegion usbC =
                SingleVectorWatermark(originalDirectory, "USB-C-SMD_TYPE-C-6PIN-2MD-073", "z_minus", failures);
            if (usbC != null)
            {
                if (!string.Equals(usbC.Text, "LCEDA", StringComparison.OrdinalIgnoreCase))
                    failures.Add("USB-C-SMD_TYPE-C-6PIN-2MD-073 z_minus should expose LCEDA text, got `" + usbC.Text + "`.");
                if (usbC.X > 470 || usbC.Y > 550 || usbC.Height > 195)
                    failures.Add("USB-C-SMD_TYPE-C-6PIN-2MD-073 z_minus should include the A stroke without connector geometry, got " + FormatBounds(usbC.X, usbC.Y, usbC.Width, usbC.Height) + ".");
            }

            IReadOnlyList<StepVectorWatermarkDetectionRegion> hdmiZPlus =
                DetectVectorWatermarks(originalDirectory, "HDMI-SMD_HDMI-001S", "z_plus", cleanText: true);
            if (hdmiZPlus.Count != 0)
            {
                failures.Add(
                    "HDMI-SMD_HDMI-001S z_plus should not have vector watermark detections, got " +
                    string.Join(
                        "; ",
                        hdmiZPlus.Select(detection =>
                            detection.Kind +
                            "/" +
                            detection.TemplateName +
                            " " +
                            FormatBounds(detection.X, detection.Y, detection.Width, detection.Height) +
                            " score=" +
                            detection.Score.ToString("0.000", CultureInfo.InvariantCulture))) +
                    ".");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector detection quality contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector detection quality contract passed.");
            return 0;
        }

        private static int RunVectorPrismCleanupContractTests()
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string verificationRoot = Path.Combine(dataRoot, "VectorPrismCleanupVerification");
            var failures = new List<string>();
            var fixtureNames = new[]
            {
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step"
            };

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing vector prism cleanup fixture: " + inputPath);
                    continue;
                }

                try
                {
                    byte[] originalBytes = File.ReadAllBytes(inputPath);
                    StepWatermarkCleanerReport cleanReport = StepWatermarkCleaner.CleanWithReport(
                        Encoding.Latin1.GetString(originalBytes),
                        new StepWatermarkCleanerOptions());
                    VerifyVectorPrismCleanupRemovesRetainedInnerBounds(
                        fixtureName,
                        originalBytes,
                        cleanReport.CleanedStep,
                        failures);

                    StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                        originalBytes,
                        fixtureName,
                        Path.Combine(verificationRoot, Path.GetFileNameWithoutExtension(fixtureName)));
                }
                catch (StepWatermarkCleanFailedException ex)
                {
                    failures.Add(
                        fixtureName +
                        " should clean only inside vector prism regions and pass post-clean verification. Report: " +
                        ex.ReportPath +
                        ". Failures: " +
                        string.Join(" | ", ex.Failures));
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector prism cleanup contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector prism cleanup contract passed.");
            return 0;
        }

        private static int RunVectorPrismRetainedBoundContractTests()
        {
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(
                dataRoot,
                "Original",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step");
            var failures = new List<string>();
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing vector prism retained-bound fixture: " + inputPath);
            }
            else
            {
                byte[] originalBytes = File.ReadAllBytes(inputPath);
                StepWatermarkCleanerReport cleanReport = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalBytes),
                    new StepWatermarkCleanerOptions());
                VerifyVectorPrismCleanupRemovesRetainedInnerBounds(
                    Path.GetFileName(inputPath),
                    originalBytes,
                    cleanReport.CleanedStep,
                    failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector prism retained-bound contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector prism retained-bound contract passed.");
            return 0;
        }

        private static int RunDetectionBoxCleanupContractTests()
        {
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(
                dataRoot,
                "Original",
                "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step");
            string verificationRoot = Path.Combine(dataRoot, "DetectionBoxCleanupVerification");
            var failures = new List<string>();
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing detection-box cleanup fixture: " + inputPath);
            }
            else
            {
                try
                {
                    byte[] originalBytes = File.ReadAllBytes(inputPath);
                    StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                        originalBytes,
                        Path.GetFileName(inputPath),
                        Path.Combine(verificationRoot, Path.GetFileNameWithoutExtension(inputPath)));

                    AssertNoResidualKnownVectorWatermark(
                        inputPath,
                        "x_plus",
                        failures);
                }
                catch (StepWatermarkCleanFailedException ex)
                {
                    failures.Add(
                        Path.GetFileName(inputPath) +
                        " should clean only inside the x_plus 3D detection box and pass post-clean verification. Report: " +
                        ex.ReportPath +
                        ". Failures: " +
                        string.Join(" | ", ex.Failures));
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Detection-box cleanup contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Detection-box cleanup contract passed.");
            return 0;
        }

        private static int RunResidualEdgeCleanupContractTests()
        {
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(
                dataRoot,
                "Original",
                "LED-SMD_XL-3838UV2SA06G3.step");
            string verificationRoot = Path.Combine(dataRoot, "ResidualEdgeCleanupVerification");
            var failures = new List<string>();
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing residual-edge cleanup fixture: " + inputPath);
            }
            else
            {
                try
                {
                    StepWatermarkCleanVerifierResult cleanResult = StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                        File.ReadAllBytes(inputPath),
                        Path.GetFileName(inputPath),
                        Path.Combine(verificationRoot, Path.GetFileNameWithoutExtension(inputPath)));
                    AssertNoResidualArbitraryTextInsideOriginalWatermarkRegion(
                        inputPath,
                        cleanResult.CleanStep,
                        "y_minus",
                        failures);
                }
                catch (StepWatermarkCleanFailedException ex)
                {
                    failures.Add(
                        Path.GetFileName(inputPath) +
                        " should remove residual text/logo edge contours inside detected boxes. Report: " +
                        ex.ReportPath +
                        ". Failures: " +
                        string.Join(" | ", ex.Failures));
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Residual-edge cleanup contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Residual-edge cleanup contract passed.");
            return 0;
        }

        private static void AssertNoResidualArbitraryTextInsideOriginalWatermarkRegion(
            string inputPath,
            byte[] cleanStep,
            string viewName,
            List<string> failures)
        {
            string modelName = Path.GetFileNameWithoutExtension(inputPath);
            StepVectorWatermarkDetectionInput originalInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    File.ReadAllBytes(inputPath),
                    modelName,
                    viewName);
            List<StepVectorWatermarkDetectionRegion> originalWatermarks =
                StepVectorWatermarkProjectionDetector
                    .Detect(originalInput, new StepTextLogoDetectionOptions { DetectArbitraryText = false })
                    .Where(IsKnownVectorWatermarkDetection)
                    .ToList();
            if (originalWatermarks.Count == 0)
            {
                failures.Add(Path.GetFileName(inputPath) + " should have an original vector watermark on " + viewName + ".");
                return;
            }

            StepVectorWatermarkDetectionInput cleanInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    cleanStep,
                    modelName,
                    viewName);
            List<StepVectorWatermarkDetectionRegion> residualText =
                StepVectorWatermarkProjectionDetector
                    .Detect(cleanInput, new StepTextLogoDetectionOptions { DetectArbitraryText = true })
                    .Where(detection => string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    .Where(detection => !IsKnownVectorWatermarkDetection(detection))
                    .Where(detection => originalWatermarks.Any(original => DetectionInsidePaddedRegion(detection, original, 8)))
                    .ToList();
            if (residualText.Count == 0)
                return;

            failures.Add(
                Path.GetFileName(inputPath) +
                " still has arbitrary text contours inside the original " +
                viewName +
                " watermark detection box after cleanup: " +
                string.Join("; ", residualText.Select(detection =>
                    FormatBounds(detection.X, detection.Y, detection.Width, detection.Height) +
                    " score=" +
                    detection.Score.ToString("0.000", CultureInfo.InvariantCulture))));
        }

        private static int RunVectorPrismTopologyRewriteContractTests()
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var cases = new[]
            {
                new { ModelName = "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50", ViewName = "x_plus" },
                new { ModelName = "LED-SMD_XL-3838UV2SA06G3", ViewName = "y_minus" },
                new { ModelName = "LED-SMD_XL-3838UV2SA06G3", ViewName = "z_minus" }
            };
            var failures = new List<string>();

            foreach (var testCase in cases)
            {
                string inputPath = Path.Combine(originalDirectory, testCase.ModelName + ".step");
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing topology rewrite fixture: " + inputPath);
                    continue;
                }

                byte[] originalBytes = File.ReadAllBytes(inputPath);
                StepWatermarkCleanerReport report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalBytes),
                    new StepWatermarkCleanerOptions());
                List<VectorPrismTopologyRewritePlan> plans = BuildResidualTopologyRewritePlans(
                    report.CleanedStep,
                    testCase.ModelName,
                    testCase.ViewName);

                if (plans.Count == 0)
                {
                    Console.WriteLine(
                        "rewrite-case model=" +
                        testCase.ModelName +
                        " view=" +
                        testCase.ViewName +
                        " residuals=0");
                    continue;
                }

                Console.WriteLine(
                    "rewrite-case model=" +
                    testCase.ModelName +
                    " view=" +
                    testCase.ViewName +
                    " residualPlans=" +
                    plans.Count.ToString(CultureInfo.InvariantCulture));
                foreach (VectorPrismTopologyRewritePlan plan in plans)
                    DumpVectorPrismTopologyRewritePlan(plan);

                failures.Add(
                    testCase.ModelName +
                    " " +
                    testCase.ViewName +
                    " still has residual vector topology requiring rewrite; see printed plan.");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Vector prism topology rewrite contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Vector prism topology rewrite contract passed.");
            return 0;
        }

        private static int RunStepEntityAppendContractTests()
        {
            var failures = new List<string>();
            Type cleanerType = typeof(StepWatermarkCleaner);
            Type stepDataType = cleanerType.GetNestedType("StepData", BindingFlags.NonPublic);
            if (stepDataType == null)
            {
                failures.Add("StepData private edit layer was not found.");
                return PrintStepEntityAppendContractResult(failures);
            }

            MethodInfo parseMethod = stepDataType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
            MethodInfo applyMethod = stepDataType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "ApplyDefinitionEdits", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 3);
            if (parseMethod == null)
                failures.Add("StepData.Parse is missing.");
            if (applyMethod == null)
                failures.Add("StepData.ApplyDefinitionEdits append overload is missing.");
            if (failures.Count > 0)
                return PrintStepEntityAppendContractResult(failures);

            const string originalStep =
                "ISO-10303-21;\r\n" +
                "HEADER;\r\n" +
                "ENDSEC;\r\n" +
                "DATA;\r\n" +
                "#7 = CARTESIAN_POINT('', (0., 0., 0.));\r\n" +
                "ENDSEC;\r\n" +
                "END-ISO-10303-21;\r\n";
            object stepData = parseMethod.Invoke(null, new object[] { originalStep });
            var appendedDefinitions = new[]
            {
                "CARTESIAN_POINT('', (1., 2., 3.))"
            };
            string editedStep = (string)applyMethod.Invoke(
                stepData,
                new object[] { null, null, appendedDefinitions });

            int appendedEntityIndex = editedStep.IndexOf(
                "#8 = CARTESIAN_POINT('', (1., 2., 3.)) ;",
                StringComparison.Ordinal);
            int dataEndIndex = editedStep.LastIndexOf("ENDSEC;", StringComparison.Ordinal);
            int fileEndIndex = editedStep.LastIndexOf("END-ISO-10303-21;", StringComparison.Ordinal);
            if (appendedEntityIndex < 0)
                failures.Add("Append overload should allocate the next STEP entity id and write the generated definition.");
            if (appendedEntityIndex >= 0 && dataEndIndex >= 0 && appendedEntityIndex > dataEndIndex)
                failures.Add("Generated STEP entity should be inserted inside the DATA section before ENDSEC.");
            if (dataEndIndex < 0 || fileEndIndex < 0 || dataEndIndex > fileEndIndex)
                failures.Add("Edited STEP should preserve DATA ENDSEC before END-ISO-10303-21.");
            if (!editedStep.Contains("#7 = CARTESIAN_POINT('', (0., 0., 0.));", StringComparison.Ordinal))
                failures.Add("Append-only edit should preserve existing entities.");

            return PrintStepEntityAppendContractResult(failures);
        }

        private static int PrintStepEntityAppendContractResult(List<string> failures)
        {
            if (failures.Count > 0)
            {
                Console.Error.WriteLine("STEP entity append contract failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("STEP entity append contract passed.");
            return 0;
        }

        private static List<VectorPrismTopologyRewritePlan> BuildResidualTopologyRewritePlans(
            string cleanedStep,
            string modelName,
            string viewName)
        {
            byte[] cleanedBytes = Encoding.Latin1.GetBytes(cleanedStep);
            StepVectorWatermarkDetectionInput input =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    cleanedBytes,
                    modelName,
                    viewName);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                StepVectorWatermarkProjectionDetector
                    .Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = false })
                    .Where(IsKnownVectorWatermarkDetection)
                    .ToList();
            if (detections.Count == 0)
                return new List<VectorPrismTopologyRewritePlan>();

            Dictionary<int, string> entities = ParseStepEntityDefinitions(cleanedStep);
            var boundsById = new Dictionary<int, StepBounds3d>();
            StepBounds3d modelBounds = GetActiveModelBounds(cleanedStep, entities, boundsById);
            List<ProjectedStepTopologySource> topology =
                BuildProjectedStepTopologySources(cleanedStep, viewName);
            var result = new List<VectorPrismTopologyRewritePlan>();
            foreach (StepVectorWatermarkDetectionRegion detection in detections)
            {
                VectorPrismDetectionBox box = CreateVectorPrismDetectionBox(input, detection, modelBounds, viewName);
                StepProjectionBounds2d projectionBounds = ToProjectionBounds(input, detection, paddingPixels: 6);
                List<ProjectedStepTopologySource> detectionTopology = topology
                    .Where(source => ProjectedSourceIntersectsRoi(source, projectionBounds, 0.02))
                    .ToList();
                List<ResidualPrimitiveSourceMatch> matches = input.Primitives
                    .Where(primitive => primitive.ImageBounds != null && VectorPrimitiveIntersectsDetection(primitive, detection))
                    .Select(primitive => MatchResidualPrimitiveSource(primitive, detectionTopology))
                    .ToList();
                result.Add(BuildVectorPrismTopologyRewritePlan(
                    detection,
                    box,
                    matches,
                    entities,
                    boundsById));
            }

            return result;
        }

        private static bool ProjectedSourceIntersectsRoi(
            ProjectedStepTopologySource source,
            StepProjectionBounds2d roi,
            double padding)
        {
            if (source == null || source.Points == null || source.Points.Count == 0)
                return false;

            double minU = source.Points.Min(point => point.U);
            double maxU = source.Points.Max(point => point.U);
            double minV = source.Points.Min(point => point.V);
            double maxV = source.Points.Max(point => point.V);
            return minU <= roi.UMax + padding &&
                maxU >= roi.UMin - padding &&
                minV <= roi.VMax + padding &&
                maxV >= roi.VMin - padding;
        }

        private static VectorPrismTopologyRewritePlan BuildVectorPrismTopologyRewritePlan(
            StepVectorWatermarkDetectionRegion detection,
            VectorPrismDetectionBox box,
            IReadOnlyList<ResidualPrimitiveSourceMatch> matches,
            IReadOnlyDictionary<int, string> entities,
            Dictionary<int, StepBounds3d> boundsById)
        {
            var plan = new VectorPrismTopologyRewritePlan
            {
                Box = box,
                TemplateName = detection.TemplateName ?? string.Empty,
                ResidualPrimitiveCount = matches.Count,
                UnknownPrimitiveCount = matches.Count(match => match.Source == null)
            };

            List<ProjectedStepTopologySource> sources = matches
                .Where(match => match.Source != null)
                .Select(match => match.Source)
                .GroupBy(source => source.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            ProjectedStepTopologySource dominantSource = sources
                .GroupBy(source => source.FaceId)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First())
                .FirstOrDefault();
            if (dominantSource != null)
            {
                plan.HostFaceId = dominantSource.FaceId;
                plan.OwnerId = FindClosedShellOwnerForFace(entities, dominantSource.FaceId);
            }

            foreach (ProjectedStepTopologySource source in sources)
            {
                StepBounds3d faceBounds = GetStepEntityBounds(source.FaceId, entities, boundsById);
                StepBounds3d boundBounds = GetStepEntityBounds(source.BoundId, entities, boundsById);
                string boundType = entities.TryGetValue(source.BoundId, out string boundDefinition)
                    ? GetStepEntityType(boundDefinition)
                    : string.Empty;

                if (StepBoundsInsideDetectionBox(faceBounds, box, 0.006))
                {
                    if (!plan.FaceIdsToRemove.Contains(source.FaceId))
                        plan.FaceIdsToRemove.Add(source.FaceId);
                    continue;
                }

                if (string.Equals(boundType, "FACE_BOUND", StringComparison.OrdinalIgnoreCase) &&
                    StepBoundsInsideDetectionBox(boundBounds, box, 0.006))
                {
                    if (!plan.FaceBoundsToRemove.TryGetValue(source.FaceId, out HashSet<int> boundIds))
                    {
                        boundIds = new HashSet<int>();
                        plan.FaceBoundsToRemove.Add(source.FaceId, boundIds);
                    }

                    boundIds.Add(source.BoundId);
                    continue;
                }

                plan.BlockedSources.Add(
                    "face=#" +
                    source.FaceId.ToString(CultureInfo.InvariantCulture) +
                    " bound=#" +
                    source.BoundId.ToString(CultureInfo.InvariantCulture) +
                    " edge=#" +
                    source.EdgeCurveId.ToString(CultureInfo.InvariantCulture) +
                    " crosses detection box");
            }

            if (plan.UnknownPrimitiveCount > Math.Max(1, plan.ResidualPrimitiveCount / 10))
                plan.BlockedSources.Add("unknown residual primitive provenance exceeds 10 percent");

            if (plan.FaceBoundsToRemove.Count > 0 && plan.FaceIdsToRemove.Count == 0 && plan.BlockedSources.Count == 0)
            {
                plan.RequiresPlanarFillPatch = false;
                plan.Reason = "contained retained FACE_BOUND removal should erase residual contours";
            }
            else if (plan.FaceIdsToRemove.Count > 0 && plan.BlockedSources.Count == 0)
            {
                plan.RequiresPlanarFillPatch = true;
                plan.Reason = "contained residual faces need replacement of the host surface patch";
            }
            else
            {
                plan.RequiresPlanarFillPatch = false;
                plan.Reason = "blocked by crossing or unknown residual topology";
            }

            plan.FaceIdsToRemove.Sort();
            return plan;
        }

        private static void DumpVectorPrismTopologyRewritePlan(VectorPrismTopologyRewritePlan plan)
        {
            Console.WriteLine(
                "rewrite-plan template=" +
                plan.TemplateName +
                " owner=#" +
                plan.OwnerId.ToString(CultureInfo.InvariantCulture) +
                " hostFace=#" +
                plan.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                " residualPrimitives=" +
                plan.ResidualPrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                " unknown=" +
                plan.UnknownPrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                " removeFaces=" +
                plan.FaceIdsToRemove.Count.ToString(CultureInfo.InvariantCulture) +
                " removeBounds=" +
                plan.FaceBoundsToRemove.Sum(kvp => kvp.Value.Count).ToString(CultureInfo.InvariantCulture) +
                " requiresPlanarFillPatch=" +
                plan.RequiresPlanarFillPatch.ToString(CultureInfo.InvariantCulture) +
                " reason=" +
                plan.Reason);
            Console.WriteLine(
                "box view=" +
                plan.Box.ViewName +
                " bounds=[" +
                plan.Box.Bounds.MinX.ToString("G6", CultureInfo.InvariantCulture) + "," +
                plan.Box.Bounds.MinY.ToString("G6", CultureInfo.InvariantCulture) + "," +
                plan.Box.Bounds.MinZ.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                plan.Box.Bounds.MaxX.ToString("G6", CultureInfo.InvariantCulture) + "," +
                plan.Box.Bounds.MaxY.ToString("G6", CultureInfo.InvariantCulture) + "," +
                plan.Box.Bounds.MaxZ.ToString("G6", CultureInfo.InvariantCulture) + "]");
            foreach (int faceId in plan.FaceIdsToRemove.Take(24))
                Console.WriteLine("remove-face #" + faceId.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<int, HashSet<int>> kvp in plan.FaceBoundsToRemove.OrderBy(kvp => kvp.Key))
            {
                Console.WriteLine(
                    "remove-bounds face=#" +
                    kvp.Key.ToString(CultureInfo.InvariantCulture) +
                    " bounds=" +
                    string.Join(",", kvp.Value.OrderBy(id => id).Take(24).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))));
            }

            foreach (string blockedSource in plan.BlockedSources.Take(24))
                Console.WriteLine("blocked " + blockedSource);
        }

        private static void AssertNoResidualKnownVectorWatermark(
            string inputPath,
            string viewName,
            List<string> failures)
        {
            byte[] originalBytes = File.ReadAllBytes(inputPath);
            StepWatermarkCleanerReport report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalBytes),
                new StepWatermarkCleanerOptions());
            StepVectorWatermarkDetectionInput cleanInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    Encoding.Latin1.GetBytes(report.CleanedStep),
                    Path.GetFileNameWithoutExtension(inputPath),
                    viewName);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> residuals =
                StepVectorWatermarkProjectionDetector
                    .Detect(cleanInput, new StepTextLogoDetectionOptions { DetectArbitraryText = false })
                    .Where(IsKnownVectorWatermarkDetection)
                    .ToList();
            if (residuals.Count == 0)
                return;

            failures.Add(
                Path.GetFileName(inputPath) +
                " still has known vector watermark detections after cleanup on " +
                viewName +
                ": " +
                string.Join(
                    "; ",
                    residuals.Select(detection =>
                        detection.Kind +
                        "/" +
                        detection.TemplateName +
                        " " +
                        FormatBounds(detection.X, detection.Y, detection.Width, detection.Height) +
                        " score=" +
                        detection.Score.ToString("0.000", CultureInfo.InvariantCulture))));
        }

        private static void VerifyVectorPrismCleanupRemovesRetainedInnerBounds(
            string fixtureName,
            byte[] originalBytes,
            string cleanedStep,
            List<string> failures)
        {
            string modelName = Path.GetFileNameWithoutExtension(fixtureName);
            StepVectorWatermarkDetectionInput input =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    originalBytes,
                    modelName,
                    "z_plus");
            StepVectorWatermarkDetectionRegion detection = StepVectorWatermarkProjectionDetector
                .Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = false })
                .Where(IsKnownVectorWatermarkDetection)
                .OrderByDescending(region => region.Score)
                .FirstOrDefault();
            if (detection == null)
            {
                failures.Add(fixtureName + " should have a z_plus vector watermark detection for retained-bound cleanup verification.");
                return;
            }

            StepProjectionBounds2d roi = ToProjectionBounds(input, detection, paddingPixels: 6);
            IReadOnlyDictionary<int, string> originalEntities = ParseStepEntityDefinitions(Encoding.Latin1.GetString(originalBytes));
            IReadOnlyDictionary<int, string> cleanedEntities = ParseStepEntityDefinitions(cleanedStep);
            List<int> originalBounds = FindRetainedInnerFaceBoundsInsideProjectionRoi(originalEntities, roi);
            List<int> cleanedBounds = FindRetainedInnerFaceBoundsInsideProjectionRoi(cleanedEntities, roi);
            if (originalBounds.Count == 0)
            {
                failures.Add(fixtureName + " retained-bound cleanup verification did not find any original z_plus inner bounds inside the vector ROI.");
                return;
            }

            if (cleanedBounds.Count > 0)
            {
                failures.Add(
                    fixtureName +
                    " retained " +
                    cleanedBounds.Count.ToString(CultureInfo.InvariantCulture) +
                    " inner face bounds inside the z_plus vector prism ROI after cleanup: " +
                    string.Join(", ", cleanedBounds.Take(12).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))) +
                    ".");
            }
        }

        private static bool IsKnownVectorWatermarkDetection(StepVectorWatermarkDetectionRegion detection)
        {
            if (detection == null)
                return false;

            string templateName = detection.TemplateName ?? string.Empty;
            return templateName.IndexOf("LCEDA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                templateName.IndexOf("EasyEDA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                templateName.IndexOf("easyeda-logo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(detection.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase);
        }

        private static StepProjectionBounds2d ToProjectionBounds(
            StepVectorWatermarkDetectionInput input,
            StepVectorWatermarkDetectionRegion detection,
            int paddingPixels)
        {
            StepVectorWatermarkImageMapping mapping = input.ImageMapping;
            double scale = Math.Max(mapping.Scale, 0.000001);
            double left = mapping.UMin + (detection.X - mapping.PaddingPixels - paddingPixels) / scale;
            double right = mapping.UMin + (detection.X + detection.Width - mapping.PaddingPixels + paddingPixels) / scale;
            double top = mapping.VMin + (input.ImageHeight - mapping.PaddingPixels - detection.Y + paddingPixels) / scale;
            double bottom = mapping.VMin + (input.ImageHeight - mapping.PaddingPixels - detection.Y - detection.Height - paddingPixels) / scale;
            return new StepProjectionBounds2d
            {
                UMin = Math.Min(left, right),
                UMax = Math.Max(left, right),
                VMin = Math.Min(bottom, top),
                VMax = Math.Max(bottom, top)
            };
        }

        private static VectorPrismDetectionBox CreateVectorPrismDetectionBox(
            StepVectorWatermarkDetectionInput input,
            StepVectorWatermarkDetectionRegion detection,
            StepBounds3d modelBounds,
            string viewName)
        {
            if (!TryGetVectorViewAxes(viewName, out int uAxis, out int uSign, out int vAxis, out int vSign))
                return new VectorPrismDetectionBox { ViewName = viewName, Bounds = modelBounds };

            StepProjectionBounds2d projectionBounds = ToProjectionBounds(input, detection, paddingPixels: 6);
            var box = new StepBounds3d();
            double u0 = uSign > 0 ? projectionBounds.UMin : -projectionBounds.UMax;
            double u1 = uSign > 0 ? projectionBounds.UMax : -projectionBounds.UMin;
            double v0 = vSign > 0 ? projectionBounds.VMin : -projectionBounds.VMax;
            double v1 = vSign > 0 ? projectionBounds.VMax : -projectionBounds.VMin;
            int depthAxis = Enumerable.Range(0, 3).First(axis => axis != uAxis && axis != vAxis);
            double[] min = { modelBounds.MinX, modelBounds.MinY, modelBounds.MinZ };
            double[] max = { modelBounds.MaxX, modelBounds.MaxY, modelBounds.MaxZ };
            min[uAxis] = Math.Min(u0, u1);
            max[uAxis] = Math.Max(u0, u1);
            min[vAxis] = Math.Min(v0, v1);
            max[vAxis] = Math.Max(v0, v1);
            box.Include(min[0], min[1], min[2]);
            box.Include(max[0], max[1], max[2]);
            return new VectorPrismDetectionBox
            {
                ViewName = viewName,
                UAxis = uAxis,
                VAxis = vAxis,
                DepthAxis = depthAxis,
                Bounds = box
            };
        }

        private static StepBounds3d GetActiveModelBounds(
            string stepText,
            IReadOnlyDictionary<int, string> entities,
            Dictionary<int, StepBounds3d> boundsById)
        {
            var result = new StepBounds3d();
            foreach (int faceId in GetActiveAdvancedFaceIds(stepText))
                result.Include(GetStepEntityBounds(faceId, entities, boundsById));

            return result;
        }

        private static bool StepBoundsInsideDetectionBox(
            StepBounds3d inner,
            VectorPrismDetectionBox box,
            double padding)
        {
            if (!inner.HasValue || box == null || box.Bounds == null || !box.Bounds.HasValue)
                return false;

            return inner.MinX >= box.Bounds.MinX - padding &&
                inner.MaxX <= box.Bounds.MaxX + padding &&
                inner.MinY >= box.Bounds.MinY - padding &&
                inner.MaxY <= box.Bounds.MaxY + padding &&
                inner.MinZ >= box.Bounds.MinZ - padding &&
                inner.MaxZ <= box.Bounds.MaxZ + padding;
        }

        private static int FindClosedShellOwnerForFace(
            IReadOnlyDictionary<int, string> entities,
            int faceId)
        {
            string needle = "#" + faceId.ToString(CultureInfo.InvariantCulture);
            foreach (KeyValuePair<int, string> entity in entities.OrderBy(kvp => kvp.Key))
            {
                if (!entity.Value.StartsWith("CLOSED_SHELL", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ExtractStepReferenceIds(entity.Value).Contains(faceId))
                    return entity.Key;

                if (entity.Value.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return entity.Key;
            }

            return 0;
        }

        private static string GetStepEntityType(string definition)
        {
            Match match = Regex.Match(
                definition ?? string.Empty,
                @"^\s*([A-Z0-9_]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        }

        private static List<int> FindRetainedInnerFaceBoundsInsideProjectionRoi(
            IReadOnlyDictionary<int, string> entities,
            StepProjectionBounds2d roi)
        {
            var boundsById = new Dictionary<int, StepBounds3d>();
            var retainedBounds = new SortedSet<int>();

            foreach (KeyValuePair<int, string> entity in entities)
            {
                if (!entity.Value.StartsWith("ADVANCED_FACE", StringComparison.OrdinalIgnoreCase))
                    continue;

                List<int> faceBounds = ExtractStepReferenceIds(entity.Value)
                    .TakeWhile(referenceId => !entities.TryGetValue(referenceId, out string referencedDefinition) ||
                        !referencedDefinition.StartsWith("PLANE", StringComparison.OrdinalIgnoreCase))
                    .Where(referenceId => entities.TryGetValue(referenceId, out string referencedDefinition) &&
                        referencedDefinition.StartsWith("FACE_BOUND", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (faceBounds.Count == 0)
                    continue;

                StepBounds3d faceBox = GetStepEntityBounds(entity.Key, entities, boundsById);
                if (!ProjectedBoundsInsideRoi(faceBox, roi))
                    continue;

                foreach (int boundId in faceBounds)
                {
                    StepBounds3d boundBox = GetStepEntityBounds(boundId, entities, boundsById);
                    if (ProjectedBoundsInsideRoi(boundBox, roi))
                        retainedBounds.Add(boundId);
                }
            }

            return retainedBounds.ToList();
        }

        private static List<int> FindActiveAdvancedFacesInsideProjectionRoiAndDepth(
            IReadOnlyDictionary<int, string> entities,
            StepProjectionBounds2d roi,
            double minZ,
            double maxZ)
        {
            var boundsById = new Dictionary<int, StepBounds3d>();
            var result = new SortedSet<int>();
            foreach (int faceId in GetActiveAdvancedFaceIds(entities))
            {
                StepBounds3d faceBox = GetStepEntityBounds(faceId, entities, boundsById);
                if (!faceBox.HasValue)
                    continue;

                if (!ProjectedBoundsInsideRoi(faceBox, roi, minZ, maxZ))
                    continue;

                result.Add(faceId);
            }

            return result.ToList();
        }

        private static StepVectorWatermarkDetectionRegion SingleVectorWatermark(
            string originalDirectory,
            string modelName,
            string viewName,
            List<string> failures)
        {
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections =
                DetectVectorWatermarks(originalDirectory, modelName, viewName, cleanText: true);
            if (detections.Count != 1)
            {
                failures.Add(
                    modelName +
                    " " +
                    viewName +
                    " expected one CleanText vector detection, got " +
                    detections.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
                return null;
            }

            return detections[0];
        }

        private static void AssertVectorTextLabels(
            string originalDirectory,
            string modelName,
            string viewName,
            List<string> failures)
        {
            StepVectorWatermarkDetectionInput input = ProjectVectorWatermarkInput(originalDirectory, modelName, viewName);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> textDetections =
                StepVectorTextDetector.Detect(
                    input,
                    new StepTextLogoDetectionOptions { DetectArbitraryText = true });
            var labels = new HashSet<string>(
                textDetections
                    .Select(detection => detection.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)),
                StringComparer.OrdinalIgnoreCase);
            if (!labels.Contains("EasyEDA") || !labels.Contains("LCEDA"))
            {
                failures.Add(
                    modelName +
                    " " +
                    viewName +
                    " should expose EasyEDA and LCEDA text labels, got [" +
                    string.Join("; ", labels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase)) +
                    "].");
            }
        }

        private static void AssertVectorLcedaCoverage(
            string originalDirectory,
            string modelName,
            string viewName,
            int maxX,
            int minWidth,
            List<string> failures)
        {
            AssertVectorLcedaCoverage(originalDirectory, modelName, viewName, maxX, minWidth, maxY: int.MaxValue, failures);
        }

        private static void AssertVectorLcedaCoverage(
            string originalDirectory,
            string modelName,
            string viewName,
            int maxX,
            int minWidth,
            int maxY,
            List<string> failures)
        {
            StepVectorWatermarkDetectionInput input = ProjectVectorWatermarkInput(originalDirectory, modelName, viewName);
            StepVectorWatermarkDetectionRegion lceda =
                StepVectorTextDetector
                    .Detect(input, new StepTextLogoDetectionOptions { DetectArbitraryText = true })
                    .Where(detection => string.Equals(detection.Text, "LCEDA", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(detection => detection.Width * detection.Height)
                    .FirstOrDefault();
            if (lceda == null)
            {
                failures.Add(modelName + " " + viewName + " should expose an LCEDA text detection.");
                return;
            }

            if (lceda.X > maxX || lceda.Width < minWidth || lceda.Y > maxY)
            {
                failures.Add(
                    modelName +
                    " " +
                    viewName +
                    " should select the full LCEDA text, got " +
                    FormatBounds(lceda.X, lceda.Y, lceda.Width, lceda.Height) +
                    ".");
            }
        }

        private static IReadOnlyList<StepVectorWatermarkDetectionRegion> DetectVectorWatermarks(
            string originalDirectory,
            string modelName,
            string viewName,
            bool cleanText)
        {
            StepVectorWatermarkDetectionInput input = ProjectVectorWatermarkInput(originalDirectory, modelName, viewName);
            return StepVectorWatermarkProjectionDetector.Detect(
                input,
                new StepTextLogoDetectionOptions { DetectArbitraryText = cleanText });
        }

        private static StepVectorWatermarkDetectionInput ProjectVectorWatermarkInput(
            string originalDirectory,
            string modelName,
            string viewName)
        {
            string stepPath = Path.Combine(originalDirectory, modelName + ".step");
            if (!File.Exists(stepPath))
                throw new FileNotFoundException("Missing original STEP for vector detection.", stepPath);

            return StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                File.ReadAllBytes(stepPath),
                modelName,
                viewName);
        }

        private static IReadOnlyList<StepVectorWatermarkDetectionRegion> FilterReportLogoDetections(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> logos,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> texts)
        {
            if (logos == null || logos.Count == 0)
                return Array.Empty<StepVectorWatermarkDetectionRegion>();
            if (texts == null || texts.Count == 0)
                return logos;

            return logos
                .Where(logo => !texts.Any(text =>
                    !string.IsNullOrWhiteSpace(text.Text) &&
                    DetectionOverlapRatio(logo, text) >= 0.55))
                .ToList();
        }

        private static double DetectionOverlapRatio(
            StepVectorWatermarkDetectionRegion left,
            StepVectorWatermarkDetectionRegion right)
        {
            int x0 = Math.Max(left.X, right.X);
            int y0 = Math.Max(left.Y, right.Y);
            int x1 = Math.Min(left.X + left.Width, right.X + right.Width);
            int y1 = Math.Min(left.Y + left.Height, right.Y + right.Height);
            int intersection = x1 <= x0 || y1 <= y0 ? 0 : (x1 - x0) * (y1 - y0);
            int leftArea = Math.Max(1, left.Width * left.Height);
            int rightArea = Math.Max(1, right.Width * right.Height);
            return intersection / (double)Math.Min(leftArea, rightArea);
        }

        private static bool DetectionInsidePaddedRegion(
            StepVectorWatermarkDetectionRegion inner,
            StepVectorWatermarkDetectionRegion outer,
            int paddingPixels)
        {
            return inner.X >= outer.X - paddingPixels &&
                inner.Y >= outer.Y - paddingPixels &&
                inner.X + inner.Width <= outer.X + outer.Width + paddingPixels &&
                inner.Y + inner.Height <= outer.Y + outer.Height + paddingPixels;
        }

        private static void DumpVectorPrimitivesInsideDetections(
            StepVectorWatermarkDetectionInput input,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections)
        {
            if (input == null || input.Primitives == null || detections == null || detections.Count == 0)
                return;

            foreach (StepVectorWatermarkDetectionRegion detection in detections)
            {
                Console.WriteLine(
                    "primitives-in=" +
                    detection.Kind +
                    " template=" +
                    (detection.TemplateName ?? string.Empty) +
                    " box=" +
                    FormatBounds(detection.X, detection.Y, detection.Width, detection.Height));
                int printed = 0;
                for (int i = 0; i < input.Primitives.Count; i++)
                {
                    StepVectorWatermarkPrimitive primitive = input.Primitives[i];
                    if (primitive.ImageBounds == null || !VectorPrimitiveIntersectsDetection(primitive, detection))
                        continue;

                    Console.WriteLine(
                        "  primitive index=" +
                        i.ToString(CultureInfo.InvariantCulture) +
                        " source=" +
                        primitive.SourceIndex.ToString(CultureInfo.InvariantCulture) +
                        " kind=" +
                        primitive.Kind +
                        " original=" +
                        (primitive.OriginalKind ?? string.Empty) +
                        " category=" +
                        (primitive.Category ?? string.Empty) +
                        " model=[" +
                        primitive.Bounds.Left.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        primitive.Bounds.Bottom.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                        primitive.Bounds.Right.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        primitive.Bounds.Top.ToString("G6", CultureInfo.InvariantCulture) + "] image=[" +
                        primitive.ImageBounds.Left.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        primitive.ImageBounds.Bottom.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                        primitive.ImageBounds.Right.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        primitive.ImageBounds.Top.ToString("G6", CultureInfo.InvariantCulture) + "]");
                    printed++;
                    if (printed >= 40)
                    {
                        Console.WriteLine("  ... truncated");
                        break;
                    }
                }
            }
        }

        private static bool VectorPrimitiveIntersectsDetection(
            StepVectorWatermarkPrimitive primitive,
            StepVectorWatermarkDetectionRegion detection)
        {
            double left = detection.X;
            double right = detection.X + detection.Width;
            double top = detection.Y;
            double bottom = detection.Y + detection.Height;
            return primitive.ImageBounds.Left <= right &&
                primitive.ImageBounds.Right >= left &&
                primitive.ImageBounds.Bottom >= top &&
                primitive.ImageBounds.Top <= bottom;
        }

        private static List<ProjectedStepTopologySource> BuildProjectedStepTopologySources(
            string stepText,
            string viewName)
        {
            if (!TryGetVectorViewAxes(viewName, out int uAxis, out int uSign, out int vAxis, out int vSign))
                return new List<ProjectedStepTopologySource>();

            Dictionary<int, string> entities = ParseStepEntityDefinitions(stepText);
            var activeFaceIds = new HashSet<int>(GetActiveAdvancedFaceIds(stepText));
            var result = new List<ProjectedStepTopologySource>();
            foreach (int faceId in activeFaceIds.OrderBy(id => id))
            {
                if (!entities.TryGetValue(faceId, out string faceDefinition))
                    continue;

                List<int> faceReferences = ExtractStepReferenceIds(faceDefinition);
                if (faceReferences.Count < 2)
                    continue;

                for (int index = 0; index < faceReferences.Count - 1; index++)
                {
                    int boundId = faceReferences[index];
                    if (!entities.TryGetValue(boundId, out string boundDefinition) ||
                        (!boundDefinition.StartsWith("FACE_BOUND", StringComparison.OrdinalIgnoreCase) &&
                         !boundDefinition.StartsWith("FACE_OUTER_BOUND", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    int edgeLoopId = ExtractStepReferenceIds(boundDefinition)
                        .FirstOrDefault(id => entities.TryGetValue(id, out string definition) &&
                            definition.StartsWith("EDGE_LOOP", StringComparison.OrdinalIgnoreCase));
                    if (edgeLoopId == 0 || !entities.TryGetValue(edgeLoopId, out string edgeLoopDefinition))
                        continue;

                    foreach (int orientedEdgeId in ExtractStepReferenceIds(edgeLoopDefinition))
                    {
                        if (!entities.TryGetValue(orientedEdgeId, out string orientedEdgeDefinition) ||
                            !orientedEdgeDefinition.StartsWith("ORIENTED_EDGE", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int edgeCurveId = ExtractStepReferenceIds(orientedEdgeDefinition)
                            .FirstOrDefault(id => entities.TryGetValue(id, out string definition) &&
                                definition.StartsWith("EDGE_CURVE", StringComparison.OrdinalIgnoreCase));
                        if (edgeCurveId == 0)
                            continue;

                        List<ProjectedStepPoint> points = BuildProjectedEdgeCurvePoints(
                            entities,
                            edgeCurveId,
                            uAxis,
                            uSign,
                            vAxis,
                            vSign);
                        if (points.Count < 2)
                            continue;

                        bool sameSense = !orientedEdgeDefinition.TrimEnd().EndsWith(".F.)", StringComparison.OrdinalIgnoreCase);
                        if (!sameSense)
                            points.Reverse();

                        result.Add(new ProjectedStepTopologySource
                        {
                            FaceId = faceId,
                            BoundId = boundId,
                            EdgeCurveId = edgeCurveId,
                            Points = points
                        });
                    }
                }
            }

            return result;
        }

        private static List<ProjectedStepPoint> BuildProjectedEdgeCurvePoints(
            IReadOnlyDictionary<int, string> entities,
            int edgeCurveId,
            int uAxis,
            int uSign,
            int vAxis,
            int vSign)
        {
            if (!entities.TryGetValue(edgeCurveId, out string edgeCurveDefinition))
                return new List<ProjectedStepPoint>();

            var result = new List<ProjectedStepPoint>();
            List<int> references = ExtractStepReferenceIds(edgeCurveDefinition);
            foreach (int referenceId in references)
            {
                if (!entities.TryGetValue(referenceId, out string referenceDefinition))
                    continue;

                if (referenceDefinition.StartsWith("VERTEX_POINT", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetProjectedVertexPoint(entities, referenceId, uAxis, uSign, vAxis, vSign, out ProjectedStepPoint point))
                        AddProjectedPoint(result, point);
                    continue;
                }

                if (referenceDefinition.IndexOf("B_SPLINE_CURVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    referenceDefinition.StartsWith("POLYLINE", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (int pointId in ExtractStepReferenceIds(referenceDefinition))
                    {
                        if (TryGetProjectedCartesianPoint(entities, pointId, uAxis, uSign, vAxis, vSign, out ProjectedStepPoint point))
                            AddProjectedPoint(result, point);
                    }
                }
            }

            if (result.Count < 2)
            {
                foreach (int pointId in BuildStepReferenceClosure(edgeCurveId, entities)
                    .Where(id => entities.TryGetValue(id, out string definition) &&
                        definition.StartsWith("CARTESIAN_POINT", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(id => id))
                {
                    if (TryGetProjectedCartesianPoint(entities, pointId, uAxis, uSign, vAxis, vSign, out ProjectedStepPoint point))
                        AddProjectedPoint(result, point);
                }
            }

            return result;
        }

        private static ResidualPrimitiveSourceMatch MatchResidualPrimitiveSource(
            StepVectorWatermarkPrimitive primitive,
            IReadOnlyList<ProjectedStepTopologySource> topology)
        {
            IReadOnlyList<StepVectorWatermarkPoint> samples = primitive.SampledPoints ?? Array.Empty<StepVectorWatermarkPoint>();
            if (samples.Count < 2 || topology.Count == 0)
                return new ResidualPrimitiveSourceMatch();

            const double endpointTolerance = 0.005;
            const double sampleTolerance = 0.005;
            ProjectedStepTopologySource bestSource = null;
            double bestScore = double.PositiveInfinity;
            foreach (ProjectedStepTopologySource source in topology)
            {
                double firstDistance = DistanceToPolyline(samples[0].X, samples[0].Y, source.Points);
                double lastDistance = DistanceToPolyline(samples[samples.Count - 1].X, samples[samples.Count - 1].Y, source.Points);
                if (firstDistance > endpointTolerance || lastDistance > endpointTolerance)
                    continue;

                int onPolylineCount = 0;
                double totalDistance = 0.0;
                foreach (StepVectorWatermarkPoint sample in samples)
                {
                    double distance = DistanceToPolyline(sample.X, sample.Y, source.Points);
                    totalDistance += distance;
                    if (distance <= sampleTolerance)
                        onPolylineCount++;
                }

                if (onPolylineCount < Math.Ceiling(samples.Count * 0.80))
                    continue;

                double score = totalDistance / Math.Max(samples.Count, 1);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestSource = source;
                }
            }

            return new ResidualPrimitiveSourceMatch
            {
                Source = bestSource,
                AverageDistance = bestScore
            };
        }

        private static bool TryGetVectorViewAxes(
            string viewName,
            out int uAxis,
            out int uSign,
            out int vAxis,
            out int vSign)
        {
            switch ((viewName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "x_plus":
                    uAxis = 1; uSign = 1; vAxis = 2; vSign = 1; return true;
                case "x_minus":
                    uAxis = 1; uSign = -1; vAxis = 2; vSign = 1; return true;
                case "y_plus":
                    uAxis = 0; uSign = -1; vAxis = 2; vSign = 1; return true;
                case "y_minus":
                    uAxis = 0; uSign = 1; vAxis = 2; vSign = 1; return true;
                case "z_plus":
                    uAxis = 0; uSign = 1; vAxis = 1; vSign = 1; return true;
                case "z_minus":
                    uAxis = 0; uSign = -1; vAxis = 1; vSign = 1; return true;
                default:
                    uAxis = -1; uSign = 1; vAxis = -1; vSign = 1; return false;
            }
        }

        private static bool TryGetProjectedVertexPoint(
            IReadOnlyDictionary<int, string> entities,
            int vertexId,
            int uAxis,
            int uSign,
            int vAxis,
            int vSign,
            out ProjectedStepPoint point)
        {
            point = default;
            if (!entities.TryGetValue(vertexId, out string vertexDefinition))
                return false;

            int pointId = ExtractStepReferenceIds(vertexDefinition)
                .FirstOrDefault(id => entities.TryGetValue(id, out string definition) &&
                    definition.StartsWith("CARTESIAN_POINT", StringComparison.OrdinalIgnoreCase));
            return pointId != 0 &&
                TryGetProjectedCartesianPoint(entities, pointId, uAxis, uSign, vAxis, vSign, out point);
        }

        private static bool TryGetProjectedCartesianPoint(
            IReadOnlyDictionary<int, string> entities,
            int pointId,
            int uAxis,
            int uSign,
            int vAxis,
            int vSign,
            out ProjectedStepPoint point)
        {
            point = default;
            if (!entities.TryGetValue(pointId, out string definition) ||
                !definition.StartsWith("CARTESIAN_POINT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            List<double> values = Regex.Matches(
                    definition,
                    @"[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?",
                    RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
                .ToList();
            if (values.Count < 3)
                return false;

            double[] coordinates =
            {
                values[values.Count - 3],
                values[values.Count - 2],
                values[values.Count - 1]
            };
            point = new ProjectedStepPoint(
                coordinates[uAxis] * uSign,
                coordinates[vAxis] * vSign);
            return true;
        }

        private static void AddProjectedPoint(List<ProjectedStepPoint> points, ProjectedStepPoint point)
        {
            if (points.Count == 0 || Distance(points[points.Count - 1].U, points[points.Count - 1].V, point.U, point.V) > 0.0000001)
                points.Add(point);
        }

        private static double DistanceToPolyline(double u, double v, IReadOnlyList<ProjectedStepPoint> points)
        {
            if (points == null || points.Count == 0)
                return double.PositiveInfinity;

            if (points.Count == 1)
                return Distance(u, v, points[0].U, points[0].V);

            double best = double.PositiveInfinity;
            for (int i = 0; i + 1 < points.Count; i++)
                best = Math.Min(best, DistanceToSegment(u, v, points[i], points[i + 1]));

            return best;
        }

        private static double DistanceToSegment(double u, double v, ProjectedStepPoint a, ProjectedStepPoint b)
        {
            double du = b.U - a.U;
            double dv = b.V - a.V;
            double lengthSquared = du * du + dv * dv;
            if (lengthSquared <= 0.0)
                return Distance(u, v, a.U, a.V);

            double t = ((u - a.U) * du + (v - a.V) * dv) / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Distance(u, v, a.U + t * du, a.V + t * dv);
        }

        private static double Distance(double leftU, double leftV, double rightU, double rightV)
        {
            double du = leftU - rightU;
            double dv = leftV - rightV;
            return Math.Sqrt(du * du + dv * dv);
        }

        private static void DumpVectorRegions(
            string label,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections)
        {
            Console.WriteLine(label + "=" + detections.Count.ToString(CultureInfo.InvariantCulture));
            foreach (StepVectorWatermarkDetectionRegion detection in detections)
            {
                Console.WriteLine(
                    "  " +
                    detection.Kind +
                    " template=" +
                    detection.TemplateName +
                    " text=" +
                    detection.Text +
                    " box=" +
                    FormatBounds(detection.X, detection.Y, detection.Width, detection.Height) +
                    " score=" +
                    detection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    " chamfer=" +
                    detection.ChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    " prims=" +
                    detection.PrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                    " orient=" +
                    detection.OrientationDegrees.ToString(CultureInfo.InvariantCulture) +
                    " logoOrient=" +
                    detection.LogoOrientationDegrees.ToString(CultureInfo.InvariantCulture) +
                    " textOrient=" +
                    detection.TextOrientationDegrees.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static StepVectorWatermarkDetectionInput CreateTemplateVectorTextInput(
            string templateName,
            int rotationDegrees,
            double offsetX,
            double offsetY,
            double scale)
        {
            StepWatermarkTemplate template = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .First(candidate => string.Equals(candidate.Name, templateName, StringComparison.OrdinalIgnoreCase));
            var primitives = new List<StepVectorWatermarkPrimitive>();
            foreach (StepWatermarkTemplatePoint point in template.EdgePoints)
            {
                RotateTemplateSmokePoint(
                    point.X * scale,
                    point.Y * scale,
                    (template.Width - 1) * scale,
                    (template.Height - 1) * scale,
                    rotationDegrees,
                    out double x,
                    out double y);
                double px = offsetX + x;
                double py = offsetY + y;
                primitives.Add(CreateVectorPrimitive(
                    new StepVectorWatermarkPoint(px, py),
                    new StepVectorWatermarkPoint(px + 0.8, py)));
            }

            return CreateVectorInput("known-template-text", primitives);
        }

        private static StepVectorWatermarkDetectionInput CreateArbitraryVectorTextInput()
        {
            var primitives = new List<StepVectorWatermarkPrimitive>();
            double x = 60;
            double y = 80;
            for (int glyph = 0; glyph < 6; glyph++)
            {
                double gx = x + glyph * 15;
                primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y), new StepVectorWatermarkPoint(gx, y + 24)));
                primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y), new StepVectorWatermarkPoint(gx + 8, y)));
                primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y + 12), new StepVectorWatermarkPoint(gx + 7, y + 12)));
                primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y + 24), new StepVectorWatermarkPoint(gx + 9, y + 24)));
                if (glyph % 2 == 0)
                    primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx + 9, y + 2), new StepVectorWatermarkPoint(gx + 9, y + 22)));
            }

            return CreateVectorInput("arbitrary-text", primitives);
        }

        private static StepVectorWatermarkDetectionInput CreateSplitArbitraryVectorTextInput()
        {
            var primitives = new List<StepVectorWatermarkPrimitive>();
            double x = 60;
            double y = 80;
            for (int group = 0; group < 2; group++)
            {
                double baseX = x + group * 150;
                for (int glyph = 0; glyph < 6; glyph++)
                {
                    double gx = baseX + glyph * 15;
                    primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y), new StepVectorWatermarkPoint(gx, y + 24)));
                    primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y), new StepVectorWatermarkPoint(gx + 8, y)));
                    primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y + 12), new StepVectorWatermarkPoint(gx + 7, y + 12)));
                    primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx, y + 24), new StepVectorWatermarkPoint(gx + 9, y + 24)));
                    if (glyph % 2 == 0)
                        primitives.Add(CreateVectorPrimitive(new StepVectorWatermarkPoint(gx + 9, y + 2), new StepVectorWatermarkPoint(gx + 9, y + 22)));
                }
            }

            return CreateVectorInput("split-arbitrary-text", primitives);
        }

        private static StepVectorWatermarkDetectionInput CreatePinRowVectorInput()
        {
            var primitives = new List<StepVectorWatermarkPrimitive>();
            for (int index = 0; index < 12; index++)
            {
                double x = 40 + index * 12;
                primitives.Add(CreateVectorPrimitive(
                    new StepVectorWatermarkPoint(x, 160),
                    new StepVectorWatermarkPoint(x, 196)));
            }

            return CreateVectorInput("pin-row", primitives);
        }

        private static StepVectorWatermarkDetectionInput CreateVectorInput(
            string modelName,
            IReadOnlyList<StepVectorWatermarkPrimitive> primitives)
        {
            return new StepVectorWatermarkDetectionInput
            {
                ModelName = modelName,
                ViewName = "front",
                ImageWidth = 400,
                ImageHeight = 400,
                Primitives = primitives
            };
        }

        private static StepVectorWatermarkPrimitive CreateVectorPrimitive(params StepVectorWatermarkPoint[] points)
        {
            StepVectorWatermarkBounds bounds = CreateVectorSmokeBounds(points);
            return new StepVectorWatermarkPrimitive
            {
                Kind = StepVectorWatermarkPrimitiveKind.Line,
                Visibility = "visible",
                Category = "visible",
                SampledPoints = points,
                SampledImagePoints = points,
                Bounds = bounds,
                ImageBounds = bounds
            };
        }

        private static StepVectorWatermarkBounds CreateVectorSmokeBounds(IReadOnlyList<StepVectorWatermarkPoint> points)
        {
            return new StepVectorWatermarkBounds
            {
                Left = points.Min(point => point.X),
                Right = points.Max(point => point.X),
                Bottom = points.Min(point => point.Y),
                Top = points.Max(point => point.Y)
            };
        }

        private static void RotateTemplateSmokePoint(
            double x,
            double y,
            double width,
            double height,
            int rotationDegrees,
            out double rotatedX,
            out double rotatedY)
        {
            if (rotationDegrees == 90)
            {
                rotatedX = height - y;
                rotatedY = x;
                return;
            }

            if (rotationDegrees == 180)
            {
                rotatedX = width - x;
                rotatedY = height - y;
                return;
            }

            if (rotationDegrees == 270)
            {
                rotatedX = y;
                rotatedY = width - x;
                return;
            }

            rotatedX = x;
            rotatedY = y;
        }

        private static bool TryParseMarkedModelAndView(string markerPath, out string modelName, out string viewName)
        {
            string name = Path.GetFileNameWithoutExtension(markerPath);
            foreach (string candidateViewName in StepProjectionRenderer.ViewNames.OrderByDescending(value => value.Length))
            {
                string suffix = "__" + candidateViewName;
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                modelName = name.Substring(0, name.Length - suffix.Length);
                viewName = candidateViewName;
                return true;
            }

            modelName = string.Empty;
            viewName = string.Empty;
            return false;
        }

        private static List<MarkedRectI> ReadMarkedRectangles(string markerPath)
        {
            MarkedRegionFile document = JsonSerializer.Deserialize<MarkedRegionFile>(File.ReadAllText(markerPath));
            return (document?.Rectangles ?? new List<MarkedRectangle>())
                .Where(rectangle => rectangle.Width > 0 && rectangle.Height > 0)
                .Select(rectangle => new MarkedRectI(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height))
                .ToList();
        }

        private static void AssertMarkedVectorParity(
            string modelName,
            string viewName,
            IReadOnlyList<MarkedRectI> markedRects,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections,
            List<string> failures)
        {
            if (detections.Count != markedRects.Count)
            {
                failures.Add(
                    modelName +
                    " " +
                    viewName +
                    " expected " +
                    markedRects.Count.ToString(CultureInfo.InvariantCulture) +
                    " vector detections from marked data, got " +
                    detections.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
                return;
            }

            var detectedRects = detections
                .Select(detection => new MarkedRectI(detection.X, detection.Y, detection.Width, detection.Height))
                .ToList();
            foreach (MarkedRectI detected in detectedRects)
            {
                MarkedRectI best = markedRects
                    .OrderByDescending(marked => IntersectArea(marked, detected))
                    .First();
                if (!IsInsideMarkedRectangle(detected, best))
                    failures.Add(modelName + " " + viewName + " vector detection " + detected + " is not fully inside marked rectangle " + best + ".");
                if (detected.Area >= best.Area)
                    failures.Add(modelName + " " + viewName + " vector detection " + detected + " should be smaller than marked rectangle " + best + ".");
            }
        }

        private static bool IsInsideMarkedRectangle(MarkedRectI inner, MarkedRectI outer)
        {
            return inner.X >= outer.X &&
                inner.Y >= outer.Y &&
                inner.X + inner.Width <= outer.X + outer.Width &&
                inner.Y + inner.Height <= outer.Y + outer.Height;
        }

        private static int IntersectArea(MarkedRectI left, MarkedRectI right)
        {
            int x0 = Math.Max(left.X, right.X);
            int y0 = Math.Max(left.Y, right.Y);
            int x1 = Math.Min(left.X + left.Width, right.X + right.Width);
            int y1 = Math.Min(left.Y + left.Height, right.Y + right.Height);
            if (x1 <= x0 || y1 <= y0)
                return 0;
            return (x1 - x0) * (y1 - y0);
        }

        private static string FormatBounds(int x, int y, int width, int height)
        {
            return x.ToString(CultureInfo.InvariantCulture) +
                "," +
                y.ToString(CultureInfo.InvariantCulture) +
                "," +
                width.ToString(CultureInfo.InvariantCulture) +
                "," +
                height.ToString(CultureInfo.InvariantCulture);
        }

        private static List<T> ReadJsonList<T>(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Required test fixture was not found.", path);

            List<T> result = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path));
            if (result == null)
                throw new InvalidDataException("Could not read JSON fixture: " + path);

            return result;
        }

        private static F3DRenderedImage RenderPreviewSmokeSameThread(byte[] stepData)
        {
            using (F3DProjectionRenderer.F3DPreviewSession session =
                F3DProjectionRenderer.CreatePreviewSession(stepData))
            {
                return session.RenderInteractivePreviewImage(
                    320,
                    240,
                    new F3DPreviewCameraState());
            }
        }

        private static F3DRenderedImage RenderPreviewSmokeCrossThread(byte[] stepData)
        {
            F3DProjectionRenderer.F3DPreviewSession session = Task.Run(
                () => F3DProjectionRenderer.CreatePreviewSession(stepData))
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            try
            {
                return Task.Run(
                    () => session.RenderInteractivePreviewImage(
                        320,
                        240,
                        new F3DPreviewCameraState()))
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                session.Dispose();
            }
        }

        private static int RunF3DNoAmbientOcclusionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string projectionRendererPath = Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs");
            string f3dRendererPath = Path.Combine(repoRoot, "StepF3DRenderLib", "F3DProjectionRenderer.cs");
            string projectionRenderer = File.ReadAllText(projectionRendererPath);
            string f3dRenderer = File.ReadAllText(f3dRendererPath);

            if (projectionRenderer.Contains("\"--ambient-occlusion\"", StringComparison.Ordinal))
                failures.Add("External f3d-console render path must not pass --ambient-occlusion.");
            if (f3dRenderer.Contains("\"render.effect.ambient_occlusion\", 1", StringComparison.Ordinal))
                failures.Add("In-process F3D render path must not enable render.effect.ambient_occlusion.");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("F3D no ambient occlusion test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("F3D no ambient occlusion test passed.");
            return 0;
        }

        private static byte[] ConvertRawF3DImageToRgba(F3DRenderedImage image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (image.RawBytes == null)
                throw new InvalidDataException("F3D preview raw bytes are missing.");
            if (image.Width <= 0 || image.Height <= 0 || image.ChannelCount <= 0)
                throw new InvalidDataException("F3D preview image shape is invalid.");
            if (image.ChannelType != 0 || image.ChannelTypeSize != 1)
                throw new InvalidDataException("F3D preview image uses an unsupported channel type.");

            int pixelCount = checked(image.Width * image.Height);
            if (image.RawBytes.Length < pixelCount * image.ChannelCount)
                throw new InvalidDataException("F3D preview image data is incomplete.");

            var rgba = new byte[pixelCount * 4];
            for (int y = 0; y < image.Height; y++)
            {
                int sourceRow = (image.Height - 1 - y) * image.Width * image.ChannelCount;
                int targetRow = y * image.Width * 4;
                for (int x = 0; x < image.Width; x++)
                {
                    int source = sourceRow + x * image.ChannelCount;
                    int target = targetRow + x * 4;
                    if (image.ChannelCount == 1)
                    {
                        byte value = image.RawBytes[source];
                        rgba[target] = value;
                        rgba[target + 1] = value;
                        rgba[target + 2] = value;
                        rgba[target + 3] = 255;
                        continue;
                    }

                    rgba[target] = image.RawBytes[source];
                    rgba[target + 1] = image.RawBytes[source + 1];
                    rgba[target + 2] = image.RawBytes[source + 2];
                    rgba[target + 3] = image.ChannelCount >= 4 ? image.RawBytes[source + 3] : (byte)255;
                }
            }

            return rgba;
        }

        private static int CountNonWhitePixels(byte[] rgba)
        {
            int count = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i] < 245 || rgba[i + 1] < 245 || rgba[i + 2] < 245)
                    count++;
            }

            return count;
        }

        private static int CountDarkPixels(byte[] rgba, byte threshold)
        {
            int count = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i + 3] > 0 && rgba[i] <= threshold && rgba[i + 1] <= threshold && rgba[i + 2] <= threshold)
                    count++;
            }

            return count;
        }

        private static int CountDarkPixels(StepProjectionImage image, MarkedRectI rectangle, byte threshold)
        {
            int count = 0;
            int left = Math.Max(0, rectangle.X);
            int top = Math.Max(0, rectangle.Y);
            int right = Math.Min(image.Width, rectangle.X + rectangle.Width);
            int bottom = Math.Min(image.Height, rectangle.Y + rectangle.Height);
            for (int y = top; y < bottom; y++)
            {
                int row = y * image.Width * 4;
                for (int x = left; x < right; x++)
                {
                    int offset = row + x * 4;
                    if (image.RgbaBytes[offset + 3] > 0 &&
                        image.RgbaBytes[offset] <= threshold &&
                        image.RgbaBytes[offset + 1] <= threshold &&
                        image.RgbaBytes[offset + 2] <= threshold)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void ValidateVisibleRawUsbXPlusBounds(
            string label,
            MarkedRectI? bounds,
            List<string> failures)
        {
            if (!bounds.HasValue)
            {
                failures.Add(label + " should contain placed x_plus vector linework.");
                return;
            }

            MarkedRectI value = bounds.Value;
            if (value.X < 50 || value.X > 120 ||
                value.Y < 140 || value.Y > 220 ||
                value.Width < 1380 || value.Width > 1490 ||
                value.Height < 1200 || value.Height > 1290)
            {
                failures.Add(
                    label +
                    " should keep USB-B x_plus linework in the expected image envelope, got " +
                    value.ToString() +
                    ".");
            }
        }

        private static MarkedRectI? GetDarkPixelBounds(StepProjectionImage image, byte threshold)
        {
            if (image == null || image.RgbaBytes == null)
                return null;

            int left = image.Width;
            int top = image.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < image.Height; y++)
            {
                int row = y * image.Width * 4;
                for (int x = 0; x < image.Width; x++)
                {
                    int offset = row + x * 4;
                    if (image.RgbaBytes[offset + 3] > 0 &&
                        image.RgbaBytes[offset] <= threshold &&
                        image.RgbaBytes[offset + 1] <= threshold &&
                        image.RgbaBytes[offset + 2] <= threshold)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            if (right < left || bottom < top)
                return null;

            return new MarkedRectI(left, top, right - left + 1, bottom - top + 1);
        }

        private static MarkedRectI? GetDarkPixelBounds(SKBitmap bitmap, byte threshold)
        {
            if (bitmap == null)
                return null;

            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    if (pixel.Alpha > 0 &&
                        pixel.Red <= threshold &&
                        pixel.Green <= threshold &&
                        pixel.Blue <= threshold)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            if (right < left || bottom < top)
                return null;

            return new MarkedRectI(left, top, right - left + 1, bottom - top + 1);
        }

        private static void SaveRgbaPng(byte[] rgba, int width, int height, string outputPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
            {
                Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
                using (SKImage image = SKImage.FromBitmap(bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 95))
                {
                    File.WriteAllBytes(outputPath, data.ToArray());
                }
            }
        }

        private static async System.Threading.Tasks.Task<int> RunModelImportMeasurementAsync(
            string partNumber,
            int repeatCount,
            bool cleanText)
        {
            using (var httpClient = new HttpClient())
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) EasyEDA-Loader-StepCleaner/1.0");

                Console.WriteLine("Model import measurement: part=" + partNumber + ", repeat=" + repeatCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Clean text: " + cleanText.ToString(CultureInfo.InvariantCulture));

                for (int repeatIndex = 1; repeatIndex <= repeatCount; repeatIndex++)
                {
                    var timings = new ModelImportMeasurementTimings();
                    Console.WriteLine("Run " + repeatIndex.ToString(CultureInfo.InvariantCulture) + "/" + repeatCount.ToString(CultureInfo.InvariantCulture));

                    MeasuredModelInfo model = await timings.MeasureAsync(
                        "component_lookup",
                        () => LoadMeasuredModelInfoAsync(httpClient, partNumber, cancellation.Token)).ConfigureAwait(false);

                    Console.WriteLine("  model_uuid=" + model.Uuid);
                    Console.WriteLine("  model_title=" + model.Title);

                    byte[] originalStep = await timings.MeasureAsync(
                        "model_download_cache_read",
                        () => GetOrDownloadBytesAsync(
                            GetOriginalStepPath(model.Uuid),
                            () => DownloadBytesAsync(httpClient, "https://modules.easyeda.com/qAxj6KHrDKw4blvCG8QJPs7Y/" + model.Uuid, cancellation.Token),
                            cancellation.Token)).ConfigureAwait(false);

                    byte[] rawObj = await timings.MeasureAsync(
                        "raw_obj_download_cache_read",
                        () => GetOrDownloadBytesAsync(
                            GetRawObjPath(model.Uuid),
                            () => DownloadBytesAsync(httpClient, "https://modules.easyeda.com/3dmodel/" + model.Uuid, cancellation.Token),
                            cancellation.Token)).ConfigureAwait(false);

                    string cleanCacheKey = CleanStepCacheKeys.GetCleanModeKey(model.Uuid, cleanText);
                    string cleanedStepPath = GetCleanStepPath(cleanCacheKey);
                    StepWatermarkCleanVerifierResult cleanMissResult = null;
                    byte[] cleanedStep = await timings.MeasureAsync(
                        "watermark_clean_cache",
                        () => GetOrDownloadBytesAsync(
                            cleanedStepPath,
                            () => System.Threading.Tasks.Task.Run(
                                () =>
                                {
                                    cleanMissResult = StepWatermarkCleanVerifier.CleanStepModelFastWithReport(
                                        originalStep,
                                        cleanText);
                                    return cleanMissResult.CleanStep;
                                },
                                cancellation.Token),
                            cancellation.Token)).ConfigureAwait(false);

                    ModelZInfo zInfo = await timings.MeasureAsync(
                        "raw_obj_z_info",
                        () => ModelZInfoCache.GetOrCreateAsync(
                            model.Uuid,
                            () => System.Threading.Tasks.Task.FromResult(rawObj),
                            cancellation.Token)).ConfigureAwait(false);

                    IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = timings.Measure(
                        "occt_hlr_projection_total",
                        () => StepSilhouetteProjection.Generate(cleanedStep, CreateMeasurementProjectionPlacement(model)));

                    Console.WriteLine("  original_step_bytes=" + originalStep.Length.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("  raw_obj_bytes=" + rawObj.Length.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("  cleaned_step_bytes=" + cleanedStep.Length.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("  z_offset_mm=" + zInfo.OffsetFromOrigin.ToString("R", CultureInfo.InvariantCulture));
                    Console.WriteLine("  model_height_mm=" + zInfo.Height.ToString("R", CultureInfo.InvariantCulture));
                    Console.WriteLine("  projection_primitives=" + projectionPrimitives.Count.ToString(CultureInfo.InvariantCulture));
                    PrintCleanerTimings(cleanMissResult?.CleanReport, "  ");
                    timings.WriteToConsole("  ");
                }
            }

            return 0;
        }

        private static void PrintCleanerTimings(StepWatermarkCleanerReport report, string prefix)
        {
            if (report?.Timings == null || report.Timings.Count == 0)
                return;

            foreach (StepWatermarkCleanerTiming timing in report.Timings)
            {
                if (timing == null || string.IsNullOrWhiteSpace(timing.Name))
                    continue;

                Console.WriteLine(
                    prefix +
                    "watermark_clean_detail_" +
                    timing.Name +
                    "_ms=" +
                    timing.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void PrintTopCleanerTiming(StepWatermarkCleanerReport report, string prefix)
        {
            if (report?.Timings == null || report.Timings.Count == 0)
                return;

            foreach (StepWatermarkCleanerTiming timing in report.Timings
                .Where(timing => timing != null && !string.IsNullOrWhiteSpace(timing.Name))
                .OrderByDescending(timing => timing.ElapsedMilliseconds)
                .ThenBy(timing => timing.Name, StringComparer.Ordinal)
                .Take(5))
            {
                Console.WriteLine(
                    prefix +
                    "watermark_clean_top_" +
                    timing.Name +
                    "_ms=" +
                    timing.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static async System.Threading.Tasks.Task<MeasuredModelInfo> LoadMeasuredModelInfoAsync(
            HttpClient httpClient,
            string partNumber,
            CancellationToken cancellationToken)
        {
            string url = "https://easyeda.com/api/products/" + Uri.EscapeDataString(partNumber) + "/components?version=6.4.19.5";
            string json = await GetOrDownloadTextAsync(
                GetComponentJsonPath(partNumber),
                () => httpClient.GetStringAsync(url, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                JsonElement packageData = root
                    .GetProperty("result")
                    .GetProperty("packageDetail")
                    .GetProperty("dataStr");
                JsonElement shapes = packageData.GetProperty("shape");
                foreach (JsonElement shape in shapes.EnumerateArray())
                {
                    string rawShape = shape.GetString();
                    if (rawShape != null && rawShape.StartsWith("SVGNODE~", StringComparison.Ordinal))
                        return ParseMeasuredModelInfo(rawShape);
                }
            }

            throw new InvalidDataException("Component does not contain a 3D SVGNODE model: " + partNumber);
        }

        private static MeasuredModelInfo ParseMeasuredModelInfo(string rawShape)
        {
            int separator = rawShape.IndexOf('~');
            if (separator < 0 || separator + 1 >= rawShape.Length)
                throw new InvalidDataException("Invalid SVGNODE model shape.");

            using (JsonDocument document = JsonDocument.Parse(rawShape.Substring(separator + 1)))
            {
                JsonElement attrs = document.RootElement.GetProperty("attrs");
                string[] rotationParts = attrs.GetProperty("c_rotation").GetString().Split(',');
                return new MeasuredModelInfo
                {
                    Uuid = attrs.GetProperty("uuid").GetString(),
                    Title = attrs.GetProperty("title").GetString(),
                    WidthMm = ConvertEasyEdaUnitToMm(ParseInvariantDouble(attrs.GetProperty("c_width").GetString())),
                    HeightMm = ConvertEasyEdaUnitToMm(ParseInvariantDouble(attrs.GetProperty("c_height").GetString())),
                    RotX = rotationParts.Length > 0 ? ParseInvariantDouble(rotationParts[0]) : 0.0,
                    RotY = rotationParts.Length > 1 ? ParseInvariantDouble(rotationParts[1]) : 0.0,
                    RotZ = rotationParts.Length > 2 ? ParseInvariantDouble(rotationParts[2]) : 0.0
                };
            }
        }

        private static async Task<byte[]> DownloadBytesAsync(
            HttpClient httpClient,
            string url,
            CancellationToken cancellationToken)
        {
            using (HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> GetOrDownloadBytesAsync(
            string cachePath,
            Func<Task<byte[]>> download,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(cachePath))
            {
                byte[] cached = File.ReadAllBytes(cachePath);
                if (cached.Length > 0)
                    return cached;
            }

            byte[] data = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (data != null && data.Length > 0)
            {
                string directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(cachePath, data);
            }

            return data;
        }

        private static async Task<string> GetOrDownloadTextAsync(
            string cachePath,
            Func<Task<string>> download,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(cachePath))
            {
                string cached = File.ReadAllText(cachePath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(cached))
                    return cached;
            }

            string data = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(data))
            {
                string directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(cachePath, data, Encoding.UTF8);
            }

            return data;
        }

        private static StepSilhouettePlacement CreateMeasurementProjectionPlacement(MeasuredModelInfo model)
        {
            FootprintModelRotation modelRotation = FootprintModelPlacement.ResolveAltiumModelRotationDeg(
                model.RotX,
                model.RotY,
                model.RotZ);
            FootprintModelRotation projectionRotation = FootprintModelPlacement.ResolveProjectionModelRotationDeg(modelRotation);
            double halfWidth = Math.Max(model.WidthMm, 1.0) / 2.0;
            double halfHeight = Math.Max(model.HeightMm, 1.0) / 2.0;
            return new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -halfWidth,
                    Bottom = -halfHeight,
                    Right = halfWidth,
                    Top = halfHeight
                },
                RotX = projectionRotation.X,
                RotY = projectionRotation.Y,
                RotZ = projectionRotation.Z,
                Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()
            };
        }

        private static string CreateMeasurementVerificationDirectory(string partNumber, string modelUuid)
        {
            string reportName =
                GetSafeFileName(partNumber) +
                "_" +
                GetSafeFileName(modelUuid) +
                "_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string reportDirectory = Path.Combine(GetLocalDataRoot(), "StepCleanerReports", reportName);
            Directory.CreateDirectory(reportDirectory);
            return reportDirectory;
        }

        private static string GetOriginalStepPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Original"), GetSafeFileName(modelUuid) + ".step");
        }

        private static string GetRawObjPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Raw"), GetSafeFileName(modelUuid) + ".obj");
        }

        private static string GetCleanStepPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Clean"), GetSafeFileName(modelUuid) + "_clean.step");
        }

        private static string GetComponentJsonPath(string partNumber)
        {
            return Path.Combine(GetLocalDataRoot(), "ComponentCache", GetSafeFileName(partNumber) + ".json");
        }

        private static string GetModelCacheDirectory(string kind)
        {
            return Path.Combine(GetLocalDataRoot(), "ModelCache", kind);
        }

        private static string GetLocalDataRoot()
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.Combine(Path.GetTempPath(), "EasyEDA-Loader")
                : Path.Combine(localApplicationData, "EasyEDA-Loader");
        }

        private static string GetSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = Guid.NewGuid().ToString("N");

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidChar, '_');

            return value;
        }

        private static double ParseInvariantDouble(string value)
        {
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static double ConvertEasyEdaUnitToMm(double value)
        {
            return value * 10.0 * 0.0254;
        }

        private sealed class MeasuredModelInfo
        {
            public string Uuid { get; set; }
            public string Title { get; set; }
            public double WidthMm { get; set; }
            public double HeightMm { get; set; }
            public double RotX { get; set; }
            public double RotY { get; set; }
            public double RotZ { get; set; }
        }

        private sealed class ModelImportMeasurementTimings
        {
            private readonly List<Tuple<string, long>> stages = new List<Tuple<string, long>>();

            public async Task<T> MeasureAsync<T>(string stageName, Func<Task<T>> action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    stopwatch.Stop();
                    stages.Add(Tuple.Create(stageName, stopwatch.ElapsedMilliseconds));
                }
            }

            public T Measure<T>(string stageName, Func<T> action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    return action();
                }
                finally
                {
                    stopwatch.Stop();
                    stages.Add(Tuple.Create(stageName, stopwatch.ElapsedMilliseconds));
                }
            }

            public void WriteToConsole(string prefix)
            {
                long total = 0;
                foreach (Tuple<string, long> stage in stages)
                {
                    total += stage.Item2;
                    Console.WriteLine(
                        prefix +
                        stage.Item1 +
                        "_ms=" +
                        stage.Item2.ToString(CultureInfo.InvariantCulture));
                }

                Console.WriteLine(prefix + "total_measured_ms=" + total.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void VerifyCleanTextPostProcessProjectionUsesCachedOriginal(
            string originalFile,
            string cleanTextFile,
            StepWatermarkDetectionReport detectionReport,
            string cachedOriginalProjectionDirectory,
            string cleanTextProjectionDirectory,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            var projectionOptions = CreateVerificationProjectionOptions();
            var detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalFile,
                    detectionReport,
                    projectionOptions)
                .ToList();

            var detectedViewNames = detectionRegions
                .Select(region => region.ViewName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (detectedViewNames.Count == 0)
            {
                failures.Add(Path.GetFileName(originalFile) + " CleanText did not produce projection verification regions.");
                return;
            }

            string originalModelName = Path.GetFileNameWithoutExtension(originalFile);
            string generatedOriginalProjectionDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(cleanTextProjectionDirectory)) ?? string.Empty,
                "CleanTextOriginalProjection");
            bool needsGeneratedOriginalProjection = detectedViewNames.Any(viewName =>
                !File.Exists(Path.Combine(cachedOriginalProjectionDirectory, originalModelName + "__" + viewName + ".png")));
            if (needsGeneratedOriginalProjection)
            {
                ClearProjectionFiles(generatedOriginalProjectionDirectory, new[] { Path.GetFileName(originalFile) });
                StepProjectionRenderer.ProjectFile(
                    originalFile,
                    generatedOriginalProjectionDirectory,
                    CreateProjectionOptionsForViews(detectedViewNames, projectionOptions));
            }

            ClearProjectionFiles(cleanTextProjectionDirectory, new[] { Path.GetFileName(cleanTextFile) });
            StepProjectionRenderer.ProjectFile(
                cleanTextFile,
                cleanTextProjectionDirectory,
                CreateProjectionOptionsForViews(detectedViewNames, projectionOptions));

            string cleanTextModelName = Path.GetFileNameWithoutExtension(cleanTextFile);
            foreach (string viewName in detectedViewNames)
            {
                string cachedOriginalProjectionPath = Path.Combine(cachedOriginalProjectionDirectory, originalModelName + "__" + viewName + ".png");
                string generatedOriginalProjectionPath = Path.Combine(generatedOriginalProjectionDirectory, originalModelName + "__" + viewName + ".png");
                string originalProjectionPath = File.Exists(cachedOriginalProjectionPath)
                    ? cachedOriginalProjectionPath
                    : generatedOriginalProjectionPath;
                string cleanTextProjectionPath = Path.Combine(cleanTextProjectionDirectory, cleanTextModelName + "__" + viewName + ".png");

                if (!File.Exists(originalProjectionPath))
                {
                    failures.Add("Original projection is missing for CleanText verification: " + originalProjectionPath);
                    continue;
                }

                if (!File.Exists(cleanTextProjectionPath))
                {
                    failures.Add("CleanText projection is missing: " + cleanTextProjectionPath);
                    continue;
                }

                var viewRegions = detectionRegions
                    .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                VerifyPostCleanProjectionImage(
                    Path.GetFileName(originalFile),
                    viewName,
                    originalProjectionPath,
                    cleanTextProjectionPath,
                    viewRegions,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    failures,
                    visualFailures);

                if (string.Equals(viewName, "z_plus", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Path.GetFileName(originalFile),
                        "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                        StringComparison.OrdinalIgnoreCase))
                {
                    VerifyProjectionRegionPreserved(
                        Path.GetFileName(originalFile),
                        viewName,
                        "pin-one marker",
                        originalProjectionPath,
                        cleanTextProjectionPath,
                        x: 318,
                        y: 128,
                        width: 68,
                        height: 68,
                        maxChangedPixels: 12,
                        failures);
                }
            }
        }

        private static void VerifyProjectionRegionVisiblePixelsRetained(
            string fileName,
            string viewName,
            string regionName,
            string originalProjectionPath,
            string cleanProjectionPath,
            int x,
            int y,
            int width,
            int height,
            int luminanceThreshold,
            double minRetainedRatio,
            List<string> failures)
        {
            using (var originalImage = SKBitmap.Decode(originalProjectionPath))
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (originalImage == null || cleanImage == null)
                {
                    failures.Add(fileName + " has an unreadable " + regionName + " projection on " + viewName + ".");
                    return;
                }

                int xEnd = Math.Min(x + width, Math.Min(originalImage.Width, cleanImage.Width));
                int yEnd = Math.Min(y + height, Math.Min(originalImage.Height, cleanImage.Height));
                int originalVisiblePixels = 0;
                int retainedVisiblePixels = 0;
                for (int row = Math.Max(y, 0); row < yEnd; row++)
                {
                    for (int col = Math.Max(x, 0); col < xEnd; col++)
                    {
                        if (!PixelLuminanceAtLeast(originalImage.GetPixel(col, row), luminanceThreshold))
                            continue;

                        originalVisiblePixels++;
                        if (PixelLuminanceAtLeast(cleanImage.GetPixel(col, row), luminanceThreshold))
                            retainedVisiblePixels++;
                    }
                }

                if (originalVisiblePixels == 0)
                {
                    failures.Add(fileName + " has no visible " + regionName + " pixels on " + viewName + ".");
                    return;
                }

                double retainedRatio = retainedVisiblePixels / (double)originalVisiblePixels;
                if (retainedRatio < minRetainedRatio)
                {
                    failures.Add(
                        fileName +
                        " did not retain " +
                        regionName +
                        " on " +
                        viewName +
                        ": retained=" +
                        retainedVisiblePixels.ToString(CultureInfo.InvariantCulture) +
                        "/" +
                        originalVisiblePixels.ToString(CultureInfo.InvariantCulture) +
                        " (" +
                        retainedRatio.ToString("P2", CultureInfo.InvariantCulture) +
                        "), required=" +
                        minRetainedRatio.ToString("P2", CultureInfo.InvariantCulture) +
                        ".");
                }
            }
        }

        private static bool PixelLuminanceAtLeast(SKColor color, int threshold)
        {
            int luminance = (int)Math.Round((0.2126 * color.Red) + (0.7152 * color.Green) + (0.0722 * color.Blue));
            return luminance >= threshold;
        }

        private static void VerifyProjectionRegionPreserved(
            string fileName,
            string viewName,
            string regionName,
            string originalProjectionPath,
            string cleanProjectionPath,
            int x,
            int y,
            int width,
            int height,
            int maxChangedPixels,
            List<string> failures)
        {
            using (var originalImage = SKBitmap.Decode(originalProjectionPath))
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (originalImage == null || cleanImage == null)
                {
                    failures.Add(fileName + " has an unreadable " + regionName + " projection on " + viewName + ".");
                    return;
                }

                int xEnd = Math.Min(x + width, Math.Min(originalImage.Width, cleanImage.Width));
                int yEnd = Math.Min(y + height, Math.Min(originalImage.Height, cleanImage.Height));
                int changedPixels = 0;
                for (int row = Math.Max(y, 0); row < yEnd; row++)
                {
                    for (int col = Math.Max(x, 0); col < xEnd; col++)
                    {
                        if (PixelsDifferent(originalImage.GetPixel(col, row), cleanImage.GetPixel(col, row), ProjectionDifferenceTolerance))
                            changedPixels++;
                    }
                }

                if (changedPixels > maxChangedPixels)
                {
                    failures.Add(
                        fileName +
                        " changed preserved " +
                        regionName +
                        " on " +
                        viewName +
                        ": pixels=" +
                        changedPixels.ToString(CultureInfo.InvariantCulture) +
                        ", allowed=" +
                        maxChangedPixels.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }
        }

        private static int RunSilhouetteCleanupTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string verifier = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepWatermarkCleanVerifier.cs"));
            string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            AssertContains(
                verifier,
                "Task.WaitAll(originalProjectionTask, cleanProjectionTask)",
                "watermark cleanup verification should render original and cleaned projections in parallel",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithOpenCascade",
                "watermark verification projections should keep an experimental OpenCascade render backend",
                failures);
            AssertContains(
                projectionRenderer,
                "IsOpenCascadeVerificationRendererEnabled",
                "OpenCascade PNG verification rendering should stay gated until visual equivalence is proven",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithOpenCascade(inputPath, outputPath, view, transform, options",
                "OpenCascade renderer must use StepProjectionRenderer's existing transform so detection masks align",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithOpenCascadeBatch",
                "multi-view watermark verification projections should avoid repeated OpenCascade process startup",
                failures);
            AssertContains(
                projectionRenderer,
                "GenerateViewsFromFile",
                "multi-view OpenCascade rendering should request all selected views from one helper process",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithF3D",
                "F3D should remain as fallback while OpenCascade rollout is verified",
                failures);

            string dataRoot = FindDataRoot();
            string sot223Path = Path.Combine(dataRoot, "Original", "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step");
            if (!File.Exists(sot223Path))
                failures.Add("SOT-223 cleanup fixture is missing: " + sot223Path);

            if (failures.Count == 0)
            {
                byte[] originalStep = File.ReadAllBytes(sot223Path);
                byte[] cleanedStep = StepWatermarkCleaner.Clean(originalStep, new StepWatermarkCleanerOptions());
                StepSilhouettePlacement placement = CreateDefaultSilhouettePlacement();

                IReadOnlyList<StepSilhouettePrimitive> originalPrimitives = StepSilhouetteProjection.Generate(originalStep, placement);
                IReadOnlyList<StepSilhouettePrimitive> cleanedPrimitives = StepSilhouetteProjection.Generate(cleanedStep, placement);

                if (cleanedPrimitives.Count >= originalPrimitives.Count)
                {
                    failures.Add(
                        "cleaned SOT-223 silhouette should drop inactive watermark topology: original=" +
                        originalPrimitives.Count.ToString(CultureInfo.InvariantCulture) +
                        ", cleaned=" +
                        cleanedPrimitives.Count.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Silhouette cleanup regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Silhouette cleanup regression test passed.");
            return 0;
        }

        private static int RunImportSavePolicyTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();

            AssertEqual(
                "False",
                ImportLibrarySavePolicy.SaveLibrariesAfterImport ? "True" : "False",
                "schematic/PCB library import must leave libraries unsaved for manual review",
                failures);
            AssertNoForbiddenLibrarySaveCalls(
                Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs"),
                "EasyEDALoader",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Import save policy regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Import save policy regression test passed.");
            return 0;
        }

        private static int RunFootprintPlacementTests()
        {
            var failures = new List<string>();

            var lm317ImportedBounds = new StepSilhouetteBounds
            {
                Left = -945.30976,
                Bottom = 657.15,
                Right = -938.20032,
                Top = 663.65
            };

            FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(lm317ImportedBounds, 0.0, 0.0);
            AssertNear(941.75504, move.XMm, 0.00001, "LM317 body should move from imported STEP bounds to footprint center on X", failures);
            AssertNear(-660.4, move.YMm, 0.00001, "LM317 body should move from imported STEP bounds to footprint center on Y", failures);
            AssertNear(0.0, lm317ImportedBounds.CenterX + move.XMm, 0.00001, "LM317 centered body X should match target center", failures);
            AssertNear(0.0, lm317ImportedBounds.CenterY + move.YMm, 0.00001, "LM317 centered body Y should match target center", failures);

            FootprintModelMove shiftedTargetMove = FootprintModelPlacement.CalculateCenteringMoveMm(lm317ImportedBounds, 2.5, -1.25);
            AssertNear(2.5, lm317ImportedBounds.CenterX + shiftedTargetMove.XMm, 0.00001, "body centering should support non-zero EasyEDA model X", failures);
            AssertNear(-1.25, lm317ImportedBounds.CenterY + shiftedTargetMove.YMm, 0.00001, "body centering should support non-zero EasyEDA model Y", failures);

            AssertNear(0.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(0.0), 0.00001, "projection placement should not apply model Z rotation a second time", failures);
            AssertNear(355.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(-5.0), 0.00001, "projection correction rotation should normalize negative angles", failures);
            AssertNear(180.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(), 0.00001, "Altium projection placement should rotate imported silhouettes 180 degrees", failures);

            FootprintModelRotation c5334147Rotation = FootprintModelPlacement.ResolveAltiumModelRotationDeg(0.0, 0.0, 0.0);
            AssertNear(0.0, c5334147Rotation.X, 0.00001, "C5334147 EasyEDA zero model rotation should preserve X rotation", failures);
            AssertNear(0.0, c5334147Rotation.Y, 0.00001, "C5334147 EasyEDA zero model rotation should preserve Y rotation", failures);
            AssertNear(0.0, c5334147Rotation.Z, 0.00001, "C5334147 EasyEDA zero model rotation should preserve Z rotation", failures);

            FootprintModelRotation c5334147ProjectionRotation = FootprintModelPlacement.ResolveProjectionModelRotationDeg(c5334147Rotation);
            AssertNear(0.0, c5334147ProjectionRotation.X, 0.00001, "C5334147 projection should preserve X rotation", failures);
            AssertNear(0.0, c5334147ProjectionRotation.Y, 0.00001, "C5334147 projection should preserve Y rotation", failures);
            AssertNear(0.0, c5334147ProjectionRotation.Z, 0.00001, "C5334147 projection should preserve Z rotation", failures);
            AssertNear(180.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(180.0), 0.00001, "C5334147 projection placement should rotate the silhouette 180 degrees", failures);

            FootprintModelMove c5334147FootprintOrigin = FootprintModelPlacement.ResolveModelCenterMm(
                0.0,
                0.0,
                -0.004826,
                -0.193294);
            AssertNear(-0.004826, c5334147FootprintOrigin.XMm, 0.00001, "C5334147 zero product transform should preserve footprint 3D origin X", failures);
            AssertNear(-0.193294, c5334147FootprintOrigin.YMm, 0.00001, "C5334147 zero product transform should preserve footprint 3D origin Y", failures);

            FootprintModelMove c5338332FootprintOrigin = FootprintModelPlacement.ResolveModelCenterMm(
                0.0,
                0.0000254,
                0.0,
                0.1599184);
            AssertNear(0.0, c5338332FootprintOrigin.XMm, 0.00001, "C5338332 near-zero product transform should preserve footprint 3D origin X", failures);
            AssertNear(0.1599184, c5338332FootprintOrigin.YMm, 0.00001, "C5338332 near-zero product transform should preserve footprint 3D origin Y", failures);

            FootprintModelMove explicitModelCenter = FootprintModelPlacement.ResolveModelCenterMm(1.2, -0.4, -0.004826, -0.193294);
            AssertNear(1.2, explicitModelCenter.XMm, 0.00001, "explicit non-zero model X offset must be preserved", failures);
            AssertNear(-0.4, explicitModelCenter.YMm, 0.00001, "explicit non-zero model Y offset must be preserved", failures);

            FootprintModelMove explicitEasyEdaOrigin = FootprintModelPlacement.ResolveFootprintOriginMm(10.0, 20.0, 4.0, 6.0, 12.0, 23.0);
            AssertNear(10.0, explicitEasyEdaOrigin.XMm, 0.00001, "EasyEDA footprint head X should define the Altium footprint origin", failures);
            AssertNear(20.0, explicitEasyEdaOrigin.YMm, 0.00001, "EasyEDA footprint head Y should define the Altium footprint origin", failures);
            AssertNear(2.0, FootprintModelPlacement.ConvertFootprintXToAltiumMm(12.0, explicitEasyEdaOrigin.XMm), 0.00001, "footprint X conversion should be relative to EasyEDA head X", failures);
            AssertNear(-3.0, FootprintModelPlacement.ConvertFootprintYToAltiumMm(23.0, explicitEasyEdaOrigin.YMm), 0.00001, "footprint Y conversion should invert around EasyEDA head Y", failures);

            FootprintModelMove fallbackBBoxOrigin = FootprintModelPlacement.ResolveFootprintOriginMm(null, null, 4.0, 6.0, 12.0, 23.0);
            AssertNear(10.0, fallbackBBoxOrigin.XMm, 0.00001, "missing EasyEDA footprint head X should fall back to BBox center X", failures);
            AssertNear(17.5, fallbackBBoxOrigin.YMm, 0.00001, "missing EasyEDA footprint head Y should fall back to BBox center Y", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Footprint placement regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Footprint placement regression test passed.");
            return 0;
        }

        private static int RunFootprintLayerTests()
        {
            var failures = new List<string>();

            AssertEqual("TopAssembly", FootprintLayerMap.NormalizeLayerName("ComponentShapeLayer"), "component shape should import as assembly documentation", failures);
            AssertEqual("TopAssembly", FootprintLayerMap.NormalizeLayerName("ComponentMarkingLayer"), "component marking should import as assembly documentation", failures);
            AssertEqual("TopAssembly", FootprintLayerMap.NormalizeLayerName("ComponentPolarityLayer"), "component polarity should import as assembly documentation", failures);
            AssertEqual("Mechanical", FootprintLayerMap.NormalizeLayerName("LeadShapeLayer"), "lead shape should import as non-production mechanical documentation", failures);
            AssertEqual("Mechanical", FootprintLayerMap.NormalizeLayerName("Document"), "document layer should import as non-production mechanical documentation", failures);
            AssertEqual("TopSilkLayer", FootprintLayerMap.NormalizeLayerName("TopSilkLayer"), "existing EasyEDA layer names should remain unchanged", failures);

            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("ComponentShapeLayer", false), "component shape should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("ComponentMarkingLayer", false), "component marking should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("ComponentPolarityLayer", false), "component polarity should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("LeadShapeLayer", false), "lead shape should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("Document", false), "document layer should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual("TopSilkLayer", FootprintLayerMap.NormalizeLayerName("TopSilkLayer", false), "existing EasyEDA layer names should remain available when LCSC mechanical layers are disabled", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Footprint layer regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Footprint layer regression test passed.");
            return 0;
        }

        private static int RunPcbLibActionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string ins = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.ins"));
            string rcs = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.rcs"));
            string module = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs"));
            string libraryNavigation = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "LibraryNavigation.cs"));
            string eePcb = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EEPCB.cs"));
            string footprintData = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintData.cs"));
            string footprint3dModel = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintShapes", "EeFootprint3dModel.cs"));
            string shapeExporter = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "PcbShapeSvgExporter.cs"));
            string padContourReader = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "PcbLibPadContourReader.cs"));
            string shapeExportSettings = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "ShapeExportSettings.cs"));
            string shapeExportProgress = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "ShapeExportProgressForm.cs"));
            string shapeExportErrors = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "ShapeExportErrorForm.cs"));
            string stepSilhouette = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepSilhouetteProjection.cs"));
            string layoutModelsPath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicationModels.cs");
            string layoutCapturePath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicationCapture.cs");
            string layoutMapperPath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicationMapper.cs");
            string layoutApplyPath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicationApply.cs");
            string layoutSchematicMatcherPath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicationSchematicMatcher.cs");
            string layoutDialogXamlPath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicatorDialog.xaml");
            string layoutDialogCodePath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicatorDialog.xaml.cs");
            string layoutViewModelsPath = Path.Combine(repoRoot, "EasyEDA-Loader", "LayoutDuplicatorViewModels.cs");
            string ollamaClientPath = Path.Combine(repoRoot, "EasyEDA-Loader", "OllamaLayoutMappingClient.cs");
            string layoutModels = ReadFileIfExists(layoutModelsPath);
            string layoutCapture = ReadFileIfExists(layoutCapturePath);
            string layoutMapper = ReadFileIfExists(layoutMapperPath);
            string layoutApply = ReadFileIfExists(layoutApplyPath);
            string layoutSchematicMatcher = ReadFileIfExists(layoutSchematicMatcherPath);
            string layoutDialogXaml = ReadFileIfExists(layoutDialogXamlPath);
            string layoutDialogCode = ReadFileIfExists(layoutDialogCodePath);
            string layoutViewModels = ReadFileIfExists(layoutViewModelsPath);
            string ollamaClient = ReadFileIfExists(ollamaClientPath);

            AssertContains(ins, "Command  Name = 'EasyEDAReproject3D'", "PcbLib action command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAAlign3DModel'", "PcbLib alignment command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDASwitchTopSignalLayer'", "PCB layer switch top command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDASwitchBottomSignalLayer'", "PCB layer switch bottom command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDASwitchNextSignalLayer'", "PCB layer switch next command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDASwitchPreviousSignalLayer'", "PCB layer switch previous command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDASwitchToSelectedPrimitiveLayer'", "PCB selected-primitive layer switch command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDACreateCustomPadFromSelected'", "PcbLib custom-pad command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDADuplicateLayout'", "PCB layout duplicator command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAExportShapeAllWithPads'", "PcbLib all-components-with-pads export command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAExportShapeSelectedWithPads'", "PcbLib selected-component-with-pads export command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAExportShapeSelectedLibraries'", "selected-library shape export command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAOpenSymbolLibrary'", "schematic symbol-library navigation command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAOpenFootprintLibrary'", "PCB footprint-library navigation command must be declared in the INS file", failures);

            AssertContains(rcs, "Caption='&EasyEDA'", "PcbLib menu should expose an EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='&Loader...'", "Loader command should move under the EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='&Reproject 3D'", "Reproject 3D command should be available in the PcbLib EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='&Align 3D model'", "Align 3D model command should be available in the PcbLib EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='Create &Custom Pad from Selected'", "Create Custom Pad from Selected command should be available in the PcbLib EasyEDA submenu", failures);
            AssertContains(rcs, "Tree MNPCB_EasyEDALoaderTree Caption='&EasyEDA'", "PCB editor menu should place EasyEDA commands under Tools > EasyEDA", failures);
            AssertContains(rcs, "Tree MNPCB_EasyEDALayerSwitchTree Caption='&Layer Switch'", "PCB editor menu should expose Tools > EasyEDA > Layer Switch", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDASwitchTopSignalLayer'", "Layer Switch menu should include switch-to-top command", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDASwitchBottomSignalLayer'", "Layer Switch menu should include switch-to-bottom command", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDASwitchNextSignalLayer'", "Layer Switch menu should include next signal command", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDASwitchPreviousSignalLayer'", "Layer Switch menu should include previous signal command", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDASwitchToSelectedPrimitiveLayer'", "Layer Switch menu should include selected-primitive layer command", failures);
            AssertContains(rcs, "PL PLEasyEDALoader:EasyEDADuplicateLayout Command='EasyEDA-Loader:EasyEDADuplicateLayout' Caption='Duplicate layout'", "Duplicate layout command should have a PCB menu resource entry", failures);
            AssertDoesNotContain(rcs, "PLID='PLEasyEDALoader:EasyEDADuplicateLayout'", "Duplicate layout must remain hidden from Tools > EasyEDA while the experimental workflow is disabled", failures);
            AssertContains(rcs, "Caption='All from selected &libraries'", "Export shape menu should expose selected-library export", failures);
            AssertContains(rcs, "Link MNPCB_EasyEDAExportShape30 PLID='PLEasyEDALoader:EasyEDAExportShapeAllWithPads'", "PCB editor Export shape menu should include all-components-with-pads export", failures);
            AssertContains(rcs, "Link MNPCB_EasyEDAExportShape40 PLID='PLEasyEDALoader:EasyEDAExportShapeSelectedLibraries'", "PCB editor Export shape menu should include selected-library export after pad export", failures);
            AssertContains(rcs, "Link MNPCBLib_EasyEDAExportShape30 PLID='PLEasyEDALoader:EasyEDAExportShapeAllWithPads'", "PcbLib editor Export shape menu should include all-components-with-pads export", failures);
            AssertContains(rcs, "Link MNPCBLib_EasyEDAExportShape40 PLID='PLEasyEDALoader:EasyEDAExportShapeSelectedWithPads'", "PcbLib editor Export shape menu should include selected-component-with-pads export", failures);
            AssertContains(rcs, "Link MNPCBLib_EasyEDAExportShape50 PLID='PLEasyEDALoader:EasyEDAExportShapeSelectedLibraries'", "PcbLib editor Export shape menu should include selected-library export after pad exports", failures);
            int pcbMenuStart = rcs.IndexOf("Insertion MNPCB_EasyEDALoader", StringComparison.Ordinal);
            int pcbLibMenuStart = rcs.IndexOf("Insertion MNPCBLib_EasyEDALoader", StringComparison.Ordinal);
            string pcbMenu = pcbMenuStart >= 0 && pcbLibMenuStart > pcbMenuStart
                ? rcs.Substring(pcbMenuStart, pcbLibMenuStart - pcbMenuStart)
                : string.Empty;
            AssertContains(pcbMenu, "EasyEDAExportShapeAllWithPads", "PCB editor Export shape menu must expose all-components-with-pads export", failures);
            AssertDoesNotContain(pcbMenu, "EasyEDAExportShapeSelectedWithPads", "PCB editor Export shape menu must not expose the PcbLib-only selected-component-with-pads command", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDAOpenSymbolLibrary'", "schematic editor menu should expose Open symbol library", failures);
            AssertContains(rcs, "PLID='PLEasyEDALoader:EasyEDAOpenFootprintLibrary'", "PCB editor menu should expose Open footprint library", failures);
            AssertContains(rcs, "PLEasyEDALoader:EasyEDASwitchTopSignalLayer Command='EasyEDA-Loader:EasyEDASwitchTopSignalLayer' Caption='&Top' Shortcut1='Ctrl+Plus'", "Top layer command should default to Ctrl+Plus", failures);
            AssertContains(rcs, "PLEasyEDALoader:EasyEDASwitchBottomSignalLayer Command='EasyEDA-Loader:EasyEDASwitchBottomSignalLayer' Caption='&Bottom' Shortcut1='Ctrl+Minus'", "Bottom layer command should default to Ctrl+Minus", failures);
            AssertContains(rcs, "PLEasyEDALoader:EasyEDASwitchNextSignalLayer Command='EasyEDA-Loader:EasyEDASwitchNextSignalLayer' Caption='&Next Signal' Shortcut1='Ctrl+Shift+Plus'", "Next signal command should default to Ctrl+Shift+Plus", failures);
            AssertContains(rcs, "PLEasyEDALoader:EasyEDASwitchPreviousSignalLayer Command='EasyEDA-Loader:EasyEDASwitchPreviousSignalLayer' Caption='&Previous Signal' Shortcut1='Ctrl+Shift+Minus'", "Previous signal command should default to Ctrl+Shift+Minus", failures);
            AssertDoesNotContain(rcs, "Link MNPCB_EasyEDALoader10 PLID='PLEasyEDALoader:EasyEDARun' End", "PCB editor Loader command must not be inserted directly under Tools", failures);

            AssertContains(module, "RegisterCommand(\"EasyEDAReproject3D\"", "module must register the Reproject 3D command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAAlign3DModel\"", "module must register the Align 3D model command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDASwitchTopSignalLayer\"", "module must register the top layer switch command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDASwitchBottomSignalLayer\"", "module must register the bottom layer switch command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDASwitchNextSignalLayer\"", "module must register the next layer switch command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDASwitchPreviousSignalLayer\"", "module must register the previous layer switch command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDASwitchToSelectedPrimitiveLayer\"", "module must register the selected-primitive layer switch command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDACreateCustomPadFromSelected\"", "module must register the Create Custom Pad from Selected command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDADuplicateLayout\"", "module must register the Duplicate layout command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDA-Loader:EasyEDADuplicateLayout\"", "module must register the namespaced Duplicate layout command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAExportShapeAllWithPads\"", "module must register all-components-with-pads shape export", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDA-Loader:EasyEDAExportShapeAllWithPads\"", "module must register namespaced all-components-with-pads shape export", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAExportShapeSelectedWithPads\"", "module must register selected-component-with-pads shape export", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDA-Loader:EasyEDAExportShapeSelectedWithPads\"", "module must register namespaced selected-component-with-pads shape export", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAExportShapeSelectedLibraries\"", "module must register selected-library shape export", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDA-Loader:EasyEDAExportShapeSelectedLibraries\"", "module must register namespaced selected-library shape export", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAOpenSymbolLibrary\"", "module must register schematic symbol-library navigation", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAOpenFootprintLibrary\"", "module must register PCB footprint-library navigation", failures);
            AssertContains(libraryNavigation, "component.GetState_LibraryPath()", "symbol-library navigation must read the selected schematic component library path", failures);
            AssertContains(libraryNavigation, "manager.FindComponentSymbol(", "symbol-library navigation must use Altium's resolver for installed and integrated libraries", failures);
            AssertDoesNotContain(libraryNavigation, "component.GetState_Selection()", "schematic selection must not query the unsupported inherited graphical-object COM interface", failures);
            AssertDoesNotContain(libraryNavigation, "CountContextMenuObjects", "schematic selection must not use Altium's popup-context object count", failures);
            AssertContains(libraryNavigation, "SCH.ISch_DocumentHelper.BoundingRectangle_Selected(schematic)", "schematic selection must use the SDK value-rectangle helper", failures);
            AssertContains(libraryNavigation, "iterator.AddFilter_Area(left, bottom, right, top)", "schematic selection must resolve components with a selected-bounds spatial iterator", failures);
            AssertContains(libraryNavigation, "EDP.Utils.LoadIntegratedLibraryManager()", "library navigation must obtain Altium's library manager through the supported API", failures);
            AssertContains(libraryNavigation, "manager.AvailableLibraryPath(index)", "library navigation must search project, installed, and search-path libraries known to Altium", failures);
            AssertContains(libraryNavigation, "manager.InstalledLibraryPath(index)", "library navigation must search common installed libraries known to Altium", failures);
            AssertContains(libraryNavigation, "workspace?.DM_Preferences()", "library navigation must read Altium workspace library preferences", failures);
            AssertContains(libraryNavigation, "preferences?.GetDefaultLibraryPath()", "library navigation must expand relative installed-library paths from Altium's configured root", failures);
            AssertContains(libraryNavigation, "component.GetState_SourceFootprintLibrary()", "footprint-library navigation must read the selected PCB component source library", failures);
            AssertContains(libraryNavigation, "internal static string TryResolvePcbLibraryPath", "PCB pad export must be able to resolve each component's source PcbLib", failures);
            AssertContains(libraryNavigation, "DXP.Utils.RunCommand(\"PCB:GotoLibraryComponent\"", "footprint-library navigation must use Altium's resolver for installed and integrated libraries", failures);
            AssertContains(libraryNavigation, "DXP.Utils.RunCommand(\"Sch:FirstComponentLibraryEditor\"", "symbol-library navigation must synchronize the SCH Library panel through native editor commands", failures);
            AssertContains(libraryNavigation, "DXP.Utils.RunCommand(\"Sch:NextComponentLibraryEditor\"", "symbol-library navigation must advance natively to the referenced symbol", failures);
            AssertContains(libraryNavigation, "library.SetState_Current_SchComponent(libraryComponent)", "symbol-library navigation must retain an API selection fallback", failures);
            AssertContains(libraryNavigation, "library.SetState_CurrentComponent(footprint)", "footprint-library navigation must select the referenced footprint", failures);
            AssertContains(module, "dialog.Filter = \"Altium PCB libraries (*.PcbLib)|*.PcbLib", "selected-library export must filter for PcbLib files", failures);
            AssertContains(module, "dialog.Multiselect = true;", "selected-library export must allow multiple PcbLib files", failures);
            AssertContains(module, "ShapeExportSettings.LoadLastLibraryFolder()", "selected-library export must restore its source folder", failures);
            AssertContains(module, "ShapeExportSettings.SaveLastLibraryFolder(", "selected-library export must persist its source folder", failures);
            AssertContains(module, "ShapeExportSettings.SaveLastFolder(dialog.SelectedPath);", "all shape export commands must persist the shared target folder", failures);
            AssertContains(module, "GetPCBLibraryByPath(libraryPath)", "selected-library export must reuse a PCB-server library that is already loaded", failures);
            AssertContains(module, "LoadPCBLibraryFromFile(libraryPath)", "selected-library export must load PcbLib files directly through the PCB server", failures);
            AssertContains(module, "PcbShapeSvgExporter.ExportLibrary(", "selected-library export must export every footprint from each loaded library", failures);
            AssertContains(module, "DestroyPCBLibrary(ref pcbLibrary)", "selected-library export must unload PCB-server libraries it loaded", failures);
            AssertContains(module, "if (loadedByExport && pcbLibrary != null)", "selected-library export must leave libraries that were already loaded untouched", failures);
            AssertContains(module, "() => progressForm.IsCancellationRequested", "selected-library export must pass progress-dialog cancellation into the exporter", failures);
            AssertContains(module, "if (errors.Count > 0)", "selected-library export must show its final dialog only when errors occurred", failures);
            AssertDoesNotContain(
                module.Substring(
                    module.IndexOf("private void ExportSelectedShapeLibraries", StringComparison.Ordinal),
                    module.IndexOf("private string[] SelectShapeExportLibraries", StringComparison.Ordinal)
                        - module.IndexOf("private void ExportSelectedShapeLibraries", StringComparison.Ordinal)),
                "ShowInfo(",
                "selected-library shape export must not show a success dialog",
                failures);
            AssertContains(shapeExporter, "public static ShapeExportResult ExportLibrary(", "shape exporter must expose a whole-library entry point", failures);
            AssertContains(shapeExporter, "HashSet<string> usedNames = null", "multi-library shape export must share output filenames across libraries", failures);
            AssertContains(shapeExporter, "result.Errors.Add(componentName + \": \" + ex.Message);", "shape exporter must collect individual footprint failures", failures);
            AssertContains(shapeExporter, "catch (OperationCanceledException)", "shape exporter must preserve cancellation while isolating footprint errors", failures);
            AssertContains(shapeExporter, "target is IPCB_RectangularPrimitive rectangularPrimitive", "shape exporter must use typed access for inherited text geometry in server-loaded libraries", failures);
            AssertContains(shapeExporter, "return rectangularPrimitive.GetState_Rotation();", "shape exporter must preserve text rotation from server-loaded libraries", failures);
            AssertContains(shapeExporter, "return rectangularPrimitive.GetState_XLocation();", "shape exporter must preserve text X location from server-loaded libraries", failures);
            AssertContains(shapeExporter, "return rectangularPrimitive.GetState_YLocation();", "shape exporter must preserve text Y location from server-loaded libraries", failures);
            AssertContains(shapeExporter, "return text.GetState_Size();", "shape exporter must preserve text size from server-loaded libraries", failures);
            AssertContains(shapeExporter, "bool boundsAreVertical = boundsHeight > boundsWidth * 1.15;", "shape exporter must recover missing text rotation from component-local text bounds", failures);
            AssertContains(shapeExporter, "return distanceToTop < distanceToBottom ? 90.0 : -90.0;", "shape exporter must account for Altium's end-anchored stroke-text bounds when recovering vertical direction", failures);
            AssertContains(shapeExporter, "characterCount < 2", "shape exporter must not infer rotation from single-character text bounds", failures);
            AssertContains(shapeExporter, "GetBool(primitive, \"GetState_Multiline\")", "shape exporter must not apply single-line rotation inference to multiline text", failures);
            AssertContains(module, "ShapeExportScope.AllComponents, includePads: true", "all-components-with-pads command must enable pad export", failures);
            AssertContains(module, "ShapeExportScope.SelectedComponent, includePads: true", "selected-component-with-pads command must enable pad export", failures);
            AssertContains(shapeExporter, "isCancellationRequested, includePads);", "PCB board export must pass the with-pads option into its component exporter", failures);
            AssertContains(shapeExporter, "includePads: includePads", "PCB board component export must write the optional Pads SVG group", failures);
            AssertContains(shapeExporter, "ReadBoardPersistedPadContours(board, componentList", "PCB pad export must preload exact custom contours from source footprint libraries", failures);
            AssertContains(shapeExporter, "PcbLibPadContourReader.Read(request.Key, request.Value)", "PCB pad export must request only footprints used by the board from each source library", failures);
            AssertContains(shapeExporter, "IsFlippedBoardComponent(existing) && !IsFlippedBoardComponent(component)", "unique PCB footprint export must prefer a top-side representative", failures);
            AssertContains(shapeExporter, "board.GetState_MechanicalPairs()", "PCB shape export must read the board's configured mechanical layer pairs", failures);
            AssertContains(shapeExporter, "pairs.FlipLayerV7(ref pairedLayer)", "bottom-side PCB shape export must resolve Mechanical 2's paired layer through Altium", failures);
            AssertContains(shapeExporter, "(int)layer.SafeV6Layer()", "bottom-side PCB shape export must retain the paired layer's legacy V6 identity", failures);
            AssertContains(shapeExporter, "IsMechanical2Primitive(primitive, objectId, alternateMechanical2LayerNumbers)", "bottom-side PCB shape export must accept every SDK representation of the paired Mechanical 2 layer", failures);
            AssertContains(shapeExporter, "TryGetRawLayerNumber(primitive, out int rawLayerNumber)", "paired mechanical layer matching must prefer globally distinct raw layer IDs", failures);
            AssertContains(shapeExporter, "shapeObjects.Any(primitive => IsAlternateMechanicalLayerPrimitive", "PCB shape export must infer flipped COM footprint proxies from paired-layer primitives", failures);
            AssertContains(shapeExporter, "rawLayerNumber == (int)bottomLayer.GetID()", "PCB shape export must infer bottom-side COM footprint proxies from their layer ID", failures);
            AssertContains(shapeExporter, "componentPadContours, componentMirrored", "PCB pad export must reuse the shape capture's resolved component side", failures);
            AssertContains(shapeExporter, "localX = rotatedX;", "bottom-side PCB export must preserve footprint-local X after removing placement rotation", failures);
            AssertContains(shapeExporter, "localY = mirrored ? -rotatedY : rotatedY;", "bottom-side PCB export must undo Altium's local-Y reflection", failures);
            AssertContains(shapeExporter, "startAngle = NormalizeAngle(-startAngle);", "bottom-side arc export must reflect arc angles across local X", failures);
            AssertDoesNotContain(shapeExporter, "NormalizeAngle(180.0 - startAngle)", "bottom-side arc export must not apply the obsolete local-X reflection", failures);
            AssertContains(shapeExporter, "Internal_GetState_ExtractedPrimitiveOnLayer(layer)", "pad export should use Altium's extracted shape-based primitive when available", failures);
            AssertContains(shapeExporter, "Internal_PCBContourMaker()", "pad export must ask Altium's contour maker for rendered pad geometry", failures);
            AssertContains(shapeExporter, "contourMaker.Internal_MakeContour(pad, 0, PadLayerNumber(layer))", "pad export must generate the exact contour on the selected copper layer", failures);
            AssertContains(shapeExporter, "TryCreateGeometricPolygonPrimitive(", "pad export must convert contour-maker polygons directly to SVG paths", failures);
            AssertContains(shapeExporter, "Internal_GetState_RegionShapeOnLayer(layer)", "pad export should retain Altium's generated pad region as a fallback", failures);
            AssertContains(shapeExporter, "pcbPrimitive.Export_ToParameters(ref parameters);", "pad export must read region PADINDEX linkage from Altium primitive parameters", failures);
            AssertContains(shapeExporter, "TryReadPrimitiveParameterInt(region, \"PADINDEX\"", "pad export must limit linked-region lookup to PADINDEX regions", failures);
            AssertContains(shapeExporter, "TryTakeLinkedPadRegion(", "pad export must prefer linked shape-based footprint regions", failures);
            AssertContains(shapeExporter, "EnumerateGroupObjectsFiltered(component, ShapeObjectIds)", "shape export should collect supported primitives with one filtered component iterator", failures);
            AssertContains(shapeExporter, "if (filteredIteratorReturnedObjects)", "shape and pad export should skip redundant fallback scans after a filtered iterator succeeds", failures);
            AssertContains(shapeExporter, "selectedComponent == null ? null : new[] { selectedComponent.ExportName }", "selected pad export should request only the active footprint's persisted contour stream", failures);
            AssertContains(shapeExporter, "candidate.PadIndex == padIndex", "custom pad contours must match Altium's stable pad index before geometric fallback", failures);
            AssertContains(shapeExporter, "if (!matchedByIndex && bestDistance > Math.Max(0.05", "geometric custom-pad matching must reject contours belonging to another nearby pad", failures);
            AssertContains(shapeExporter, "TransformPersistedStoredContours(candidate)", "PCB pad export must evaluate source-library contours without applying board placement rotation", failures);
            AssertContains(shapeExporter, "point.X * millimetersPerCoord", "source-library custom pad contours must retain footprint-local coordinates", failures);
            AssertContains(padContourReader, "ReadNamedChildDataStreams(libraryPath, requestedNames)", "selected pad export should directly read named PcbLib storage streams", failures);
            AssertContains(padContourReader, "if (requestedNames.All(parsedNames.Contains))", "targeted PcbLib reads should retain a full-library fallback when direct storage lookup fails", failures);
            AssertContains(padContourReader, "result[footprintName] = contours;", "persisted pad reads should record footprints without custom contours to avoid redundant region scans", failures);
            AssertContains(shapeExporter, "List<LinkedPadRegion> linkedRegions = persistedContours != null", "pad export should skip linked-region enumeration after persisted footprint data was parsed", failures);
            int customPadRegionIndex = shapeExporter.IndexOf("Internal_GetState_ExtractedPrimitiveOnLayer(layer)", StringComparison.Ordinal);
            int generatedPadRegionIndex = shapeExporter.IndexOf("Internal_GetState_RegionShapeOnLayer(layer)", StringComparison.Ordinal);
            if (customPadRegionIndex < 0 || generatedPadRegionIndex < 0 || customPadRegionIndex >= generatedPadRegionIndex)
                failures.Add("pad export must prefer extracted shape-based primitives over generated rectangular envelopes");
            AssertContains(shapeExporter, "RegionCenterDistance(documentRegion, padX, padY)", "pad region export must compare document-coordinate geometry with the pad anchor", failures);
            AssertContains(shapeExporter, "RegionCenterDistance(localRegion, padX, padY)", "pad region export must compare pad-local geometry with the pad anchor", failures);
            AssertContains(shapeExporter, "double maximumDistance = Math.Max(5.0, extent * 4.0);", "pad region export must reject distant SDK sentinel geometry", failures);
            AssertContains(shapeExporter, "TryCreateShapedPadPrimitiveFromLocalRegionBounds(", "four-point pad envelopes must be reconstructed using the pad shape", failures);
            AssertContains(shapeExporter, "pointCount > 8", "detailed custom pad contours must not be replaced by envelope reconstruction", failures);
            AssertContains(shapeExporter, "TShapeSubKind.eRoundedFinger", "rounded-finger custom pads must be reconstructed as rounded contours", failures);
            AssertContains(shapeExporter, "TShapeSubKind.eRoundedRectangle", "custom rounded rectangles must preserve rounded geometry", failures);
            AssertContains(shapeExporter, "customPadShape.Internal_GetProperty_CustomShapeKind(layer)", "pad reconstruction must read the explicit custom shape subtype", failures);
            AssertContains(shapeExporter, "pad4.HasCustomRoundedRectangle()", "custom rounded pads must use Altium's Pad4 shape flag when shape enums remain Custom", failures);
            AssertContains(shapeExporter, "pad2.GetState_CRPercentageOnLayer(layer)", "custom rounded pads must preserve Altium's per-layer corner radius percentage", failures);
            AssertContains(shapeExporter, "pad.Internal_GetState_TopShape()", "pad reconstruction must retain the base top-layer shape when ShapeOnLayer reports Custom", failures);
            AssertContains(shapeExporter, "pad.Internal_GetState_BotShape()", "pad reconstruction must retain the base bottom-layer shape when ShapeOnLayer reports Custom", failures);
            AssertContains(shapeExporter, "TransformPadRegionPoint(pad, x, y", "generated pad regions must be transformed from pad-local coordinates", failures);
            AssertContains(shapeExporter, "pad.GetState_Rotation() * Math.PI / 180.0", "generated pad regions must preserve pad rotation", failures);
            AssertContains(shapeExporter, "pad.GetState_XLocation() +", "generated pad regions must preserve pad X location", failures);
            AssertContains(shapeExporter, "pad.GetState_YLocation() +", "generated pad regions must preserve pad Y location", failures);
            AssertContains(shapeExporter, "(int)TObjectId.ePadObject", "pad export must enumerate footprint pad primitives", failures);
            AssertContains(shapeExporter, "private const string PadFillColor = \"#2F80ED\";", "pad SVG group must use a blue fill", failures);
            AssertContains(shapeExporter, "private const string PadFillOpacity = \"0.45\";", "pad SVG group must be semi-transparent", failures);
            AssertContains(shapeExporter, "StandaloneMountingHoleMinimumDiameterMm = 1.0", "board SVG export must require a 1 mm minimum standalone mounting-hole diameter", failures);
            AssertContains(shapeExporter, "iterator.AddFilter_ObjectSet(CreateObjectSet((int)TObjectId.ePadObject))", "board SVG export must use the typed SDK pad filter without requesting vias", failures);
            AssertContains(shapeExporter, "owningComponent != null", "board SVG export must reject component-owned pads from the standalone mounting-pad layer", failures);
            AssertContains(shapeExporter, "pad.GetState_HoleSize()", "standalone mounting-pad circles must use the actual drilled-hole diameter", failures);
            AssertContains(shapeExporter, "if (holeSizeCoord <= 0)", "standalone pads without drilled holes must be excluded", failures);
            AssertContains(shapeExporter, "writer.WriteStartElement(\"circle\")", "standalone mounting pads must be emitted as SVG circle elements", failures);
            AssertContains(shapeExporter, "writer.WriteAttributeString(\"id\", \"BoardShape\")", "board contour and standalone mounting pads must share the board-shape layer", failures);
            AssertContains(shapeExporter, "writer.WriteAttributeString(\"id\", \"StandaloneMountingPads\")", "standalone mounting pads must use a separate subgroup inside BoardShape", failures);
            AssertContains(shapeExporter, "WriteSvgGroup(writer, \"Pads\", padPrimitives, PadFillColor, PadFillOpacity);", "with-pads export must identify the pad SVG group as Pads", failures);
            AssertContains(shapeExporter, "WriteSvgGroup(writer, \"Shape\", shapePrimitives);", "shape exporter must identify the Mechanical 2 SVG group as Shape", failures);
            int padsSvgGroupIndex = shapeExporter.IndexOf("WriteSvgGroup(writer, \"Pads\"", StringComparison.Ordinal);
            int shapeSvgGroupIndex = shapeExporter.IndexOf("WriteSvgGroup(writer, \"Shape\"", StringComparison.Ordinal);
            if (padsSvgGroupIndex < 0 || shapeSvgGroupIndex < 0 || padsSvgGroupIndex >= shapeSvgGroupIndex)
                failures.Add("pad SVG group must be written before Shape so pads render behind the Mechanical 2 shape");
            AssertContains(module, "foreach (string error in result.Errors)", "selected-library export must collect footprint errors and continue with later libraries", failures);
            int selectedLibrariesHandlerStart = module.IndexOf("private void ExportSelectedShapeLibraries", StringComparison.Ordinal);
            int selectedLibrariesHandlerEnd = module.IndexOf("private static void ShowShapeExportErrors", selectedLibrariesHandlerStart, StringComparison.Ordinal);
            string selectedLibrariesHandler = selectedLibrariesHandlerStart >= 0 && selectedLibrariesHandlerEnd > selectedLibrariesHandlerStart
                ? module.Substring(selectedLibrariesHandlerStart, selectedLibrariesHandlerEnd - selectedLibrariesHandlerStart)
                : string.Empty;
            AssertDoesNotContain(selectedLibrariesHandler, "OpenDocument(", "selected-library export must not create Altium documents or recent-document entries", failures);
            AssertDoesNotContain(selectedLibrariesHandler, "CloseDocument(", "selected-library export must not use Workspace Manager's recent-document-producing close path", failures);
            AssertContains(shapeExportSettings, "shape-export-library-folder.txt", "selected-library source folder must persist across Altium restarts", failures);
            AssertContains(shapeExportSettings, "shape-export-folder.txt", "shape export target folder must persist across Altium restarts", failures);
            AssertContains(shapeExportProgress, "Text = \"Cancel\"", "shape export progress dialog must have a Cancel button", failures);
            AssertContains(shapeExportProgress, "IsCancellationRequested = true;", "shape export Cancel button must request cancellation", failures);
            AssertContains(shapeExportProgress, "TopMost = true;", "shape export progress dialog must remain visible above Altium", failures);
            AssertContains(shapeExportProgress, "BringToFront();", "shape export progress must return to the foreground after Altium activates a library", failures);
            AssertContains(module, "new ShapeExportErrorForm(message)", "shape export errors must use the selectable error dialog", failures);
            AssertContains(shapeExportErrors, "ReadOnly = true", "shape export error list must be read-only", failures);
            AssertContains(shapeExportErrors, "Multiline = true", "shape export error list must display all library and footprint errors", failures);
            AssertContains(shapeExportErrors, "Clipboard.SetText(errorTextBox.Text)", "shape export error dialog must copy the complete list to the clipboard", failures);
            AssertContains(shapeExportErrors, "Text = \"Copy to clipboard\"", "shape export error dialog must expose a clipboard button", failures);
            AssertContains(shapeExportErrors, "Size = new Size(200, 34)", "shape export clipboard button must fit its label and use the enlarged export-dialog button height", failures);
            AssertContains(shapeExportErrors, "Size = new Size(104, 34)", "shape export Close button must use the enlarged export-dialog button height", failures);
            AssertContains(shapeExportProgress, "Size = new Size(92, 32)", "shape export Cancel button must use the enlarged export-dialog button height", failures);
            AssertContains(module, "SwitchTopSignalLayer", "top command should dispatch to a PCB layer switch handler", failures);
            AssertContains(module, "SwitchBottomSignalLayer", "bottom command should dispatch to a PCB layer switch handler", failures);
            AssertContains(module, "SwitchNextSignalLayer", "next command should dispatch to a PCB layer switch handler", failures);
            AssertContains(module, "SwitchPreviousSignalLayer", "previous command should dispatch to a PCB layer switch handler", failures);
            AssertContains(module, "SwitchToSelectedPrimitiveLayer", "selected primitive command should dispatch to a PCB layer switch handler", failures);
            AssertContains(module, "ReprojectActiveFootprint3D", "Reproject command should dispatch to an active-footprint handler", failures);
            AssertContains(module, "AlignActiveFootprint3DModel", "Align command should dispatch to an active-footprint handler", failures);
            AssertContains(module, "CreateCustomPadFromSelected", "custom-pad command should dispatch to an active-footprint handler", failures);
            AssertContains(module, "DuplicateLayout", "Duplicate layout command should dispatch to a PCB dialog handler", failures);
            AssertContains(module, "TryCaptureLayoutDuplicationSession(argContext", "Duplicate layout handler must pass the PCB editor command view into capture", failures);
            AssertContains(module, "LayoutDuplicatorDialog", "Duplicate layout handler must open the layout duplicator dialog", failures);
            int reprojectHandlerStart = module.IndexOf("private void ReprojectActiveFootprint3D", StringComparison.Ordinal);
            int reprojectHandlerEnd = reprojectHandlerStart >= 0
                ? module.IndexOf("private void AlignActiveFootprint3DModel", reprojectHandlerStart, StringComparison.Ordinal)
                : -1;
            string reprojectHandler = reprojectHandlerStart >= 0 && reprojectHandlerEnd > reprojectHandlerStart
                ? module.Substring(reprojectHandlerStart, reprojectHandlerEnd - reprojectHandlerStart)
                : string.Empty;
            AssertDoesNotContain(reprojectHandler, "ShowInfo(", "Reproject command must not show a success dialog", failures);
            int alignHandlerStart = module.IndexOf("private void AlignActiveFootprint3DModel", StringComparison.Ordinal);
            int alignHandlerEnd = alignHandlerStart >= 0
                ? module.IndexOf("private void CreateCustomPadFromSelected", alignHandlerStart, StringComparison.Ordinal)
                : -1;
            string alignHandler = alignHandlerStart >= 0 && alignHandlerEnd > alignHandlerStart
                ? module.Substring(alignHandlerStart, alignHandlerEnd - alignHandlerStart)
                : string.Empty;
            AssertDoesNotContain(alignHandler, "ShowInfo(", "Align 3D model command must not show a success dialog", failures);
            AssertContains(module, "EEPCB.CreateCustomPadFromSelected(component)", "custom-pad command should call the PCB helper inside the edit transaction", failures);
            AssertContains(module, "MarkCurrentDocumentModified();", "custom-pad command must mark the document dirty without saving it", failures);
            int customPadHandlerStart = module.IndexOf("private void CreateCustomPadFromSelected", StringComparison.Ordinal);
            int customPadHandlerEnd = customPadHandlerStart >= 0
                ? module.IndexOf("private void SwitchTopSignalLayer", customPadHandlerStart, StringComparison.Ordinal)
                : -1;
            string customPadHandler = customPadHandlerStart >= 0 && customPadHandlerEnd > customPadHandlerStart
                ? module.Substring(customPadHandlerStart, customPadHandlerEnd - customPadHandlerStart)
                : string.Empty;
            AssertDoesNotContain(customPadHandler, "ShowInfo(", "custom-pad command must not show a success dialog", failures);

            AssertContains(eePcb, "SwitchToTopSignalLayer", "PCB helper must switch directly to the active board's top signal layer", failures);
            AssertContains(eePcb, "SwitchToBottomSignalLayer", "PCB helper must switch directly to the active board's bottom signal layer", failures);
            AssertContains(eePcb, "SwitchToNextSignalLayer", "PCB helper must cycle to the next displayed signal layer", failures);
            AssertContains(eePcb, "SwitchToPreviousSignalLayer", "PCB helper must cycle to the previous displayed signal layer", failures);
            AssertContains(eePcb, "SwitchToSelectedPrimitiveLayer", "PCB helper must switch to the active board selected primitive layer", failures);
            AssertContains(eePcb, "public static IPCB_Board GetCurrentPcbBoard", "PCB helper must expose the same active-board lookup used by PCB editor commands", failures);
            AssertContains(eePcb, "DXP.IServerDocumentView", "PCB helper active-board lookup must accept a command view fallback", failures);
            AssertContains(eePcb, "Internal_GetOwnerDocument", "PCB helper active-board lookup must inspect the command view owner document", failures);
            AssertContains(eePcb, "Internal_GetPCBBoardByPath", "PCB helper active-board lookup must resolve PCB boards by document path", failures);
            AssertContains(eePcb, "Internal_LoadPCBBoardByPath", "PCB helper active-board lookup must load PCB boards by document path if needed", failures);
            AssertContains(eePcb, "FindSelectedPrimitiveLayer", "selected-primitive layer switching must read the selected object layer before changing board current layer", failures);
            AssertContains(eePcb, "Internal_GetState_SelectecObject", "selected-primitive layer switching must use Altium's selected-object list", failures);
            AssertContains(eePcb, "TryGetPrimitiveLayer", "selected-primitive layer switching must accept selected primitives with V7 layer metadata", failures);
            AssertContains(eePcb, "SwitchToFirstDisplayedSignalLayer", "Top layer command must use the displayed signal-layer iterator fallback that works in PCB editor commands", failures);
            AssertContains(eePcb, "SwitchToLastDisplayedSignalLayer", "Bottom layer command must use the displayed signal-layer iterator fallback that works in PCB editor commands", failures);
            AssertContains(eePcb, "Internal_SignalLayerIterator", "Signal layer cycling must use Altium's signal-layer iterator", failures);
            AssertContains(eePcb, "GetState_LayerIsDisplayed", "Signal layer cycling must skip hidden/non-opened layers", failures);
            AssertContains(eePcb, "Internal_GetState_TopSignalLayer", "Top layer command must use the layer stack top signal layer", failures);
            AssertContains(eePcb, "Internal_GetState_BottomSignalLayer", "Bottom layer command must use the layer stack bottom signal layer", failures);
            AssertContains(eePcb, "SetState_CurrentLayerV7", "Layer switching must set the board current V7 layer", failures);
            AssertDoesNotContain(eePcb, "SetState_RouteToolPathLayer", "Layer switching must not update Altium's route tool path layer because it can rename the active signal layer", failures);
            AssertContains(eePcb, "ViewManager_UpdateLayerTabs", "Layer switching should refresh PCB layer tabs", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:Zoom\", \"Action=Redraw\")", "Layer switching should redraw the PCB editor after changing layer", failures);
            AssertContains(eePcb, "ClearMechanical2Projection", "Reproject 3D must clear Mechanical Layer 2 before regenerating projection", failures);
            AssertContains(eePcb, "SyncPcbLibComponentFromBoard(component)", "Reproject 3D cleanup must sync the active PcbLib footprint from its board view before enumerating old primitives", failures);
            AssertContains(eePcb, "TransferAllPrimitivesBackFromBoard", "Reproject 3D cleanup must call the PcbLib transfer-back API so visible board-view primitives are enumerable", failures);
            AssertContains(eePcb, "SyncPcbLibComponentToBoard(component)", "Reproject 3D must push regenerated projection primitives back to the active PcbLib board view", failures);
            AssertContains(eePcb, "private static void SyncPcbLibComponentToBoard", "Whole-footprint PcbLib component-to-board sync must stay scoped to reproject and not normal import", failures);
            AssertContains(eePcb, "SetImportedComponentBodyLayer(stepModel)", "Footprint import must place imported 3D model bodies on Mechanical Layer 1", failures);
            AssertContains(eePcb, "SetImportedComponentBodyLayer(body)", "3D body model-origin updates must restore Mechanical Layer 1 after SetState_FromModel", failures);
            AssertContains(eePcb, "V7_Layer.MechanicalLayer(1)", "Imported 3D bodies must use Altium's V7 mechanical layer index 1 helper like altium-mcp", failures);
            AssertDoesNotContain(eePcb, "TryInvoke(target, \"SetState_Layer\", layerNumber)", "Imported 3D bodies must not pass V7 layer number 1 into the legacy Layer setter", failures);
            AssertContains(footprint3dModel, "EEPCB.SetImportedComponentBodyLayer(body)", "Footprint import must restore Mechanical Layer 1 after adding the 3D body to the PcbLib", failures);
            AssertContains(footprint3dModel, "Add3dBodyProjection(c, projectionPrimitives, true)", "Footprint import must add generated Mechanical 2 projection primitives directly to both component and board view", failures);
            AssertContains(footprintData, "AddAssemblyTexts(c, ctx.HasAssemblyDesignatorText, ctx.HasAssemblyCommentText, ctx.Box.Height, ctx.ProjectionPrimitives, true)", "Footprint import must add generated Mechanical 2 assembly texts directly to both component and board view", failures);
            AssertDoesNotContain(footprintData, "SyncPcbLibComponentToBoard(c)", "Footprint import must not transfer all component primitives onto the board because it can disturb pad locations", failures);
            AssertContains(eePcb, "TransferAllPrimitivesOntoBoard", "Reproject 3D must call the PcbLib transfer-onto-board API after regenerating component primitives", failures);
            AssertContains(eePcb, "AddProjectionPrimitive(c, pcbPrimitive, addToBoardView)", "Generated Mechanical 2 projection primitives must use the common import/reproject ownership helper", failures);
            AssertContains(eePcb, "AddToPcbLibComponent(c, primitive)", "Default reprojected Mechanical 2 projection primitives must be added to the footprint component, not duplicated through the board helper", failures);
            AssertContains(eePcb, "EnumerateFilteredComponentProjectionPrimitives", "Reproject 3D cleanup must use filtered PcbLib iterators so old Mechanical 2 projection tracks are visible", failures);
            AssertContains(eePcb, "GetCurrentPcbLibraryBoard", "Reproject 3D cleanup must also scan the active PcbLib board for free Mechanical 2 projection primitives", failures);
            AssertContains(eePcb, "AddDistinctBoard(boards, GetCurrentPcbLibraryBoard())", "Reproject 3D cleanup must remove primitives from the active PcbLib board, not only the component board", failures);
            AssertContains(eePcb, "ClearMechanical2ByEditorCommand", "Reproject 3D cleanup must use PcbLib editor select/delete when SDK primitive enumeration hides visible Mechanical 2 objects", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:DeSelect\", \"Scope=All\")", "Reproject 3D cleanup must clear editor selection before selecting Mechanical 2 objects", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:Select\", \"Scope=Layer\")", "Reproject 3D cleanup must select all objects on Mechanical 2 through the PCB editor command", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:DeleteObjects\", \"Object=FOCUSED\")", "Reproject 3D cleanup must use the same DeleteObjects mode as the working altium-mcp PcbLib editor cleanup", failures);
            AssertContains(eePcb, "DXP.Utils.RunCommand", "Reproject 3D cleanup must send PCB editor commands through the view-less SDK command helper when CurrentView is unavailable", failures);
            AssertContains(eePcb, "MessageRouterSendCommandToModule", "Reproject 3D cleanup should retain message router fallback when a document view is available", failures);
            AssertContains(eePcb, "new V7_Layer(TLayerConstant.eMechanical2).Number()", "Reproject 3D cleanup must filter by V7 mechanical layer number, not raw TLayerConstant enum value", failures);
            AssertContains(eePcb, "CreatePcbLayerSet(TLayerConstant.eMechanical2)", "Reproject 3D cleanup must use Altium's typed IPCB_LayerSet factory for Mechanical 2 when available", failures);
            AssertContains(eePcb, "AddFilter_IPCB_LayerSet", "Reproject 3D cleanup must apply typed IPCB_LayerSet filters like AltiumScript LayerSet helpers", failures);
            AssertContains(eePcb, "AddFilter_ObjectSet", "Reproject 3D cleanup must filter iterators by projection object types", failures);
            AssertContains(eePcb, "AddFilter_LayerSet", "Reproject 3D cleanup must filter iterators by Mechanical 2", failures);
            AssertContains(eePcb, "ReprojectComponentBodySilhouette", "Reproject 3D must regenerate silhouette primitives from a 3D body", failures);
            AssertContains(eePcb, "Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()", "Reproject 3D must apply the common Altium 180-degree projection placement correction", failures);
            AssertContains(footprint3dModel, "Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()", "3D model import must apply the common Altium 180-degree projection placement correction", failures);
            AssertContains(stepSilhouette, "EasyEDALoaderHlrResult_", "OCCT HLR bridge must write result JSON to a temp file instead of parsing stdout that native OCCT diagnostics can pollute", failures);
            AssertContains(stepSilhouette, "startInfo.ArgumentList.Add(outputPath);", "OCCT HLR bridge must pass the temp JSON output path to StepOcctHlr", failures);
            AssertContains(stepSilhouette, "resultJson = File.Exists(outputPath) ? File.ReadAllText(outputPath) : \"\";", "OCCT HLR bridge must parse JSON from the temp output file", failures);

            AssertContains(eePcb, "CreateCustomPadFromSelected", "PCB helper must implement selected-geometry to custom-pad conversion", failures);
            AssertContains(eePcb, "GetSelectedObjects(board)", "custom-pad helper must read the editor selection from the active PcbLib board", failures);
            AssertContains(eePcb, "Internal_GetState_SelectecObject", "selected-object enumeration must use Altium's selected-object list", failures);
            AssertContains(eePcb, "CreateCustomPadWithEditorConversion", "custom-pad helper must use Altium's editor conversion flow", failures);
            AssertContains(eePcb, "PCB:CustomPadShape", "custom-pad helper must invoke Altium's native custom pad conversion command", failures);
            AssertContains(eePcb, "Action=Convert|Object=Track", "custom-pad helper must convert selected track/arc outline into a custom pad", failures);
            AssertContains(eePcb, "AddCustomPadContourTrack", "custom-pad helper must prepare temporary outline tracks like altium-mcp", failures);
            AssertContains(eePcb, "AddCustomPadContourArc", "custom-pad helper must preserve rounded pad corners with temporary outline arcs", failures);
            AssertContains(eePcb, "IPCB_Pad2", "custom-pad helper must read exact Altium rounded-corner radius when available", failures);
            AssertContains(eePcb, "GetState_CornerRadiusOnLayer", "custom-pad helper must preserve the source pad corner radius instead of guessing it", failures);
            AssertContains(eePcb, "GetFallbackPadCornerRadius", "custom-pad helper must not collapse rounded pads to sharp corners when exact radius is unavailable", failures);
            AssertContains(eePcb, "HasCustomRoundedRectangle", "custom-pad helper must detect Altium rounded-rectangle pad metadata", failures);
            AssertContains(eePcb, "SelectOnlyCustomPadConversionObjects", "custom-pad helper must select only the anchor pad and prepared outline before conversion", failures);
            AssertContains(eePcb, "DeleteCustomPadConversionObjects", "custom-pad helper must cleanup temporary/source primitives through the PCB editor delete path", failures);
            AssertContains(eePcb, "FindConvertedCustomPad", "custom-pad helper must verify Altium created a custom pad before deleting source primitives", failures);
            AssertContains(eePcb, "TShape.eCustomShape", "custom-pad helper must verify conversion by reading back the custom pad shape", failures);
            AssertDoesNotContain(eePcb, "CreateJoinedCustomPadPolygon(sources)", "custom-pad command must not use the failing manual polygon custom-shape path", failures);
            AssertContains(eePcb, "RemoveSelectedCustomPadSourcePrimitives", "custom-pad helper must replace selected pads/primitives after creating the custom pad", failures);
            AssertDoesNotContain(eePcb, "SaveDocument", "custom-pad command must not save the PCB library document", failures);
            AssertContains(footprint3dModel, "StepSilhouetteProjection.Generate(", "3D model import should project from in-memory STEP bytes instead of reloading the already-written body file", failures);
            AssertContains(footprint3dModel, "footprintModel,", "3D model import should pass the already-loaded STEP bytes to silhouette projection", failures);
            AssertContains(module, "ReprojectComponentBodySilhouette(component, out removedCount)", "Reproject 3D must only clear Mechanical 2 after projection generation succeeds", failures);
            AssertContains(eePcb, "BeginPcbPrimitiveModify(component)", "Reproject 3D must open a footprint primitive modify transaction for undo", failures);
            AssertContains(eePcb, "EndPcbPrimitiveModify(component, modifying, changed)", "Reproject 3D must close or cancel the footprint primitive modify transaction for undo", failures);
            AssertContains(eePcb, "SaveModelToFile failed for 3D body projection, trying model fallback", "Reproject 3D must continue to model export fallback when SaveModelToFile is unsupported", failures);
            AssertContains(eePcb, "AlignComponentBodiesToPads", "Align 3D model must align bodies using pad bounds", failures);
            AssertContains(eePcb, "body.BeginModify()", "Align 3D model must open a primitive modify transaction for undo", failures);
            AssertContains(eePcb, "body.EndModify()", "Align 3D model must close a changed primitive modify transaction for undo", failures);
            AssertContains(eePcb, "body.CancelModify()", "Align 3D model must cancel the primitive modify transaction when no change was applied", failures);
            AssertContains(eePcb, "TranslateComponentBodyModelOriginMm(body, move.XMm, move.YMm)", "Align 3D model must attempt body model-origin translation", failures);
            AssertDoesNotContain(eePcb, "body.MoveByXY", "Align 3D model must never move an already-owned component body with primitive MoveByXY in PcbLib", failures);
            AssertContains(eePcb, "FindCurrentPcbLibComponentFallback", "PcbLib commands must find the active footprint even when CurrentComponent is not populated", failures);

            AssertFileExists(layoutModelsPath, "Layout duplication models file must exist", failures);
            AssertFileExists(layoutCapturePath, "Layout duplication capture file must exist", failures);
            AssertFileExists(layoutMapperPath, "Layout duplication mapper file must exist", failures);
            AssertFileExists(layoutApplyPath, "Layout duplication apply file must exist", failures);
            AssertFileExists(layoutSchematicMatcherPath, "Layout duplication schematic matcher file must exist", failures);
            AssertFileExists(layoutDialogXamlPath, "Layout duplicator dialog XAML must exist", failures);
            AssertFileExists(layoutDialogCodePath, "Layout duplicator dialog code-behind must exist", failures);
            AssertFileExists(layoutViewModelsPath, "Layout duplicator view models must exist", failures);
            AssertFileExists(ollamaClientPath, "Ollama layout mapping client must exist", failures);

            AssertContains(layoutModels, "LayoutDuplicationSession", "layout models must represent the captured session", failures);
            AssertContains(layoutModels, "LayoutComponentSnapshot", "layout models must represent component snapshots", failures);
            AssertContains(layoutModels, "LayoutMappingValidationResult", "layout models must represent validation results", failures);
            AssertContains(layoutModels, "DefaultModelName = \"qwen3.5:9b\"", "Ollama defaults must use qwen3.5:9b", failures);
            AssertContains(layoutModels, "FallbackModelName = \"qwen2.5-coder:7b-instruct\"", "Ollama defaults must include qwen2.5 coder fallback", failures);
            AssertContains(layoutModels, "UseSchematicMatching", "layout mapping requests must carry the schematic matching option", failures);
            AssertContains(layoutModels, "SchematicHints", "layout mapping requests must carry schematic match hints", failures);
            AssertContains(layoutModels, "LayoutSchematicComponentHint", "layout models must represent schematic component hints", failures);
            AssertContains(layoutModels, "Description", "layout models must carry separate description metadata", failures);
            AssertContains(layoutModels, "IPCB_Board Board", "layout duplication sessions must retain the PCB board captured from command context", failures);

            AssertContains(layoutCapture, "TryCaptureLayoutDuplicationSession", "capture layer must expose selected-component validation", failures);
            AssertContains(layoutCapture, "DXP.IServerDocumentView", "capture layer must accept the PCB editor command view", failures);
            AssertContains(layoutCapture, "GetCurrentBoard(commandView)", "capture layer must resolve the board using the command view", failures);
            AssertContains(layoutCapture, "CaptureSelectedSourceComponents", "capture layer must capture selected PCB components", failures);
            AssertContains(layoutCapture, "GetSelectedObjects(board)", "capture layer must use Altium's selected-object list for selected components", failures);
            AssertContains(layoutCapture, "Internal_GetState_Component", "capture layer must map selected child primitives back to their parent component", failures);
            AssertContains(layoutCapture, "IPCB_Primitive selectedPrimitive", "capture layer must use typed PCB primitive parent-component access", failures);
            AssertContains(layoutCapture, "IsTextObject(selectedObject)", "capture layer must match selected designator/comment text back to components", failures);
            AssertContains(layoutCapture, "AddComponentSnapshotIfValid(selectedComponents, board, selectedObject)", "capture layer must snapshot selected components directly before any board scan", failures);
            AssertContains(layoutCapture, "EnumerateBoardObjects(board, (int)TObjectId.eComponentObject)", "capture layer must use a component-filtered iterator for target-table capture", failures);
            int sessionCaptureStart = layoutCapture.IndexOf("public static bool TryCaptureLayoutDuplicationSession(", StringComparison.Ordinal);
            int sessionCaptureEnd = sessionCaptureStart >= 0
                ? layoutCapture.IndexOf("public static List<LayoutComponentSnapshot> CaptureSelectedSourceComponents", sessionCaptureStart, StringComparison.Ordinal)
                : -1;
            string sessionCaptureMethod = sessionCaptureStart >= 0 && sessionCaptureEnd > sessionCaptureStart
                ? layoutCapture.Substring(sessionCaptureStart, sessionCaptureEnd - sessionCaptureStart)
                : string.Empty;
            AssertDoesNotContain(sessionCaptureMethod, "CaptureBoardComponents", "menu invocation must not scan every PCB component before opening the dialog", failures);
            AssertContains(layoutCapture, "EnsureEquivalentTargetComponentsFromSchematic", "target discovery must use schematic candidates before direct PCB component lookup", failures);
            AssertContains(layoutCapture, "EnsureDirectRefDesFamilyTargets", "target discovery must have a direct refdes fallback when schematic hints are unavailable", failures);
            AssertContains(layoutCapture, "Duplicate layout schematic target discovery", "target discovery must trace schematic candidate counts for live Altium diagnostics", failures);
            AssertContains(layoutCapture, "Duplicate layout direct refdes target discovery", "direct refdes target discovery must trace candidate counts for live Altium diagnostics", failures);
            AssertContains(layoutCapture, "LooseSame", "schematic target matching must tolerate schematic/PCB footprint and comment formatting differences", failures);
            AssertContains(layoutCapture, "SameDesignatorFamily", "schematic target matching must have a same-prefix fallback for equivalent repeated components", failures);
            AssertContains(layoutApply, "Internal_GetPcbComponentByRefDes", "layout duplication PCB access must resolve target candidates by refdes without full board scanning", failures);
            AssertContains(layoutCapture, "CaptureComponentSnapshot(board, primitive, includePads: false)", "initial target-table capture must not enumerate component pads while opening the dialog", failures);
            AssertContains(layoutCapture, "EnsurePadsCaptured", "pad/net metadata must be captured lazily only when routing copy needs it", failures);
            AssertContains(layoutCapture, "Internal_GetPrimitiveAt", "pad capture must use direct pad primitive lookup instead of a component group iterator", failures);
            int snapshotStart = layoutCapture.IndexOf("private static LayoutComponentSnapshot CaptureComponentSnapshot", StringComparison.Ordinal);
            int snapshotEnd = snapshotStart >= 0
                ? layoutCapture.IndexOf("public static void EnsurePadsCaptured", snapshotStart, StringComparison.Ordinal)
                : -1;
            string snapshotMethod = snapshotStart >= 0 && snapshotEnd > snapshotStart
                ? layoutCapture.Substring(snapshotStart, snapshotEnd - snapshotStart)
                : string.Empty;
            AssertDoesNotContain(snapshotMethod, "ReadLayerName", "initial component snapshot must not read component V7 layer while opening the dialog", failures);
            AssertContains(layoutCapture, "ComponentHasSelectedChild", "capture layer must accept component selections exposed through selected child primitives", failures);
            AssertContains(layoutCapture, "TraceSelectedComponentCapture", "capture layer must log selected-object diagnostics when no source component is detected", failures);
            AssertContains(layoutCapture, "CaptureSelectedRoutingPrimitives", "capture layer must capture selected routing primitives", failures);
            AssertContains(layoutCapture, "ReadDescription", "capture layer must separate source description from component comment", failures);
            AssertContains(layoutCapture, "ReadComponentLayerName", "capture layer must populate component layer cells without reading V7 layer metadata", failures);
            AssertContains(layoutCapture, "GetState_FlippedOnLayer", "capture layer must infer Top/Bottom layer from component flipped state", failures);
            AssertContains(layoutCapture, "exclude all selected source components", "capture layer must document excluding all selected source components from targets", failures);
            AssertContains(layoutCapture, "LayoutSchematicMatchContext", "capture layer must accept schematic matching context for destination ordering", failures);
            AssertContains(layoutCapture, "ScoreCandidate", "capture layer must use schematic scores to rank destination candidates when enabled", failures);
            AssertContains(layoutApply, "session.Board", "layout duplication apply must reuse the board captured before opening the dialog", failures);
            AssertContains(layoutApply, "return EEPCB.GetCurrentPcbBoard(commandView);", "layout duplication must reuse the working PCB editor active-board lookup", failures);
            AssertContains(layoutApply, "IPCB_Board typedBoard", "layout duplication PCB access must use typed board selected-object APIs", failures);
            AssertContains(layoutApply, "GetObjectIdentity", "layout duplication PCB access must compare selected child primitives by stable object identity", failures);
            AssertContains(layoutApply, "EnumerateBoardObjects(IPCB_Board board, params int[] objectIds)", "layout duplication PCB access must support filtered board iteration", failures);
            AssertContains(layoutApply, "AddFilter_ObjectSet", "layout duplication PCB access must filter board iterators by object type when possible", failures);
            AssertContains(layoutApply, "EnsureRoutingPadData", "layout duplication apply must capture pad/net data only when selected routing is being copied", failures);
            AssertContains(layoutApply, "ReadPadName", "layout duplication PCB access must read pad names through the typed pad API", failures);
            int selectedObjectsStart = layoutApply.IndexOf("public static List<object> GetSelectedObjects", StringComparison.Ordinal);
            int selectedObjectsEnd = selectedObjectsStart >= 0
                ? layoutApply.IndexOf("public static bool IsSelected", selectedObjectsStart, StringComparison.Ordinal)
                : -1;
            string selectedObjectsHelper = selectedObjectsStart >= 0 && selectedObjectsEnd > selectedObjectsStart
                ? layoutApply.Substring(selectedObjectsStart, selectedObjectsEnd - selectedObjectsStart)
                : string.Empty;
            AssertDoesNotContain(selectedObjectsHelper, "EnumerateBoardObjects(board)", "selected-object lookup must not fall back to full-board enumeration on PCB editor menu invocation", failures);

            AssertContains(layoutMapper, "BuildMappingPrompt", "mapper must build a constrained AI prompt", failures);
            AssertContains(layoutMapper, "ValidateMappingResponse", "mapper must validate AI mapping before edits", failures);
            AssertContains(layoutMapper, "ambiguous", "mapper must reject ambiguous AI mappings", failures);
            AssertContains(layoutMapper, "Do not return coordinates or edit commands", "mapper prompt must forbid AI-generated edit commands", failures);
            AssertContains(layoutMapper, "Schematic matching hints", "mapper prompt must include schematic hints when requested", failures);
            AssertContains(layoutMapper, "Prefer candidates on matching schematic", "mapper prompt must direct AI to prefer schematic matches", failures);
            AssertContains(layoutMapper, "description=", "mapper prompt must include component description", failures);
            AssertDoesNotContain(layoutMapper, " | part=", "mapper prompt must not include removed part-number metadata", failures);

            AssertContains(layoutSchematicMatcher, "TryBuildSchematicMatchContext", "schematic matcher must expose best-effort context capture", failures);
            AssertContains(layoutSchematicMatcher, "TraceContext", "schematic matcher must log hint counts for live target-discovery diagnostics", failures);
            AssertContains(layoutSchematicMatcher, "ScoreCandidate", "schematic matcher must rank PCB candidates by schematic context", failures);
            AssertContains(layoutSchematicMatcher, "SCHServer", "schematic matcher must use native C# SCH server access", failures);
            AssertDoesNotContain(layoutSchematicMatcher, "RunProcess", "schematic matcher must not invoke script commands", failures);
            AssertDoesNotContain(layoutSchematicMatcher, "AltiumScript", "schematic matcher must not invoke AltiumScript", failures);
            AssertDoesNotContain(layoutSchematicMatcher, "DelphiScript", "schematic matcher must not invoke DelphiScript", failures);
            AssertDoesNotContain(layoutSchematicMatcher, "layout_duplicator", "schematic matcher must not call MCP layout duplicator", failures);

            AssertContains(layoutApply, "ApplyLayoutDuplication", "apply layer must expose deterministic layout duplication", failures);
            AssertContains(layoutApply, "PCBServer.PreProcess()", "apply layer must wrap edits in an Altium undo transaction", failures);
            AssertContains(layoutApply, "PCBServer.PostProcess()", "apply layer must close the Altium undo transaction", failures);
            AssertContains(layoutApply, "ApplyPlacement", "apply layer must copy component placement/orientation/layer", failures);
            AssertContains(layoutApply, "ReplicateRoutingPrimitive", "apply layer must copy selected routing primitives in C#", failures);
            AssertContains(layoutApply, "eTrackObject", "routing copy must support tracks", failures);
            AssertContains(layoutApply, "eArcObject", "routing copy must support arcs", failures);
            AssertContains(layoutApply, "eViaObject", "routing copy must support vias", failures);
            AssertContains(layoutApply, "ePolyObject", "routing copy must support polygons", failures);
            AssertContains(layoutApply, "eRegionObject", "routing copy must support regions", failures);
            AssertContains(layoutApply, "eFillObject", "routing copy must support fills", failures);
            AssertDoesNotContain(layoutApply, "RunProcess", "layout duplication must not invoke script commands", failures);
            AssertDoesNotContain(layoutApply, "AltiumScript", "layout duplication must not invoke AltiumScript", failures);
            AssertDoesNotContain(layoutApply, "DelphiScript", "layout duplication must not invoke DelphiScript", failures);
            AssertDoesNotContain(layoutApply, "layout_duplicator_apply", "layout duplication must not call MCP layout duplicator", failures);
            AssertDoesNotContain(layoutApply, "InvokeMember", "layout duplication must not use generic COM late binding on Altium's UI thread", failures);
            AssertDoesNotContain(layoutApply, "SaveDocument", "layout duplication must not save the PCB", failures);

            AssertContains(ollamaClient, "GetInstalledModelsAsync", "Ollama client must list installed models", failures);
            AssertContains(ollamaClient, "GetLoadedModelsAsync", "Ollama client must list loaded models", failures);
            AssertContains(ollamaClient, "SelectInitialModel", "Ollama client must select loaded/last-used/default model in order", failures);
            AssertContains(ollamaClient, "PullModelAsync", "Ollama client must support model pull only after confirmation", failures);
            AssertContains(ollamaClient, "\"think\"", "Ollama requests must set think=false", failures);
            AssertContains(ollamaClient, "\"keep_alive\"", "Ollama requests must keep the selected model warm", failures);
            AssertContains(ollamaClient, "\"temperature\"", "Ollama requests must use deterministic temperature", failures);

            AssertContains(layoutDialogXaml, "sourceComponentsGrid", "layout dialog must show selected source components", failures);
            AssertContains(layoutDialogXaml, "targetAnchorsGrid", "layout dialog must show checked target anchors", failures);
            AssertContains(layoutDialogXaml, "modelComboBox", "layout dialog must expose model selection", failures);
            AssertContains(layoutDialogXaml, "useSchematicMatchingCheckBox", "layout dialog must expose optional schematic matching", failures);
            AssertContains(layoutDialogXaml, "Use schematic matching", "layout dialog must label optional schematic matching", failures);
            AssertContains(layoutDialogXaml, "Foreground=\"Black\"", "schematic matching checkbox text must stay black under Altium themes", failures);
            AssertContains(layoutDialogXaml, "IsChecked=\"True\"", "layout dialog must enable schematic matching by default", failures);
            AssertContains(layoutDialogXaml, "operationProgressBar", "layout dialog must show operation progress", failures);
            AssertContains(layoutDialogXaml, "Duplicate", "layout dialog must expose Duplicate command", failures);
            AssertContains(layoutDialogXaml, "Header=\"Comment\"", "layout dialog must show component comment", failures);
            AssertContains(layoutDialogXaml, "Header=\"Description\"", "layout dialog must show component description", failures);
            AssertDoesNotContain(layoutDialogXaml, "Part number", "layout dialog must remove the part-number column", failures);
            AssertContains(layoutDialogCode, "MessageBoxResult.Yes", "layout dialog must ask before pulling a missing model", failures);
            AssertContains(layoutDialogCode, "PullModelAsync", "layout dialog must pull a model only after user confirmation", failures);
            AssertContains(layoutDialogCode, "WarmModelAsync", "layout dialog should warm installed models", failures);
            AssertContains(layoutDialogCode, "useSchematicMatchingCheckBox.IsChecked == true", "layout dialog must read the optional schematic matching checkbox", failures);
            AssertContains(layoutDialogCode, "TryBuildSchematicMatchContext", "layout dialog must build schematic matching context only when requested", failures);
            AssertContains(layoutViewModels, "IsChecked", "target rows must have default-selected checkboxes", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("PcbLib action regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("PcbLib action regression test passed.");
            return 0;
        }

        private static int RunUlanziPluginTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string bridgePath = Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEdaCommandBridge.cs");
            string modulePath = Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs");
            string projectPath = Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.csproj");
            string insPath = Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.ins");
            string pluginRoot = Path.Combine(repoRoot, "UlanziStudioPlugin", "com.ulanzi.easyedaloader.ulanziPlugin");
            string manifestPath = Path.Combine(pluginRoot, "manifest.json");
            string appPath = Path.Combine(pluginRoot, "plugin", "app.js");
            string pipeClientPath = Path.Combine(pluginRoot, "plugin", "easyedaBridgeClient.js");
            string packagePath = Path.Combine(pluginRoot, "package.json");
            string installScriptPath = Path.Combine(repoRoot, "BuildAndInstall-UlanziStudio.ps1");
            string altiumInstallScriptPath = Path.Combine(repoRoot, "BuildAndInstall-Altium.ps1");
            string readmePath = Path.Combine(repoRoot, "README.md");

            AssertFileExists(bridgePath, "EasyEDALoader must include the named-pipe command bridge", failures);
            AssertFileExists(insPath, "EasyEDALoader must include an Altium INS registration file", failures);
            AssertFileExists(manifestPath, "Ulanzi Studio plugin must include a manifest", failures);
            AssertFileExists(appPath, "Ulanzi Studio plugin must include a Node main service", failures);
            AssertFileExists(pipeClientPath, "Ulanzi Studio plugin must include a named-pipe client", failures);
            AssertFileExists(packagePath, "Ulanzi Studio plugin must include package metadata for ws dependency install", failures);
            AssertFileExists(installScriptPath, "Ulanzi Studio plugin must include an installer script", failures);
            AssertFileExists(altiumInstallScriptPath, "Altium extension must include an installer script", failures);

            string bridge = ReadFileIfExists(bridgePath);
            string module = ReadFileIfExists(modulePath);
            string project = ReadFileIfExists(projectPath);
            string ins = ReadFileIfExists(insPath);
            string manifestText = ReadFileIfExists(manifestPath);
            string app = ReadFileIfExists(appPath);
            string pipeClient = ReadFileIfExists(pipeClientPath);
            string packageText = ReadFileIfExists(packagePath);
            string installScript = ReadFileIfExists(installScriptPath);
            string altiumInstallScript = ReadFileIfExists(altiumInstallScriptPath);
            string readme = ReadFileIfExists(readmePath);

            AssertContains(bridge, "NamedPipeServerStream", "Bridge must listen through a Windows named pipe", failures);
            AssertContains(bridge, "EasyEDA-Loader.CommandBridge", "Bridge must expose a stable pipe name for the Ulanzi plugin", failures);
            AssertContains(bridge, "IsPipeInstanceBusy", "Bridge must detect an already-owned named pipe when multiple Altium processes load the extension", failures);
            AssertContains(bridge, "disabled in this Altium process", "Bridge must disable itself instead of spinning when another process already owns the pipe", failures);
            AssertContains(bridge, "Task.Delay(TimeSpan.FromSeconds(1)", "Bridge must back off after listener failures instead of flooding the UI process", failures);
            AssertContains(bridge, "GetForegroundWindow", "Bridge must check the active foreground window before dispatch", failures);
            AssertContains(bridge, "GetWindowThreadProcessId", "Bridge must compare the foreground window owner process with Altium", failures);
            AssertContains(bridge, "Process.GetCurrentProcess().Id", "Bridge active-window check must require the Altium process to own the foreground window", failures);
            AssertContains(bridge, "altium-not-active", "Bridge must report a clear inactive-Altium error", failures);
            AssertContains(bridge, "CommandReceived", "Bridge must dispatch commands through an event back into EasyEDALoaderModule", failures);

            AssertContains(module, "EasyEdaCommandBridge", "EasyEDALoaderModule must create the bridge", failures);
            AssertContains(module, "HandleBridgeCommand", "EasyEDALoaderModule must route bridge commands to existing handlers", failures);
            AssertContains(module, "IsLoaderDialogOpen", "Bridge commands must be rejected while the EasyEDALoader dialog is open so Ulanzi inputs cannot queue behind the modal window", failures);
            AssertContains(module, "loader-dialog-open", "Bridge must report a clear busy error while the EasyEDALoader dialog is open", failures);
            AssertContains(module, "Interlocked.Exchange(ref loaderDialogOpen", "Dialog open state must be updated in a thread-visible way before ShowDialog blocks the UI thread", failures);
            AssertContains(module, "finally", "Dialog open state must be cleared even if ShowDialog throws", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandOpenLoader", "Bridge must expose Open Loader", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandReproject3D", "Bridge must expose Reproject 3D", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandAlign3DModel", "Bridge must expose Align 3D model", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandLayerTop", "Bridge must expose Top layer", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandLayerBottom", "Bridge must expose Bottom layer", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandLayerNext", "Bridge must expose Next signal layer", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandLayerPrevious", "Bridge must expose Previous signal layer", failures);
            AssertContains(module, "case EasyEdaCommandBridge.CommandLayerSelectedPrimitive", "Bridge must expose selected-primitive layer", failures);
            AssertContains(project, "EasyEdaCommandBridge.cs", "EasyEDA project must explicitly compile the command bridge", failures);
            AssertContains(ins, "SystemExtension   = True", "Altium must load EasyEDALoader as a system extension so the bridge starts without opening the EasyEDALoader dialog", failures);

            if (!string.IsNullOrWhiteSpace(manifestText))
            {
                try
                {
                    using (JsonDocument manifest = JsonDocument.Parse(manifestText))
                    {
                        JsonElement root = manifest.RootElement;
                        AssertEqual("com.ulanzi.ulanzideck.easyedaloader", root.GetProperty("UUID").GetString(), "Plugin UUID must match the installed UlanziDeck plugin namespace", failures);
                        AssertEqual("plugin/app.js", root.GetProperty("CodePath").GetString(), "Plugin must use the Node main service", failures);
                        JsonElement actions = root.GetProperty("Actions");
                        AssertJsonActionExists(actions, "EasyEDA Loader Dial", "com.ulanzi.ulanzideck.easyedaloader.dial", failures);
                        AssertJsonActionExists(actions, "Open Loader", "com.ulanzi.ulanzideck.easyedaloader.openloader", failures);
                        AssertJsonActionExists(actions, "Next Signal Layer", "com.ulanzi.ulanzideck.easyedaloader.layernext", failures);
                        AssertJsonActionExists(actions, "Previous Signal Layer", "com.ulanzi.ulanzideck.easyedaloader.layerprevious", failures);
                        AssertJsonActionExists(actions, "Top Signal Layer", "com.ulanzi.ulanzideck.easyedaloader.layertop", failures);
                        AssertJsonActionExists(actions, "Bottom Signal Layer", "com.ulanzi.ulanzideck.easyedaloader.layerbottom", failures);
                        AssertJsonActionExists(actions, "Switch to Selected Primitive Layer", "com.ulanzi.ulanzideck.easyedaloader.layerselectedprimitive", failures);
                        AssertJsonActionExists(actions, "Reproject 3D", "com.ulanzi.ulanzideck.easyedaloader.reproject3d", failures);
                        AssertJsonActionExists(actions, "Align 3D Model", "com.ulanzi.ulanzideck.easyedaloader.align3dmodel", failures);
                        foreach (JsonElement action in actions.EnumerateArray())
                        {
                            AssertJsonArrayContains(action.GetProperty("Controllers"), "Encoder", "Each EasyEDA action must be assignable to the Ulanzi knob like System Hotkey", failures);
                            if (action.TryGetProperty("Devices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() > 0)
                                failures.Add("EasyEDA actions must not filter Devices because Ulanzi Studio hides filtered plugins for the currently connected Deck model");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add("Ulanzi manifest must parse as JSON: " + ex.Message);
                }
            }

            AssertContains(app, "onDialRotateLeft", "Plugin must handle counter-clockwise dial rotation", failures);
            AssertContains(app, "onDialRotateRight", "Plugin must handle clockwise dial rotation", failures);
            AssertContains(app, "com.ulanzi.ulanzideck.easyedaloader", "Plugin runtime UUID must match the installed UlanziDeck plugin namespace", failures);
            AssertContains(app, "commandByActionId", "Plugin must map separate action assignments to EasyEDALoader commands", failures);
            AssertContains(app, "resolveCommand", "Plugin must resolve action-specific commands from Ulanzi event context", failures);
            AssertContains(app, "message?.uuid", "Plugin button resolution must use Ulanzi's action UUID field, not the per-slot actionid instance field", failures);
            AssertContains(app, "decodedContext.uuid", "Plugin button resolution must recover the action UUID from SDK contexts generated as uuid/key/actionid", failures);
            AssertContains(app, "onKeyDown", "Plugin must handle button action key-down events because Ulanzi button slots do not always emit run", failures);
            AssertContains(app, "runCommandFromEvent", "Plugin must share command dispatch between run, key, and encoder events", failures);
            AssertContains(app, "recentKeyCommands", "Plugin must de-duplicate button key-down and run events for the same action", failures);
            AssertContains(app, "actionInstances.get", "Plugin must resolve button commands from the action stored during onAdd when key events omit actionid", failures);
            AssertContains(app, "runCommandFromEvent(message, null)", "Button events must not default to Open Loader when the action cannot be resolved", failures);
            AssertContains(app, "if (!command)", "Plugin must ignore unresolved button events instead of running Open Loader", failures);
            AssertContains(app, "com.ulanzi.ulanzideck.easyedaloader.layernext", "Plugin must handle the separate Next Signal Layer action", failures);
            AssertContains(app, "com.ulanzi.ulanzideck.easyedaloader.layerprevious", "Plugin must handle the separate Previous Signal Layer action", failures);
            AssertContains(app, "com.ulanzi.ulanzideck.easyedaloader.layertop", "Plugin must handle the separate Top Signal Layer action", failures);
            AssertContains(app, "com.ulanzi.ulanzideck.easyedaloader.layerbottom", "Plugin must handle the separate Bottom Signal Layer action", failures);
            AssertContains(app, "com.ulanzi.ulanzideck.easyedaloader.layerselectedprimitive", "Plugin must handle the separate selected-primitive layer action", failures);
            AssertContains(app, "CommandLayerPrevious", "Left rotation must call previous signal layer", failures);
            AssertContains(app, "CommandLayerNext", "Right rotation must call next signal layer", failures);
            AssertContains(app, "CommandOpenLoader", "Plugin must expose Open Loader as a keypad/run action", failures);
            AssertContains(app, "CommandLayerTop", "Plugin must expose Top layer command", failures);
            AssertContains(app, "CommandLayerBottom", "Plugin must expose Bottom layer command", failures);
            AssertContains(app, "CommandLayerSelectedPrimitive", "Plugin must expose selected-primitive layer command", failures);
            AssertDoesNotContain(app, "onDialDown", "Plugin must not bind a dial press command", failures);
            AssertDoesNotContain(app, "onDialUp", "Plugin must not bind a dial release command", failures);
            AssertContains(pipeClient, "\\\\\\\\.\\\\pipe\\\\EasyEDA-Loader.CommandBridge", "Plugin pipe client must connect to the EasyEDALoader pipe", failures);
            AssertContains(pipeClient, "net.createConnection", "Plugin pipe client must use Node named-pipe support", failures);
            AssertContains(pipeClient, "commandQueue", "Plugin pipe client must serialize bridge requests so fast knob movement cannot race the named-pipe listener", failures);
            AssertContains(pipeClient, "activeCommand === CommandOpenLoader", "Plugin pipe client must reject inputs while Open Loader is still showing its modal window instead of queueing them for later replay", failures);
            AssertContains(pipeClient, "activeCommandStartedAt", "Plugin pipe client must track long-running bridge commands so later inputs are not queued indefinitely", failures);
            AssertContains(pipeClient, "EasyEDALoader is busy", "Plugin pipe client must report busy state instead of replaying queued commands after the loader window closes", failures);
            AssertContains(pipeClient, "connectWithRetry", "Plugin pipe client must retry transient pipe connection gaps from rapid events", failures);
            AssertContains(pipeClient, "isTransientPipeError", "Plugin pipe client must distinguish transient pipe errors from a missing bridge", failures);
            AssertContains(pipeClient, "code === 'ENOENT'", "Plugin pipe client must translate missing bridge pipe errors into an actionable message", failures);
            AssertContains(pipeClient, "Reinstall or restart the EasyEDALoader Altium extension", "Missing bridge pipe message must explain the Altium-side fix", failures);
            AssertContains(packageText, "\"ws\"", "Plugin package must declare the Ulanzi SDK websocket dependency", failures);

            AssertContains(installScript, "Resolve-UlanziStudioPluginRoot", "Installer must auto-detect Ulanzi Studio plugin folders", failures);
            AssertContains(installScript, "com.ulanzi.easyedaloader.ulanziPlugin", "Installer must copy the EasyEDALoader plugin package", failures);
            AssertContains(installScript, "npm install --omit=dev", "Installer must install Node runtime dependencies when npm is available", failures);
            AssertContains(installScript, "$env:APPDATA", "Installer must search user AppData Ulanzi plugin locations", failures);
            AssertContains(installScript, "$env:LOCALAPPDATA", "Installer must search LocalAppData Ulanzi plugin locations", failures);
            AssertContains(installScript, "$env:PROGRAMDATA", "Installer must search ProgramData Ulanzi plugin locations", failures);
            AssertContains(installScript, "Ulanzi\\UlanziDeck\\Plugins", "Installer must search the actual UlanziDeck user plugin folder", failures);
            AssertContains(installScript, "Ulanzi\\UlanziDeck\\System\\Plugins", "Installer must also search UlanziDeck system plugin folders used by visible bundled plugins", failures);
            AssertContains(installScript, "Install-UlanziPluginPackage", "Installer must install the package into every selected Ulanzi plugin root", failures);
            AssertContains(installScript, "Restart-UlanziStudio", "Installer must be able to restart Ulanzi Studio after plugin installation", failures);
            AssertContains(installScript, "CloseMainWindow", "Installer restart must try a clean Ulanzi Studio shutdown before forcing processes", failures);
            AssertContains(installScript, "Start-Process", "Installer restart must restore Ulanzi Studio after copying the plugin", failures);
            AssertContains(installScript, "$npmOutput", "Installer must capture npm stdout so installed path reporting stays clean", failures);
            AssertContains(altiumInstallScript, "UTF8Encoding($false)", "Altium installer must preserve the no-BOM UTF-8 registry encoding required by Altium's extension loader", failures);
            AssertContains(altiumInstallScript, "Registry backup:", "Altium installer must report the registry backup created before rewriting ExtensionsRegistry.xml", failures);

            AssertContains(readme, "Ulanzi Studio plugin", "README must document the Ulanzi Studio plugin", failures);
            AssertContains(readme, "BuildAndInstall-UlanziStudio.ps1", "README must document the Ulanzi installer script", failures);
            AssertContains(readme, "Altium window must be active", "README must document the active-Altium-window guard", failures);
            AssertContains(readme, "Dial clockwise", "README must document clockwise dial behavior", failures);
            AssertContains(readme, "Dial counter-clockwise", "README must document counter-clockwise dial behavior", failures);
            AssertContains(readme, "Switch to Selected Primitive Layer", "README must document the selected-primitive layer Ulanzi action", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Ulanzi Studio plugin regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Ulanzi Studio plugin regression test passed.");
            return 0;
        }

        private static int RunAsyncImportTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();

            AssertAwaitsUseConfigureAwaitFalse(
                Path.Combine(repoRoot, "EasyEDA-Loader", "ModelCache.cs"),
                "ModelCache",
                failures);
            AssertAwaitsUseConfigureAwaitFalse(
                Path.Combine(repoRoot, "EasyEDA-Loader", "API", "EasyedaApi.cs"),
                "EasyedaApi",
                failures);
            AssertNoBlockingTaskWaits(
                Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintShapes", "EeFootprint3dModel.cs"),
                "EeFootprint3dModel",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Async footprint import regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Async footprint import regression test passed.");
            return 0;
        }

        private static void AssertAwaitsUseConfigureAwaitFalse(string filePath, string label, List<string> failures)
        {
            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.Contains("await "))
                    continue;

                string expression = line;
                // ConfigureAwait can be placed on the next line of the same
                // statement. Do not diagnose line wrapping as a missing call.
                for (int next = i + 1; expression.IndexOf(';') < 0 && next < lines.Length; next++)
                    expression += " " + lines[next].Trim();

                if (!expression.Contains("ConfigureAwait(false)"))
                    failures.Add(label + " line " + (i + 1).ToString(CultureInfo.InvariantCulture) + " awaits without ConfigureAwait(false): " + line);
            }
        }

        private static void AssertNoBlockingTaskWaits(string filePath, string label, List<string> failures)
        {
            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Contains(".Wait()") || line.Contains(".Result"))
                    failures.Add(label + " line " + (i + 1).ToString(CultureInfo.InvariantCulture) + " blocks synchronously on a Task: " + line);
            }
        }

        private static void AssertNoForbiddenLibrarySaveCalls(string filePath, string label, List<string> failures)
        {
            string[] lines = File.ReadAllLines(filePath);
            string[] forbiddenTokens =
            {
                "SaveDocument",
                "SaveObject",
                "WorkspaceManager:SaveObject",
                "CloseDocument"
            };

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                foreach (string forbiddenToken in forbiddenTokens)
                {
                    if (line.IndexOf(forbiddenToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        failures.Add(
                            label +
                            " line " +
                            (i + 1).ToString(CultureInfo.InvariantCulture) +
                            " contains forbidden import-time library persistence call: " +
                            line);
                    }
                }
            }
        }

        private static string FindRepoRoot()
        {
            string directory = AppContext.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) &&
                    Directory.Exists(Path.Combine(directory, "EasyEDA-Loader")))
                    return directory;

                directory = Directory.GetParent(directory)?.FullName;
            }

            directory = Directory.GetCurrentDirectory();
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) &&
                    Directory.Exists(Path.Combine(directory, "EasyEDA-Loader")))
                    return directory;

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate repository root.");
        }

        private static int RunMetadataTests()
        {
            var failures = new List<string>();

            string synthesizedDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "MR30PB-M30.A.G.Y",
                componentDescription: "MR30PB-M30.A.G.Y",
                packageTitle: "MR30PB-M30.A.G.Y",
                packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                partNumber: "MR30PB-M30.A.G.Y",
                mounting: "through-hole");

            AssertEqual(
                "CONN-TH package, through-hole",
                synthesizedDescription,
                "part-number-only candidates should be ignored when selecting the footprint description",
                failures);

            string usefulDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "Amass MR30PB connector, 3 position plug, through-hole",
                componentDescription: "MR30PB-M30.A.G.Y",
                packageTitle: "MR30PB-M30.A.G.Y",
                packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                partNumber: "MR30PB-M30.A.G.Y",
                mounting: "through-hole");

            AssertEqual(
                "Amass MR30PB connector, 3 position plug, through-hole",
                usefulDescription,
                "descriptive product text should be kept",
                failures);

            string richDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "",
                componentDescription: "",
                packageTitle: "CONN-TH_MR30PB-M30.A.G.Y",
                packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                partNumber: "MR30PB-M30.A.G.Y",
                mounting: "through-hole",
                parameters: new Dictionary<string, string>
                {
                    { "Manufacturer", "AMASS(艾迈斯)" },
                    { "Manufacturer Part", "MR30PB-M30.A.G.Y" },
                    { "LCSC Part Name", "AMASS(艾迈斯)三芯动力电池马达e电调航模插头连接器 PCB板立式插头公头 金 黄MR30PB-M30.A.G.Y" }
                },
                geometry: CreateMr30FootprintGeometry());

            AssertEqual(
                "AMASS MR30PB, 3-position vertical through-hole male PCB power plug connector, 3.5 mm pitch, 11.9 x 5.3 mm body",
                richDescription,
                "C30170185-style metadata should synthesize a detailed footprint description",
                failures);

            string inferredConnectorDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "CONN-SMD_DF56_40S_0.3V_51",
                componentDescription: "DF56-40S-0.3V(51)",
                packageTitle: "CONN-SMD_DF56_40S_0.3V_51",
                packageName: "CONN-SMD_DF56_40S_0.3V_51",
                partNumber: "CONN-SMD_DF56_40S_0.3V_51",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 40,
                    PitchMm = 0.3,
                    BodyWidthMm = 6.2,
                    BodyHeightMm = 4.8
                });

            AssertEqual(
                "HRS DF56, 40-position vertical SMT female connector, 0.3 mm pitch, 6.2 x 4.8 mm body",
                inferredConnectorDescription,
                "connector footprint descriptions should be synthesized from package identity and geometry without manufacturer metadata",
                failures);

            string catalogConnectorDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "CONN-SMD_DF56_40S_0.3V_51",
                componentDescription: "CONN-SMD_DF56_40S_0.3V_51",
                packageTitle: "CONN-SMD_DF56_40S_0.3V_51",
                packageName: "CONN-SMD_DF56_40S_0.3V_51",
                partNumber: "C5334147",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 40,
                    PitchMm = 0.3,
                    BodyWidthMm = 6.2,
                    BodyHeightMm = 4.8
                });

            AssertEqual(
                "HRS DF56, 40-position vertical SMT female connector, 0.3 mm pitch, 6.2 x 4.8 mm body",
                catalogConnectorDescription,
                "connector footprint descriptions should ignore LCSC catalog IDs and use inferred manufacturer identity",
                failures);

            string parameterBlobConnectorDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "Number of Pins:40P Pitch:0.6mm Mounting Type:Surface Mount,Vertical Number of Rows:2 Connection Type:Slot Type Butting Contact Material:Phosphor bronze Contact Plating:Tin",
                componentDescription: "Number of Pins:40P Pitch:0.6mm Mounting Type:Surface Mount,Vertical Number of Rows:2 Connection Type:Slot Type Butting Contact Material:Phosphor bronze Contact Plating:Tin",
                packageTitle: "CONN-SMD_DF56_40S_0.3V_51",
                packageName: "CONN-SMD_DF56_40S_0.3V_51",
                partNumber: "DF56C-40S-0.3V(51)",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 40,
                    PitchMm = 0.3,
                    BodyWidthMm = 6.2,
                    BodyHeightMm = 4.8
                });

            AssertEqual(
                "HRS DF56C, 40-position vertical SMT female connector, 0.3 mm pitch, 6.2 x 4.8 mm body",
                parameterBlobConnectorDescription,
                "connector footprint descriptions should synthesize rich text instead of keeping EasyEDA parameter blobs",
                failures);

            string genericPackageDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "LM317",
                componentDescription: "SOT-223",
                packageTitle: "SOT-223",
                packageName: "SOT-223",
                partNumber: "LM317",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 3,
                    PitchMm = 2.3,
                    BodyWidthMm = 6.5,
                    BodyHeightMm = 3.5
                });

            AssertEqual(
                "SOT-223 package, 3-pad SMT footprint, 2.3 mm pitch, 6.5 x 3.5 mm body",
                genericPackageDescription,
                "generic footprint descriptions should use package, pad count, mounting, and body geometry",
                failures);

            AssertEqual(
                "SOT-223",
                FootprintMetadataSelector.SelectName("SOT-223", "LM317"),
                "generic IC footprint names should prefer the standard package over the part number",
                failures);

            AssertEqual(
                "MR30PB-M30.A.G.Y",
                FootprintMetadataSelector.SelectName("CONN-TH_MR30PB-M30.A.G.Y", "MR30PB-M30.A.G.Y"),
                "part-number-specific connector footprint names should keep the manufacturer part number",
                failures);

            AssertEqual(
                "DF56C-30S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51", "DF56C-30S-0.3V(51)"),
                "part-number-specific connector footprint names should handle package suffix notation variants",
                failures);

            AssertEqual(
                "DF56C-26S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_26P-P0.30_DF56C-26S-0.3V51", "DF56C-26S-0.3V(51)"),
                "part-number-specific connector footprint names should handle omitted suffix separators",
                failures);

            AssertEqual(
                "DF56-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "DF56-40S-0.3V(51)"),
                "part-number-specific connector footprint names should handle underscore-separated package names",
                failures);

            AssertEqual(
                "DF56C-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "DF56C-40S-0.3V(51)"),
                "part-number-specific connector footprint names should prefer manufacturer part numbers even when package family omits a variant letter",
                failures);

            AssertEqual(
                "DF56C-26S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_26P-P0.30_DF56C-26S-0.3V51", "CONN-SMD_26P-P0.30_DF56C-26S-0.3V51"),
                "part-number-specific connector footprint names should be inferred when import passes the package as the part number",
                failures);

            AssertEqual(
                "DF56-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "CONN-SMD_DF56_40S_0.3V_51"),
                "underscore-separated connector footprint names should be inferred when import passes the package as the part number",
                failures);

            AssertEqual(
                "DF56-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "C5334147"),
                "underscore-separated connector footprint names should be inferred when import passes an LCSC catalog ID",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Metadata regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Metadata regression test passed.");
            return 0;
        }

        private static int RunSymbolRuleTests()
        {
            var failures = new List<string>();

            AssertNear(98.4251968503937, SymbolImportRules.GostGridMil, 0.0000001, "GOST schematic grid must be exactly 2.5 mm in mils", failures);
            AssertNear(196.850393700787, SymbolImportRules.GostPinPitchMil, 0.0000001, "generated schematic pins must advance by 5.0 mm rows", failures);
            AssertNear(196.850393700787, SymbolImportRules.GostPinLengthMil, 0.0000001, "generated schematic pin length must be 5.0 mm", failures);

            AssertEqual("Pin 1", SymbolImportRules.FormatPinName("1", "1", true), "numeric connector pins should have readable Pin N names", failures);
            AssertEqual("Pin 12", SymbolImportRules.FormatPinName("", "12", true), "numeric connector pin names should be normalized even when the designator is absent", failures);
            AssertEqual("D+", SymbolImportRules.FormatPinName("2", "D+", true), "non-numeric connector pin names should keep electrical meaning", failures);
            AssertEqual("1", SymbolImportRules.FormatPinName("1", "1", false), "numeric IC pin names should not be rewritten as connector contacts", failures);

            AssertEqual("XS?", SymbolImportRules.SelectDesignator("J?", "USB4110", "USB Type-C receptacle", "USB-C-SMD"), "USB receptacles should use the socket designator family", failures);
            AssertEqual("XP?", SymbolImportRules.SelectDesignator("J?", "DF40C-10DP-0.4V", "board-to-board header", "CONN-SMD"), "headers should use the plug/header designator family", failures);
            AssertEqual("DD?", SymbolImportRules.SelectDesignator("U?", "CH334P", "USB hub controller", "SOP-16"), "IC source U? designators should map to GOST DD?", failures);

            AssertEqual("Разъём USB", SymbolImportRules.SelectValueType("XS?", "USB4110", "USB Type-C receptacle", "USB-C-SMD"), "USB connector value type should use the USB connector vocabulary", failures);
            AssertEqual("Разъём", SymbolImportRules.SelectValueType("XP?", "DF40C", "mezzanine header", "CONN-SMD"), "generic connectors should use connector value type", failures);
            AssertEqual("Микросхема", SymbolImportRules.SelectValueType("DD?", "CH334P", "USB hub controller", "SOP-16"), "ICs should use microcircuit value type", failures);

            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("Footprint") ? "custom" : "not custom", "footprint model name must not be added as a custom parameter", failures);
            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("FootprintLibrary") ? "custom" : "not custom", "footprint library link must not be added as a custom parameter", failures);
            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("Package") ? "custom" : "not custom", "package must not be added as a custom parameter", failures);
            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("mounting") ? "custom" : "not custom", "mounting must not be added as a custom parameter", failures);
            AssertEqual("custom", SymbolImportRules.IsCustomParameter("Manufacturer") ? "custom" : "not custom", "manufacturer remains a custom GOST parameter", failures);

            AssertEqual(
                "3-position vertical through-hole male PCB power plug connector, 3.5 mm pitch",
                SymbolImportRules.SelectSymbolDescription(
                    productDescription: "",
                    componentDescription: "",
                    packageTitle: "MR30PB-M30.A.G.Y",
                    packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                    partNumber: "MR30PB-M30.A.G.Y",
                    mounting: "through-hole",
                    parameters: new Dictionary<string, string>
                    {
                        { "Manufacturer", "AMASS(艾迈斯)" },
                        { "Manufacturer Part", "MR30PB-M30.A.G.Y" },
                        { "LCSC Part Name", "AMASS(艾迈斯)三芯动力电池马达e电调航模插头连接器 PCB板立式插头公头 金 黄MR30PB-M30.A.G.Y" }
                    },
                    geometry: CreateMr30FootprintGeometry()),
                "schematic description should synthesize key selection facts without manufacturer or part-number-only text",
                failures);

            AssertEqual(
                "BM08B-GHS-TBT",
                SymbolImportRules.SelectLibraryComment("BM08B-GHS-TBT"),
                "symbol comment should equal design item ID instead of Altium default *",
                failures);

            AssertEqual(
                "X?",
                SymbolImportRules.SelectVisibleDesignator("X?"),
                "visible schematic designator should use the selected GOST prefix instead of Altium default *",
                failures);

            AssertEqual(
                "BM08B-GHS-TBT",
                SymbolImportRules.SelectDesignItemId(
                    manufacturerPart: "",
                    symbolName: "*",
                    componentTitle: "C123456",
                    searchResultName: "C123456",
                    searchPart: "BM08B-GHS-TBT",
                    lcscNumber: "C123456",
                    szlcscNumber: ""),
                "design item ID must skip EasyEDA placeholder * and catalog IDs before falling back to manufacturer-style search part",
                failures);

            AssertEqual(
                "DD?",
                SymbolImportRules.SelectDesignator("*", "STM32F103C8T6", "", "LQFP-48"),
                "placeholder source designator must not become the visible schematic designator",
                failures);

            AssertEqual(
                "",
                SymbolImportRules.SelectLibraryComment("*"),
                "symbol comment selector must reject EasyEDA placeholder *",
                failures);

            AssertEqual(
                "BM08B-GHS-TBT",
                SymbolImportRules.SelectDesignItemId(
                    manufacturerPart: "BM08B-GHS-TBT",
                    symbolName: "EasyEDA Generic Name",
                    componentTitle: "C123456",
                    searchResultName: "C123456",
                    searchPart: "C123456",
                    lcscNumber: "C123456",
                    szlcscNumber: "C123456"),
                "design item ID should prefer manufacturer part number over EasyEDA or LCSC identifiers",
                failures);

            AssertEqual(
                "USB4110-GF-A",
                SymbolImportRules.SelectDesignItemId(
                    manufacturerPart: "",
                    symbolName: "USB4110-GF-A",
                    componentTitle: "C999999",
                    searchResultName: "C999999",
                    searchPart: "C999999",
                    lcscNumber: "C999999",
                    szlcscNumber: "C999999"),
                "design item ID should fall back to EasyEDA manufacturer-style symbol name before LCSC identifiers",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Symbol rule regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Symbol rule regression test passed.");
            return 0;
        }

        private static FootprintDescriptionGeometry CreateMr30FootprintGeometry()
        {
            return new FootprintDescriptionGeometry
            {
                PositionCount = 3,
                PitchMm = 3.5,
                BodyWidthMm = 11.9,
                BodyHeightMm = 5.3
            };
        }

        private static void AssertEqual(string expected, string actual, string message, List<string> failures)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                failures.Add(message + ": expected '" + expected + "', got '" + actual + "'.");
        }

        private static void AssertContains(string text, string expectedSubstring, string message, List<string> failures)
        {
            if (text == null || text.Replace("\r\n", "\n").IndexOf(expectedSubstring.Replace("\r\n", "\n"), StringComparison.Ordinal) < 0)
                failures.Add(message + ": missing '" + expectedSubstring + "'.");
        }

        private static void AssertFileExists(string filePath, string message, List<string> failures)
        {
            if (!File.Exists(filePath))
                failures.Add(message + ": missing file '" + filePath + "'.");
        }

        private static string ReadFileIfExists(string filePath)
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
        }

        private static void AssertJsonArrayContains(JsonElement array, string expectedValue, string message, List<string> failures)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                failures.Add(message + ": JSON value is not an array.");
                return;
            }

            foreach (JsonElement item in array.EnumerateArray())
            {
                if (string.Equals(item.GetString(), expectedValue, StringComparison.Ordinal))
                    return;
            }

            failures.Add(message + ": missing '" + expectedValue + "'.");
        }

        private static void AssertJsonActionExists(JsonElement actions, string expectedName, string expectedUuid, List<string> failures)
        {
            if (actions.ValueKind != JsonValueKind.Array)
            {
                failures.Add("Ulanzi manifest Actions value is not an array.");
                return;
            }

            foreach (JsonElement action in actions.EnumerateArray())
            {
                string name = action.TryGetProperty("Name", out JsonElement nameElement) ? nameElement.GetString() : string.Empty;
                string uuid = action.TryGetProperty("UUID", out JsonElement uuidElement) ? uuidElement.GetString() : string.Empty;
                if (string.Equals(name, expectedName, StringComparison.Ordinal) &&
                    string.Equals(uuid, expectedUuid, StringComparison.Ordinal))
                    return;
            }

            failures.Add("Ulanzi manifest must expose action '" + expectedName + "' with UUID '" + expectedUuid + "'.");
        }

        private static void AssertDoesNotContain(string text, string unexpectedSubstring, string message, List<string> failures)
        {
            if (text != null && text.IndexOf(unexpectedSubstring, StringComparison.Ordinal) >= 0)
                failures.Add(message + ": found '" + unexpectedSubstring + "'.");
        }

        private static void AssertAppearsBefore(
            string text,
            string earlierSubstring,
            string laterSubstring,
            string message,
            List<string> failures)
        {
            int earlierIndex = text?.IndexOf(earlierSubstring, StringComparison.Ordinal) ?? -1;
            int laterIndex = text?.IndexOf(laterSubstring, StringComparison.Ordinal) ?? -1;
            if (earlierIndex < 0 || laterIndex < 0 || earlierIndex >= laterIndex)
            {
                failures.Add(
                    message +
                    ": expected '" +
                    earlierSubstring +
                    "' before '" +
                    laterSubstring +
                    "'.");
            }
        }

        private static void AssertNear(double expected, double actual, double tolerance, string message, List<string> failures)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                failures.Add(
                    message +
                    ": expected '" +
                    expected.ToString(CultureInfo.InvariantCulture) +
                    "', got '" +
                    actual.ToString(CultureInfo.InvariantCulture) +
                    "'.");
            }
        }

        private static int SaveSilhouetteProjectionImage(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette <input.step> <output.png> [--rotx deg] [--roty deg] [--rotz deg] [--rotation2d deg] [--size pixels] [--padding pixels] [--no-grid] [--no-axes]");
                return 2;
            }

            string inputPath = args[1];
            string outputPath = args[2];
            double rotX = 0.0;
            double rotY = 0.0;
            double rotZ = 0.0;
            double rotation2D = 0.0;
            int imageSizePixels = 1600;
            int paddingPixels = 90;
            bool drawGrid = true;
            bool drawAxes = true;

            for (int index = 3; index < args.Length; index++)
            {
                string option = args[index];
                if (IsOption(option, "--no-grid"))
                {
                    drawGrid = false;
                    continue;
                }

                if (IsOption(option, "--no-axes"))
                {
                    drawAxes = false;
                    continue;
                }

                if (IsOption(option, "--rotx"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotX))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--roty"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotY))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--rotz"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotZ))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--rotation2d"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotation2D))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--size"))
                {
                    if (!TryReadIntOption(args, ref index, option, out imageSizePixels))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--padding"))
                {
                    if (!TryReadIntOption(args, ref index, option, out paddingPixels))
                        return 2;
                    continue;
                }

                Console.Error.WriteLine("Unknown silhouette option: " + option);
                return 2;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            byte[] stepData = File.ReadAllBytes(inputPath);
            var placement = CreateDefaultSilhouettePlacement();
            placement.RotX = rotX;
            placement.RotY = rotY;
            placement.RotZ = rotZ;
            placement.Rotation2D = rotation2D;

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(stepData, placement);
            var renderOptions = new StepSilhouetteImageRenderOptions
            {
                ImageSizePixels = imageSizePixels,
                PaddingPixels = paddingPixels,
                DrawGrid = drawGrid,
                DrawAxes = drawAxes,
                Title = Path.GetFileName(inputPath)
            };
            StepSilhouetteImageRenderer.SavePng(primitives, outputPath, renderOptions);

            int lineCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
            int arcCount = primitives.Count - lineCount;
            Console.WriteLine("Silhouette image written: " + Path.GetFullPath(outputPath));
            Console.WriteLine(
                "Primitives: " +
                lineCount.ToString(CultureInfo.InvariantCulture) +
                " line(s), " +
                arcCount.ToString(CultureInfo.InvariantCulture) +
                " arc(s), " +
                primitives.Count.ToString(CultureInfo.InvariantCulture) +
                " total.");
            return 0;
        }

        private static int SaveSilhouettePrimitiveDump(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette-dump <input.step> <output.csv>");
                return 2;
            }

            string inputPath = args[1];
            string outputPath = args[2];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(
                File.ReadAllBytes(inputPath),
                CreateDefaultSilhouettePlacement());
            using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                writer.WriteLine("index,kind,x1,y1,x2,y2,centerX,centerY,radius,startAngle,endAngle");
                for (int index = 0; index < primitives.Count; index++)
                {
                    StepSilhouettePrimitive primitive = primitives[index];
                    writer.Write(index.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Kind.ToString());
                    writer.Write(",");
                    writer.Write(primitive.X1.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Y1.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.X2.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Y2.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.CenterX.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.CenterY.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Radius.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.StartAngle.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.WriteLine(primitive.EndAngle.ToString(CultureInfo.InvariantCulture));
                }
            }

            Console.WriteLine("Silhouette primitive dump written: " + Path.GetFullPath(outputPath));
            Console.WriteLine("Primitives: " + primitives.Count.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunOcctHlrBenchmark(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-benchmark <input.step> [--repeat count]");
                return 2;
            }

            string inputPath = args[1];
            int repeatCount = 1;
            for (int index = 2; index < args.Length; index++)
            {
                string option = args[index];
                if (IsOption(option, "--repeat"))
                {
                    if (!TryReadIntOption(args, ref index, option, out repeatCount))
                        return 2;
                    repeatCount = Math.Max(1, repeatCount);
                    continue;
                }

                Console.Error.WriteLine("Unknown OCCT HLR benchmark option: " + option);
                return 2;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            byte[] stepData = File.ReadAllBytes(inputPath);
            bool failed = false;
            for (int runIndex = 1; runIndex <= repeatCount; runIndex++)
            {
                var stopwatch = Stopwatch.StartNew();
                IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(
                    stepData,
                    CreateDefaultSilhouettePlacement());
                stopwatch.Stop();

                int lineCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
                int arcCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Arc);
                Console.WriteLine(
                    "run_index=" + runIndex.ToString(CultureInfo.InvariantCulture) +
                    " total_ms=" + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " line_count=" + lineCount.ToString(CultureInfo.InvariantCulture) +
                    " arc_count=" + arcCount.ToString(CultureInfo.InvariantCulture) +
                    " primitive_count=" + primitives.Count.ToString(CultureInfo.InvariantCulture));

                if (lineCount < OcctHlrBenchmarkMinimumExpectedLines ||
                    arcCount < OcctHlrBenchmarkMinimumExpectedArcs)
                {
                    failed = true;
                    Console.Error.WriteLine(
                        "OCCT HLR benchmark primitive count below smoke threshold: " +
                        lineCount.ToString(CultureInfo.InvariantCulture) +
                        " line(s), " +
                        arcCount.ToString(CultureInfo.InvariantCulture) +
                        " arc(s), " +
                        primitives.Count.ToString(CultureInfo.InvariantCulture) +
                        " total.");
                }
            }

            return failed ? 1 : 0;
        }

        private static StepSilhouettePlacement CreateDefaultSilhouettePlacement()
        {
            return new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -0.5,
                    Bottom = -0.5,
                    Right = 0.5,
                    Top = 0.5
                },
                RotX = 0.0,
                RotY = 0.0,
                RotZ = 0.0,
                Rotation2D = 0.0
            };
        }

        private static bool TryReadDoubleOption(string[] args, ref int index, string option, out double value)
        {
            value = 0.0;
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing value for " + option);
                return false;
            }

            index++;
            if (!double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                Console.Error.WriteLine("Invalid numeric value for " + option + ": " + args[index]);
                return false;
            }

            return true;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static bool TryReadIntOption(string[] args, ref int index, string option, out int value)
        {
            value = 0;
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing value for " + option);
                return false;
            }

            index++;
            if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                Console.Error.WriteLine("Invalid integer value for " + option + ": " + args[index]);
                return false;
            }

            return true;
        }

        private static bool IsOption(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void VerifyPostCleanProjections(
            List<string> originalFiles,
            Dictionary<string, string> generatedCleanByName,
            string originalProjectionDirectory,
            string cleanProjectionDirectory,
            StepProjectionOptions projectionOptions,
            Dictionary<string, List<string>> detectionViewNamesByFileName,
            FullTestDetectionCache detectionCache,
            ProjectionVerificationTimings projectionTimings,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            var matchedFileNames = generatedCleanByName.Keys
                .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchedFileNames.Count == 0)
                return;

            var originalByName = originalFiles.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            int comparedImages = 0;
            int checkedRegions = 0;

            foreach (string fileName in matchedFileNames)
            {
                if (!originalByName.TryGetValue(fileName, out string originalFile))
                {
                    failures.Add("Generated clean file has no matching Original model: " + fileName);
                    continue;
                }

                projectionTimings.Measure(
                    "post_clean_detection_ms",
                    () => detectionCache.GetReport(originalFile));
                var detectionRegions = projectionTimings.Measure(
                    "post_clean_detection_region_projection_ms",
                    () => detectionCache.GetDetectionRegions(originalFile, projectionOptions).ToList());

                string modelName = Path.GetFileNameWithoutExtension(fileName);
                var detectedViewNames = detectionRegions
                    .Select(region => region.ViewName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                detectionViewNamesByFileName[fileName] = detectedViewNames;
                if (detectedViewNames.Count == 0)
                {
                    if (RequiresAutomaticWatermarkCleanup(fileName))
                    {
                        postCleanFaultFileNames.Add(fileName);
                        failures.Add(fileName + " has no detected watermark cleanup views for a known watermark fixture.");
                    }

                    continue;
                }

                var renderOptions = CreateProjectionOptionsForViews(detectedViewNames, projectionOptions);
                projectionTimings.Measure(
                    "original_detection_side_projection_render_ms",
                    () => ProjectFileIfNeeded(originalFile, originalProjectionDirectory, renderOptions));

                foreach (string viewName in detectedViewNames)
                {
                    string originalProjectionPath = Path.Combine(originalProjectionDirectory, modelName + "__" + viewName + ".png");
                    string cleanProjectionPath = Path.Combine(cleanProjectionDirectory, modelName + "__" + viewName + ".png");

                    if (!File.Exists(originalProjectionPath))
                    {
                        failures.Add("Original projection is missing: " + originalProjectionPath);
                        continue;
                    }

                    if (!File.Exists(cleanProjectionPath))
                    {
                        failures.Add("Clean projection is missing: " + cleanProjectionPath);
                        continue;
                    }

                    var viewRegions = detectionRegions
                        .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    projectionTimings.Measure(
                        "original_vs_clean_projection_compare_ms",
                        () => VerifyPostCleanProjectionImage(
                            fileName,
                            viewName,
                            originalProjectionPath,
                            cleanProjectionPath,
                            viewRegions,
                            postCleanFaultFileNames,
                            failures,
                            visualFailures));

                    comparedImages++;
                    checkedRegions += viewRegions.Count;
                }
            }

            Console.WriteLine(
                "Post-clean projection verification: models=" +
                matchedFileNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", images=" +
                comparedImages.ToString(CultureInfo.InvariantCulture) +
                ", detected regions=" +
                checkedRegions.ToString(CultureInfo.InvariantCulture));
        }

        private static StepProjectionOptions CreateVerificationProjectionOptions()
        {
            return new StepProjectionOptions
            {
                ImageSizePixels = VerificationProjectionImageSizePixels,
                PaddingPixels = VerificationProjectionPaddingPixels,
                WriteMetadata = false,
                SkipGeometryModelForExternalRender = true,
                MaxParallelFiles = 2
            };
        }

        private static StepProjectionOptions CreateProjectionOptionsForViews(
            IReadOnlyList<string> viewNames,
            StepProjectionOptions template)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = template.ImageSizePixels,
                PaddingPixels = template.PaddingPixels,
                WriteMetadata = template.WriteMetadata,
                SkipGeometryModelForExternalRender = template.SkipGeometryModelForExternalRender,
                MaxParallelFiles = template.MaxParallelFiles,
                RenderMode = template.RenderMode
            };

            foreach (string viewName in viewNames)
                options.ViewNames.Add(viewName);

            return options;
        }

        private static void VerifyPostCleanProjectionImage(
            string fileName,
            string viewName,
            string originalProjectionPath,
            string cleanProjectionPath,
            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            using (var originalImage = SKBitmap.Decode(originalProjectionPath))
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (originalImage == null || cleanImage == null)
                {
                    failures.Add(fileName + " has an unreadable original or clean projection on " + viewName + ".");
                    return;
                }

                if (originalImage.Width != cleanImage.Width || originalImage.Height != cleanImage.Height)
                {
                    failures.Add(fileName + " original and clean projections have different sizes on " + viewName + ".");
                    return;
                }

                bool[] allowedMask = BuildAllowedChangeMask(
                    originalImage.Width,
                    originalImage.Height,
                    detectionRegions,
                    AllowedDetectionRegionPaddingPixels);

                int changedOutsideRegion = 0;
                int firstOutsideX = -1;
                int firstOutsideY = -1;
                int[] originalPixels = CopyBitmapPixelsToInt32Rows(originalImage);
                int[] cleanPixels = CopyBitmapPixelsToInt32Rows(cleanImage);

                for (int y = 0; y < originalImage.Height; y++)
                {
                    int row = y * originalImage.Width;
                    for (int x = 0; x < originalImage.Width; x++)
                    {
                        int pixelIndex = row + x;
                        if (!PixelsDifferent(originalPixels[pixelIndex], cleanPixels[pixelIndex], ProjectionDifferenceTolerance))
                            continue;

                        if (allowedMask[pixelIndex])
                            continue;

                        changedOutsideRegion++;
                        if (firstOutsideX < 0)
                        {
                            firstOutsideX = x;
                            firstOutsideY = y;
                        }
                    }
                }

                int allowedOutsideRegionChanges = GetAllowedOutsideRegionChanges(originalImage.Width, originalImage.Height);
                if (changedOutsideRegion > allowedOutsideRegionChanges)
                {
                    postCleanFaultFileNames.Add(fileName);
                    string message =
                        fileName +
                        " changed outside detected cleanup region on " +
                        viewName +
                        ": pixels=" +
                        changedOutsideRegion.ToString(CultureInfo.InvariantCulture) +
                        ", allowed=" +
                        allowedOutsideRegionChanges.ToString(CultureInfo.InvariantCulture) +
                        ", first=(" +
                        firstOutsideX.ToString(CultureInfo.InvariantCulture) +
                        "," +
                        firstOutsideY.ToString(CultureInfo.InvariantCulture) +
                        ").";
                    failures.Add(
                        message);
                    visualFailures.Add(new ProjectionVisualFailure
                    {
                        Category = "Original vs Clean: outside detected region",
                        FileName = fileName,
                        ViewName = viewName,
                        Message = message,
                        LeftLabel = "Original",
                        LeftImagePath = originalProjectionPath,
                        RightLabel = "Clean",
                        RightImagePath = cleanProjectionPath
                    });
                }

                foreach (StepProjectionDetectionRegion region in detectionRegions)
                {
                    if (!IsVisualDetectionRegion(region))
                        continue;

                    VerifyCleanedRegionFlatness(fileName, viewName, originalImage, cleanImage, region, postCleanFaultFileNames, failures);
                }
            }
        }

        private static int GetAllowedOutsideRegionChanges(int imageWidth, int imageHeight)
        {
            return Math.Max(
                1,
                (int)Math.Round(
                    imageWidth * imageHeight * MaxOutsideDetectionRegionChangeRatio,
                    MidpointRounding.AwayFromZero));
        }

        private static bool[] BuildAllowedChangeMask(
            int imageWidth,
            int imageHeight,
            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions,
            int paddingPixels)
        {
            var mask = new bool[imageWidth * imageHeight];
            foreach (StepProjectionDetectionRegion region in detectionRegions)
            {
                int left = Math.Max(0, region.RectangleX - paddingPixels);
                int top = Math.Max(0, region.RectangleY - paddingPixels);
                int right = Math.Min(imageWidth - 1, region.RectangleX + region.RectangleWidth - 1 + paddingPixels);
                int bottom = Math.Min(imageHeight - 1, region.RectangleY + region.RectangleHeight - 1 + paddingPixels);
                if (right < left || bottom < top)
                    continue;

                for (int y = top; y <= bottom; y++)
                {
                    int row = y * imageWidth;
                    for (int x = left; x <= right; x++)
                        mask[row + x] = true;
                }
            }

            return mask;
        }

        private static void VerifyCleanedRegionFlatness(
            string fileName,
            string viewName,
            SKBitmap originalImage,
            SKBitmap cleanImage,
            StepProjectionDetectionRegion region,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures)
        {
            int left = Math.Max(0, region.RectangleX);
            int top = Math.Max(0, region.RectangleY);
            int right = Math.Min(cleanImage.Width - 1, region.RectangleX + region.RectangleWidth - 1);
            int bottom = Math.Min(cleanImage.Height - 1, region.RectangleY + region.RectangleHeight - 1);
            int width = right - left + 1;
            int height = bottom - top + 1;
            if (width < 16 || height < 16 || width * height < 500)
                return;

            double originalEdgeRatio = MeasureRegionEdgeRatio(originalImage, left, top, right, bottom);
            double cleanEdgeRatio = MeasureRegionEdgeRatio(cleanImage, left, top, right, bottom);
            if (originalEdgeRatio < MinOriginalRegionEdgeRatioForFlatness)
                return;

            if (cleanEdgeRatio <= MaxCleanedRegionEdgeRatio ||
                cleanEdgeRatio <= originalEdgeRatio * MaxRetainedRegionEdgeRatio)
                return;

            postCleanFaultFileNames.Add(fileName);
            failures.Add(
                fileName +
                " cleaned region still has non-flat visual detail on " +
                viewName +
                " at [" +
                left.ToString(CultureInfo.InvariantCulture) +
                "," +
                top.ToString(CultureInfo.InvariantCulture) +
                " " +
                width.ToString(CultureInfo.InvariantCulture) +
                "x" +
                height.ToString(CultureInfo.InvariantCulture) +
                "]: clean edge ratio=" +
                cleanEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                ", original edge ratio=" +
                originalEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                ".");
        }

        private static bool IsVisualDetectionRegion(StepProjectionDetectionRegion region)
        {
            return region != null &&
                string.Equals(region.Kind, "visual", StringComparison.OrdinalIgnoreCase);
        }

        private static double MeasureRegionEdgeRatio(SKBitmap image, int left, int top, int right, int bottom)
        {
            int highContrastEdges = 0;
            int sampledEdges = 0;

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    SKColor current = image.GetPixel(x, y);
                    if (IsBackgroundLike(current))
                        continue;

                    if (x < right)
                    {
                        SKColor next = image.GetPixel(x + 1, y);
                        if (!IsBackgroundLike(next))
                        {
                            sampledEdges++;
                            if (ColorDistance(current, next) > FlatnessEdgeThreshold)
                                highContrastEdges++;
                        }
                    }

                    if (y < bottom)
                    {
                        SKColor next = image.GetPixel(x, y + 1);
                        if (!IsBackgroundLike(next))
                        {
                            sampledEdges++;
                            if (ColorDistance(current, next) > FlatnessEdgeThreshold)
                                highContrastEdges++;
                        }
                    }
                }
            }

            if (sampledEdges == 0)
                return 0.0;

            return (double)highContrastEdges / sampledEdges;
        }

        private static bool PixelsDifferent(SKColor left, SKColor right, int tolerance)
        {
            return ColorDistance(left, right) > tolerance ||
                Math.Abs(left.Alpha - right.Alpha) > tolerance;
        }

        private static bool PixelsDifferent(int left, int right, int tolerance)
        {
            int color0 = Math.Abs((left & 0xFF) - (right & 0xFF));
            int color1 = Math.Abs(((left >> 8) & 0xFF) - ((right >> 8) & 0xFF));
            int color2 = Math.Abs(((left >> 16) & 0xFF) - ((right >> 16) & 0xFF));
            int alpha = Math.Abs(((left >> 24) & 0xFF) - ((right >> 24) & 0xFF));
            return Math.Max(color0, Math.Max(color1, color2)) > tolerance ||
                alpha > tolerance;
        }

        private static int ColorDistance(SKColor left, SKColor right)
        {
            int red = Math.Abs(left.Red - right.Red);
            int green = Math.Abs(left.Green - right.Green);
            int blue = Math.Abs(left.Blue - right.Blue);
            return Math.Max(red, Math.Max(green, blue));
        }

        private static bool IsBackgroundLike(SKColor color)
        {
            return color.Red >= 245 && color.Green >= 245 && color.Blue >= 245;
        }

        private static void VerifyCleanupIgnoresMarkedOptions(List<string> originalFiles, List<string> failures)
        {
            if (originalFiles.Count == 0)
                return;

            byte[] originalStep = File.ReadAllBytes(originalFiles[0]);
            byte[] automaticClean = StepWatermarkCleaner.Clean(originalStep, new StepWatermarkCleanerOptions());
            var markedOptions = new StepWatermarkCleanerOptions
            {
                UseMarkedRegionsOnly = true
            };
            markedOptions.MarkedRegions.Add(new StepWatermarkMarkedRegion
            {
                ViewName = "z_plus",
                UAxis = 0,
                USign = 1,
                VAxis = 1,
                VSign = 1,
                DepthAxis = 2,
                DepthSign = 1,
                ModelUMin = -1000.0,
                ModelUMax = 1000.0,
                ModelVMin = -1000.0,
                ModelVMax = 1000.0,
                ScalePixelsPerModelUnit = 1.0,
                ImageWidth = 2000,
                ImageHeight = 2000,
                RectangleX = 0,
                RectangleY = 0,
                RectangleWidth = 2000,
                RectangleHeight = 2000
            });

            byte[] markedClean = StepWatermarkCleaner.Clean(originalStep, markedOptions);
            if (!automaticClean.SequenceEqual(markedClean))
                failures.Add("Clean output changed when marker-only options were supplied; cleanup must use automatic detection only.");
        }

        private static int RunRegenerateDetectionDebugImages()
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string cleanDirectory = Path.Combine(dataRoot, "Clean");
            string projectionDirectory = Path.Combine(dataRoot, "Projection");
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            string detectionDirectory = Path.Combine(cleanDirectory, "Detection");
            var originalFiles = GetStepFiles(originalDirectory);
            var originalBaseNames = new HashSet<string>(
                originalFiles.Select(file => Path.GetFileNameWithoutExtension(file)),
                StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            VerifyDetectionDebugImages(
                originalFiles,
                originalBaseNames,
                projectionDirectory,
                markedDirectory,
                detectionDirectory,
                new FullTestDetectionCache(),
                regenerateImages: true,
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Detection debug image regeneration failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Detection debug image regeneration passed.");
            return 0;
        }

        private static void VerifyDetectionDebugImages(
            List<string> originalFiles,
            HashSet<string> originalBaseNames,
            string projectionDirectory,
            string markedDirectory,
            string detectionDirectory,
            FullTestDetectionCache detectionCache,
            bool regenerateImages,
            List<string> failures)
        {
            if (!Directory.Exists(markedDirectory))
            {
                failures.Add("Marked directory was not found: " + markedDirectory);
                return;
            }

            if (!Directory.Exists(projectionDirectory))
            {
                failures.Add("Projection directory was not found: " + projectionDirectory);
                return;
            }

            Directory.CreateDirectory(detectionDirectory);

            Stopwatch stopwatch = Stopwatch.StartNew();
            if (regenerateImages)
            {
                foreach (string staleImage in Directory.GetFiles(detectionDirectory, "*.png"))
                    File.Delete(staleImage);
            }

            int regeneratedModels = 0;
            long loadMarkedRegionsMs = 0;
            long detectMs = 0;
            long projectDetectionFileMs = 0;
            var expectedNames = new List<string>();
            foreach (string originalFile in originalFiles)
            {
                Stopwatch stageStopwatch = Stopwatch.StartNew();
                var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                    originalFile,
                    projectionDirectory,
                    markedDirectory);
                stageStopwatch.Stop();
                loadMarkedRegionsMs += stageStopwatch.ElapsedMilliseconds;

                stageStopwatch = Stopwatch.StartNew();
                var detectionReport = detectionCache.GetReport(originalFile);
                stageStopwatch.Stop();
                detectMs += stageStopwatch.ElapsedMilliseconds;

                if (regenerateImages)
                {
                    stageStopwatch = Stopwatch.StartNew();
                    StepProjectionReport projectionReport = StepProjectionRenderer.ProjectDetectionFile(
                        originalFile,
                        detectionDirectory,
                        detectionReport,
                        new StepProjectionOptions
                        {
                            WriteMetadata = false
                        },
                        markedRegions);
                    stageStopwatch.Stop();
                    projectDetectionFileMs += stageStopwatch.ElapsedMilliseconds;
                    expectedNames.AddRange(projectionReport.OutputFiles.Select(Path.GetFileName));
                    regeneratedModels++;
                }
                else
                {
                    expectedNames.AddRange(GetExpectedDetectionDebugImageNames(originalFile, detectionReport, markedRegions));
                }
            }

            expectedNames = expectedNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var expectedSet = new HashSet<string>(expectedNames, StringComparer.OrdinalIgnoreCase);
            var actualNames = Directory.GetFiles(detectionDirectory, "*.png")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            stopwatch.Stop();
            Console.WriteLine(
                "Detection debug images: expected=" +
                expectedNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", generated=" +
                actualNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", regenerated models=" +
                regeneratedModels.ToString(CultureInfo.InvariantCulture) +
                ", elapsed=" +
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " ms");
            Console.WriteLine("  detection_debug_load_marked_regions_ms=" + loadMarkedRegionsMs.ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("  detection_debug_detect_ms=" + detectMs.ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("  detection_debug_project_file_ms=" + projectDetectionFileMs.ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("  detection_debug_skipped_existing=" + (!regenerateImages).ToString(CultureInfo.InvariantCulture).ToLowerInvariant());

            if (!regenerateImages)
                return;

            if (actualNames.Count != expectedNames.Count)
            {
                failures.Add(
                    "Detection debug image count differs from renderer outputs: expected=" +
                    expectedNames.Count.ToString(CultureInfo.InvariantCulture) +
                    ", generated=" +
                    actualNames.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            var actualSet = new HashSet<string>(actualNames, StringComparer.OrdinalIgnoreCase);

            foreach (string expectedName in expectedNames)
            {
                if (!actualSet.Contains(expectedName))
                    failures.Add("Detection debug image is missing for renderer output: " + expectedName);
            }

            foreach (string actualName in actualNames)
            {
                if (!expectedSet.Contains(actualName))
                    failures.Add("Detection debug image was not reported by renderer: " + actualName);
            }
        }

        private static IReadOnlyList<string> GetExpectedDetectionDebugImageNames(
            string originalFile,
            StepWatermarkDetectionReport detectionReport,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions)
        {
            if (detectionReport?.Regions == null || detectionReport.Regions.Count == 0)
                return Array.Empty<string>();

            string modelName = Path.GetFileNameWithoutExtension(originalFile);
            var viewNames = detectionReport.Regions
                .Where(region => region != null && !string.IsNullOrWhiteSpace(region.ViewName))
                .Select(region => region.ViewName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var markedViewNames = new HashSet<string>(
                (markedRegions ?? Array.Empty<StepWatermarkMarkedRegion>())
                    .Select(region => region.ViewName)
                    .Where(viewName => !string.IsNullOrWhiteSpace(viewName)),
                StringComparer.OrdinalIgnoreCase);
            if (markedViewNames.Count > 0)
                viewNames = viewNames.Where(viewName => markedViewNames.Contains(viewName)).ToList();

            return viewNames
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .Select(viewName => modelName + "__" + viewName + ".png")
                .ToList();
        }

        private static bool IsDetectionDebugImageCacheFresh(
            string originalFile,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions,
            string detectionDirectory)
        {
            if (markedRegions == null || markedRegions.Count == 0)
                return true;

            DateTime latestInputWriteTimeUtc = GetLatestDetectionDebugInputWriteTimeUtc(originalFile, markedRegions);
            foreach (StepWatermarkMarkedRegion region in markedRegions)
            {
                string markerPath = region.SourceMarkerPath;
                if (string.IsNullOrEmpty(markerPath))
                    return false;

                string outputPath = Path.Combine(
                    detectionDirectory,
                    Path.GetFileNameWithoutExtension(markerPath) + ".png");
                if (!File.Exists(outputPath))
                    return false;

                if (File.GetLastWriteTimeUtc(outputPath) < latestInputWriteTimeUtc)
                    return false;
            }

            return true;
        }

        private static DateTime GetLatestDetectionDebugInputWriteTimeUtc(
            string originalFile,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions)
        {
            DateTime latest = File.GetLastWriteTimeUtc(originalFile);
            AddLatestWriteTimeUtc(ref latest, typeof(Program).Assembly.Location);
            AddLatestWriteTimeUtc(ref latest, typeof(StepWatermarkCleaner).Assembly.Location);
            AddLatestWriteTimeUtc(ref latest, typeof(StepProjectionRenderer).Assembly.Location);

            foreach (StepWatermarkMarkedRegion region in markedRegions)
            {
                AddLatestWriteTimeUtc(ref latest, region.SourceMarkerPath);
                AddLatestWriteTimeUtc(ref latest, region.SourceProjectionPath);
            }

            return latest;
        }

        private static void AddLatestWriteTimeUtc(ref DateTime latest, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime > latest)
                latest = writeTime;
        }

        private static void CompareCleanAndValidatedProjections(
            Dictionary<string, string> generatedCleanByName,
            Dictionary<string, string> validatedByName,
            string validatedDirectory,
            string cleanProjectionDirectory,
            string validatedProjectionDirectory,
            StepProjectionOptions projectionOptions,
            Dictionary<string, List<string>> detectionViewNamesByFileName,
            ProjectionVerificationTimings projectionTimings,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            var matchedFileNames = generatedCleanByName.Keys
                .Where(fileName => validatedByName.ContainsKey(fileName))
                .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchedFileNames.Count == 0)
                return;

            projectionTimings.Measure(
                "validated_projection_render_ms",
                () => ProjectDirectoryIfNeeded(validatedDirectory, validatedProjectionDirectory, projectionOptions));

            int comparedImages = 0;
            foreach (string fileName in matchedFileNames)
            {
                string modelName = Path.GetFileNameWithoutExtension(fileName);
                if (!detectionViewNamesByFileName.TryGetValue(fileName, out List<string> detectionViewNames) ||
                    detectionViewNames.Count == 0)
                    continue;

                foreach (string viewName in detectionViewNames)
                {
                    string cleanProjectionPath = Path.Combine(cleanProjectionDirectory, modelName + "__" + viewName + ".png");
                    string validatedProjectionPath = Path.Combine(validatedProjectionDirectory, modelName + "__" + viewName + ".png");

                    if (!File.Exists(cleanProjectionPath))
                    {
                        failures.Add("Clean projection is missing: " + cleanProjectionPath);
                        continue;
                    }

                    if (!File.Exists(validatedProjectionPath))
                    {
                        failures.Add("Validated projection is missing: " + validatedProjectionPath);
                        continue;
                    }

                    bool projectionsEqual = projectionTimings.Measure(
                        "clean_vs_validated_projection_compare_ms",
                        () => ProjectionPixelsEqual(cleanProjectionPath, validatedProjectionPath));
                    if (!projectionsEqual)
                    {
                        postCleanFaultFileNames.Add(fileName);
                        string message =
                            fileName +
                            " differs from Validated projection on " +
                            viewName +
                            ": " +
                            cleanProjectionPath +
                            " vs " +
                            validatedProjectionPath;
                        failures.Add(message);
                        visualFailures.Add(new ProjectionVisualFailure
                        {
                            Category = "Clean vs Validated",
                            FileName = fileName,
                            ViewName = viewName,
                            Message = message,
                            LeftLabel = "Clean",
                            LeftImagePath = cleanProjectionPath,
                            RightLabel = "Validated",
                            RightImagePath = validatedProjectionPath
                        });
                    }

                    comparedImages++;
                }
            }

            Console.WriteLine(
                "Projection comparison: models=" +
                matchedFileNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", images=" +
                comparedImages.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ProjectionPixelsEqual(string cleanProjectionPath, string validatedProjectionPath)
        {
            if (FilesEqualByLengthAndBytes(cleanProjectionPath, validatedProjectionPath))
                return true;

            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            using (var validatedImage = SKBitmap.Decode(validatedProjectionPath))
            {
                if (cleanImage == null || validatedImage == null)
                    return false;

                if (cleanImage.Width != validatedImage.Width || cleanImage.Height != validatedImage.Height)
                    return false;

                int[] cleanPixels = CopyBitmapPixelsToInt32Rows(cleanImage);
                int[] validatedPixels = CopyBitmapPixelsToInt32Rows(validatedImage);
                for (int i = 0; i < cleanPixels.Length; i++)
                {
                    if (cleanPixels[i] != validatedPixels[i])
                        return false;
                }

                return true;
            }
        }

        private static int[] CopyBitmapPixelsToInt32Rows(SKBitmap bitmap)
        {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            int[] pixels = new int[bitmap.Width * bitmap.Height];
            IntPtr source = bitmap.GetPixels();
            if (source == IntPtr.Zero)
                return pixels;

            int bytesPerPixel = bitmap.BytesPerPixel;
            if (bytesPerPixel != 4)
                return pixels;

            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr row = IntPtr.Add(source, y * bitmap.RowBytes);
                Marshal.Copy(row, pixels, y * bitmap.Width, bitmap.Width);
            }

            return pixels;
        }

        private static bool FilesEqualByLengthAndBytes(string leftPath, string rightPath)
        {
            var leftInfo = new FileInfo(leftPath);
            var rightInfo = new FileInfo(rightPath);
            if (leftInfo.Length != rightInfo.Length)
                return false;

            byte[] leftBytes = File.ReadAllBytes(leftPath);
            byte[] rightBytes = File.ReadAllBytes(rightPath);
            return leftBytes.SequenceEqual(rightBytes);
        }

        private static void WriteFailedProjectionReport(
            string reportPath,
            string reportDirectory,
            List<ProjectionVisualFailure> visualFailures)
        {
            Directory.CreateDirectory(reportDirectory);
            foreach (string staleImage in Directory.GetFiles(reportDirectory, "*.png"))
                File.Delete(staleImage);

            var lines = new List<string>
            {
                "# StepCleaner Failed Projection Report",
                string.Empty,
                "Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                string.Empty
            };

            if (visualFailures.Count == 0)
            {
                lines.Add("No failed projections.");
                File.WriteAllLines(reportPath, lines, Encoding.UTF8);
                return;
            }

            lines.Add("Failed projections: " + visualFailures.Count.ToString(CultureInfo.InvariantCulture));
            lines.Add(string.Empty);

            int index = 1;
            foreach (ProjectionVisualFailure failure in visualFailures)
            {
                string imageName = BuildReportImageName(index, failure);
                string outputPath = Path.Combine(reportDirectory, imageName);
                bool wroteImage = TryWriteSideBySideProjectionImage(failure, outputPath);

                lines.Add("## " + failure.Category);
                lines.Add(string.Empty);
                lines.Add("- Model: `" + failure.FileName + "`");
                lines.Add("- View: `" + failure.ViewName + "`");
                lines.Add("- Detail: " + failure.Message);
                lines.Add(string.Empty);

                if (wroteImage)
                    lines.Add("![" + failure.FileName + " " + failure.ViewName + "](" + BuildMarkdownImagePath(reportPath, outputPath) + ")");
                else
                    lines.Add("Could not create side-by-side image for this projection.");

                lines.Add(string.Empty);
                index++;
            }

            File.WriteAllLines(reportPath, lines, Encoding.UTF8);
        }

        private static bool TryWriteSideBySideProjectionImage(ProjectionVisualFailure failure, string outputPath)
        {
            using (var leftImage = SKBitmap.Decode(failure.LeftImagePath))
            using (var rightImage = SKBitmap.Decode(failure.RightImagePath))
            {
                if (leftImage == null || rightImage == null)
                    return false;

                const int panelSize = 720;
                const int labelHeight = 46;
                const int gutter = 18;
                const int margin = 18;

                int outputWidth = panelSize * 2 + gutter + margin * 2;
                int outputHeight = panelSize + labelHeight + margin * 2;

                using (var output = new SKBitmap(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(output))
                using (var textPaint = new SKPaint())
                using (var labelFont = new SKFont())
                using (var backgroundPaint = new SKPaint())
                using (var framePaint = new SKPaint())
                {
                    canvas.Clear(new SKColor(250, 250, 250));

                    backgroundPaint.Color = SKColors.White;
                    backgroundPaint.Style = SKPaintStyle.Fill;
                    framePaint.Color = new SKColor(205, 205, 205);
                    framePaint.Style = SKPaintStyle.Stroke;
                    framePaint.StrokeWidth = 2;
                    textPaint.Color = new SKColor(35, 35, 35);
                    textPaint.IsAntialias = true;
                    labelFont.Size = 24;

                    var leftRect = new SKRect(margin, margin + labelHeight, margin + panelSize, margin + labelHeight + panelSize);
                    var rightRect = new SKRect(leftRect.Right + gutter, leftRect.Top, leftRect.Right + gutter + panelSize, leftRect.Bottom);

                    DrawProjectionPanel(canvas, leftImage, leftRect, backgroundPaint, framePaint);
                    DrawProjectionPanel(canvas, rightImage, rightRect, backgroundPaint, framePaint);

                    canvas.DrawText(failure.LeftLabel, leftRect.Left, margin + 30, SKTextAlign.Left, labelFont, textPaint);
                    canvas.DrawText(failure.RightLabel, rightRect.Left, margin + 30, SKTextAlign.Left, labelFont, textPaint);

                    using (SKImage image = SKImage.FromBitmap(output))
                    using (SKData data = image.Encode(SKEncodedImageFormat.Png, 95))
                    using (Stream stream = File.Create(outputPath))
                        data.SaveTo(stream);
                }
            }

            return true;
        }

        private static void DrawProjectionPanel(
            SKCanvas canvas,
            SKBitmap image,
            SKRect target,
            SKPaint backgroundPaint,
            SKPaint framePaint)
        {
            canvas.DrawRect(target, backgroundPaint);

            float scale = Math.Min(target.Width / image.Width, target.Height / image.Height);
            float width = image.Width * scale;
            float height = image.Height * scale;
            var imageRect = new SKRect(
                target.Left + (target.Width - width) / 2.0f,
                target.Top + (target.Height - height) / 2.0f,
                target.Left + (target.Width + width) / 2.0f,
                target.Top + (target.Height + height) / 2.0f);

            canvas.DrawBitmap(image, imageRect);
            canvas.DrawRect(target, framePaint);
        }

        private static string BuildReportImageName(int index, ProjectionVisualFailure failure)
        {
            return index.ToString("000", CultureInfo.InvariantCulture) +
                "_" +
                SanitizeFileName(Path.GetFileNameWithoutExtension(failure.FileName)) +
                "__" +
                SanitizeFileName(failure.ViewName) +
                "__" +
                SanitizeFileName(failure.Category) +
                ".png";
        }

        private static string BuildMarkdownImagePath(string reportPath, string imagePath)
        {
            string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? string.Empty;
            return Path.GetRelativePath(reportDirectory, Path.GetFullPath(imagePath)).Replace('\\', '/');
        }

        private static string SanitizeFileName(string value)
        {
            var invalidCharacters = new HashSet<char>(Path.GetInvalidFileNameChars());
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (invalidCharacters.Contains(character) || char.IsWhiteSpace(character) || character == ':')
                    builder.Append('_');
                else
                    builder.Append(character);
            }

            return builder.ToString();
        }

        private static void ClearProjectionFiles(string projectionDirectory, IEnumerable<string> stepFileNames)
        {
            Directory.CreateDirectory(projectionDirectory);
            foreach (string fileName in stepFileNames)
            {
                string modelName = Path.GetFileNameWithoutExtension(fileName);
                foreach (string projectionFile in Directory.GetFiles(projectionDirectory, modelName + "__*.png"))
                    File.Delete(projectionFile);

                foreach (string projectionFile in Directory.GetFiles(projectionDirectory, modelName + "__*.json"))
                    File.Delete(projectionFile);
            }
        }

        private static IReadOnlyList<FullRegressionCleanResult> CleanOriginalFilesForFullRegression(
            IReadOnlyList<string> originalFiles,
            string cleanDirectory,
            int maxDegreeOfParallelism)
        {
            var results = new List<FullRegressionCleanResult>();
            var sync = new object();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism)
            };

            Parallel.ForEach(originalFiles, options, originalFile =>
            {
                string fileName = Path.GetFileName(originalFile);
                string outputFile = Path.Combine(cleanDirectory, fileName);
                var result = new FullRegressionCleanResult
                {
                    OriginalFile = originalFile,
                    OutputFile = outputFile
                };

                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    StepWatermarkCleanerReport report = StepWatermarkCleaner.CleanWithReport(
                        File.ReadAllText(originalFile, Encoding.Latin1),
                        new StepWatermarkCleanerOptions
                        {
                            BuildRemovedGeometryStep = false
                        });
                    File.WriteAllBytes(outputFile, Encoding.Latin1.GetBytes(report.CleanedStep));
                    result.Report = report;
                }
                catch (Exception ex)
                {
                    result.Exception = ex;
                }
                finally
                {
                    stopwatch.Stop();
                    result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                    lock (sync)
                        results.Add(result);
                }
            });

            var indexByFile = originalFiles
                .Select((file, index) => new { file, index })
                .ToDictionary(item => item.file, item => item.index, StringComparer.OrdinalIgnoreCase);
            return results
                .OrderBy(result => indexByFile.TryGetValue(result.OriginalFile, out int index) ? index : int.MaxValue)
                .ToList();
        }

        private static int GetFullRegressionCleanupParallelism()
        {
            int processorCount = Math.Max(1, Environment.ProcessorCount);
            string configured = Environment.GetEnvironmentVariable("STEPCLEANER_TEST_CLEANUP_PARALLELISM");
            if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested) &&
                requested > 0)
            {
                return Math.Max(1, Math.Min(processorCount, requested));
            }

            return Math.Min(2, processorCount);
        }

        private static IReadOnlyList<StepProjectionReport> ProjectDirectoryIfNeeded(
            string inputDirectory,
            string outputDirectory,
            StepProjectionOptions options)
        {
            var reports = new List<StepProjectionReport>();
            int renderedCount = 0;
            int skippedCount = 0;
            foreach (string inputFile in GetStepFiles(inputDirectory))
            {
                reports.Add(ProjectFileIfNeeded(inputFile, outputDirectory, options, out bool rendered));
                if (rendered)
                    renderedCount++;
                else
                    skippedCount++;
            }

            Console.WriteLine(
                "projection_cache input=" +
                Path.GetFileName(inputDirectory) +
                " rendered=" +
                renderedCount.ToString(CultureInfo.InvariantCulture) +
                " skipped=" +
                skippedCount.ToString(CultureInfo.InvariantCulture));

            return reports;
        }

        private static StepProjectionReport ProjectFileIfNeeded(
            string inputFile,
            string outputDirectory,
            StepProjectionOptions options)
        {
            return ProjectFileIfNeeded(inputFile, outputDirectory, options, out _);
        }

        private static StepProjectionReport ProjectFileIfNeeded(
            string inputFile,
            string outputDirectory,
            StepProjectionOptions options,
            out bool rendered)
        {
            Directory.CreateDirectory(outputDirectory);
            options = options ?? new StepProjectionOptions();
            string modelName = Path.GetFileNameWithoutExtension(inputFile);
            IReadOnlyList<string> viewNames = GetProjectionOptionViewNames(options);
            string signature = BuildProjectionOptionSignature(options, viewNames);
            string signaturePath = Path.Combine(outputDirectory, modelName + ".__projection-options.txt");
            List<string> expectedOutputs = viewNames
                .Select(viewName => Path.Combine(outputDirectory, modelName + "__" + viewName + ".png"))
                .ToList();
            DateTime latestInputWriteTimeUtc = GetLatestProjectionInputWriteTimeUtc(inputFile);
            bool outputsFresh =
                File.Exists(signaturePath) &&
                string.Equals(File.ReadAllText(signaturePath, Encoding.UTF8), signature, StringComparison.Ordinal) &&
                expectedOutputs.All(outputPath =>
                    File.Exists(outputPath) &&
                    File.GetLastWriteTimeUtc(outputPath) >= latestInputWriteTimeUtc);

            if (outputsFresh)
            {
                rendered = false;
                return new StepProjectionReport
                {
                    InputPath = inputFile,
                    FaceCount = 0,
                    EdgeCount = 0,
                    OutputFiles = expectedOutputs
                };
            }

            StepProjectionReport report = StepProjectionRenderer.ProjectFile(inputFile, outputDirectory, options);
            File.WriteAllText(signaturePath, signature, Encoding.UTF8);
            rendered = true;
            return report;
        }

        private static IReadOnlyList<string> GetProjectionOptionViewNames(StepProjectionOptions options)
        {
            if (options?.ViewNames != null && options.ViewNames.Count > 0)
                return options.ViewNames.ToList();

            return StepProjectionRenderer.ViewNames.ToList();
        }

        private static string BuildProjectionOptionSignature(StepProjectionOptions options, IReadOnlyList<string> viewNames)
        {
            return string.Join(
                Environment.NewLine,
                "imageSize=" + options.ImageSizePixels.ToString(CultureInfo.InvariantCulture),
                "imageWidth=" + options.ImageWidthPixels.ToString(CultureInfo.InvariantCulture),
                "imageHeight=" + options.ImageHeightPixels.ToString(CultureInfo.InvariantCulture),
                "padding=" + options.PaddingPixels.ToString(CultureInfo.InvariantCulture),
                "views=" + string.Join(",", viewNames),
                "mode=" + options.RenderMode,
                "writeMetadata=" + options.WriteMetadata.ToString(CultureInfo.InvariantCulture),
                "skipGeometryModelForExternalRender=" + options.SkipGeometryModelForExternalRender.ToString(CultureInfo.InvariantCulture));
        }

        private static DateTime GetLatestProjectionInputWriteTimeUtc(string inputFile)
        {
            DateTime latest = File.GetLastWriteTimeUtc(inputFile);
            AddLatestWriteTimeUtc(ref latest, typeof(Program).Assembly.Location);
            AddLatestWriteTimeUtc(ref latest, typeof(StepProjectionRenderer).Assembly.Location);
            AddLatestWriteTimeUtc(ref latest, typeof(StepWatermarkCleaner).Assembly.Location);
            return latest;
        }

        private static IReadOnlyList<CleanupExpectation> GetCleanupExpectations()
        {
            return new[]
            {
                new CleanupExpectation
                {
                    FileName = "CONN-TH_XT60PB-M.step",
                    Note = "CONN-TH_XT60PB-M.step bottom geometric LCEDA watermark should be removed from the z_minus view."
                },
                new CleanupExpectation
                {
                    FileName = "LED-SMD_XL-3838UV2SA06G3.step",
                    Note = "LED-SMD_XL-3838UV2SA06G3.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "USB-A-TH_FUS264-FDSW3K.step",
                    Note = "USB-A-TH_FUS264-FDSW3K.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "CONN-TH_MR30PW-M30-G-Y.step",
                    Note = "CONN-TH_MR30PW-M30-G-Y.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "USB-B-TH_USB-B10-BRW.step",
                    Note = "USB-B-TH_USB-B10-BRW.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                    Note = "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step",
                    Note = "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step cleaned output should be reviewed as cleaned."
                }
            };
        }

        private static IReadOnlyList<string> GetCleanupNotes()
        {
            return GetCleanupExpectations()
                .Select(expectation => expectation.Note)
                .ToList();
        }

        private static bool RequiresAutomaticWatermarkCleanup(string fileName)
        {
            string actualFileName = Path.GetFileName(fileName);
            return GetCleanupExpectations()
                .Any(expectation => string.Equals(expectation.FileName, actualFileName, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class CleanupExpectation
        {
            public string FileName { get; set; }
            public string Note { get; set; }
        }

        private sealed class TextLogoDetectionExpectation
        {
            public string FileName { get; set; }
            public string ViewName { get; set; }
            public string RequiredTemplate { get; set; }
            public List<string> ExpectedTemplates { get; set; }
            public int ExpectedX { get; set; }
            public int ExpectedY { get; set; }
            public int ExpectedWidth { get; set; }
            public int ExpectedHeight { get; set; }
            public int BoundsTolerance { get; set; }
            public double MinScore { get; set; }
            public double MaxChamferDistance { get; set; }
            public double MaxUnexpectedHighScore { get; set; }
        }

        private sealed class MarkedRegionFile
        {
            public int ImageWidth { get; set; }
            public int ImageHeight { get; set; }
            public List<MarkedRectangle> Rectangles { get; set; }
        }

        private sealed class MarkedRectangle
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private readonly struct MarkedRectI
        {
            public MarkedRectI(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }

            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }
            public int Area => Math.Max(0, Width) * Math.Max(0, Height);

            public override string ToString()
            {
                return "[" +
                    X.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    Y.ToString(CultureInfo.InvariantCulture) +
                    " " +
                    Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    Height.ToString(CultureInfo.InvariantCulture) +
                    "]";
            }
        }

        private static string FindDataRoot()
        {
            var roots = new List<string>
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            };

            foreach (string root in roots)
            {
                string current = Path.GetFullPath(root);
                for (int i = 0; i < 12; i++)
                {
                    string directData = Path.Combine(current, "Data");
                    if (IsDataRoot(directData))
                        return directData;

                    string repoData = Path.Combine(current, "Test", "StepCleaner", "Data");
                    if (IsDataRoot(repoData))
                        return repoData;

                    string parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                        break;

                    current = parent;
                }
            }

            throw new DirectoryNotFoundException("Could not find Test\\StepCleaner\\Data.");
        }

        private static bool IsDataRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, "Original"))
                && Directory.Exists(Path.Combine(path, "Validated"));
        }

        private sealed class ProjectionVisualFailure
        {
            public string Category { get; set; }
            public string FileName { get; set; }
            public string ViewName { get; set; }
            public string Message { get; set; }
            public string LeftLabel { get; set; }
            public string LeftImagePath { get; set; }
            public string RightLabel { get; set; }
            public string RightImagePath { get; set; }
        }

        private sealed class VectorProjectionResultDto
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            public string Engine { get; set; }
            public List<VectorProjectionViewDto> Views { get; set; } = new List<VectorProjectionViewDto>();
        }

        private sealed class VectorProjectionViewDto
        {
            public string Name { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
            public VectorProjectionBoundsDto Bounds { get; set; }
            public List<VectorProjectionPrimitiveDto> Primitives { get; set; } = new List<VectorProjectionPrimitiveDto>();
        }

        private sealed class VectorProjectionBoundsDto
        {
            public double Left { get; set; }
            public double Bottom { get; set; }
            public double Right { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        private sealed class VectorProjectionPrimitiveDto
        {
            public string Kind { get; set; }
            public string Visibility { get; set; }
            public string Category { get; set; }
            public int SourceIndex { get; set; }
            public double[] Points { get; set; }
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double Radius { get; set; }
            public double StartAngle { get; set; }
            public double EndAngle { get; set; }
            public string OriginalKind { get; set; }
            public double Tolerance { get; set; }
        }

        private sealed class ResidualPrimitiveSourceMatch
        {
            public ProjectedStepTopologySource Source { get; set; }
            public double AverageDistance { get; set; }
        }

        private sealed class VectorPrismTopologyRewritePlan
        {
            public VectorPrismDetectionBox Box { get; set; }
            public string TemplateName { get; set; }
            public int OwnerId { get; set; }
            public int HostFaceId { get; set; }
            public int ResidualPrimitiveCount { get; set; }
            public int UnknownPrimitiveCount { get; set; }
            public List<int> FaceIdsToRemove { get; } = new List<int>();
            public Dictionary<int, HashSet<int>> FaceBoundsToRemove { get; } =
                new Dictionary<int, HashSet<int>>();
            public bool RequiresPlanarFillPatch { get; set; }
            public string Reason { get; set; }
            public List<string> BlockedSources { get; } = new List<string>();
        }

        private sealed class VectorPrismDetectionBox
        {
            public string ViewName { get; set; }
            public int UAxis { get; set; }
            public int VAxis { get; set; }
            public int DepthAxis { get; set; }
            public StepBounds3d Bounds { get; set; }
        }

        private sealed class ProjectedStepTopologySource
        {
            public int FaceId { get; set; }
            public int BoundId { get; set; }
            public int EdgeCurveId { get; set; }
            public List<ProjectedStepPoint> Points { get; set; } = new List<ProjectedStepPoint>();

            public string Key =>
                FaceId.ToString(CultureInfo.InvariantCulture) + "|" +
                BoundId.ToString(CultureInfo.InvariantCulture) + "|" +
                EdgeCurveId.ToString(CultureInfo.InvariantCulture);
        }

        private readonly struct ProjectedStepPoint
        {
            public ProjectedStepPoint(double u, double v)
            {
                U = u;
                V = v;
            }

            public double U { get; }
            public double V { get; }
        }

        private sealed class StepCleanerProfileResult
        {
            public string ModelName { get; set; }
            public int ByteCount { get; set; }
            public int DetectRegionCount { get; set; }
            public int CleanedStepByteCount { get; set; }
            public int RemovedGeometryByteCount { get; set; }
            public int VisualDetectionCount { get; set; }
            public int ProjectionOutputCount { get; set; }
            public long CleanerDetectOnlyMs { get; set; }
            public long CleanWithoutRemovedGeometryMs { get; set; }
            public long CleanWithReportMs { get; set; }
            public long VisualOracleAllViewsMs { get; set; }
            public long ProjectFileAllViewsMs { get; set; }
            public Dictionary<string, long> CleanDetailTimings { get; } =
                new Dictionary<string, long>(StringComparer.Ordinal);
            public Dictionary<string, long> VectorProjectDetectMsByView { get; } =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> VectorPrimitiveCountByView { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> VectorDetectionCountByView { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class StepCleanerSpeedContractResult
        {
            public string ModelName { get; set; }
            public int ByteCount { get; set; }
            public int DetectRegionCount { get; set; }
            public int CleanedStepByteCount { get; set; }
            public int RemovedGeometryByteCount { get; set; }
            public int ScopedViewCount { get; set; }
            public int VisualOracleOriginalDetectionCount { get; set; }
            public int VisualOracleResidualDetectionCount { get; set; }
            public int VisualOracleFailureCount { get; set; }
            public long CleanWithReportWithoutRemovedGeometryMs { get; set; }
            public long ScopedVisualOracleMs { get; set; }
            public List<string> ScopedViewNames { get; } = new List<string>();
            public Dictionary<string, long> CleanDetailTimings { get; } =
                new Dictionary<string, long>(StringComparer.Ordinal);
        }

        private sealed class FullRegressionCleanResult
        {
            public string OriginalFile { get; set; }
            public string OutputFile { get; set; }
            public StepWatermarkCleanerReport Report { get; set; }
            public long ElapsedMilliseconds { get; set; }
            public Exception Exception { get; set; }
        }

        private sealed class FullTestDetectionCache
        {
            private readonly Dictionary<string, StepWatermarkDetectionReport> _reportsByFileName =
                new Dictionary<string, StepWatermarkDetectionReport>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IReadOnlyList<StepProjectionDetectionRegion>> _regionsByKey =
                new Dictionary<string, IReadOnlyList<StepProjectionDetectionRegion>>(StringComparer.OrdinalIgnoreCase);

            public void SetReport(string originalFile, StepWatermarkDetectionReport report)
            {
                if (report == null)
                    return;

                string fileName = Path.GetFileName(originalFile);
                _reportsByFileName[fileName] = report;
            }

            public StepWatermarkDetectionReport GetReport(string originalFile)
            {
                string fileName = Path.GetFileName(originalFile);
                if (!_reportsByFileName.TryGetValue(fileName, out StepWatermarkDetectionReport report))
                {
                    report = StepWatermarkCleaner.Detect(
                        File.ReadAllBytes(originalFile),
                        new StepWatermarkCleanerOptions());
                    _reportsByFileName[fileName] = report;
                }

                return report;
            }

            public IReadOnlyList<StepProjectionDetectionRegion> GetDetectionRegions(
                string originalFile,
                StepProjectionOptions projectionOptions)
            {
                string key =
                    Path.GetFileName(originalFile) +
                    "|" +
                    projectionOptions.ImageSizePixels.ToString(CultureInfo.InvariantCulture) +
                    "|" +
                    projectionOptions.PaddingPixels.ToString(CultureInfo.InvariantCulture) +
                    "|" +
                    string.Join(",", projectionOptions.ViewNames);

                if (!_regionsByKey.TryGetValue(key, out IReadOnlyList<StepProjectionDetectionRegion> regions))
                {
                    var verifiedReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(GetReport(originalFile));
                    regions = StepProjectionRenderer.ProjectDetectionRegions(
                            originalFile,
                            verifiedReport,
                            projectionOptions)
                        .ToList();
                    _regionsByKey[key] = regions;
                }

                return regions;
            }
        }

        private sealed class ProjectionVerificationTimings
        {
            private readonly Dictionary<string, long> _elapsedByStage = new Dictionary<string, long>(StringComparer.Ordinal);
            private readonly List<string> _stageOrder = new List<string>();

            public void Measure(string stageName, Action action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    action();
                }
                finally
                {
                    stopwatch.Stop();
                    Add(stageName, stopwatch.ElapsedMilliseconds);
                }
            }

            public T Measure<T>(string stageName, Func<T> action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    return action();
                }
                finally
                {
                    stopwatch.Stop();
                    Add(stageName, stopwatch.ElapsedMilliseconds);
                }
            }

            public void WriteToConsole()
            {
                Console.WriteLine("Projection verification timings:");
                long total = 0;
                foreach (string stageName in _stageOrder)
                {
                    long elapsed = _elapsedByStage[stageName];
                    total += elapsed;
                    Console.WriteLine("  " + stageName + "=" + elapsed.ToString(CultureInfo.InvariantCulture) + " ms");
                }

                Console.WriteLine("  total_measured_projection_verification_ms=" + total.ToString(CultureInfo.InvariantCulture) + " ms");
            }

            private void Add(string stageName, long elapsedMilliseconds)
            {
                if (!_elapsedByStage.ContainsKey(stageName))
                {
                    _elapsedByStage.Add(stageName, 0);
                    _stageOrder.Add(stageName);
                }

                _elapsedByStage[stageName] += elapsedMilliseconds;
            }
        }

        private static Dictionary<int, string> ParseStepEntityDefinitions(string stepText)
        {
            var result = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(stepText))
                return result;

            foreach (Match match in Regex.Matches(
                stepText,
                @"#(\d+)\s*=\s*(.*?);",
                RegexOptions.Singleline | RegexOptions.CultureInvariant))
            {
                int id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                result[id] = NormalizeStepEntityDefinition(match.Groups[2].Value);
            }

            return result;
        }

        private static IEnumerable<int> GetAdvancedFaceIds(string stepText)
        {
            foreach (var kvp in ParseStepEntityDefinitions(stepText))
            {
                if (kvp.Value.StartsWith("ADVANCED_FACE", StringComparison.OrdinalIgnoreCase))
                    yield return kvp.Key;
            }
        }

        private static IEnumerable<int> GetActiveAdvancedFaceIds(string stepText)
        {
            Dictionary<int, string> entities = ParseStepEntityDefinitions(stepText);
            return GetActiveAdvancedFaceIds(entities);
        }

        private static IEnumerable<int> GetActiveAdvancedFaceIds(IReadOnlyDictionary<int, string> entities)
        {
            var referencedFaceIds = new HashSet<int>();
            foreach (string definition in entities.Values)
            {
                if (!definition.StartsWith("CLOSED_SHELL", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (Match match in Regex.Matches(definition, @"#(\d+)", RegexOptions.CultureInvariant))
                {
                    int referencedId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (entities.TryGetValue(referencedId, out string referencedDefinition) &&
                        referencedDefinition.StartsWith("ADVANCED_FACE", StringComparison.OrdinalIgnoreCase))
                    {
                        referencedFaceIds.Add(referencedId);
                    }
                }
            }

            return referencedFaceIds.OrderBy(id => id).ToList();
        }

        private static List<int> ExtractStepReferenceIds(string definition)
        {
            var result = new List<int>();
            foreach (Match match in Regex.Matches(
                definition ?? string.Empty,
                @"#(\d+)",
                RegexOptions.CultureInvariant))
            {
                result.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            return result;
        }

        private static StepBounds3d GetStepEntityBounds(
            int rootId,
            IReadOnlyDictionary<int, string> entities,
            Dictionary<int, StepBounds3d> boundsById)
        {
            if (boundsById.TryGetValue(rootId, out StepBounds3d cached))
                return cached;

            var bounds = new StepBounds3d();
            foreach (int entityId in BuildStepReferenceClosure(rootId, entities))
            {
                if (!entities.TryGetValue(entityId, out string definition) ||
                    !definition.StartsWith("CARTESIAN_POINT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                List<double> values = Regex.Matches(
                        definition,
                        @"[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?",
                        RegexOptions.CultureInvariant)
                    .Cast<Match>()
                    .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
                    .ToList();
                if (values.Count < 3)
                    continue;

                bounds.Include(
                    values[values.Count - 3],
                    values[values.Count - 2],
                    values[values.Count - 1]);
            }

            boundsById[rootId] = bounds;
            return bounds;
        }

        private static bool ProjectedBoundsInsideRoi(StepBounds3d bounds, StepProjectionBounds2d roi)
        {
            const double zPlusTopMin = 1.699;
            const double zPlusTopMax = 1.702;
            return ProjectedBoundsInsideRoi(bounds, roi, zPlusTopMin, zPlusTopMax);
        }

        private static bool ProjectedBoundsInsideRoi(
            StepBounds3d bounds,
            StepProjectionBounds2d roi,
            double minZ,
            double maxZ)
        {
            const double padding = 0.006;
            if (!bounds.HasValue)
                return false;

            return bounds.MinX >= roi.UMin - padding &&
                bounds.MaxX <= roi.UMax + padding &&
                bounds.MinY >= roi.VMin - padding &&
                bounds.MaxY <= roi.VMax + padding &&
                bounds.MaxZ >= minZ &&
                bounds.MinZ <= maxZ;
        }

        private sealed class StepProjectionBounds2d
        {
            public double UMin { get; set; }
            public double UMax { get; set; }
            public double VMin { get; set; }
            public double VMax { get; set; }
        }

        private sealed class StepBounds3d
        {
            public bool HasValue { get; private set; }
            public double MinX { get; private set; }
            public double MaxX { get; private set; }
            public double MinY { get; private set; }
            public double MaxY { get; private set; }
            public double MinZ { get; private set; }
            public double MaxZ { get; private set; }

            public void Include(double x, double y, double z)
            {
                if (!HasValue)
                {
                    MinX = MaxX = x;
                    MinY = MaxY = y;
                    MinZ = MaxZ = z;
                    HasValue = true;
                    return;
                }

                MinX = Math.Min(MinX, x);
                MaxX = Math.Max(MaxX, x);
                MinY = Math.Min(MinY, y);
                MaxY = Math.Max(MaxY, y);
                MinZ = Math.Min(MinZ, z);
                MaxZ = Math.Max(MaxZ, z);
            }

            public void Include(StepBounds3d bounds)
            {
                if (bounds == null || !bounds.HasValue)
                    return;

                Include(bounds.MinX, bounds.MinY, bounds.MinZ);
                Include(bounds.MaxX, bounds.MaxY, bounds.MaxZ);
            }
        }

        private static void VerifyProtectedFaceEntityClosurePreserved(
            int faceId,
            IReadOnlyDictionary<int, string> originalEntities,
            IReadOnlyDictionary<int, string> cleanedEntities,
            IReadOnlyDictionary<int, string> removedEntities,
            List<string> failures)
        {
            HashSet<int> closure = BuildStepReferenceClosure(faceId, originalEntities);
            if (closure.Count == 0)
            {
                failures.Add("Could not build protected face closure for #" + faceId.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }

            foreach (int entityId in closure.OrderBy(id => id))
            {
                if (!originalEntities.TryGetValue(entityId, out string originalDefinition))
                    continue;

                if (!cleanedEntities.TryGetValue(entityId, out string cleanedDefinition))
                {
                    failures.Add(
                        "Protected contact face #" +
                        faceId.ToString(CultureInfo.InvariantCulture) +
                        " lost referenced entity #" +
                        entityId.ToString(CultureInfo.InvariantCulture) +
                        " in cleaned STEP.");
                    continue;
                }

                if (!string.Equals(originalDefinition, cleanedDefinition, StringComparison.Ordinal))
                {
                    failures.Add(
                        "Protected contact face #" +
                        faceId.ToString(CultureInfo.InvariantCulture) +
                        " referenced entity #" +
                        entityId.ToString(CultureInfo.InvariantCulture) +
                        " changed in cleaned STEP.");
                }

                if (removedEntities.ContainsKey(entityId))
                {
                    failures.Add(
                        "Protected contact face #" +
                        faceId.ToString(CultureInfo.InvariantCulture) +
                        " referenced entity #" +
                        entityId.ToString(CultureInfo.InvariantCulture) +
                        " was exported as removed geometry.");
                }
            }
        }

        private static HashSet<int> BuildStepReferenceClosure(
            int rootId,
            IReadOnlyDictionary<int, string> entities)
        {
            var result = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(rootId);

            while (pending.Count > 0)
            {
                int entityId = pending.Pop();
                if (!result.Add(entityId))
                    continue;

                if (!entities.TryGetValue(entityId, out string definition))
                    continue;

                foreach (Match match in Regex.Matches(
                    definition,
                    @"#(\d+)",
                    RegexOptions.CultureInvariant))
                {
                    int referencedId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (!result.Contains(referencedId))
                        pending.Push(referencedId);
                }
            }

            foreach (KeyValuePair<int, string> entity in entities)
            {
                if (entity.Key == rootId || result.Contains(entity.Key))
                    continue;

                if (Regex.IsMatch(
                    entity.Value,
                    @"^STYLED_ITEM\s*\(",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    Regex.IsMatch(
                        entity.Value,
                        @"#" + rootId.ToString(CultureInfo.InvariantCulture) + @"(?!\d)",
                        RegexOptions.CultureInvariant))
                {
                    result.Add(entity.Key);
                }
            }

            return result;
        }

        private static string NormalizeStepEntityDefinition(string definition)
        {
            return Regex.Replace(
                    definition ?? string.Empty,
                    @"\s+",
                    " ",
                    RegexOptions.CultureInvariant)
                .Trim();
        }

        private static List<string> GetStepFiles(string directory)
        {
            if (!Directory.Exists(directory))
                return new List<string>();

            var result = new List<string>();
            foreach (string file in Directory.GetFiles(directory))
            {
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
