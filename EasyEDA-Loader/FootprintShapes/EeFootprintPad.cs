using PCB;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyEDA_Loader
{
    public class EeFootprintPad : EeFootprintShape
    {
        public static EeFootprintPad FromString(string data)
        {
            var parts = data.Split(new[] { "~" }, StringSplitOptions.None);
            return new EeFootprintPad
            {
                Shape = parts[1],
                CenterX = ConvertToMM(EeShape.ParseDouble(parts[2])),
                CenterY = ConvertToMM(EeShape.ParseDouble(parts[3])),
                Width = ConvertToMM(EeShape.ParseDouble(parts[4])),
                Height = ConvertToMM(EeShape.ParseDouble(parts[5])),
                Layer = parts[6],
                Net = parts[7],
                Number = parts[8],
                HoleRadius = ConvertToMM(EeShape.ParseDouble(parts[9])),
                Points = EePoint.ListFromString(parts[10]),
                Rotation = EeShape.ParseDouble(parts[11]),
                Id = parts[12],
                HoleLength = ConvertToMM(EeShape.ParseDouble(parts[13])),
                HolePoints = EePoint.ListFromString(parts[14]),
                IsPlated = ParseBoolean(parts[15]),
                IsLocked = ParseBoolean(parts[16]),
                PasteMaskExpansion = ParseExpansion(parts, 17),
                SolderMaskExpansion = ParseExpansion(parts, 18),
            };
        }

        private static double? ParseExpansion(string[] parts, int index)
        {
            double? value = index < parts.Length ? ParseNullableDouble(parts[index]) : null;
            if (value.HasValue && !double.IsFinite(value.Value))
                throw new FormatException("Non-finite pad mask expansion.");
            return value.HasValue ? ConvertToMM(value.Value) : null;
        }
        public override List<UIElement> AddToCanvas(Canvas c, EeFootprintContext ctx)
        {
            var elements = new List<UIElement>();

            double Radius = 0;
            if (Shape == "OVAL")
            {
                if (Width > Height)
                    Radius = Height / 2;
                else
                    Radius = Width / 2;
            }
            else if (Shape == "ELLIPSE")
            {
                Radius = Math.Max(Height / 2, Width / 2);
            }
            if (Shape == "POLYGON")
            {
                var geometry = new StreamGeometry();
                using (var drawing = geometry.Open())
                {
                    if (Points == null || Points.Count < 3)
                        throw new FormatException("Polygon pad must contain at least three points.");
                    // EasyEDA polygon vertices already include the pad rotation.
                    drawing.BeginFigure(new Point(Points[0].X - ctx.Box.X, Points[0].Y - ctx.Box.Y), true, true);
                    for (int index = 1; index < Points.Count; index++)
                        drawing.LineTo(new Point(Points[index].X - ctx.Box.X, Points[index].Y - ctx.Box.Y), true, false);
                }
                elements.Add(new Path
                {
                    Data = geometry,
                    Fill = new SolidColorBrush(ColorHelper.FromHex(ctx.Layers.GetLayerColor(Layer)))
                });
            }
            else
            {
                Rectangle rect = new Rectangle
                {
                    Width = Width,
                    Height = Height,
                    RadiusX = Radius,
                    RadiusY = Radius,
                    Fill = new SolidColorBrush(ColorHelper.FromHex(ctx.Layers.GetLayerColor(Layer))),
                };
                Canvas.SetLeft(rect, CenterX - (Width / 2) - ctx.Box.X);
                Canvas.SetTop(rect, CenterY - (Height / 2) - ctx.Box.Y);

                if (Rotation > 0)
                {
                    rect.RenderTransform = new RotateTransform
                    {
                        Angle = Rotation,
                        CenterX = Width / 2,
                        CenterY = Height / 2,
                    };
                }

                elements.Add(rect);
            }

            if (HolePoints.Count >= 2) // Add a pathed hole
            {
                var holePath = new Path
                {
                    Fill = new SolidColorBrush(Color.FromRgb(0, 145, 144))
                };
                var holeGeometry = new StreamGeometry();

                using (var gctx = holeGeometry.Open())
                {
                    var start = new Point(HolePoints[1].X - ctx.Box.X, HolePoints[1].Y - ctx.Box.Y);
                    var end = new Point(HolePoints[0].X - ctx.Box.X, HolePoints[0].Y - ctx.Box.Y);

                    // Direction vector
                    var dx = HolePoints[1].X - HolePoints[0].X;
                    var dy = HolePoints[1].Y - HolePoints[0].Y;
                    var len = Math.Sqrt(dx * dx + dy * dy);
                    var ux = dx / len;
                    var uy = dy / len;

                    // Perpendicular vector for radius
                    var rx = -uy * HoleRadius;
                    var ry = ux * HoleRadius;

                    // Rounded ends
                    var p1 = new Point(start.X + rx, start.Y + ry);
                    var p2 = new Point(end.X + rx, end.Y + ry);
                    var p3 = new Point(end.X - rx, end.Y - ry);
                    var p4 = new Point(start.X - rx, start.Y - ry);

                    gctx.BeginFigure(p1, true, true);
                    gctx.LineTo(p2, true, false);
                    gctx.ArcTo(p3, new Size(HoleRadius, HoleRadius), 0, false, SweepDirection.Clockwise, true, false);
                    gctx.LineTo(p4, true, false);
                    gctx.ArcTo(p1, new Size(HoleRadius, HoleRadius), 0, false, SweepDirection.Clockwise, true, false);
                }
                holePath.Data = holeGeometry;

                if (Rotation > 0)
                {
                    holePath.RenderTransform = new RotateTransform
                    {
                        Angle = Rotation,
                        CenterX = (HolePoints[1].X - ctx.Box.X - HolePoints[0].X - ctx.Box.X) / 2,
                        CenterY = (HolePoints[1].Y - ctx.Box.Y - HolePoints[0].Y - ctx.Box.Y) / 2,
                    };
                }

                elements.Add(holePath);
            }
            else if (HoleRadius > 0) // Add the single hole if there's a radius
            {
                Ellipse ellipse = new Ellipse
                {
                    Width = HoleRadius * 2,
                    Height = HoleRadius * 2,
                    Fill = new SolidColorBrush(Color.FromRgb(0, 145, 144)),
                };
                Canvas.SetLeft(ellipse, CenterX - HoleRadius - ctx.Box.X);
                Canvas.SetTop(ellipse, CenterY - HoleRadius - ctx.Box.Y);
                elements.Add(ellipse);
            }

            return elements;
        }
        public override bool AddToComponent(IPCB_LibComponent c, EeFootprintContext ctx)
        {
            TLayerConstant targetLayer;
            var layer = ctx.Layers.GetLayer(Layer);
            if (layer != null)
            {
                try
                {
                    TShape padShape;
                    TExtendedHoleType holeType = TExtendedHoleType.eRoundHole;
                    switch (Shape)
                    {
                        case "RECT": padShape = TShape.eRectangular; break;
                        case "OVAL": padShape = TShape.eRounded; break;
                        case "ELLIPSE": padShape = TShape.eRounded; break;
                        case "POLYGON": padShape = TShape.eCustomShape; break;
                        default: throw new Exception($"Unknown pad shape {Shape}");
                    }
                    if (HoleLength > 0)
                    {
                        holeType = TExtendedHoleType.eSlotHole;
                    }

                    if (!EEPCB.TryEELayerToAltium(layer.Name, ctx.ImportLcscMechanicalLayers, out targetLayer, ctx.ImportAdditionalLayers))
                        return true;

                    string padName = Number;
                    if (string.IsNullOrWhiteSpace(padName))
                    {
                        padName = HoleRadius > 0 || targetLayer == TLayerConstant.eMultiLayer
                            ? "MH" + (++ctx.MountingHoleCount).ToString()
                            : "MP" + (++ctx.MountingPadCount).ToString();
                    }

                    bool isSmdCopperPad = targetLayer == TLayerConstant.eTopLayer || targetLayer == TLayerConstant.eBottomLayer;
                    bool isRoundBall = isSmdCopperPad && padShape == TShape.eRounded && Math.Abs(Width - Height) < 0.001 && HoleRadius <= 0;
                    bool useRoundedRectangle =
                        (isSmdCopperPad && !isRoundBall && (padShape == TShape.eRectangular || padShape == TShape.eRounded)) ||
                        (targetLayer == TLayerConstant.eMultiLayer && padName == "1" && padShape == TShape.eRectangular);
                    TShape altiumPadShape = useRoundedRectangle ? TShape.eRoundedRectangular : padShape;

                    bool isPolygon = Shape == "POLYGON";
                    var pad = EEPCB.CreatePTH(c, targetLayer, holeType, altiumPadShape, ConvertX(CenterX, ctx), ConvertY(CenterY, ctx), Height, Width, HoleRadius * 2, padName, IsPlated, isPolygon ? 0 : Rotation);
                    if (isPolygon)
                    {
                        var contour = new ShapeContour { IsClosed = true };
                        foreach (var point in Points)
                            contour.Points.Add(new EePoint { X = point.X - CenterX, Y = CenterY - point.Y });
                        EEPCB.SetPolygonPadShape(pad, targetLayer, contour);
                    }
                    else if (altiumPadShape == TShape.eRoundedRectangular)
                    {
                        pad.SetState_StackCRPctOnLayer(new V7_Layer(targetLayer), 25);
                    }
                    else if (padShape == TShape.eRounded)
                    {
                        pad.SetState_StackCRPctOnLayer(new V7_Layer(targetLayer), 100);
                    }
                    if (HoleLength > 0)
                    {
                        pad.SetState_HoleRotation((Height > Width ? 90 : 0) + (isPolygon ? Rotation : 0));
                        pad.SetState_HoleWidth(AltiumApi.MmToCoord(HoleLength));
                    }
                    if (PasteMaskExpansion.HasValue || SolderMaskExpansion.HasValue)
                    {
                        var cache = pad.GetState_Cache();
                        if (SolderMaskExpansion.HasValue)
                        {
                            cache.SolderMaskExpansionValid = TCacheState.eCacheManual;
                            cache.UseSeparateExpansions = true;
                            cache.SolderMaskExpansion = AltiumApi.MmToCoord(SolderMaskExpansion.Value);
                            cache.SolderMaskBottomExpansion = AltiumApi.MmToCoord(SolderMaskExpansion.Value);
                        }
                        if (PasteMaskExpansion.HasValue)
                        {
                            cache.PasteMaskExpansionValid = TCacheState.eCacheManual;
                            cache.PasteMaskExpansion = AltiumApi.MmToCoord(PasteMaskExpansion.Value);
                        }
                        pad.SetState_Cache(cache);
                    }
                    EEPCB.AddToPCB(c, pad);
                }
                catch (Exception ex)
                {
                    if (ctx.Exception != null && !ctx.Exception(ex))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        public string Shape { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Layer { get; set; }
        public string Net { get; set; }
        public string Number { get; set; }
        public double HoleRadius { get; set; }
        public List<EePoint> Points { get; set; }
        public double Rotation { get; set; }
        public string Id { get; set; }
        public double HoleLength { get; set; }
        public List<EePoint> HolePoints { get; set; }
        public bool IsPlated { get; set; }
        public bool IsLocked { get; set; }
        public double? PasteMaskExpansion { get; set; }
        public double? SolderMaskExpansion { get; set; }
    }

}
