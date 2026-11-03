using EasyEDA_Loader;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static class UpstreamImportTests
{
    internal static async Task SearchKeyword()
    {
        const string keyword = "C1 &other=value/+";
        using var handler = new SearchHandler();
        using var client = new HttpClient(handler);
        await new EasyedaApi(client).SearchProductInfoAsync(keyword);
        if (handler.Query != "?keyword=" + Uri.EscapeDataString(keyword))
            throw new InvalidOperationException("Required escaped keyword query was not sent: " + handler.Query);
    }

    internal static void NullableTimestamps()
    {
        foreach (string value in new[] { "null", "\"\"", "123" })
        {
            string json = "{\"utime\":" + value + "}";
            var footprint = JsonConvert.DeserializeObject<FootprintHead>(json);
            var symbol = JsonConvert.DeserializeObject<SymbolHead>(json);
            int? expected = value == "123" ? 123 : null;
            if (footprint.Utime != expected || symbol.Utime != expected)
                throw new InvalidOperationException("Timestamp was not preserved for " + value);
        }
        if (JsonConvert.DeserializeObject<FootprintHead>("{}").Utime != null ||
            JsonConvert.DeserializeObject<SymbolHead>("{}").Utime != null)
            throw new InvalidOperationException("Missing timestamp must stay unknown.");
    }

    internal static void OptionalLayers()
    {
        foreach (string layer in new[] { "BoardOutLine", "BottomAssembly" })
            if (FootprintLayerMap.NormalizeLayerName(layer, true) != null)
                throw new InvalidOperationException("Additional layer imported by default: " + layer);
        if (FootprintLayerMap.NormalizeLayerName("BoardOutLine", false, true) != "BoardOutline" ||
            FootprintLayerMap.NormalizeLayerName("BottomAssembly", false, true) != "TopAssembly")
            throw new InvalidOperationException("Additional layers were not mapped when enabled.");
        if (FootprintLayerMap.NormalizeLayerName("Document", false, true) != null ||
            FootprintLayerMap.NormalizeLayerName("BoardOutline", false) != "BoardOutline")
            throw new InvalidOperationException("Existing layer policy changed.");
        if (new ComponentSelection().ImportAdditionalLayers || new EeFootprintContext().ImportAdditionalLayers)
            throw new InvalidOperationException("Additional layers must default off.");
    }

    internal static void Diagnostics()
    {
        string directory = Path.Combine(Path.GetTempPath(), "import-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "errors.log");
        try
        {
            var log = new ImportLog(path);
            log.Error("Footprint C123", new InvalidOperationException("outer", new Exception("inner details")));
            string text = File.ReadAllText(path);
            if (log.ErrorCount != 1 || log.WriteFailed || !text.Contains("C123") || !text.Contains("inner details"))
                throw new InvalidOperationException("Full exception context was not retained.");
            if (new ImportLog(path).ErrorCount != 0)
                throw new InvalidOperationException("Warning counts leaked between imports.");
            var blocked = new ImportLog(Path.Combine(path, "cannot-write.log"));
            blocked.Error("test", new Exception("write failure"));
            if (!blocked.WriteFailed || blocked.ErrorCount != 1)
                throw new InvalidOperationException("An unavailable log must not interrupt or hide the warning count.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal static void ModelCoordinateFrame()
    {
        int Coord(double mm) => (int)Math.Round(mm * 10000000 / 25.4);
        object board = PcbTestProxy.Create("PCB.IPCB_Board", (method, args) => method.Name switch
        {
            "GetState_XOrigin" => Coord(90),
            "GetState_YOrigin" => Coord(40),
            _ => PcbTestProxy.Default(method.ReturnType)
        });
        object component = PcbTestProxy.Create("PCB.IPCB_LibComponent", (method, args) => method.Name switch
        {
            "GetState_XLocation" => Coord(100),
            "GetState_YLocation" => Coord(50),
            "GetState_Board" or "Internal_GetState_Board" => board,
            _ => PcbTestProxy.Default(method.ReturnType)
        });
        var primitive = new BoundsPrimitive(new BoundsRectangle(Coord(98), Coord(49), Coord(102), Coord(51)));
        var arguments = new object[] { component, primitive, null };
        bool measured = (bool)typeof(EEPCB).GetMethod("TryGetPrimitiveBoundsMm", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
        var bounds = arguments[2] as StepSilhouetteBounds;
        if (!measured || Math.Abs(bounds.CenterX) > 1e-5 || Math.Abs(bounds.CenterY) > 1e-5)
            throw new InvalidOperationException($"Expected component-local model center (0,0), got ({bounds?.CenterX},{bounds?.CenterY}).");

        int movedX = 0, movedY = 0;
        int modelZ = int.MinValue;
        object model = PcbTestProxy.Create("PCB.IPCB_Model", (method, args) =>
        {
            if (method.Name == "SetState") modelZ = Convert.ToInt32(args[3]);
            return PcbTestProxy.Default(method.ReturnType);
        });
        object body = PcbTestProxy.Create("PCB.IPCB_ComponentBody", (method, args) =>
        {
            if (method.Name.Contains("ModelFactory_FromFilename")) return model;
            if (method.Name == "MoveByXY") { movedX = (int)args[0]; movedY = (int)args[1]; }
            return PcbTestProxy.Default(method.ReturnType);
        });
        object server = PcbTestProxy.Create("PCB.IPCB_ServerInterface", (method, args) => method.Name.Contains("PCBObjectFactory") ? body : PcbTestProxy.Default(method.ReturnType));
        var serverField = typeof(AltiumApi).GetNestedType("GlobalVars", BindingFlags.NonPublic).GetField("pcbServer", BindingFlags.Static | BindingFlags.NonPublic);
        object previous = serverField.GetValue(null);
        try
        {
            serverField.SetValue(null, server);
            typeof(EEPCB).GetMethod("CreateComponentBody").Invoke(null, new object[] { component, "fixture.step", 0d, 0d, 0d, 0.75d, -0.25d, -0.20d, "fixture", 0d });
            if (Math.Abs(movedX - Coord(100.75)) > 1 || Math.Abs(movedY - Coord(49.75)) > 1)
                throw new InvalidOperationException("Model fallback placement used board origin instead of footprint location.");
            if (Math.Abs(modelZ - Coord(-0.20)) > 1)
                throw new InvalidOperationException("Signed model Z correction did not reach Altium unchanged.");
        }
        finally { serverField.SetValue(null, previous); }
    }

    public sealed class BoundsPrimitive
    {
        private readonly BoundsRectangle rectangle;
        public BoundsPrimitive(BoundsRectangle value) { rectangle = value; }
        public object Internal_BoundingRectangle() => rectangle;
    }

    public sealed class BoundsRectangle
    {
        private readonly int left, bottom, right, top;
        public BoundsRectangle(int l, int b, int r, int t) { left = l; bottom = b; right = r; top = t; }
        public int GetLeft() => left;
        public int GetBottom() => bottom;
        public int GetRight() => right;
        public int GetTop() => top;
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        internal string Query;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Query = request.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":{\"productList\":[]}}")
            });
        }
    }
}

public class PcbTestProxy : DispatchProxy
{
    public Func<MethodInfo, object[], object> Call { get; set; }
    protected override object Invoke(MethodInfo method, object[] args) => Call(method, args);
    public static object Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    public static object Create(string typeName, Func<MethodInfo, object[], object> call)
    {
        object proxy = DispatchProxy.Create(Type.GetType(typeName + ", Altium.SDK.Interfaces", true), typeof(PcbTestProxy));
        ((PcbTestProxy)proxy).Call = call;
        return proxy;
    }
}
