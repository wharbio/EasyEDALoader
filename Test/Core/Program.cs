using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--renderer-output")
        {
            Console.Out.Write(new string('x', 1024 * 1024));
            Console.Error.Write(new string('x', 1024 * 1024));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--renderer-hang")
        {
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("thumbnail URL forms", ThumbnailUrls),
            ("search without optional description", SearchWithoutDescription),
            ("search cancellation reaches HTTP", SearchCancellation),
            ("search sends escaped keyword query", UpstreamImportTests.SearchKeyword),
            ("component timestamps accept missing values", () => Run(UpstreamImportTests.NullableTimestamps)),
            ("additional footprint layers are opt-in", () => Run(UpstreamImportTests.OptionalLayers)),
            ("import diagnostics retain exceptions", () => Run(UpstreamImportTests.Diagnostics)),
            ("model bounds use footprint coordinate frame", () => Run(UpstreamImportTests.ModelCoordinateFrame)),
            ("pad expansion overrides are optional", () => Run(Pr9ImportTests.MaskOverrides)),
            ("C2895290 imports all eight pads", () => Run(PolygonPadImportTests.ImportAllPads)),
            ("one polygon pad imports without aborting", () => Run(PolygonPadImportTests.ImportOnePolygon)),
            ("bottom polygon pad retains geometry and position", () => Run(PolygonPadImportTests.BottomLayer)),
            ("multilayer polygon pad assigns every stack section", () => Run(PolygonPadImportTests.MultiLayer)),
            ("rotated polygon slot retains drill direction", () => Run(PolygonPadImportTests.RotatedSlot)),
            ("C2895290 preview preserves polygon contours", () => Run(PolygonPadImportTests.Preview)),
            ("failed footprint import closes PCB edit", () => Run(PolygonPadImportTests.FailedImportClosesEdit)),
            ("solid regions retain cutouts and curved paths", () => Run(Pr9ImportTests.SolidRegions)),
            ("multipart symbols keep every gate", () => Run(Pr9ImportTests.MultipartSymbols)),
            ("schematic graphics retain shape and fill", () => Run(Pr9ImportTests.Graphics)),
            ("schematic graphics leave pin labels clear", () => Run(Pr9ImportTests.GraphicLabelClearance)),
            ("region interop retains hole contours", () => Run(Pr9ImportTests.RegionInterop)),
            ("schematic interop assigns multipart owners", () => Run(Pr9ImportTests.MultipartInterop)),
            ("cache directory keys stay below root", () => Run(CacheDirectoryKeys)),
            ("invalid component downloads are not cached", InvalidComponentCache),
            ("layout destinations cannot overlap groups", () => Run(LayoutOverlap)),
            ("malformed layout groups are reported", () => Run(LayoutMalformedGroups)),
            ("layout rejects unknown source keys", () => Run(LayoutUnknownKeys)),
            ("missing layout targets are reported", () => Run(LayoutMissingTargets)),
            ("independent layout groups remain valid", () => Run(LayoutValidGroups)),
            ("closing layout dialog cancels pending work", LayoutDialogCancellation),
            ("edge rails reject non-finite dimensions", () => Run(EdgeRailDimensions)),
            ("bridge cancels an idle client", BridgeCancellation),
            ("renderer drains both output pipes", () => Run(RendererOutput)),
            ("renderer terminates on timeout", () => Run(RendererTimeout)),
            ("board SVG is valid XML with millimetre dimensions", () => Run(BoardSvgXml))
        };
        int failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.GetBaseException().Message); }
        }
        Console.WriteLine($"Core tests: {tests.Length - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static EasyedaApi Api(HttpClient client)
    {
        return new EasyedaApi(client);
    }

    private static async Task ThumbnailUrls()
    {
        var received = new List<string>();
        using var client = new HttpClient(new Handler((request, token) =>
        {
            received.Add(request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        }));
        var api = Api(client);
        foreach (string url in new[] { "https://image.lceda.cn/a.png", "//image.lceda.cn/a.png", "/a.png" })
        {
            byte[] bytes = await api.LoadPngBytesAsync(url, CancellationToken.None);
            Check(bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Image bytes changed.");
        }
        Check(received.Count == 3 && received.All(uri => uri == "https://image.lceda.cn/a.png"), "Incorrect thumbnail URI.");
    }

    private static async Task SearchWithoutDescription()
    {
        using var client = new HttpClient(new Handler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\":{\"productList\":[{\"mpn\":\"PART\",\"number\":\"C1\",\"device_info\":{\"attributes\":{\"Manufacturer\":\"Vendor\"},\"symbol_info\":{},\"footprint_info\":{\"model_3d\":{}}}}]}}")
        })));
        var results = await Api(client).SearchProductInfoAsync("C1");
        Check(results?.Count == 1 && results[0].HasFootprint && results[0].HasSymbol && results[0].Has3d,
            "Missing optional description hides available component data.");
    }

    private static async Task SearchCancellation()
    {
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            await Task.Delay(1500, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"productList\":[]}}") };
        }));
        using var cancellation = new CancellationTokenSource();
        Task search = ModelCache.GetSearchProductInfoAsync(Api(client), "review-" + Guid.NewGuid().ToString("N"), cancellation.Token);
        cancellation.Cancel();
        try { await search.WaitAsync(TimeSpan.FromSeconds(1)); throw new InvalidOperationException("Search completed after cancellation."); }
        catch (OperationCanceledException) { }
        finally { try { await search; } catch (OperationCanceledException) { } }
    }

    private static void CacheDirectoryKeys()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cache-key-review")) + Path.DirectorySeparatorChar;
        foreach (string key in new[] { ".", "..", "...", ".. ", "../outside", "C123" })
        {
            string path = Path.GetFullPath(Path.Combine(root, ModelCache.GetSafeFileName(key)));
            Check(path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.Length > root.Length,
                "Cache key escapes its component directory: " + key);
        }
        Check(ModelCache.GetSafeFileName("C123") == "C123", "Existing valid cache key changed.");
    }

    private static LayoutMappingRequest MappingRequest()
    {
        LayoutComponentSnapshot Part(string name) => new LayoutComponentSnapshot { Designator = name, Comment = "10k", Footprint = "R0603" };
        var source = new[] { Part("R1"), Part("R2") };
        var destination = new[] { Part("R3"), Part("R4"), Part("R5"), Part("R6") };
        return new LayoutMappingRequest { SourceAnchor = source[0], SourceComponents = source, TargetAnchors = new[] { destination[0], destination[2] }, DestinationCandidates = destination };
    }

    private static async Task InvalidComponentCache()
    {
        string key = "review-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(ModelCache.GetLocalDataRoot(), "ComponentCache", key, "component.json");
        string response = "not JSON";
        using var client = new HttpClient(new Handler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(response) })));
        var api = Api(client);
        try
        {
            try { await ModelCache.GetComponentJsonAsync(api, key, CancellationToken.None); throw new Exception("Malformed JSON accepted."); }
            catch (Newtonsoft.Json.JsonException) { }
            Check(!File.Exists(path), "Malformed JSON was written to cache.");
            response = "{\"result\":null}";
            Check(await ModelCache.GetComponentJsonAsync(api, key, CancellationToken.None) == null, "Unusable component accepted.");
            Check(!File.Exists(path), "Unusable component was cached.");
            response = "{\"success\":true,\"result\":{}}";
            Check((await ModelCache.GetComponentJsonAsync(api, key, CancellationToken.None))?.Component != null, "Valid retry failed.");
            Check(File.Exists(path), "Valid retry was not cached.");
        }
        finally { ModelCache.DeleteSelectedComponentCache(key, key, null); }
    }

    private static void LayoutOverlap()
    {
        var result = LayoutDuplicationMapper.ValidateMappingResponse("{\"groups\":[{\"target_anchor\":\"R3\",\"map\":{\"R1\":\"R3\",\"R2\":\"R4\"}},{\"target_anchor\":\"R5\",\"map\":{\"R1\":\"R5\",\"R2\":\"R4\"}}]}", MappingRequest());
        Check(!result.HasValidGroups && result.Errors.Count > 0, "Overlapping groups can move the same component twice.");
    }

    private static void LayoutMalformedGroups()
    {
        var result = LayoutDuplicationMapper.ValidateMappingResponse("{\"groups\":[null,42,\"invalid\",[]]}", MappingRequest());
        Check(!result.HasValidGroups && result.Errors.Count > 0, "Malformed groups were not rejected.");
    }

    private static void LayoutUnknownKeys()
    {
        var result = LayoutDuplicationMapper.ValidateMappingResponse("{\"groups\":[{\"target_anchor\":\"R3\",\"map\":{\"R1\":\"R3\",\"R2\":\"R4\",\"R99\":\"R6\"}}]}", MappingRequest());
        Check(!result.HasValidGroups && result.Errors.Count > 0, "Unknown source keys were ignored.");
    }

    private static void LayoutMissingTargets()
    {
        var result = LayoutDuplicationMapper.ValidateMappingResponse("{\"groups\":[]}", MappingRequest());
        Check(result.Errors.Count > 0, "Omitted target anchors were not reported.");
    }

    private static void LayoutValidGroups()
    {
        var result = LayoutDuplicationMapper.ValidateMappingResponse("{\"groups\":[{\"target_anchor\":\"R3\",\"map\":{\"R1\":\"R3\",\"R2\":\"R4\"}},{\"target_anchor\":\"R5\",\"map\":{\"R1\":\"R5\",\"R2\":\"R6\"}}]}", MappingRequest());
        Check(result.ValidGroups.Count == 2 && result.Errors.Count == 0, "Independent valid groups rejected.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void EdgeRailDimensions()
    {
        var parse = typeof(EdgeRailsDialog).GetMethod("TryParseMm", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (string value in new[] { "NaN", "Infinity", "-Infinity" })
            Check(!(bool)parse.Invoke(null, new object[] { value, 0.0 }), "Invalid dimension accepted: " + value);
        Check((bool)parse.Invoke(null, new object[] { "10.25", 0.0 }), "Valid dimension rejected.");
    }

    private static Task LayoutDialogCancellation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dialog = new LayoutDuplicatorDialog(new LayoutDuplicationSession());
                using var source = new CancellationTokenSource();
                CancellationToken token = source.Token;
                typeof(LayoutDuplicatorDialog).GetField("cancellation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dialog, source);
                typeof(LayoutDuplicatorDialog).GetMethod("OnClosed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, new object[] { EventArgs.Empty });
                Check(token.IsCancellationRequested, "Closing the dialog left a mapping request active.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task BridgeCancellation()
    {
        string name = "easyeda-review-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(3000));
        using var cancellation = new CancellationTokenSource();
        using var bridge = new EasyEdaCommandBridge();
        Task handling = bridge.HandleClientAsync(server, cancellation.Token);
        cancellation.Cancel();
        try { await handling.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Cancelled request was handled."); }
        catch (OperationCanceledException) { }
    }

    private static Process StartRendererFixture(string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    private static void RendererOutput()
    {
        using var process = StartRendererFixture("--renderer-output");
        Check(StepProjectionRenderer.WaitForExternalRenderer(process, 5000), "Renderer blocked on redirected output.");
    }

    private static void RendererTimeout()
    {
        using var process = StartRendererFixture("--renderer-hang");
        Check(!StepProjectionRenderer.WaitForExternalRenderer(process, 200), "Hung renderer reported success.");
        Check(process.HasExited, "Timed-out renderer is still running.");
    }

    private static void BoardSvgXml()
    {
        var bounds = new EdgeRailBounds { MinX = 0, MinY = 0, MaxX = 20, MaxY = 10 };
        var contour = new EdgeRailContour { Bounds = bounds };
        contour.Points.AddRange(new[] { new EdgeRailPoint(0, 0), new EdgeRailPoint(20, 0), new EdgeRailPoint(20, 10), new EdgeRailPoint(0, 10), new EdgeRailPoint(0, 0) });
        Type exporter = typeof(PcbShapeSvgExporter);
        Type componentType = exporter.GetNestedType("BoardAssemblyComponent", BindingFlags.NonPublic);
        object components = Activator.CreateInstance(typeof(List<>).MakeGenericType(componentType));
        string xml = (string)exporter.GetMethod("BuildBoardAssemblySvg", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { bounds, contour, components, null, false });
        string path = Path.Combine(AppContext.BaseDirectory, "board-review.svg");
        File.WriteAllText(path, xml);
        var document = XDocument.Load(path);
        XNamespace svg = "http://www.w3.org/2000/svg";
        Check(document.Root.Name == svg + "svg", "SVG namespace missing.");
        Check((string)document.Root.Attribute("width") == "20.5mm" && (string)document.Root.Attribute("height") == "10.5mm", "Board dimensions changed.");
        Check(document.Descendants(svg + "path").Any(element => (string)element.Attribute("id") == "BoardOutline" && (string)element.Attribute("stroke-width") == "0.1"), "Board outline missing.");
    }
}
