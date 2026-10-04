using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GasProfile.Core.Geometry;
using GasProfile.Core.Profile;
using GasProfile.Core.Tin;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class RecalcProfileCommand
    {
        private const string GridLayer = "GASPROF_PROFILE_GRID";
        private const string TextLayer = "GASPROF_PROFILE_TEXT";
        private const string TextStyleName = "GASPROF_TEXT";
        private const string PreferredTextFont = "CS_Gost2304.shx";
        private static readonly LineWeight PipeLineWeight = LineWeight.LineWeight050;
        private static readonly LineWeight GridLineWeight = LineWeight.LineWeight009;
        private static readonly LineWeight TableLineWeight = LineWeight.LineWeight013;
        private static readonly LineWeight UnfoldedPlanLineWeight = LineWeight.LineWeight025;
        private static readonly LineWeight ProfileSituationLineWeight = LineWeight.LineWeight025;

        [CommandMethod("GASPROF_RECALC_PROFILE")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var pipeResult = editor.GetEntity("\nВыберите отредактированную проектную линию трубы: ");
            if (pipeResult.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var pipePolyline = transaction.GetObject(pipeResult.ObjectId, OpenMode.ForRead, false) as Polyline;
                if (pipePolyline == null)
                {
                    editor.WriteMessage("\nДля пересчета нужна 2D Polyline проектной трубы на профиле.");
                    return;
                }

                if (!PipeProfileXData.TryRead(pipePolyline, out var metadata))
                {
                    editor.WriteMessage("\nУ проектной линии нет метаданных GASPROF_PIPE. Постройте профиль заново текущей GASPROF_DRAW_DRAFT.");
                    return;
                }

                var routePolyline = TryGetPolylineByHandle(database, transaction, metadata.RouteHandle);
                var surfaceLayer = metadata.SurfaceLayer;
                var usedStoredLinks = routePolyline != null && !string.IsNullOrWhiteSpace(surfaceLayer);

                if (!usedStoredLinks)
                {
                    editor.WriteMessage("\nСвязь профиля неполная или трасса не найдена. Укажите исходные объекты один раз для обновления памяти профиля.");
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

                    routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead, false) as Polyline;
                    var samplePoint = transaction.GetObject(pointResult.ObjectId, OpenMode.ForRead, false) as DBPoint;
                    if (routePolyline == null || samplePoint == null)
                    {
                        editor.WriteMessage("\nДля обновления памяти нужны 2D Polyline трассы и DBPoint отметки.");
                        return;
                    }

                    surfaceLayer = samplePoint.Layer;
                }

                if (routePolyline == null)
                {
                    editor.WriteMessage("\nТрасса профиля не найдена.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var surfacePoints = CollectDbPointsOnLayer(database, transaction, surfaceLayer);
                var tin = DelaunayTriangulator.Build(surfacePoints);
                var groundProfile = ProfileBuilder.BuildGroundProfile(route, tin)
                    .Where(p => p.GroundElevation.HasValue)
                    .ToList();
                var pipeProfile = ReadPipeProfile(pipePolyline, metadata);
                var situationStations = CollectSituationStations(database, transaction);
                var casingPlanSpans = CollectCasingPlanSpans(database, transaction);
                var profileSituationItems = CollectProfileSituationItems(database, transaction);

                if (groundProfile.Count < 2 || pipeProfile.Count < 2)
                {
                    editor.WriteMessage("\nНедостаточно точек земли или проектной трубы для пересчета.");
                    return;
                }

                EnsureLayers(database, transaction);
                ProfileEntityXData.EnsureRegApp(database, transaction);
                var textStyleId = EnsureTextStyle(database, transaction);
                var modelSpace = GetModelSpace(database, transaction, OpenMode.ForWrite);
                var erasedCount = DeleteGeneratedObjects(
                    modelSpace,
                    transaction,
                    metadata.SessionId,
                    ProfileEntityXData.RoleTable,
                    ProfileEntityXData.RoleUnfoldedPlan,
                    ProfileEntityXData.RoleProfileSituation);
                var tableStations = BuildTableValueStations(route, pipeProfile, situationStations);
                var unfoldedStations = BuildUnfoldedPlanStations(route, situationStations);
                var slopeSegments = BuildPipeSlopeSegments(pipeProfile);
                var tableOrigin = new Point3d(metadata.InsertX, metadata.TableTopY, 0.0);
                DrawProfileSituation(modelSpace, transaction, new Point3d(metadata.InsertX, metadata.InsertY, 0.0), route, groundProfile, pipeProfile, metadata, textStyleId, profileSituationItems);
                DrawRecalculatedTable(modelSpace, transaction, tableOrigin, route, tin, pipeProfile, tableStations, unfoldedStations, slopeSegments, casingPlanSpans, metadata, textStyleId);
                UpdatePipeMetadata(
                    transaction,
                    pipeResult.ObjectId,
                    metadata,
                    routePolyline,
                    surfaceLayer,
                    route.Length,
                    surfacePoints);

                transaction.Commit();
                editor.WriteMessage("\n--- GASPROF_RECALC_PROFILE ---");
                editor.WriteMessage($"\nТочек таблицы: {tableStations.Count}");
                editor.WriteMessage($"\nВершин проектной трубы: {pipeProfile.Count}");
                editor.WriteMessage($"\nСвязь с трассой/поверхностью: {(usedStoredLinks ? "из памяти профиля" : "обновлена вручную")}");
                WriteSignatureWarning(editor, "Трасса", metadata.RouteSignature, ProfileSignatures.ForRoute(routePolyline, route.Length));
                WriteSignatureWarning(editor, "Поверхность", metadata.SurfaceSignature, ProfileSignatures.ForSurface(surfaceLayer, surfacePoints));
                WriteSignatureWarning(editor, "Труба профиля", metadata.PipeSignature, ProfileSignatures.ForPipe(pipePolyline));
                editor.WriteMessage($"\nУдалено старых объектов таблицы/плана: {erasedCount}");
                editor.WriteMessage("\nТаблица построена по текущей геометрии проектной линии трубы в исходной зоне таблицы.");
                editor.WriteMessage("\n------------------------------");
            }
        }

        private static Polyline? TryGetPolylineByHandle(Database database, Transaction transaction, string handleText)
        {
            if (string.IsNullOrWhiteSpace(handleText))
            {
                return null;
            }

            try
            {
                var handleValue = Convert.ToInt64(handleText, 16);
                var objectId = database.GetObjectId(false, new Handle(handleValue), 0);
                return transaction.GetObject(objectId, OpenMode.ForRead, false) as Polyline;
            }
            catch
            {
                return null;
            }
        }

        private static void UpdatePipeMetadata(
            Transaction transaction,
            ObjectId pipeObjectId,
            PipeProfileMetadata metadata,
            Polyline routePolyline,
            string surfaceLayer,
            double routeLength,
            IReadOnlyList<Point3> surfacePoints)
        {
            var pipePolyline = transaction.GetObject(pipeObjectId, OpenMode.ForWrite, false) as Polyline;
            if (pipePolyline == null)
            {
                return;
            }

            pipePolyline.Linetype = "Continuous";
            pipePolyline.LineWeight = PipeLineWeight;
            PipeProfileXData.Attach(
                pipePolyline,
                metadata.InsertX,
                metadata.InsertY,
                metadata.BaseElevation,
                metadata.HorizontalScale,
                metadata.VerticalScale,
                routeLength,
                metadata.SessionId,
                routePolyline.Handle.ToString(),
                surfaceLayer,
                metadata.TableTopY,
                ProfileSignatures.ForRoute(routePolyline, routeLength),
                ProfileSignatures.ForSurface(surfaceLayer, surfacePoints),
                ProfileSignatures.ForPipe(pipePolyline),
                metadata.PipeOuterDiameter,
                metadata.SandBeddingThickness,
                metadata.PipeLabel,
                metadata.FoundationLabel);
        }

        private static void WriteSignatureWarning(Editor editor, string label, string oldSignature, string currentSignature)
        {
            if (string.IsNullOrWhiteSpace(oldSignature))
            {
                return;
            }

            if (!string.Equals(oldSignature, currentSignature, StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage($"\nПредупреждение: {label} изменилась после прошлого сохранения метаданных профиля.");
            }
        }

        private static int DeleteGeneratedObjects(
            BlockTableRecord modelSpace,
            Transaction transaction,
            string sessionId,
            params string[] roles)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return 0;
            }

            var roleSet = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
            var eraseIds = new List<ObjectId>();

            foreach (ObjectId objectId in modelSpace)
            {
                var entity = transaction.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                if (entity == null ||
                    !ProfileEntityXData.TryRead(entity, out var metadata) ||
                    !string.Equals(metadata.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                    !roleSet.Contains(metadata.Role))
                {
                    continue;
                }

                eraseIds.Add(objectId);
            }

            foreach (var objectId in eraseIds)
            {
                var entity = transaction.GetObject(objectId, OpenMode.ForWrite, false) as Entity;
                entity?.Erase();
            }

            return eraseIds.Count;
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

        private static List<PipePoint> ReadPipeProfile(Polyline polyline, PipeProfileMetadata metadata)
        {
            var points = new List<PipePoint>();
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint2dAt(i);
                var chainage = Clamp(metadata.GetChainage(point.X), 0.0, metadata.RouteLength);
                var elevation = metadata.GetElevation(point.Y);
                points.Add(new PipePoint(chainage, elevation));
            }

            return points
                .OrderBy(p => p.Chainage)
                .GroupBy(p => Math.Round(p.Chainage, 3))
                .Select(g => g.First())
                .ToList();
        }

        private static IReadOnlyList<TableValueStation> BuildTableValueStations(
            Route2D route,
            IReadOnlyList<PipePoint> pipeProfile,
            IReadOnlyList<TableValueStation> situationStations)
        {
            var result = new List<TableValueStation>();
            result.Add(new TableValueStation(0.0, "Н", 100));
            result.Add(new TableValueStation(route.Length, "К", 100));

            for (var i = 1; i < route.Vertices.Count - 1; i++)
            {
                result.Add(new TableValueStation(route.GetVertexChainage(i), $"УП{i}", 90));
            }

            foreach (var pipePoint in pipeProfile)
            {
                result.Add(new TableValueStation(pipePoint.Chainage, "ПТ", 95));
            }

            result.AddRange(situationStations);

            return MergeCloseTableStations(result);
        }

        private static IReadOnlyList<TableValueStation> BuildUnfoldedPlanStations(
            Route2D route,
            IReadOnlyList<TableValueStation> situationStations)
        {
            var result = new List<TableValueStation>();
            result.Add(new TableValueStation(0.0, "Н", 100));
            for (var i = 1; i < route.Vertices.Count - 1; i++)
            {
                result.Add(new TableValueStation(route.GetVertexChainage(i), $"УП{i}", 90));
            }

            result.Add(new TableValueStation(route.Length, "К", 100));
            for (var chainage = 100.0; chainage < route.Length - 0.001; chainage += 100.0)
            {
                result.Add(new TableValueStation(chainage, $"ПК{(int)(chainage / 100.0)}", 30));
            }

            result.AddRange(situationStations);
            return MergeCloseTableStations(result);
        }

        private static IReadOnlyList<PipeSlopeSegment> BuildPipeSlopeSegments(IReadOnlyList<PipePoint> pipeProfile)
        {
            var result = new List<PipeSlopeSegment>();
            for (var i = 1; i < pipeProfile.Count; i++)
            {
                var prev = pipeProfile[i - 1];
                var next = pipeProfile[i];
                if (next.Chainage - prev.Chainage < 0.000001)
                {
                    continue;
                }

                result.Add(new PipeSlopeSegment(prev.Chainage, next.Chainage, prev.Elevation, next.Elevation));
            }

            return result;
        }

        private static IReadOnlyList<TableValueStation> CollectSituationStations(Database database, Transaction transaction)
        {
            var result = new List<TableValueStation>();
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
                var blockName = GetEffectiveBlockName(transaction, blockReference, block);
                if (string.Equals(blockName, AddSituationCommand.BlockName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!attrs.TryGetValue(AddSituationCommand.AttrChainage, out var chainageText) ||
                        !double.TryParse(chainageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var chainage))
                    {
                        continue;
                    }

                    attrs.TryGetValue(AddSituationCommand.AttrType, out var type);
                    result.Add(new TableValueStation(chainage, GetSituationKind(type), 96));
                    continue;
                }

                if (!IsCasing(blockName, attrs))
                {
                    continue;
                }

                if (attrs.TryGetValue("START_CHAINAGE", out var startText) &&
                    double.TryParse(startText, NumberStyles.Float, CultureInfo.InvariantCulture, out var start))
                {
                    result.Add(new TableValueStation(start, "ФутН", 98));
                }

                if (attrs.TryGetValue("END_CHAINAGE", out var endText) &&
                    double.TryParse(endText, NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
                {
                    result.Add(new TableValueStation(end, "ФутК", 98));
                }
            }

            return result;
        }

        private static IReadOnlyList<PlanSpan> CollectCasingPlanSpans(Database database, Transaction transaction)
        {
            var result = new List<PlanSpan>();
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
                var blockName = GetEffectiveBlockName(transaction, blockReference, block);
                if (!IsCasing(blockName, attrs) ||
                    !CasingData.TryReadFromAttributes(blockName, attrs, out var casing))
                {
                    continue;
                }

                if (casing.Length < 0.001)
                {
                    continue;
                }

                result.Add(new PlanSpan(casing.StartChainage, casing.EndChainage, casing.UnfoldedPlanLabel, casing.ControlTubeChainage, casing.HasControlTube, casing.NormOffsetPlanLabel));
            }

            return result;
        }

        private static IReadOnlyList<SituationSymbol> CollectProfileSituationItems(Database database, Transaction transaction)
        {
            var result = new List<SituationSymbol>();
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
                var blockName = GetEffectiveBlockName(transaction, blockReference, block);
                if (string.Equals(blockName, AddSituationCommand.BlockName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!attrs.TryGetValue(AddSituationCommand.AttrChainage, out var chainageText) ||
                        !double.TryParse(chainageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var chainage))
                    {
                        continue;
                    }

                    attrs.TryGetValue(AddSituationCommand.AttrType, out var type);
                    var symbolType = type ?? string.Empty;
                    result.Add(SituationSymbol.Point(
                        GetSymbolKind(symbolType),
                        chainage,
                        string.IsNullOrWhiteSpace(symbolType) ? "Сит." : symbolType,
                        string.IsNullOrWhiteSpace(symbolType) ? string.Empty : FormatPicket(chainage),
                        !IsRoutePlanMark(symbolType),
                        attrs.TryGetValue(AddSituationCommand.AttrName, out var name) ? name : string.Empty));
                    continue;
                }

                if (!IsCasing(blockName, attrs) ||
                    !CasingData.TryReadFromAttributes(blockName, attrs, out var casing))
                {
                    continue;
                }

                if (casing.Length < 0.001)
                {
                    continue;
                }

                result.Add(SituationSymbol.Casing(
                    casing.StartChainage,
                    casing.EndChainage,
                    casing.ProfileLabel,
                    FormatPicket(casing.StartChainage),
                    FormatPicket(casing.EndChainage),
                    casing.Name,
                    casing.ControlTubeChainage,
                    casing.HasControlTube,
                    casing.NormOffsetPlanLabel));
            }

            return result.OrderBy(i => i.Chainage).ToList();
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

        private static Dictionary<string, string> ReadAttributes(Transaction transaction, BlockReference blockReference)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                if (attribute == null)
                {
                    continue;
                }

                result[attribute.Tag] = attribute.TextString;
            }

            return result;
        }

        private static string GetEffectiveBlockName(
            Transaction transaction,
            BlockReference blockReference,
            BlockTableRecord block)
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

        private static string GetSituationKind(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return "Сит";
            }

            var normalized = type.Trim().ToLowerInvariant();
            if (normalized.Contains("фут"))
            {
                return "Фут";
            }

            if (normalized.Contains("дор"))
            {
                return "Дор";
            }

            if (normalized.Contains("каб"))
            {
                return "Каб";
            }

            if (normalized.Contains("вод"))
            {
                return "Вод";
            }

            if (normalized.Contains("кан"))
            {
                return "Кан";
            }

            return type.Length <= 4 ? type : type.Substring(0, 4);
        }

        private static IReadOnlyList<TableValueStation> MergeCloseTableStations(IReadOnlyList<TableValueStation> candidates)
        {
            const double minGap = 0.05;
            var sorted = candidates
                .OrderBy(s => s.Chainage)
                .ThenByDescending(s => s.Priority)
                .ToList();
            var result = new List<TableValueStation>();

            foreach (var candidate in sorted)
            {
                if (result.Count == 0 || Math.Abs(candidate.Chainage - result[result.Count - 1].Chainage) > minGap)
                {
                    result.Add(candidate);
                    continue;
                }

                if (candidate.Priority > result[result.Count - 1].Priority)
                {
                    result[result.Count - 1] = candidate;
                }
            }

            return result;
        }

        private static void DrawRecalculatedTable(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            TinSurface tin,
            IReadOnlyList<PipePoint> pipeProfile,
            IReadOnlyList<TableValueStation> tableStations,
            IReadOnlyList<TableValueStation> unfoldedStations,
            IReadOnlyList<PipeSlopeSegment> slopeSegments,
            IReadOnlyList<PlanSpan> casingPlanSpans,
            PipeProfileMetadata metadata,
            ObjectId textStyleId)
        {
            const double labelWidth = 36.0;
            const double textHeight = 1.45;
            const double valueTextHeight = 1.10;
            var horizontalScale = metadata.HorizontalScale;
            var sessionId = metadata.SessionId;
            var rows = GetGostTableRows();

            var tableTop = insertPoint.Y;
            var tableLeft = insertPoint.X - labelWidth;
            var tableRight = insertPoint.X + route.Length * horizontalScale;
            var tableBottom = GetTableBottom(tableTop, rows);

            for (var row = 0; row <= rows.Count; row++)
            {
                var y = row == rows.Count ? tableBottom : GetRowTop(tableTop, rows, row);
                AddLine(modelSpace, transaction, tableLeft, y, tableRight, y, GridLayer, sessionId, ProfileEntityXData.RoleTable);
            }

            AddLine(modelSpace, transaction, tableLeft, tableTop, tableLeft, tableBottom, GridLayer, sessionId, ProfileEntityXData.RoleTable);
            AddLine(modelSpace, transaction, insertPoint.X, tableTop, insertPoint.X, tableBottom, GridLayer, sessionId, ProfileEntityXData.RoleTable);
            AddLine(modelSpace, transaction, tableRight, tableTop, tableRight, tableBottom, GridLayer, sessionId, ProfileEntityXData.RoleTable);

            for (var row = 0; row < rows.Count; row++)
            {
                AddText(modelSpace, transaction, rows[row].Label, tableLeft + 1.0, GetRowCenter(tableTop, rows, row) - textHeight * 0.45, textHeight, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleTable);
            }

            var labelLanes = BuildTableLabelLanes(tableStations, horizontalScale);
            foreach (var station in tableStations)
            {
                var chainage = Clamp(station.Chainage, 0.0, route.Length);
                var x = insertPoint.X + chainage * horizontalScale;
                var lane = GetTableLabelLane(labelLanes, chainage);
                AddTableStationLine(modelSpace, transaction, x, tableTop, tableBottom, rows, sessionId);

                var routePoint = route.GetPointAtChainage(chainage);
                var hasGround = tin.TryGetElevation(routePoint, out var ground);
                var pipeElevation = InterpolatePipeElevation(pipeProfile, chainage);
                var trenchBottom = pipeElevation - metadata.PipeOuterDiameter - metadata.SandBeddingThickness;
                var trenchDepth = ground - trenchBottom;

                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(ground) : "-", x, tableTop, rows, 0, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(ground) : "-", x, tableTop, rows, 1, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(trenchBottom) : "-", x, tableTop, rows, 2, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, FormatNumber(pipeElevation), x, tableTop, rows, 3, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(trenchDepth) : "-", x, tableTop, rows, 4, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, FormatTablePicket(chainage), x, tableTop, rows, 8, lane, valueTextHeight, textStyleId, sessionId);
            }

            AddCenteredSpanText(modelSpace, transaction, metadata.PipeLabel, insertPoint.X, tableRight, GetRowCenter(tableTop, rows, 5), textHeight, textStyleId, sessionId);
            AddCenteredSpanText(modelSpace, transaction, metadata.FoundationLabel, insertPoint.X, tableRight, GetRowCenter(tableTop, rows, 6), textHeight, textStyleId, sessionId);
            DrawDistanceRow(modelSpace, transaction, insertPoint, route, tableStations, horizontalScale, tableTop, rows, textHeight, textStyleId, sessionId);
            DrawPipeSegmentRows(modelSpace, transaction, insertPoint, slopeSegments, horizontalScale, tableTop, rows, textHeight, textStyleId, sessionId);
            DrawUnfoldedPlan(
                modelSpace,
                transaction,
                insertPoint,
                route,
                unfoldedStations,
                casingPlanSpans,
                GetRowCenter(tableTop, rows, 10),
                horizontalScale,
                textStyleId,
                sessionId);
        }

        private static void DrawUnfoldedPlan(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            IReadOnlyList<TableValueStation> stations,
            IReadOnlyList<PlanSpan> casingPlanSpans,
            double planY,
            double horizontalScale,
            ObjectId textStyleId,
            string sessionId)
        {
            const double labelWidth = 36.0;
            const double textHeight = 1.45;
            const double minLabelGap = 14.0;
            var left = insertPoint.X - labelWidth;
            var right = insertPoint.X + route.Length * horizontalScale;

            AddLine(modelSpace, transaction, insertPoint.X, planY, right, planY, GridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, left, planY - 4.0, right, planY - 4.0, GridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, left, planY + 4.0, right, planY + 4.0, GridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, left, planY + 4.0, left, planY - 4.0, GridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, right, planY + 4.0, right, planY - 4.0, GridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);

            for (var i = 0; i < route.SegmentCount; i++)
            {
                var start = route.GetVertexChainage(i);
                var end = route.GetVertexChainage(i + 1);
                var midX = insertPoint.X + (start + end) * 0.5 * horizontalScale;
                AddText(modelSpace, transaction, FormatNumber(end - start), midX - 1.5, planY + 5.8, textHeight, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            }

            foreach (var span in casingPlanSpans)
            {
                var startChainage = Clamp(span.StartChainage, 0.0, route.Length);
                var endChainage = Clamp(span.EndChainage, 0.0, route.Length);
                if (Math.Abs(endChainage - startChainage) < 0.001)
                {
                    continue;
                }

                SymbolRenderer.DrawUnfoldedPlanCasing(
                    modelSpace,
                    transaction,
                    insertPoint.X + startChainage * horizontalScale,
                    insertPoint.X + endChainage * horizontalScale,
                    planY,
                    span.Label,
                    FormatPicket(startChainage),
                    FormatPicket(endChainage),
                    textHeight,
                    textStyleId,
                    sessionId,
                    GridLayer,
                    TextLayer,
                    span.HasControlTube
                        ? insertPoint.X + Clamp(span.ControlTubeChainage, startChainage, endChainage) * horizontalScale
                        : (double?)null,
                    span.Note);
            }

            var orderedMarks = stations;
            var lastLabelX = double.NegativeInfinity;
            var lane = 0;
            foreach (var mark in orderedMarks)
            {
                if (mark.Kind.StartsWith("Фут", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var chainage = Clamp(mark.Chainage, 0.0, route.Length);
                var x = insertPoint.X + chainage * horizontalScale;
                if (x - lastLabelX < minLabelGap)
                {
                    lane = (lane + 1) % 3;
                }
                else
                {
                    lane = 0;
                }

                lastLabelX = x;
                var laneOffset = lane * 3.0;
                var symbol = SituationSymbol.Point(
                    GetSymbolKind(mark.Kind),
                    chainage,
                    mark.Kind,
                    mark.Kind.StartsWith("ПК", StringComparison.OrdinalIgnoreCase) ? string.Empty : FormatPicket(chainage),
                    !IsRoutePlanMark(mark.Kind));
                SymbolRenderer.DrawUnfoldedPlanPointMark(
                    modelSpace,
                    transaction,
                    x,
                    planY,
                    symbol,
                    laneOffset + 4.2,
                    textHeight,
                    textStyleId,
                    sessionId,
                    GridLayer,
                    AddSituationCommand.LayerName,
                    TextLayer);
            }
        }

        private static SituationSymbolKind GetSymbolKind(string label)
        {
            if (string.Equals(label, "Н", StringComparison.OrdinalIgnoreCase))
            {
                return SituationSymbolKind.RouteStart;
            }

            if (string.Equals(label, "К", StringComparison.OrdinalIgnoreCase))
            {
                return SituationSymbolKind.RouteEnd;
            }

            if (label.StartsWith("УП", StringComparison.OrdinalIgnoreCase))
            {
                return SituationSymbolKind.Bend;
            }

            if (label.StartsWith("ПК", StringComparison.OrdinalIgnoreCase))
            {
                return SituationSymbolKind.Picket;
            }

            return SituationSymbolKind.Crossing;
        }

        private static bool IsRoutePlanMark(string label)
        {
            return string.Equals(label, "Н", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "К", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("УП", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("ПК", StringComparison.OrdinalIgnoreCase);
        }

        private static void DrawProfileSituation(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            IReadOnlyList<ProfilePoint> groundProfile,
            IReadOnlyList<PipePoint> pipeProfile,
            PipeProfileMetadata metadata,
            ObjectId textStyleId,
            IReadOnlyList<SituationSymbol> situationItems)
        {
            if (situationItems.Count == 0)
            {
                return;
            }

            const double textHeight = 1.45;
            const double minLabelGap = 12.0;
            var profileHeight = (Math.Ceiling(groundProfile.Max(p => p.GroundElevation!.Value)) + 1.0 - metadata.BaseElevation) * metadata.VerticalScale;
            var laneEnds = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };

            foreach (var item in situationItems)
            {
                if (item.Chainage < -0.001 || item.Chainage > route.Length + 0.001)
                {
                    continue;
                }

                var chainage = Clamp(item.Chainage, 0.0, route.Length);
                var x = insertPoint.X + chainage * metadata.HorizontalScale;
                var lane = GetProfileSituationLane(laneEnds, x, minLabelGap);
                var labelTop = insertPoint.Y + profileHeight + 4.0 + lane * 5.0;

                SymbolRenderer.DrawProfileSituationSymbol(
                    modelSpace,
                    transaction,
                    insertPoint,
                    route,
                    c => InterpolatePipeElevation(pipeProfile, c),
                    metadata.BaseElevation,
                    metadata.HorizontalScale,
                    metadata.VerticalScale,
                    profileHeight,
                    item,
                    labelTop,
                    textHeight,
                    textStyleId,
                    metadata.SessionId,
                    AddSituationCommand.LayerName,
                    TextLayer);
            }
        }

        private static int GetProfileSituationLane(double[] laneEnds, double x, double minLabelGap)
        {
            var lane = 0;
            for (var i = 0; i < laneEnds.Length; i++)
            {
                if (x - laneEnds[i] >= minLabelGap)
                {
                    lane = i;
                    break;
                }
            }

            laneEnds[lane] = x;
            return lane;
        }

        private static double InterpolatePipeElevation(IReadOnlyList<PipePoint> pipeProfile, double chainage)
        {
            if (chainage <= pipeProfile[0].Chainage)
            {
                return pipeProfile[0].Elevation;
            }

            for (var i = 1; i < pipeProfile.Count; i++)
            {
                if (chainage > pipeProfile[i].Chainage)
                {
                    continue;
                }

                var prev = pipeProfile[i - 1];
                var next = pipeProfile[i];
                var length = next.Chainage - prev.Chainage;
                if (Math.Abs(length) < 0.000001)
                {
                    return next.Elevation;
                }

                var t = (chainage - prev.Chainage) / length;
                return prev.Elevation + (next.Elevation - prev.Elevation) * t;
            }

            return pipeProfile[pipeProfile.Count - 1].Elevation;
        }

        private static void DrawPipeSegmentRows(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            IReadOnlyList<PipeSlopeSegment> slopeSegments,
            double horizontalScale,
            double tableTop,
            IReadOnlyList<TableRowDef> rows,
            double textHeight,
            ObjectId textStyleId,
            string sessionId)
        {
            var slopeTop = GetRowTop(tableTop, rows, 7);
            var slopeBottom = GetRowBottom(tableTop, rows, 7);
            const double pad = 0.7;
            var boundaryChainages = slopeSegments
                .SelectMany(s => new[] { s.StartChainage, s.EndChainage })
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            foreach (var chainage in boundaryChainages)
            {
                var x = insertPoint.X + chainage * horizontalScale;
                AddLine(modelSpace, transaction, x, slopeTop, x, slopeBottom, GridLayer, sessionId, ProfileEntityXData.RoleTable);
            }

            foreach (var segment in slopeSegments)
            {
                var length = segment.EndChainage - segment.StartChainage;
                if (Math.Abs(length) < 0.000001)
                {
                    continue;
                }

                var slopePermille = (segment.EndElevation - segment.StartElevation) / length * 1000.0;
                var x1 = insertPoint.X + segment.StartChainage * horizontalScale;
                var x2 = insertPoint.X + segment.EndChainage * horizontalScale;
                DrawSlopeCell(modelSpace, transaction, x1, x2, slopeTop, slopeBottom, pad, slopePermille, length, textHeight, textStyleId, sessionId);
            }
        }

        private static void AddTableStationLine(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x,
            double tableTop,
            double tableBottom,
            IReadOnlyList<TableRowDef> rows,
            string sessionId)
        {
            for (var row = 0; row <= 4; row++)
            {
                AddValueRowStationLine(modelSpace, transaction, x, tableTop, rows, row, sessionId);
            }

            AddValueRowStationLine(modelSpace, transaction, x, tableTop, rows, 8, sessionId);
            AddLine(modelSpace, transaction, x, GetRowTop(tableTop, rows, 9), x, tableBottom, GridLayer, sessionId, ProfileEntityXData.RoleTable);
        }

        private static void AddValueRowStationLine(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x,
            double tableTop,
            IReadOnlyList<TableRowDef> rows,
            int row,
            string sessionId)
        {
            var rowTop = GetRowTop(tableTop, rows, row);
            var rowBottom = GetRowBottom(tableTop, rows, row);
            const double stub = 0.45;
            AddLine(modelSpace, transaction, x, rowTop, x, rowTop - stub, GridLayer, sessionId, ProfileEntityXData.RoleTable);
            AddLine(modelSpace, transaction, x, rowBottom + stub, x, rowBottom, GridLayer, sessionId, ProfileEntityXData.RoleTable);
        }

        private static string GetGroundKind(ProfilePointType type)
        {
            switch (type)
            {
                case ProfilePointType.Start:
                    return "Н";
                case ProfilePointType.End:
                    return "К";
                case ProfilePointType.RouteVertex:
                    return "УП";
                default:
                    return "З";
            }
        }

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private static ObjectId EnsureTextStyle(Database database, Transaction transaction)
        {
            var textStyleTable = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            if (textStyleTable.Has(TextStyleName))
            {
                return textStyleTable[TextStyleName];
            }

            textStyleTable.UpgradeOpen();
            var textStyle = new TextStyleTableRecord
            {
                Name = TextStyleName,
                FileName = PreferredTextFont
            };

            var objectId = textStyleTable.Add(textStyle);
            transaction.AddNewlyCreatedDBObject(textStyle, true);
            return objectId;
        }

        private static void EnsureLayers(Database database, Transaction transaction)
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            EnsureLayer(layerTable, transaction, GridLayer, 8, GridLineWeight);
            EnsureLayer(layerTable, transaction, TextLayer, 7, LineWeight.LineWeight013);
            EnsureLayer(layerTable, transaction, AddSituationCommand.LayerName, 2, ProfileSituationLineWeight);
        }

        private static void EnsureLayer(LayerTable layerTable, Transaction transaction, string name, short colorIndex, LineWeight lineWeight)
        {
            if (layerTable.Has(name))
            {
                var existing = (LayerTableRecord)transaction.GetObject(layerTable[name], OpenMode.ForWrite);
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
                existing.LineWeight = lineWeight;
                return;
            }

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                LineWeight = lineWeight
            };

            layerTable.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }

        private static IReadOnlyList<TableRowDef> GetGostTableRows()
        {
            return new[]
            {
                new TableRowDef("Отметка земли проектная, м", 7.0),
                new TableRowDef("Отметка земли фактическая, м", 7.0),
                new TableRowDef("Отметка дна траншеи, м", 7.0),
                new TableRowDef("Отметка верха трубы, м", 7.0),
                new TableRowDef("Глубина траншеи, м", 7.0),
                new TableRowDef("Обозначение трубы и тип изоляции", 6.0),
                new TableRowDef("Основание", 5.0),
                new TableRowDef("Уклон, промилле; длина, м", 8.0),
                new TableRowDef("Пикет", 7.0),
                new TableRowDef("Расстояние, м", 5.0),
                new TableRowDef("Развернутый план", 10.0)
            };
        }

        private static double GetRowTop(double tableTop, IReadOnlyList<TableRowDef> rows, int row)
        {
            var y = tableTop;
            for (var i = 0; i < row; i++)
            {
                y -= rows[i].Height;
            }

            return y;
        }

        private static double GetRowBottom(double tableTop, IReadOnlyList<TableRowDef> rows, int row)
        {
            return GetRowTop(tableTop, rows, row) - rows[row].Height;
        }

        private static double GetRowCenter(double tableTop, IReadOnlyList<TableRowDef> rows, int row)
        {
            return (GetRowTop(tableTop, rows, row) + GetRowBottom(tableTop, rows, row)) * 0.5;
        }

        private static double GetTableBottom(double tableTop, IReadOnlyList<TableRowDef> rows)
        {
            return tableTop - rows.Sum(r => r.Height);
        }

        private static void DrawSlopeCell(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x1,
            double x2,
            double rowTop,
            double rowBottom,
            double pad,
            double slopePermille,
            double length,
            double textHeight,
            ObjectId textStyleId,
            string sessionId)
        {
            if (x2 - x1 < 1.0)
            {
                return;
            }

            var leftY = slopePermille >= 0.0 ? rowBottom + pad : rowTop - pad;
            var rightY = slopePermille >= 0.0 ? rowTop - pad : rowBottom + pad;
            AddLine(modelSpace, transaction, x1 + pad, leftY, x2 - pad, rightY, GridLayer, sessionId, ProfileEntityXData.RoleTable);
            var midX = (x1 + x2) * 0.5;
            AddText(modelSpace, transaction, slopePermille.ToString("0.0", CultureInfo.InvariantCulture), midX - 2.4, rowTop - 2.8, textHeight, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleTable);
            AddText(modelSpace, transaction, FormatNumber(length), midX - 2.4, rowBottom + 0.8, textHeight, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleTable);
        }

        private static void DrawDistanceRow(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            IReadOnlyList<TableValueStation> stations,
            double horizontalScale,
            double tableTop,
            IReadOnlyList<TableRowDef> rows,
            double textHeight,
            ObjectId textStyleId,
            string sessionId)
        {
            const double minReadableWidth = 2.8;
            const double shortWidth = 7.0;
            var ordered = stations
                .Select(s => Clamp(s.Chainage, 0.0, route.Length))
                .Distinct()
                .OrderBy(c => c)
                .ToList();
            var rowTop = GetRowTop(tableTop, rows, 9);
            var rowBottom = GetRowBottom(tableTop, rows, 9);
            var rowCenter = GetRowCenter(tableTop, rows, 9);

            for (var i = 1; i < ordered.Count; i++)
            {
                var start = ordered[i - 1];
                var end = ordered[i];
                if (end - start < 0.001)
                {
                    continue;
                }

                var width = (end - start) * horizontalScale;
                if (width < minReadableWidth)
                {
                    continue;
                }

                var value = FormatNumber(end - start);
                var height = width < shortWidth ? textHeight * 0.72 : textHeight;
                var lane = width < shortWidth ? i % 2 : 0;
                var y = lane == 0
                    ? rowCenter - height * 0.55
                    : Math.Max(rowBottom + 0.35, rowTop - height - 0.25);
                var x = insertPoint.X + (start + end) * 0.5 * horizontalScale - EstimateTextWidth(value, height) * 0.5;
                AddText(modelSpace, transaction, value, x, y, height, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleTable);
            }
        }

        private static Dictionary<int, int> BuildTableLabelLanes(
            IReadOnlyList<TableValueStation> stations,
            double horizontalScale)
        {
            const double minGap = 3.0;
            var result = new Dictionary<int, int>();
            var ordered = stations
                .Select(s => s.Chainage)
                .OrderBy(c => c)
                .ToList();
            var laneEndX = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };

            foreach (var chainage in ordered)
            {
                var x = chainage * horizontalScale;
                var lane = 0;
                for (var i = 0; i < laneEndX.Length; i++)
                {
                    if (x - laneEndX[i] >= minGap)
                    {
                        lane = i;
                        break;
                    }
                }

                laneEndX[lane] = x;
                result[GetStationKey(chainage)] = lane;
            }

            return result;
        }

        private static int GetTableLabelLane(IReadOnlyDictionary<int, int> lanes, double chainage)
        {
            return lanes.TryGetValue(GetStationKey(chainage), out var lane) ? lane : 0;
        }

        private static int GetStationKey(double chainage)
        {
            return (int)Math.Round(chainage * 1000.0);
        }

        private static void AddTableValue(
            BlockTableRecord modelSpace,
            Transaction transaction,
            string value,
            double x,
            double tableTop,
            IReadOnlyList<TableRowDef> rows,
            int row,
            double textHeight,
            ObjectId textStyleId,
            string? sessionId = null)
        {
            AddText(modelSpace, transaction, value, x + 0.8, GetRowCenter(tableTop, rows, row) - textHeight * 0.45, textHeight, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleTable);
        }

        private static void AddVerticalTableValue(
            BlockTableRecord modelSpace,
            Transaction transaction,
            string value,
            double x,
            double tableTop,
            IReadOnlyList<TableRowDef> rows,
            int row,
            int lane,
            double textHeight,
            ObjectId textStyleId,
            string? sessionId = null)
        {
            var laneOffset = Math.Min(Math.Max(lane, 0), 3) * 0.25;
            AddText(
                modelSpace,
                transaction,
                value,
                x + 0.45 + laneOffset,
                GetRowBottom(tableTop, rows, row) + 0.55,
                textHeight,
                Math.PI / 2.0,
                TextLayer,
                textStyleId,
                sessionId,
                ProfileEntityXData.RoleTable);
        }

        private static double EstimateTextWidth(string value, double height)
        {
            return value.Length * height * 0.55;
        }

        private static void AddCenteredSpanText(
            BlockTableRecord modelSpace,
            Transaction transaction,
            string text,
            double left,
            double right,
            double centerY,
            double textHeight,
            ObjectId textStyleId,
            string sessionId)
        {
            var x = (left + right) * 0.5 - EstimateTextWidth(text, textHeight) * 0.5;
            AddText(modelSpace, transaction, text, x, centerY - textHeight * 0.45, textHeight, 0.0, TextLayer, textStyleId, sessionId, ProfileEntityXData.RoleTable);
        }

        private static void AddLine(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x1,
            double y1,
            double x2,
            double y2,
            string layer,
            string? sessionId = null,
            string? role = null)
        {
            var line = new Line(new Point3d(x1, y1, 0.0), new Point3d(x2, y2, 0.0));
            line.Layer = layer;
            line.Linetype = "Continuous";
            line.LineWeight = GetLineWeight(role);
            modelSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            ProfileEntityXData.Attach(line, sessionId ?? string.Empty, role ?? string.Empty);
        }

        private static void AddCircle(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x,
            double y,
            double radius,
            string layer,
            string? sessionId = null,
            string? role = null)
        {
            var circle = new Circle(new Point3d(x, y, 0.0), Vector3d.ZAxis, radius);
            circle.Layer = layer;
            circle.Linetype = "Continuous";
            circle.LineWeight = GetLineWeight(role);
            modelSpace.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
            ProfileEntityXData.Attach(circle, sessionId ?? string.Empty, role ?? string.Empty);
        }

        private static void AddPolyline(
            BlockTableRecord modelSpace,
            Transaction transaction,
            IReadOnlyList<Point2d> points,
            string layer,
            string? sessionId = null,
            string? role = null)
        {
            if (points.Count < 2)
            {
                return;
            }

            var polyline = new Polyline();
            polyline.Layer = layer;
            polyline.Linetype = "Continuous";
            polyline.LineWeight = GetLineWeight(role);
            for (var i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
            }

            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            ProfileEntityXData.Attach(polyline, sessionId ?? string.Empty, role ?? string.Empty);
        }

        private static LineWeight GetLineWeight(string? role)
        {
            if (string.Equals(role, ProfileEntityXData.RoleTable, StringComparison.OrdinalIgnoreCase))
            {
                return TableLineWeight;
            }

            if (string.Equals(role, ProfileEntityXData.RoleUnfoldedPlan, StringComparison.OrdinalIgnoreCase))
            {
                return UnfoldedPlanLineWeight;
            }

            if (string.Equals(role, ProfileEntityXData.RoleProfileSituation, StringComparison.OrdinalIgnoreCase))
            {
                return ProfileSituationLineWeight;
            }

            return GridLineWeight;
        }

        private static void AddText(
            BlockTableRecord modelSpace,
            Transaction transaction,
            string text,
            double x,
            double y,
            double height,
            double rotation,
            string layer,
            ObjectId textStyleId,
            string? sessionId = null,
            string? role = null)
        {
            var dbText = new DBText
            {
                TextString = text,
                Position = new Point3d(x, y, 0.0),
                Height = height,
                Layer = layer,
                TextStyleId = textStyleId
            };

            if (Math.Abs(rotation) > 0.000001)
            {
                dbText.TransformBy(Matrix3d.Rotation(rotation, Vector3d.ZAxis, dbText.Position));
            }

            modelSpace.AppendEntity(dbText);
            transaction.AddNewlyCreatedDBObject(dbText, true);
            ProfileEntityXData.Attach(dbText, sessionId ?? string.Empty, role ?? string.Empty);
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        private static string FormatPicket(double chainage)
        {
            return ChainageFormatter.Format(chainage).Replace('.', ',');
        }

        private static string FormatTablePicket(double chainage)
        {
            var picket = (int)Math.Floor((chainage + 0.000001) / 100.0);
            var offset = chainage - picket * 100.0;
            if (Math.Abs(offset) < 0.005)
            {
                return $"ПК{picket}";
            }

            return $"+{FormatNumber(offset)}";
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

        private sealed class TableValueStation
        {
            public TableValueStation(double chainage, string kind, int priority)
            {
                Chainage = chainage;
                Kind = kind;
                Priority = priority;
            }

            public double Chainage { get; }

            public string Kind { get; }

            public int Priority { get; }
        }

        private sealed class PipeSlopeSegment
        {
            public PipeSlopeSegment(double startChainage, double endChainage, double startElevation, double endElevation)
            {
                StartChainage = startChainage;
                EndChainage = endChainage;
                StartElevation = startElevation;
                EndElevation = endElevation;
            }

            public double StartChainage { get; }

            public double EndChainage { get; }

            public double StartElevation { get; }

            public double EndElevation { get; }
        }

        private sealed class PlanSpan
        {
            public PlanSpan(double startChainage, double endChainage, string label)
                : this(startChainage, endChainage, label, startChainage + Math.Abs(endChainage - startChainage) * 0.78, true, string.Empty)
            {
            }

            public PlanSpan(double startChainage, double endChainage, string label, double controlTubeChainage, bool hasControlTube, string note)
            {
                StartChainage = startChainage;
                EndChainage = endChainage;
                Label = label;
                ControlTubeChainage = controlTubeChainage;
                HasControlTube = hasControlTube;
                Note = note ?? string.Empty;
            }

            public double StartChainage { get; }

            public double EndChainage { get; }

            public string Label { get; }

            public double ControlTubeChainage { get; }

            public bool HasControlTube { get; }

            public string Note { get; }
        }

        private sealed class TableRowDef
        {
            public TableRowDef(string label, double height)
            {
                Label = label;
                Height = height;
            }

            public string Label { get; }

            public double Height { get; }
        }
    }
}
