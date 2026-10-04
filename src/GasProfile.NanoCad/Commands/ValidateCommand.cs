using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GasProfile.Core.Geometry;
using GasProfile.Core.Profile;
using GasProfile.Core.Tin;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class ValidateCommand
    {
        private const double MinUiColumnSpacing = 5.0;
        private const double MinSlopeSegmentWidth = 8.0;
        private const double MinSlopeSegmentLength = 2.0;
        private const double MaxSlopePermille = 150.0;
        private const double MinTopDepthWarning = 0.60;

        [CommandMethod("GASPROF_VALIDATE")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var routeResult = editor.GetEntity("\nВыберите 2D-полилинию трассы газопровода: ");
            if (routeResult.Status != PromptStatus.OK)
            {
                return;
            }

            var pointResult = editor.GetEntity("\nВыберите одну точку отметки поверхности: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            var pipeResult = editor.GetEntity("\nВыберите проектную линию трубы или Enter для пропуска: ");

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead, false) as Polyline;
                var samplePoint = transaction.GetObject(pointResult.ObjectId, OpenMode.ForRead, false) as DBPoint;
                if (routePolyline == null || samplePoint == null)
                {
                    editor.WriteMessage("\nДля диагностики нужны 2D Polyline трассы и DBPoint отметки.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var surfacePoints = CollectDbPointsOnLayer(database, transaction, samplePoint.Layer);
                var tin = DelaunayTriangulator.Build(surfacePoints);
                var profilePoints = ProfileBuilder.BuildGroundProfile(route, tin);
                var outsideTin = profilePoints.Count(p => !p.GroundElevation.HasValue);
                var casingStats = CheckCasings(database, transaction);
                var report = new ValidationReport();

                editor.WriteMessage("\n--- GASPROF_VALIDATE ---");
                editor.WriteMessage($"\nДлина трассы: {FormatNumber(route.Length)} м");
                editor.WriteMessage($"\nТочек поверхности: {surfacePoints.Count}");
                editor.WriteMessage($"\nТреугольников TIN: {tin.Triangles.Count}");
                editor.WriteMessage($"\nРасчетных точек профиля вне TIN: {outsideTin}");
                editor.WriteMessage($"\nФутляров найдено: {casingStats.Total}");
                editor.WriteMessage($"\nФутляров без START/END: {casingStats.MissingChainage}");
                editor.WriteMessage($"\nФутляров без GEOM_SIGNATURE: {casingStats.MissingSignature}");
                editor.WriteMessage($"\nФутляров с устаревшей геометрией: {casingStats.StaleSignature}");
                editor.WriteMessage($"\nФутляров без структурных параметров: {casingStats.MissingStructuredParameters}");
                editor.WriteMessage($"\nФутляров короче нормативных отступов: {casingStats.ShorterThanNormOffsets}");
                editor.WriteMessage($"\nФутляров с КТ вне границ: {casingStats.ControlTubeOutsideInterval}");

                if (surfacePoints.Count < 20)
                {
                    report.Warning($"Мало точек поверхности: {surfacePoints.Count}. TIN может быть грубым.");
                }

                if (outsideTin > 0)
                {
                    report.Warning($"Есть расчетные точки профиля вне TIN: {outsideTin}.");
                }

                if (casingStats.MissingChainage > 0)
                {
                    report.Warning("Есть футляры без START_CHAINAGE/END_CHAINAGE. Запустите GASPROF_RECALC_CASINGS.");
                }

                if (casingStats.MissingSignature > 0)
                {
                    report.Warning("Есть футляры без GEOM_SIGNATURE. Запустите GASPROF_RECALC_CASINGS.");
                }

                if (casingStats.StaleSignature > 0)
                {
                    report.Warning("Есть измененные после пересчета футляры. Запустите GASPROF_RECALC_CASINGS перед GREC.");
                }

                if (casingStats.MissingStructuredParameters > 0)
                {
                    report.Warning("Есть футляры без MATERIAL/HAS_CONTROL_TUBE/NORM_OFFSET. Запустите GASPROF_RECALC_CASINGS или GCEDIT.");
                }

                if (casingStats.ShorterThanNormOffsets > 0)
                {
                    report.Warning("Есть футляры, длина которых меньше суммы нормативных отступов начала/конца. Проверьте длину футляра или NORM_OFFSET_START/END.");
                }

                if (casingStats.ControlTubeOutsideInterval > 0)
                {
                    report.Warning("Есть футляры, у которых CONTROL_TUBE_CHAINAGE выходит за START_CHAINAGE/END_CHAINAGE. Запустите GCEDIT или GASPROF_RECALC_CASINGS.");
                }

                if (pipeResult.Status == PromptStatus.OK)
                {
                    ValidatePipe(editor, transaction, pipeResult.ObjectId, routePolyline, route, surfacePoints, samplePoint.Layer, tin, casingStats, report);
                }

                report.Write(editor);

                editor.WriteMessage("\n------------------------");
                transaction.Commit();
            }
        }

        private static void ValidatePipe(
            Editor editor,
            Transaction transaction,
            ObjectId pipeObjectId,
            Polyline routePolyline,
            Route2D route,
            IReadOnlyList<Point3> surfacePoints,
            string surfaceLayer,
            TinSurface tin,
            CasingStats casingStats,
            ValidationReport report)
        {
            var pipe = transaction.GetObject(pipeObjectId, OpenMode.ForRead, false) as Polyline;
            if (pipe == null)
            {
                report.Error("Труба: выбранный объект не 2D Polyline.");
                return;
            }

            if (!PipeProfileXData.TryRead(pipe, out var metadata))
            {
                report.Error("Труба: нет метаданных GASPROF_PIPE.");
                return;
            }

            var pipePoints = new List<PipePoint>();
            for (var i = 0; i < pipe.NumberOfVertices; i++)
            {
                var point = pipe.GetPoint2dAt(i);
                var chainage = Clamp(metadata.GetChainage(point.X), 0.0, metadata.RouteLength);
                var elevation = metadata.GetElevation(point.Y);
                pipePoints.Add(new PipePoint(chainage, elevation));
            }

            pipePoints = pipePoints.OrderBy(p => p.Chainage).ToList();
            var minDepth = double.MaxValue;
            var minTrenchDepth = double.MaxValue;
            var negativeDepthCount = 0;
            var highSlopeCount = 0;
            var shortSlopeCount = 0;
            var duplicateChainageCount = 0;

            if (Math.Abs(metadata.RouteLength - route.Length) > 0.05)
            {
                report.Warning($"Длина трассы в профиле {FormatNumber(metadata.RouteLength)} м отличается от выбранной трассы {FormatNumber(route.Length)} м.");
            }

            var currentRouteSignature = ProfileSignatures.ForRoute(routePolyline, route.Length);
            if (!string.IsNullOrWhiteSpace(metadata.RouteSignature) &&
                !string.Equals(metadata.RouteSignature, currentRouteSignature, StringComparison.OrdinalIgnoreCase))
            {
                report.Warning("Геометрия трассы изменилась после построения профиля. Лучше перестроить профиль или пересчитать связь.");
            }

            var currentSurfaceSignature = ProfileSignatures.ForSurface(surfaceLayer, surfacePoints);
            if (!string.IsNullOrWhiteSpace(metadata.SurfaceSignature) &&
                !string.Equals(metadata.SurfaceSignature, currentSurfaceSignature, StringComparison.OrdinalIgnoreCase))
            {
                report.Warning("Набор точек поверхности изменился после построения профиля. Рекомендуется перестроить профиль.");
            }

            var currentPipeSignature = ProfileSignatures.ForPipe(pipe);
            if (!string.IsNullOrWhiteSpace(metadata.PipeSignature) &&
                !string.Equals(metadata.PipeSignature, currentPipeSignature, StringComparison.OrdinalIgnoreCase))
            {
                report.Warning("Проектная труба изменена после последнего пересчета таблицы. Запустите GREC перед выдачей.");
            }

            if (metadata.PipeOuterDiameter <= 0.0)
            {
                report.Error("Не задан наружный диаметр трубы.");
            }

            if (metadata.SandBeddingThickness < 0.0)
            {
                report.Error("Толщина песчаной подушки меньше нуля.");
            }

            if (string.IsNullOrWhiteSpace(metadata.PipeLabel))
            {
                report.Warning("Не задана подпись трубы и типа изоляции.");
            }

            if (string.IsNullOrWhiteSpace(metadata.FoundationLabel))
            {
                report.Warning("Не задана подпись основания.");
            }

            foreach (var pipePoint in pipePoints)
            {
                if (!tin.TryGetElevation(route.GetPointAtChainage(Clamp(pipePoint.Chainage, 0.0, route.Length)), out var ground))
                {
                    continue;
                }

                var depth = ground - pipePoint.Elevation;
                var trenchBottom = pipePoint.Elevation - metadata.PipeOuterDiameter - metadata.SandBeddingThickness;
                var trenchDepth = ground - trenchBottom;
                minDepth = Math.Min(minDepth, depth);
                minTrenchDepth = Math.Min(minTrenchDepth, trenchDepth);
                if (depth < -0.001)
                {
                    negativeDepthCount++;
                    report.Error($"{ChainageFormatter.Format(pipePoint.Chainage)}: верх трубы выше земли.");
                }
                else if (depth < MinTopDepthWarning)
                {
                    report.Warning($"{ChainageFormatter.Format(pipePoint.Chainage)}: глубина до верха трубы {FormatNumber(depth)} м меньше {FormatNumber(MinTopDepthWarning)} м.");
                }
            }

            for (var i = 1; i < pipePoints.Count; i++)
            {
                var prev = pipePoints[i - 1];
                var next = pipePoints[i];
                var length = next.Chainage - prev.Chainage;
                if (Math.Abs(length) < 0.000001)
                {
                    duplicateChainageCount++;
                    continue;
                }

                var slopePermille = Math.Abs((next.Elevation - prev.Elevation) / length * 1000.0);
                if (slopePermille > MaxSlopePermille)
                {
                    highSlopeCount++;
                    report.Warning($"{ChainageFormatter.Format(prev.Chainage)}-{ChainageFormatter.Format(next.Chainage)}: уклон {FormatSlope(slopePermille)}‰ больше {FormatSlope(MaxSlopePermille)}‰.");
                }

                if (length < MinSlopeSegmentLength || length * metadata.HorizontalScale < MinSlopeSegmentWidth)
                {
                    shortSlopeCount++;
                    report.Warning($"{ChainageFormatter.Format(prev.Chainage)}-{ChainageFormatter.Format(next.Chainage)}: короткий участок уклонов L={FormatNumber(length)} м, подпись может налезать.");
                }
            }

            ValidateUiColumnDensity(route, pipePoints, casingStats, metadata, report);

            editor.WriteMessage($"\nТруба: вершин {pipePoints.Count}");
            editor.WriteMessage($"\nТруба: мин. глубина {(minDepth < double.MaxValue ? FormatNumber(minDepth) : "-")} м");
            editor.WriteMessage($"\nТруба: мин. глубина траншеи {(minTrenchDepth < double.MaxValue ? FormatNumber(minTrenchDepth) : "-")} м");
            editor.WriteMessage($"\nТруба: вершин с отрицательной глубиной {negativeDepthCount}");
            editor.WriteMessage($"\nТруба: участков с уклоном >150‰ {highSlopeCount}");
            editor.WriteMessage($"\nТруба: коротких участков уклонов {shortSlopeCount}");
            editor.WriteMessage($"\nТруба: дублей пикетажа вершин {duplicateChainageCount}");
        }

        private static CasingStats CheckCasings(Database database, Transaction transaction)
        {
            var result = new CasingStats();
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForRead);

            foreach (ObjectId objectId in modelSpace)
            {
                var blockReference = transaction.GetObject(objectId, OpenMode.ForRead, false) as BlockReference;
                if (blockReference == null)
                {
                    continue;
                }

                var block = transaction.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                if (block == null)
                {
                    continue;
                }

                var attrs = ReadAttributes(transaction, blockReference);
                var name = GetEffectiveBlockName(transaction, blockReference, block);
                if (!IsCasing(name, attrs))
                {
                    continue;
                }

                result.Total++;
                if (!CasingData.HasStructuredParameters(attrs))
                {
                    result.MissingStructuredParameters++;
                }

                if (!CasingData.TryReadFromAttributes(name, attrs, out var casing))
                {
                    result.MissingChainage++;
                }
                else
                {
                    if (casing.Length <= 0.001)
                    {
                        result.BadLength++;
                    }

                    if (casing.NormOffsetStart + casing.NormOffsetEnd > casing.Length + 0.001)
                    {
                        result.ShorterThanNormOffsets++;
                    }

                    if (casing.HasControlTube &&
                        (casing.ControlTubeChainage < casing.StartChainage - 0.001 ||
                         casing.ControlTubeChainage > casing.EndChainage + 0.001))
                    {
                        result.ControlTubeOutsideInterval++;
                    }

                    result.Intervals.Add(new CasingInterval(casing.StartChainage, casing.EndChainage));
                }

                if (!attrs.TryGetValue(CasingAttributeNames.GeomSignature, out var savedSignature) ||
                    string.IsNullOrWhiteSpace(savedSignature))
                {
                    result.MissingSignature++;
                    continue;
                }

                var currentSignature = ComputeCasingStateSignature(name, blockReference, block, transaction);
                if (!string.Equals(savedSignature, currentSignature, StringComparison.OrdinalIgnoreCase))
                {
                    result.StaleSignature++;
                }
            }

            return result;
        }

        private static void ValidateUiColumnDensity(
            Route2D route,
            IReadOnlyList<PipePoint> pipePoints,
            CasingStats casingStats,
            PipeProfileMetadata metadata,
            ValidationReport report)
        {
            var stations = new List<double>
            {
                0.0,
                route.Length
            };

            for (var i = 1; i < route.Vertices.Count - 1; i++)
            {
                stations.Add(route.GetVertexChainage(i));
            }

            foreach (var point in pipePoints)
            {
                stations.Add(point.Chainage);
            }

            foreach (var interval in casingStats.Intervals)
            {
                stations.Add(interval.StartChainage);
                stations.Add(interval.EndChainage);
            }

            var ordered = stations
                .Where(s => s >= -0.001 && s <= route.Length + 0.001)
                .Select(s => Clamp(s, 0.0, route.Length))
                .OrderBy(s => s)
                .ToList();

            var merged = new List<double>();
            foreach (var station in ordered)
            {
                if (merged.Count == 0 || Math.Abs(station - merged[merged.Count - 1]) > 0.01)
                {
                    merged.Add(station);
                }
            }

            for (var i = 1; i < merged.Count; i++)
            {
                var distance = merged[i] - merged[i - 1];
                var drawingWidth = distance * metadata.HorizontalScale;
                if (drawingWidth < MinUiColumnSpacing)
                {
                    report.Warning($"{ChainageFormatter.Format(merged[i - 1])}-{ChainageFormatter.Format(merged[i])}: плотные табличные колонки, ширина {FormatNumber(drawingWidth)} ед. чертежа.");
                }
            }
        }

        private static Route2D ReadRoute(Polyline polyline)
        {
            var vertices = new List<Point2>(polyline.NumberOfVertices);
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint2dAt(i);
                vertices.Add(new Point2(point.X, point.Y));
            }

            return new Route2D(vertices);
        }

        private static List<Point3> CollectDbPointsOnLayer(Database database, Transaction transaction, string layer)
        {
            var result = new List<Point3>();
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForRead);

            foreach (ObjectId objectId in modelSpace)
            {
                var dbPoint = transaction.GetObject(objectId, OpenMode.ForRead, false) as DBPoint;
                if (dbPoint == null || !string.Equals(dbPoint.Layer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var position = dbPoint.Position;
                result.Add(new Point3(position.X, position.Y, position.Z));
            }

            return result;
        }

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private static Dictionary<string, string> ReadAttributes(Transaction transaction, BlockReference blockReference)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null)
                {
                    result[attribute.Tag] = attribute.TextString;
                }
            }

            return result;
        }

        private static bool IsCasing(string blockName, IReadOnlyDictionary<string, string> attrs)
        {
            if (!string.IsNullOrWhiteSpace(blockName) &&
                blockName.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return attrs.TryGetValue("DN", out var dn) &&
                !string.IsNullOrWhiteSpace(dn) &&
                dn.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ComputeCasingStateSignature(
            string blockName,
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction)
        {
            var points = CollectCasingGeometryPoints(blockReference, block, transaction);
            TryGetAxisEnds(points, out var startPoint, out var endPoint);

            var dx = endPoint.X - startPoint.X;
            var dy = endPoint.Y - startPoint.Y;
            var axisLength = Math.Sqrt(dx * dx + dy * dy);
            var axisAngle = Math.Atan2(dy, dx);
            var raw = string.Join("|", new[]
            {
                blockName,
                FormatHashNumber(blockReference.Position.X),
                FormatHashNumber(blockReference.Position.Y),
                FormatHashNumber(blockReference.Rotation),
                FormatHashNumber(blockReference.ScaleFactors.X),
                FormatHashNumber(blockReference.ScaleFactors.Y),
                FormatHashNumber(startPoint.X),
                FormatHashNumber(startPoint.Y),
                FormatHashNumber(endPoint.X),
                FormatHashNumber(endPoint.Y),
                FormatHashNumber(axisLength),
                FormatHashNumber(axisAngle)
            });

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 24);
            }
        }

        private static List<Point2> CollectCasingGeometryPoints(
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction)
        {
            var points = new List<Point2>();
            foreach (ObjectId entityId in block)
            {
                var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                if (entity == null || string.Equals(entity.Layer, "Defpoints", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entity is Polyline polyline)
                {
                    for (var i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        var point = polyline.GetPoint3dAt(i).TransformBy(blockReference.BlockTransform);
                        points.Add(new Point2(point.X, point.Y));
                    }

                    continue;
                }

                if (entity is Line line)
                {
                    var startPoint = line.StartPoint.TransformBy(blockReference.BlockTransform);
                    var endPoint = line.EndPoint.TransformBy(blockReference.BlockTransform);
                    points.Add(new Point2(startPoint.X, startPoint.Y));
                    points.Add(new Point2(endPoint.X, endPoint.Y));
                }
            }

            return points;
        }

        private static bool TryGetAxisEnds(IReadOnlyList<Point2> points, out Point2 startPoint, out Point2 endPoint)
        {
            startPoint = points.Count > 0 ? points[0] : new Point2(0.0, 0.0);
            endPoint = points.Count > 1 ? points[1] : startPoint;
            if (points.Count < 2)
            {
                return false;
            }

            var axisStart = points[0];
            var axisEnd = points[1];
            var bestLengthSquared = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = i + 1; j < points.Count; j++)
                {
                    var dx = points[j].X - points[i].X;
                    var dy = points[j].Y - points[i].Y;
                    var lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared > bestLengthSquared)
                    {
                        bestLengthSquared = lengthSquared;
                        axisStart = points[i];
                        axisEnd = points[j];
                    }
                }
            }

            if (bestLengthSquared < 0.000001)
            {
                return false;
            }

            var axisLength = Math.Sqrt(bestLengthSquared);
            var ux = (axisEnd.X - axisStart.X) / axisLength;
            var uy = (axisEnd.Y - axisStart.Y) / axisLength;
            var minProjection = double.MaxValue;
            var maxProjection = double.MinValue;
            var projections = new double[points.Count];

            for (var i = 0; i < points.Count; i++)
            {
                var projection = (points[i].X - axisStart.X) * ux + (points[i].Y - axisStart.Y) * uy;
                projections[i] = projection;
                minProjection = Math.Min(minProjection, projection);
                maxProjection = Math.Max(maxProjection, projection);
            }

            var tolerance = Math.Max(0.05, (maxProjection - minProjection) * 0.05);
            startPoint = AverageProjectedEnd(points, projections, minProjection, tolerance);
            endPoint = AverageProjectedEnd(points, projections, maxProjection, tolerance);
            return true;
        }

        private static Point2 AverageProjectedEnd(
            IReadOnlyList<Point2> points,
            IReadOnlyList<double> projections,
            double target,
            double tolerance)
        {
            var x = 0.0;
            var y = 0.0;
            var count = 0;
            for (var i = 0; i < points.Count; i++)
            {
                if (Math.Abs(projections[i] - target) > tolerance)
                {
                    continue;
                }

                x += points[i].X;
                y += points[i].Y;
                count++;
            }

            return count == 0
                ? points[0]
                : new Point2(x / count, y / count);
        }

        private static bool TryReadAttributeDouble(IReadOnlyDictionary<string, string> attrs, string tag, out double value)
        {
            value = 0.0;
            if (!attrs.TryGetValue(tag, out var text) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string GetEffectiveBlockName(Transaction transaction, BlockReference blockReference, BlockTableRecord block)
        {
            try
            {
                if (!blockReference.DynamicBlockTableRecord.IsNull)
                {
                    var dynamicBlock = transaction.GetObject(blockReference.DynamicBlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (dynamicBlock != null && !string.IsNullOrWhiteSpace(dynamicBlock.Name))
                    {
                        return dynamicBlock.Name;
                    }
                }
            }
            catch
            {
            }

            return block.Name;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatSlope(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string FormatHashNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private sealed class PipePoint
        {
            public PipePoint(double chainage, double elevation)
            {
                Chainage = chainage;
                Elevation = elevation;
            }

            public double Chainage { get; }

            public double Elevation { get; }
        }

        private sealed class CasingStats
        {
            public CasingStats()
            {
                Intervals = new List<CasingInterval>();
            }

            public int Total { get; set; }

            public int MissingChainage { get; set; }

            public int MissingSignature { get; set; }

            public int StaleSignature { get; set; }

            public int BadLength { get; set; }

            public int MissingStructuredParameters { get; set; }

            public int ShorterThanNormOffsets { get; set; }

            public int ControlTubeOutsideInterval { get; set; }

            public List<CasingInterval> Intervals { get; }
        }

        private sealed class CasingInterval
        {
            public CasingInterval(double startChainage, double endChainage)
            {
                StartChainage = startChainage;
                EndChainage = endChainage;
            }

            public double StartChainage { get; }

            public double EndChainage { get; }
        }

        private sealed class ValidationReport
        {
            private const int MaxPrintedItems = 20;
            private readonly List<string> _errors = new List<string>();
            private readonly List<string> _warnings = new List<string>();

            public void Error(string text)
            {
                _errors.Add(text);
            }

            public void Warning(string text)
            {
                _warnings.Add(text);
            }

            public void Write(Editor editor)
            {
                editor.WriteMessage($"\nОшибок: {_errors.Count}");
                foreach (var item in _errors.Take(MaxPrintedItems))
                {
                    editor.WriteMessage($"\n  ОШИБКА: {item}");
                }

                if (_errors.Count > MaxPrintedItems)
                {
                    editor.WriteMessage($"\n  ... еще {_errors.Count - MaxPrintedItems} ошибок");
                }

                editor.WriteMessage($"\nПредупреждений: {_warnings.Count}");
                foreach (var item in _warnings.Take(MaxPrintedItems))
                {
                    editor.WriteMessage($"\n  ВНИМАНИЕ: {item}");
                }

                if (_warnings.Count > MaxPrintedItems)
                {
                    editor.WriteMessage($"\n  ... еще {_warnings.Count - MaxPrintedItems} предупреждений");
                }
            }
        }
    }
}
