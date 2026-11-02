using EasyEDA_Loader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class PolygonPadImportTests
{
    public interface CustomPad : PCB.IPCB_Pad4, PCB.IPCB_CustomPadShape { }

    private sealed class PadCapture
    {
        public string Name;
        public double Rotation;
        public int Layer;
        public int X;
        public int Y;
        public int HoleSize;
        public int HoleWidth;
        public double HoleRotation;
        public PCB.TExtendedHoleType HoleType;
        public bool Plated;
        public readonly Dictionary<int, RegionCapture> Shapes = new Dictionary<int, RegionCapture>();
        public readonly HashSet<RegionCapture> LinkedShapes = new HashSet<RegionCapture>();
        public readonly Dictionary<int, int> StackShapes = new Dictionary<int, int>();
    }

    private sealed class RegionCapture
    {
        public int Layer;
        public List<(int X, int Y)> Points;
    }

    private static int Coord(double mm) => (int)Math.Round(mm * 10000000 / 25.4);
    private static int LayerNumber(object layer) => (int)layer.GetType().GetMethod("Number").Invoke(layer, null);
    private static int LayerNumber(string layer)
    {
        var constantType = Type.GetType("PCB.TLayerConstant, Altium.SDK", true);
        var layerType = Type.GetType("PCB.V7_Layer, Altium.SDK", true);
        return LayerNumber(Activator.CreateInstance(layerType, new[] { Enum.Parse(constantType, layer) }));
    }

    private static FootprintData ReadFixture() => JsonConvert.DeserializeObject<FootprintData>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "C2895290-footprint.json")));

    internal static void ImportAllPads() => ImportFixturePads(false);
    internal static void ImportOnePolygon() => ImportFixturePads(true);

    private static void ImportFixturePads(bool minimal)
    {
        var footprint = ReadFixture();
        var pads = footprint.Shapes.OfType<EeFootprintPad>();
        if (minimal) pads = pads.Where(pad => pad.Shape == "POLYGON").Take(1);
        var sources = pads.ToList();
        var imported = ImportPads(footprint, sources);
        if (imported.Count != (minimal ? 1 : 8) || imported.Count(pad => pad.Name == "3") != (minimal ? 1 : 4))
            throw new InvalidOperationException("Expected all eight pads, including four separate pad-3 polygons.");
        for (int index = 0; index < sources.Count; index++)
        {
            AssertPadProperties(footprint, sources[index], imported[index], "eTopLayer");
            if (sources[index].Shape == "POLYGON")
                AssertPolygon(sources[index], imported[index], "eTopLayer");
        }
    }

    internal static void BottomLayer()
    {
        var footprint = ReadFixture();
        var source = footprint.Shapes.OfType<EeFootprintPad>().Where(pad => pad.Shape == "POLYGON").ElementAt(1);
        source.Layer = "2";
        var actual = ImportPads(footprint, new[] { source }).Single();
        AssertPadProperties(footprint, source, actual, "eBottomLayer");
        AssertPolygon(source, actual, "eBottomLayer");
    }

    internal static void MultiLayer()
    {
        var footprint = ReadFixture();
        var source = footprint.Shapes.OfType<EeFootprintPad>().Where(pad => pad.Shape == "POLYGON").ElementAt(2);
        source.Layer = "11";
        source.HoleRadius = 0.075;
        var actual = ImportPads(footprint, new[] { source }).Single();
        AssertPadProperties(footprint, source, actual, "eMultiLayer");
        AssertPolygon(source, actual, "eTopLayer", "eMidLayer1", "eBottomLayer");
    }

    internal static void RotatedSlot()
    {
        var footprint = ReadFixture();
        var source = footprint.Shapes.OfType<EeFootprintPad>().First(pad => pad.Shape == "POLYGON");
        source.Layer = "11";
        source.HoleRadius = 0.075;
        source.HoleLength = 0.25;
        double halfCenterline = (source.HoleLength - 2 * source.HoleRadius) / 2;
        source.HolePoints = new List<EePoint>
        {
            new EePoint { X = source.CenterX - halfCenterline, Y = source.CenterY },
            new EePoint { X = source.CenterX + halfCenterline, Y = source.CenterY }
        };
        source.Rotation = 37;
        double radians = source.Rotation * Math.PI / 180;
        // EasyEDA positive rotation is counterclockwise in its Y-down frame.
        // Rotate absolute copper and drill endpoints together to keep the source valid.
        foreach (var point in source.Points.Concat(source.HolePoints))
        {
            double x = point.X - source.CenterX, y = point.Y - source.CenterY;
            point.X = source.CenterX + x * Math.Cos(radians) + y * Math.Sin(radians);
            point.Y = source.CenterY - x * Math.Sin(radians) + y * Math.Cos(radians);
        }
        var actual = ImportPads(footprint, new[] { source }).Single();
        AssertPadProperties(footprint, source, actual, "eMultiLayer");
        AssertPolygon(source, actual, "eTopLayer", "eMidLayer1", "eBottomLayer");
        // Derive the required drill direction from source endpoints after Y inversion,
        // independently of the importer's Width/Height and Rotation expression.
        double dx = source.HolePoints[1].X - source.HolePoints[0].X;
        double dy = source.HolePoints[0].Y - source.HolePoints[1].Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double effectiveAngle = (actual.Rotation + actual.HoleRotation) * Math.PI / 180;
        double alignment = Math.Abs((dx * Math.Cos(effectiveAngle) + dy * Math.Sin(effectiveAngle)) / length);
        if (Math.Abs(alignment - 1) > 1e-10)
            throw new InvalidOperationException("Polygon slot direction differs from its source drill endpoints.");
    }

    private static void AssertPadProperties(FootprintData footprint, EeFootprintPad source, PadCapture actual, string layer)
    {
        if (actual.Name != source.Number || actual.Layer != LayerNumber(layer) ||
            actual.X != Coord(source.CenterX - footprint.Head.X) + Coord(10) ||
            actual.Y != Coord(footprint.Head.Y - source.CenterY) + Coord(20) ||
            actual.HoleSize != Coord(source.HoleRadius * 2) || actual.Plated != source.IsPlated ||
            actual.HoleType != (source.HoleLength > 0 ? PCB.TExtendedHoleType.eSlotHole : PCB.TExtendedHoleType.eRoundHole) ||
            (source.HoleLength > 0 && actual.HoleWidth != Coord(source.HoleLength)))
            throw new InvalidOperationException("Pad lost its position, layer, number, plating, drill diameter, or slot dimensions.");
    }

    private static void AssertPolygon(EeFootprintPad source, PadCapture actual, params string[] layers)
    {
        var expectedLayers = layers.Select(LayerNumber).OrderBy(layer => layer).ToArray();
        if (actual.Rotation != 0 || !actual.Shapes.Keys.OrderBy(layer => layer).SequenceEqual(expectedLayers) ||
            !actual.StackShapes.Keys.OrderBy(layer => layer).SequenceEqual(expectedLayers) ||
            actual.StackShapes.Values.Any(shape => shape != (int)PCB.TShape.eCustomShape) ||
            actual.LinkedShapes.Count != layers.Length)
            throw new InvalidOperationException("Polygon custom geometry was assigned to the wrong pad layers or rotated twice.");
        var expectedPoints = source.Points.Select(point => (Coord(point.X - source.CenterX), Coord(source.CenterY - point.Y))).ToList();
        foreach (int layer in expectedLayers)
        {
            var shape = actual.Shapes[layer];
            if (shape.Layer != layer || !actual.LinkedShapes.Contains(shape) || !shape.Points.SequenceEqual(expectedPoints))
                throw new InvalidOperationException("Polygon pad lost its linked native region, region layer, or pad-local vertices.");
        }
    }

    private static List<PadCapture> ImportPads(FootprintData footprint, IEnumerable<EeFootprintPad> pads)
    {
        var imported = new List<PadCapture>();
        var padsByObject = new Dictionary<object, PadCapture>();
        var regions = new Dictionary<object, RegionCapture>();
        var polygons = new Dictionary<object, List<(int X, int Y)>>();
        var contours = new Dictionary<object, List<(int X, int Y)>>();
        var warnings = new List<Exception>();
        object component = PcbTestProxy.Create("PCB.IPCB_LibComponent", (method, args) =>
        {
            if (method.Name == "AddPCBObject")
                imported.Add(padsByObject[args[0]]);
            if (method.Name == "GetState_XLocation") return Coord(10);
            if (method.Name == "GetState_YLocation") return Coord(20);
            return PcbTestProxy.Default(method.ReturnType);
        });
        object server = PcbTestProxy.Create("PCB.IPCB_ServerInterface", (method, args) =>
        {
            if (method.Name.Contains("GeometricPolygonFactory"))
            {
                object polygon = null;
                polygon = PcbTestProxy.Create("PCB.IPCB_GeometricPolygon", (member, values) =>
                {
                    if (member.Name == "Internal_AddContourIsHole") polygons[polygon] = contours[values[0]];
                    return PcbTestProxy.Default(member.ReturnType);
                });
                return polygon;
            }
            if (method.Name.Contains("ContourFactory"))
            {
                var points = new List<(int X, int Y)>();
                object contour = PcbTestProxy.Create("PCB.IPCB_Contour", (member, values) =>
                {
                    if (member.Name == "AddPoint") points.Add(((int)values[0], (int)values[1]));
                    return PcbTestProxy.Default(member.ReturnType);
                });
                contours[contour] = points;
                return contour;
            }
            if (method.Name.Contains("PCBObjectFactory") && (int)args[0] == (int)PCB.TObjectId.eRegionObject)
            {
                var capture = new RegionCapture();
                object region = PcbTestProxy.Create("PCB.IPCB_Region", (member, values) =>
                {
                    if (member.Name == "SetGeometricPolygon") capture.Points = polygons[values[0]];
                    if (member.Name == "SetState_V7Layer") capture.Layer = LayerNumber(values[0]);
                    return PcbTestProxy.Default(member.ReturnType);
                });
                regions[region] = capture;
                return region;
            }
            if (method.Name.Contains("PCBObjectFactory"))
            {
                var capture = new PadCapture();
                object pad = DispatchProxy.Create(typeof(CustomPad), typeof(PcbTestProxy));
                ((PcbTestProxy)pad).Call = (member, values) =>
                {
                    if (member.Name == "SetState_Name") capture.Name = (string)values[0];
                    if (member.Name == "SetState_Rotation") capture.Rotation = (double)values[0];
                    if (member.Name == "SetState_XLocation") capture.X = (int)values[0];
                    if (member.Name == "SetState_YLocation") capture.Y = (int)values[0];
                    if (member.Name == "SetState_V7Layer") capture.Layer = LayerNumber(values[0]);
                    if (member.Name == "SetState_HoleSize") capture.HoleSize = (int)values[0];
                    if (member.Name == "SetState_HoleWidth") capture.HoleWidth = (int)values[0];
                    if (member.Name == "SetState_HoleRotation") capture.HoleRotation = (double)values[0];
                    if (member.Name == "SetState_HoleType") capture.HoleType = (PCB.TExtendedHoleType)values[0];
                    if (member.Name == "SetState_Plated") capture.Plated = (bool)values[0];
                    if (member.Name == "SetState_StackShapeOnLayer") capture.StackShapes[LayerNumber(values[0])] = (int)values[1];
                    if (member.Name == "SetProperty_CustomShape") capture.Shapes[LayerNumber(values[0])] = regions[values[1]];
                    if (member.Name == "LinkCustomShape") capture.LinkedShapes.Add(regions[values[0]]);
                    return PcbTestProxy.Default(member.ReturnType);
                };
                padsByObject[pad] = capture;
                return pad;
            }
            return PcbTestProxy.Default(method.ReturnType);
        });
        var field = typeof(AltiumApi).GetNestedType("GlobalVars", BindingFlags.NonPublic)
            .GetField("pcbServer", BindingFlags.NonPublic | BindingFlags.Static);
        object previous = field.GetValue(null);
        try
        {
            field.SetValue(null, server);
            var context = new EeFootprintContext
            {
                Box = footprint.BoundingBox,
                Layers = footprint.Layers,
                OriginX = footprint.Head.X,
                OriginY = footprint.Head.Y,
                Exception = error => { warnings.Add(error); return true; }
            };
            foreach (var pad in pads)
                typeof(EeFootprintPad).GetMethod("AddToComponent").Invoke(pad, new[] { component, context });
            if (warnings.Count != 0)
                throw new InvalidOperationException("Pad import warnings: " + string.Join("; ", warnings.Select(error => error.Message)));
            return imported;
        }
        finally { field.SetValue(null, previous); }
    }

    internal static void FailedImportClosesEdit()
    {
        int begins = 0, ends = 0;
        object server = PcbTestProxy.Create("PCB.IPCB_ServerInterface", (method, args) =>
        {
            if (method.Name == "PreProcess") begins++;
            if (method.Name == "PostProcess") ends++;
            return PcbTestProxy.Default(method.ReturnType);
        });
        var field = typeof(AltiumApi).GetNestedType("GlobalVars", BindingFlags.NonPublic)
            .GetField("pcbServer", BindingFlags.NonPublic | BindingFlags.Static);
        object previous = field.GetValue(null);
        var expected = new InvalidOperationException("primitive import failed");
        try
        {
            field.SetValue(null, server);
            var footprint = new FootprintData { Shapes = new List<EeFootprintShape> { new FailingShape(expected) } };
            bool threw = false;
            try { footprint.AddToComponent(null, new EeFootprintContext()); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, expected)) { threw = true; }
            if (!threw || begins != 1 || ends != 1)
                throw new InvalidOperationException($"Failed import left PCB edit unbalanced: PreProcess={begins}, PostProcess={ends}.");
        }
        finally { field.SetValue(null, previous); }
    }

    internal static void Preview()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var footprint = JsonConvert.DeserializeObject<FootprintData>(File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "C2895290-footprint.json")));
                var context = new EeFootprintContext { Box = footprint.BoundingBox, Layers = footprint.Layers };
                foreach (var pad in footprint.Shapes.OfType<EeFootprintPad>().Where(pad => pad.Shape == "POLYGON"))
                {
                    var elements = pad.AddToCanvas(null, context);
                    if (elements.Count != 1 || !(elements[0] is System.Windows.Shapes.Path path) || !path.RenderTransform.Value.IsIdentity)
                        throw new InvalidOperationException("Polygon preview was replaced by a rectangle or rotated twice.");
                    var bounds = path.Data.Bounds;
                    if (Math.Abs(bounds.Left - (pad.Points.Min(point => point.X) - context.Box.X)) > 1e-6 ||
                        Math.Abs(bounds.Top - (pad.Points.Min(point => point.Y) - context.Box.Y)) > 1e-6 ||
                        Math.Abs(bounds.Right - (pad.Points.Max(point => point.X) - context.Box.X)) > 1e-6 ||
                        Math.Abs(bounds.Bottom - (pad.Points.Max(point => point.Y) - context.Box.Y)) > 1e-6)
                        throw new InvalidOperationException("Polygon preview lost its source contour bounds.");
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    private sealed class FailingShape : EeFootprintShape
    {
        private readonly Exception error;
        internal FailingShape(Exception error) { this.error = error; }
        public override bool AddToComponent(PCB.IPCB_LibComponent component, EeFootprintContext context) => throw error;
    }
}
