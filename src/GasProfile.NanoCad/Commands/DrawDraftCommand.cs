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
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class DrawDraftCommand
    {
        private const string GroundLayer = "GASPROF_PROFILE_GROUND";
        private const string PipeLayer = "GASPROF_PROFILE_PIPE";
        private const string GridLayer = "GASPROF_PROFILE_GRID";
        private const string TextLayer = "GASPROF_PROFILE_TEXT";
        private const string TextStyleName = "GASPROF_TEXT";
        private const string PreferredTextFont = "CS_Gost2304.shx";
        private static readonly LineWeight GroundLineWeight = LineWeight.LineWeight013;
        private static readonly LineWeight PipeLineWeight = LineWeight.LineWeight050;
        private static readonly LineWeight GridLineWeight = LineWeight.LineWeight009;
        private static readonly LineWeight TableLineWeight = LineWeight.LineWeight013;
        private static readonly LineWeight UnfoldedPlanLineWeight = LineWeight.LineWeight025;
        private static readonly LineWeight SituationLineWeight = LineWeight.LineWeight025;

        [CommandMethod("GASPROF_DRAW_DRAFT")]
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

            var insertResult = editor.GetPoint("\nУкажите левый нижний угол черновика профиля: ");
            if (insertResult.Status != PromptStatus.OK)
            {
                return;
            }

            var depth = GetDouble(editor, "\nГлубина заложения газопровода от земли, м", 1.20);
            var pipeOuterDiameter = GetDouble(editor, "\nНаружный диаметр газопровода, м", PipeProfileXData.DefaultPipeOuterDiameter);
            var sandBeddingThickness = GetDouble(editor, "\nТолщина песчаной подушки, м", PipeProfileXData.DefaultSandBeddingThickness);
            var horizontalScale = GetDouble(editor, "\nГоризонтальный коэффициент чертежа, ед./м (1:500)", 2.00);
            var verticalScale = GetDouble(editor, "\nВертикальный коэффициент чертежа, ед./м (1:100)", 10.00);
            var pipeLabel = GetString(editor, "\nОбозначение трубы и тип изоляции", PipeProfileXData.DefaultPipeLabel);
            var foundationLabel = GetString(editor, "\nОснование", PipeProfileXData.DefaultFoundationLabel);

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var routePolyline = transaction.GetObject(routeResult.ObjectId, OpenMode.ForRead) as Polyline;
                var samplePoint = transaction.GetObject(pointResult.ObjectId, OpenMode.ForRead) as DBPoint;

                if (routePolyline == null || samplePoint == null)
                {
                    editor.WriteMessage("\nДля построения нужны 2D Polyline и DBPoint.");
                    return;
                }

                var route = ReadRoute(routePolyline);
                var surfacePoints = CollectDbPointsOnLayer(database, transaction, samplePoint.Layer);
                var tin = DelaunayTriangulator.Build(surfacePoints);
                var profilePoints = ProfileBuilder.BuildGroundProfile(route, tin)
                    .Where(p => p.GroundElevation.HasValue)
                    .ToList();

                if (profilePoints.Count < 2)
                {
                    editor.WriteMessage("\nНедостаточно расчетных точек профиля внутри TIN.");
                    return;
                }

                EnsureLayers(database, transaction);
                var textStyleId = EnsureTextStyle(database, transaction);
                PipeProfileXData.EnsureRegApp(database, transaction);
                ProfileEntityXData.EnsureRegApp(database, transaction);
                var situationItems = CollectSituationItems(database, transaction, route, editor);

                var minGround = profilePoints.Min(p => p.GroundElevation!.Value);
                var baseElevation = Math.Floor(minGround) - 1.0;
                var modelSpace = GetModelSpace(database, transaction, OpenMode.ForWrite);
                var sessionId = Guid.NewGuid().ToString("N");

                DrawGridAndLabels(
                    modelSpace,
                    transaction,
                    insertResult.Value,
                    route,
                    profilePoints,
                    tin,
                    depth,
                    pipeOuterDiameter,
                    sandBeddingThickness,
                    baseElevation,
                    horizontalScale,
                    verticalScale,
                    textStyleId,
                    situationItems,
                    pipeLabel,
                    foundationLabel,
                    sessionId);

                DrawProfilePolyline(
                    modelSpace,
                    transaction,
                    insertResult.Value,
                    profilePoints,
                    baseElevation,
                    horizontalScale,
                    verticalScale,
                    0.0,
                    GroundLayer,
                    sessionId,
                    ProfileEntityXData.RoleGround);

                var pipeStations = BuildPipeDesignStations(route, situationItems);
                var pipePolyline = DrawPipeDesignPolyline(
                    modelSpace,
                    transaction,
                    insertResult.Value,
                    route,
                    tin,
                    pipeStations,
                    baseElevation,
                    horizontalScale,
                    verticalScale,
                    depth,
                    PipeLayer,
                    sessionId,
                    ProfileEntityXData.RolePipe);
                PipeProfileXData.Attach(
                    pipePolyline,
                    insertResult.Value.X,
                    insertResult.Value.Y,
                    baseElevation,
                    horizontalScale,
                    verticalScale,
                    route.Length,
                    sessionId,
                    routePolyline.Handle.ToString(),
                    samplePoint.Layer,
                    insertResult.Value.Y - 12.0,
                    ProfileSignatures.ForRoute(routePolyline, route.Length),
                    ProfileSignatures.ForSurface(samplePoint.Layer, surfacePoints),
                    ProfileSignatures.ForPipe(pipePolyline),
                    pipeOuterDiameter,
                    sandBeddingThickness,
                    pipeLabel,
                    foundationLabel);

                transaction.Commit();

                editor.WriteMessage("\n--- GASPROF_DRAW_DRAFT ---");
                editor.WriteMessage($"\nПостроено точек профиля: {profilePoints.Count}");
                editor.WriteMessage($"\nБазовая отметка профиля: {baseElevation.ToString("0.00", CultureInfo.InvariantCulture)}");
                editor.WriteMessage($"\nГлубина газопровода: {depth.ToString("0.00", CultureInfo.InvariantCulture)} м");
                editor.WriteMessage($"\nНаружный диаметр газопровода: {pipeOuterDiameter.ToString("0.000", CultureInfo.InvariantCulture)} м");
                editor.WriteMessage($"\nПесчаная подушка: {sandBeddingThickness.ToString("0.000", CultureInfo.InvariantCulture)} м");
                editor.WriteMessage("\n--------------------------");
            }
        }

        private static double GetDouble(Editor editor, string message, double defaultValue)
        {
            var options = new PromptDoubleOptions($"{message} <{defaultValue.ToString("0.00", CultureInfo.InvariantCulture)}>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue
            };

            var result = editor.GetDouble(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        private static string GetString(Editor editor, string message, string defaultValue)
        {
            var options = new PromptStringOptions($"{message} <{defaultValue}>: ")
            {
                AllowSpaces = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };

            var result = editor.GetString(options);
            return result.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(result.StringResult)
                ? result.StringResult
                : defaultValue;
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

        private static Polyline DrawProfilePolyline(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            IReadOnlyList<ProfilePoint> profilePoints,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double elevationOffset,
            string layer,
            string? sessionId = null,
            string? role = null)
        {
            var polyline = new Polyline();
            polyline.Layer = layer;
            polyline.Linetype = "Continuous";
            polyline.LineWeight = GetLineWeight(layer, role);

            for (var i = 0; i < profilePoints.Count; i++)
            {
                var point = profilePoints[i];
                var x = insertPoint.X + point.Chainage * horizontalScale;
                var y = insertPoint.Y + (point.GroundElevation!.Value + elevationOffset - baseElevation) * verticalScale;
                polyline.AddVertexAt(i, new Point2d(x, y), 0.0, 0.0, 0.0);
            }

            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            ProfileEntityXData.Attach(polyline, sessionId ?? string.Empty, role ?? string.Empty);
            return polyline;
        }

        private static Polyline DrawPipeDesignPolyline(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            TinSurface tin,
            IReadOnlyList<double> chainages,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double depth,
            string layer,
            string? sessionId = null,
            string? role = null)
        {
            var polyline = new Polyline();
            polyline.Layer = layer;
            polyline.Linetype = "Continuous";
            polyline.LineWeight = GetLineWeight(layer, role);

            foreach (var chainage in chainages)
            {
                var planPoint = route.GetPointAtChainage(chainage);
                if (!tin.TryGetElevation(planPoint, out var groundElevation))
                {
                    continue;
                }

                var x = insertPoint.X + chainage * horizontalScale;
                var y = insertPoint.Y + (groundElevation - depth - baseElevation) * verticalScale;
                polyline.AddVertexAt(polyline.NumberOfVertices, new Point2d(x, y), 0.0, 0.0, 0.0);
            }

            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            ProfileEntityXData.Attach(polyline, sessionId ?? string.Empty, role ?? string.Empty);
            return polyline;
        }

        private static void DrawGridAndLabels(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            IReadOnlyList<ProfilePoint> profilePoints,
            TinSurface tin,
            double depth,
            double pipeOuterDiameter,
            double sandBeddingThickness,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            ObjectId textStyleId,
            IReadOnlyList<SituationItem> situationItems,
            string pipeLabel,
            string foundationLabel,
            string sessionId)
        {
            var maxElevation = profilePoints.Max(p => p.GroundElevation!.Value);
            var profileHeight = (Math.Ceiling(maxElevation) + 1.0 - baseElevation) * verticalScale;
            var profileWidth = route.Length * horizontalScale;

            AddLine(modelSpace, transaction, insertPoint.X, insertPoint.Y, insertPoint.X + profileWidth, insertPoint.Y, GridLayer, sessionId, ProfileEntityXData.RoleGrid);
            AddLine(modelSpace, transaction, insertPoint.X, insertPoint.Y, insertPoint.X, insertPoint.Y + profileHeight, GridLayer, sessionId, ProfileEntityXData.RoleGrid);

            foreach (var point in profilePoints.Where(p => p.Type != ProfilePointType.TinEdge))
            {
                var x = insertPoint.X + point.Chainage * horizontalScale;
                AddLine(modelSpace, transaction, x, insertPoint.Y, x, insertPoint.Y + profileHeight, GridLayer, sessionId, ProfileEntityXData.RoleGrid);
            }

            foreach (var item in situationItems)
            {
                if (!item.IsCasing || item.Chainage < -0.001 || item.Chainage > route.Length + 0.001)
                {
                    continue;
                }

                var startChainage = item.IsCasing ? Math.Max(0.0, item.StartChainage) : item.Chainage;
                var endChainage = item.IsCasing ? Math.Min(route.Length, item.EndChainage) : item.Chainage;

                var x = insertPoint.X + startChainage * horizontalScale;
                AddLine(modelSpace, transaction, x, insertPoint.Y, x, insertPoint.Y + profileHeight, AddSituationCommand.LayerName, sessionId, ProfileEntityXData.RoleProfileSituation);
                if (item.IsCasing && Math.Abs(endChainage - startChainage) > 0.001)
                {
                    var endX = insertPoint.X + endChainage * horizontalScale;
                    AddLine(modelSpace, transaction, endX, insertPoint.Y, endX, insertPoint.Y + profileHeight, AddSituationCommand.LayerName, sessionId, ProfileEntityXData.RoleProfileSituation);
                }

            }

            var situationSymbols = BuildSituationSymbols(situationItems);
            DrawProfileSituation(
                modelSpace,
                transaction,
                insertPoint,
                route,
                tin,
                depth,
                baseElevation,
                horizontalScale,
                verticalScale,
                profileHeight,
                textStyleId,
                situationSymbols,
                sessionId);

            AddText(
                modelSpace,
                transaction,
                $"Черновой продольный профиль. База Z={FormatNumber(baseElevation)}",
                insertPoint.X,
                insertPoint.Y + profileHeight + 24.0,
                2.2,
                0.0,
                TextLayer,
                textStyleId,
                sessionId,
                ProfileEntityXData.RoleProfileLabel);

            DrawProfileTable(
                modelSpace,
                transaction,
                insertPoint,
                route,
                profilePoints,
                tin,
                depth,
                pipeOuterDiameter,
                sandBeddingThickness,
                horizontalScale,
                textStyleId,
                situationItems,
                pipeLabel,
                foundationLabel,
                sessionId);
        }

        private static void DrawProfileSituation(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            TinSurface tin,
            double depth,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double profileHeight,
            ObjectId textStyleId,
            IReadOnlyList<SituationSymbol> situationItems,
            string sessionId)
        {
            const double textHeight = 1.45;
            const double minLabelGap = 12.0;
            var laneEnds = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };

            foreach (var item in situationItems.OrderBy(i => i.Chainage))
            {
                if (item.Chainage < -0.001 || item.Chainage > route.Length + 0.001)
                {
                    continue;
                }

                var chainage = Clamp(item.Chainage, 0.0, route.Length);
                var x = insertPoint.X + chainage * horizontalScale;
                var lane = GetProfileSituationLane(laneEnds, x, minLabelGap);
                var labelTop = insertPoint.Y + profileHeight + 4.0 + lane * 5.0;

                SymbolRenderer.DrawProfileSituationSymbol(
                    modelSpace,
                    transaction,
                    insertPoint,
                    route,
                    c => TryGetDraftPipeElevation(route, tin, c, depth, out var elevation) ? elevation : (double?)null,
                    baseElevation,
                    horizontalScale,
                    verticalScale,
                    profileHeight,
                    item,
                    labelTop,
                    textHeight,
                    textStyleId,
                    sessionId,
                    AddSituationCommand.LayerName,
                    TextLayer);
            }
        }

        private static bool TryGetDraftPipeElevation(Route2D route, TinSurface tin, double chainage, double depth, out double elevation)
        {
            var point = route.GetPointAtChainage(chainage);
            if (!tin.TryGetElevation(point, out var ground))
            {
                elevation = 0.0;
                return false;
            }

            elevation = ground - depth;
            return true;
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

        private static void DrawProfileTable(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            IReadOnlyList<ProfilePoint> profilePoints,
            TinSurface tin,
            double depth,
            double pipeOuterDiameter,
            double sandBeddingThickness,
            double horizontalScale,
            ObjectId textStyleId,
            IReadOnlyList<SituationItem> situationItems,
            string pipeLabel,
            string foundationLabel,
            string sessionId)
        {
            const double labelWidth = 36.0;
            const double textHeight = 1.45;
            const double valueTextHeight = 1.10;

            var tableTop = insertPoint.Y - 12.0;
            var tableLeft = insertPoint.X - labelWidth;
            var tableRight = insertPoint.X + route.Length * horizontalScale;
            var rows = GetGostTableRows();
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
                AddText(
                    modelSpace,
                    transaction,
                    rows[row].Label,
                    tableLeft + 1.0,
                    GetRowCenter(tableTop, rows, row) - textHeight * 0.45,
                    textHeight,
                    0.0,
                    TextLayer,
                    textStyleId,
                    sessionId,
                    ProfileEntityXData.RoleTable);
            }

            var pipeDesignStations = BuildPipeDesignStations(route, situationItems);
            var tableStations = BuildTableValueStations(route, pipeDesignStations, situationItems);
            var labelLanes = BuildTableLabelLanes(tableStations, horizontalScale);
            foreach (var station in tableStations)
            {
                var chainage = station.Chainage;
                var x = insertPoint.X + chainage * horizontalScale;
                var lane = GetTableLabelLane(labelLanes, chainage);
                AddTableStationLine(modelSpace, transaction, x, tableTop, tableBottom, rows, sessionId);

                var planPoint = route.GetPointAtChainage(chainage);
                var hasGround = tin.TryGetElevation(planPoint, out var ground);
                var pipeElevation = ground - depth;
                var trenchBottom = pipeElevation - pipeOuterDiameter - sandBeddingThickness;
                var trenchDepth = ground - trenchBottom;

                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(ground) : "-", x, tableTop, rows, 0, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(ground) : "-", x, tableTop, rows, 1, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(trenchBottom) : "-", x, tableTop, rows, 2, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(pipeElevation) : "-", x, tableTop, rows, 3, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, hasGround ? FormatNumber(trenchDepth) : "-", x, tableTop, rows, 4, lane, valueTextHeight, textStyleId, sessionId);
                AddVerticalTableValue(modelSpace, transaction, FormatTablePicket(chainage), x, tableTop, rows, 8, lane, valueTextHeight, textStyleId, sessionId);
            }

            AddCenteredSpanText(modelSpace, transaction, pipeLabel, insertPoint.X, tableRight, GetRowCenter(tableTop, rows, 5), textHeight, textStyleId, sessionId);
            AddCenteredSpanText(modelSpace, transaction, foundationLabel, insertPoint.X, tableRight, GetRowCenter(tableTop, rows, 6), textHeight, textStyleId, sessionId);
            DrawDistanceRow(modelSpace, transaction, insertPoint, route, tableStations, horizontalScale, tableTop, rows, textHeight, textStyleId, sessionId);
            DrawDraftSlopeRows(modelSpace, transaction, insertPoint, BuildDraftPipeSlopeSegments(route, tin, pipeDesignStations, depth), horizontalScale, tableTop, rows, textHeight, textStyleId, sessionId);

            DrawUnfoldedPlan(
                modelSpace,
                transaction,
                insertPoint,
                route,
                GetRowCenter(tableTop, rows, 10),
                horizontalScale,
                textStyleId,
                situationItems,
                sessionId);
        }

        private static void DrawUnfoldedPlan(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            double planY,
            double horizontalScale,
            ObjectId textStyleId,
            IReadOnlyList<SituationItem> situationItems,
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

            var marks = new List<SituationSymbol>();
            for (var i = 0; i < route.Vertices.Count; i++)
            {
                var chainage = route.GetVertexChainage(i);
                var kind = i == 0 ? "Н" : i == route.Vertices.Count - 1 ? "К" : $"УП{i}";
                var symbolKind = i == 0
                    ? SituationSymbolKind.RouteStart
                    : i == route.Vertices.Count - 1
                        ? SituationSymbolKind.RouteEnd
                        : SituationSymbolKind.Bend;
                marks.Add(SituationSymbol.Point(symbolKind, chainage, kind, FormatPicket(chainage)));
            }

            for (var chainage = 100.0; chainage < route.Length - 0.001; chainage += 100.0)
            {
                marks.Add(SituationSymbol.Point(SituationSymbolKind.Picket, chainage, $"ПК{(int)(chainage / 100.0)}", string.Empty));
            }

            for (var i = 0; i < route.SegmentCount; i++)
            {
                var start = route.GetVertexChainage(i);
                var end = route.GetVertexChainage(i + 1);
                var midX = insertPoint.X + (start + end) * 0.5 * horizontalScale;
                var length = end - start;
                AddText(
                    modelSpace,
                    transaction,
                    length.ToString("0.00", CultureInfo.InvariantCulture),
                    midX - 2.5,
                    planY + 5.2,
                    textHeight,
                    0.0,
                    TextLayer,
                    textStyleId,
                    sessionId,
                    ProfileEntityXData.RoleUnfoldedPlan);
            }

            foreach (var item in situationItems)
            {
                if (item.Chainage < -0.001 || item.Chainage > route.Length + 0.001)
                {
                    continue;
                }

                if (item.IsCasing)
                {
                    var casingLabel = item.Casing?.UnfoldedPlanLabel ?? item.Type;
                    marks.Add(SituationSymbol.Casing(
                        item.StartChainage,
                        item.EndChainage,
                        item.Casing?.ProfileLabel ?? item.Type,
                        FormatPicket(item.StartChainage),
                        FormatPicket(item.EndChainage),
                        casingLabel,
                        item.Casing?.ControlTubeChainage,
                        item.Casing?.HasControlTube ?? true,
                        item.Casing?.NormOffsetPlanLabel ?? string.Empty));
                }
                else
                {
                    marks.Add(SituationSymbol.Point(
                        SituationSymbolKind.Crossing,
                        item.Chainage,
                        item.Type,
                        item.Picket,
                        true,
                        item.Name));
                }
            }

            var laneEnds = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var mark in marks.OrderBy(m => m.Chainage).ThenBy(m => m.IsSituation ? 1 : 0))
            {
                var x = insertPoint.X + mark.Chainage * horizontalScale;
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

                if (mark.IsCasing)
                {
                    var startChainage = Clamp(mark.StartChainage, 0.0, route.Length);
                    var endChainage = Clamp(mark.EndChainage, 0.0, route.Length);
                    var startX = insertPoint.X + startChainage * horizontalScale;
                    var endX = insertPoint.X + endChainage * horizontalScale;
                    SymbolRenderer.DrawUnfoldedPlanCasing(
                        modelSpace,
                        transaction,
                        startX,
                        endX,
                        planY,
                        mark.Name,
                        FormatPicket(startChainage),
                        FormatPicket(endChainage),
                        textHeight,
                        textStyleId,
                        sessionId,
                        AddSituationCommand.LayerName,
                        TextLayer,
                        mark.HasControlTube && mark.ControlTubeChainage.HasValue
                            ? insertPoint.X + Clamp(mark.ControlTubeChainage.Value, startChainage, endChainage) * horizontalScale
                            : (double?)null,
                        mark.Note);
                    continue;
                }

                SymbolRenderer.DrawUnfoldedPlanPointMark(
                    modelSpace,
                    transaction,
                    x,
                    planY,
                    mark,
                    lane * 4.2,
                    textHeight,
                    textStyleId,
                    sessionId,
                    GridLayer,
                    AddSituationCommand.LayerName,
                    TextLayer);
            }
        }

        private static IReadOnlyList<SituationItem> CollectSituationItems(Database database, Transaction transaction, Route2D route, Editor editor)
        {
            var result = new List<SituationItem>();
            var modelSpace = GetModelSpace(database, transaction, OpenMode.ForRead);

            foreach (ObjectId objectId in modelSpace)
            {
                var blockReference = transaction.GetObject(objectId, OpenMode.ForRead, false) as BlockReference;
                if (blockReference == null)
                {
                    continue;
                }

                var block = transaction.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                if (block == null)
                {
                    continue;
                }

                var attrs = ReadAttributes(transaction, blockReference);
                var blockName = GetEffectiveBlockName(transaction, blockReference, block);
                if (!string.Equals(blockName, AddSituationCommand.BlockName, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryReadLibraryCasing(route, blockName, blockReference, block, transaction, attrs, editor, out var casingItem))
                    {
                        result.Add(casingItem);
                    }

                    continue;
                }

                if (!attrs.TryGetValue(AddSituationCommand.AttrChainage, out var chainageText) ||
                    !double.TryParse(chainageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var chainage))
                {
                    continue;
                }

                attrs.TryGetValue(AddSituationCommand.AttrType, out var type);
                attrs.TryGetValue(AddSituationCommand.AttrName, out var name);
                attrs.TryGetValue(AddSituationCommand.AttrPicket, out var picket);
                attrs.TryGetValue(AddSituationCommand.AttrLength, out var lengthText);
                var length = double.TryParse(lengthText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLength)
                    ? parsedLength
                    : 0.0;

                result.Add(new SituationItem(
                    chainage,
                    string.IsNullOrWhiteSpace(type) ? "Сит." : type,
                    string.IsNullOrWhiteSpace(name) ? "" : name,
                    string.IsNullOrWhiteSpace(picket) ? FormatPicket(chainage) : FormatPicketText(picket),
                    length));
            }

            result.Sort((a, b) => a.Chainage.CompareTo(b.Chainage));
            return result;
        }

        private static IReadOnlyList<SituationSymbol> BuildSituationSymbols(IReadOnlyList<SituationItem> situationItems)
        {
            var result = new List<SituationSymbol>();
            foreach (var item in situationItems)
            {
                if (item.IsCasing)
                {
                    result.Add(SituationSymbol.Casing(
                        item.StartChainage,
                        item.EndChainage,
                        "Футляр",
                        FormatPicket(item.StartChainage),
                        FormatPicket(item.EndChainage),
                        item.Casing?.UnfoldedPlanLabel ?? item.Name,
                        item.Casing?.ControlTubeChainage,
                        item.Casing?.HasControlTube ?? true,
                        item.Casing?.NormOffsetPlanLabel ?? string.Empty));
                    continue;
                }

                result.Add(SituationSymbol.Point(
                    GetSymbolKind(item.Type),
                    item.Chainage,
                    item.Type,
                    item.Type.StartsWith("ПК", StringComparison.OrdinalIgnoreCase) ? string.Empty : FormatPicket(item.Chainage),
                    !IsRoutePlanMark(item.Type),
                    item.Name));
            }

            return result.OrderBy(s => s.Chainage).ToList();
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

        private static bool TryReadLibraryCasing(
            Route2D route,
            string blockName,
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction,
            IReadOnlyDictionary<string, string> attrs,
            Editor editor,
            out SituationItem item)
        {
            item = default!;

            attrs.TryGetValue("DN", out var dn);
            var isCasingBlock = !string.IsNullOrWhiteSpace(blockName) &&
                blockName.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0;
            var isCasingAttribute = !string.IsNullOrWhiteSpace(dn) &&
                dn.IndexOf("Футляр", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isCasingBlock && !isCasingAttribute)
            {
                return false;
            }

            var hasActualCache = TryReadCasingIntervalFromAttributes(
                route,
                blockName,
                blockReference,
                block,
                transaction,
                attrs,
                out var start,
                out var end,
                out var signatureMismatch);

            if (!hasActualCache)
            {
                if (!TryGetProjectedBlockGeometryInterval(route, blockReference, block, transaction, out start, out end) &&
                    !TryGetProjectedExtentsInterval(route, blockReference, out start, out end))
                {
                    return false;
                }
            }

            start = Math.Max(0.0, Math.Min(route.Length, start));
            end = Math.Max(0.0, Math.Min(route.Length, end));
            if (end < start)
            {
                var swap = start;
                start = end;
                end = swap;
            }

            var length = end - start;
            var center = (start + end) * 0.5;
            var casing = CasingData.FromAttributes(blockName, attrs, start, end);

            if (!hasActualCache)
            {
                UpdateCasingAttributes(transaction, blockReference, blockName, block, start, end);
                if (signatureMismatch)
                {
                    editor.WriteMessage(
                        $"\nФутляр {blockName}: геометрия изменилась, атрибуты ПК автоматически пересчитаны ({casing.PicketRange}).");
                }
            }

            item = new SituationItem(
                casing.CenterChainage,
                casing.ProfileLabel,
                casing.Name,
                casing.PicketRange,
                length,
                start,
                end,
                casing);
            return true;
        }

        private static bool TryReadCasingIntervalFromAttributes(
            Route2D route,
            string blockName,
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction,
            IReadOnlyDictionary<string, string> attrs,
            out double start,
            out double end,
            out bool signatureMismatch)
        {
            start = 0.0;
            end = 0.0;
            signatureMismatch = false;

            if (!attrs.TryGetValue("START_CHAINAGE", out var startText) ||
                !attrs.TryGetValue("END_CHAINAGE", out var endText) ||
                !attrs.TryGetValue("GEOM_SIGNATURE", out var savedSignature) ||
                !double.TryParse(startText, NumberStyles.Float, CultureInfo.InvariantCulture, out start) ||
                !double.TryParse(endText, NumberStyles.Float, CultureInfo.InvariantCulture, out end))
            {
                return false;
            }

            var currentSignature = ComputeCasingStateSignature(blockName, blockReference, block, transaction);
            if (!string.Equals(savedSignature, currentSignature, StringComparison.OrdinalIgnoreCase))
            {
                signatureMismatch = true;
                return false;
            }

            return start >= -0.001 &&
                end >= -0.001 &&
                start <= route.Length + 0.001 &&
                end <= route.Length + 0.001 &&
                Math.Abs(end - start) > 0.001;
        }

        private static void UpdateCasingAttributes(
            Transaction transaction,
            BlockReference blockReference,
            string blockName,
            BlockTableRecord block,
            double start,
            double end)
        {
            var center = (start + end) * 0.5;
            var length = end - start;
            var controlTubeChainage = start + Math.Abs(length) * 0.78;
            UpdateAttribute(transaction, blockReference, "START_CHAINAGE", FormatStorageNumber(start));
            UpdateAttribute(transaction, blockReference, "END_CHAINAGE", FormatStorageNumber(end));
            UpdateAttribute(transaction, blockReference, "START_PICKET", ChainageFormatter.Format(start));
            UpdateAttribute(transaction, blockReference, "END_PICKET", ChainageFormatter.Format(end));
            UpdateAttribute(transaction, blockReference, "CENTER_CHAINAGE", FormatStorageNumber(center));
            UpdateAttribute(transaction, blockReference, "CENTER_PICKET", ChainageFormatter.Format(center));
            UpdateAttribute(transaction, blockReference, "LENGTH", FormatStorageNumber(length));
            UpdateAttributeIfMissing(transaction, blockReference, CasingAttributeNames.Material, CasingAttributeNames.DefaultMaterial);
            UpdateAttributeIfMissing(transaction, blockReference, CasingAttributeNames.HasControlTube, CasingAttributeNames.DefaultHasControlTube);
            UpdateAttribute(transaction, blockReference, CasingAttributeNames.ControlTubeChainage, FormatStorageNumber(controlTubeChainage));
            UpdateAttribute(transaction, blockReference, CasingAttributeNames.ControlTubePicket, ChainageFormatter.Format(controlTubeChainage));
            UpdateAttributeIfMissing(transaction, blockReference, CasingAttributeNames.NormOffsetStart, FormatStorageNumber(CasingAttributeNames.DefaultNormOffset));
            UpdateAttributeIfMissing(transaction, blockReference, CasingAttributeNames.NormOffsetEnd, FormatStorageNumber(CasingAttributeNames.DefaultNormOffset));
            UpdateAttribute(transaction, blockReference, "GEOM_SIGNATURE", ComputeCasingStateSignature(blockName, blockReference, block, transaction));
        }

        private static void UpdateAttributeIfMissing(
            Transaction transaction,
            BlockReference blockReference,
            string tag,
            string value)
        {
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null &&
                    string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(attribute.TextString))
                {
                    return;
                }
            }

            UpdateAttribute(transaction, blockReference, tag, value);
        }

        private static void UpdateAttribute(
            Transaction transaction,
            BlockReference blockReference,
            string tag,
            string value)
        {
            blockReference.UpgradeOpen();
            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                if (attribute == null || !string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                attribute.TextString = value;
                return;
            }

            var newAttribute = new AttributeReference
            {
                Tag = tag,
                TextString = value,
                Position = blockReference.Position,
                Height = 1.0,
                Invisible = true
            };

            blockReference.AttributeCollection.AppendAttribute(newAttribute);
            transaction.AddNewlyCreatedDBObject(newAttribute, true);
        }

        private static bool TryGetProjectedBlockGeometryInterval(
            Route2D route,
            BlockReference blockReference,
            BlockTableRecord block,
            Transaction transaction,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;
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

            return TryGetProjectedAxisInterval(route, points, out start, out end);
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

        private static bool TryGetProjectedAxisInterval(
            Route2D route,
            IReadOnlyList<Point2> points,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;
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
            var startPoint = AverageProjectedEnd(points, projections, minProjection, tolerance);
            var endPoint = AverageProjectedEnd(points, projections, maxProjection, tolerance);
            start = route.GetClosestChainage(startPoint);
            end = route.GetClosestChainage(endPoint);
            return Math.Abs(end - start) > 0.001;
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

        private static void AddProjectedChainage(Route2D route, Point2 point, ref double start, ref double end)
        {
            var chainage = route.GetClosestChainage(point);
            if (chainage < start)
            {
                start = chainage;
            }

            if (chainage > end)
            {
                end = chainage;
            }
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

        private static bool TryGetProjectedExtentsInterval(
            Route2D route,
            BlockReference blockReference,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;

            Extents3d extents;
            try
            {
                extents = blockReference.GeometricExtents;
            }
            catch
            {
                return false;
            }

            var min = extents.MinPoint;
            var max = extents.MaxPoint;
            var corners = new[]
            {
                new Point2(min.X, min.Y),
                new Point2(min.X, max.Y),
                new Point2(max.X, min.Y),
                new Point2(max.X, max.Y)
            };

            start = double.MaxValue;
            end = double.MinValue;

            foreach (var corner in corners)
            {
                var chainage = route.GetClosestChainage(corner);
                if (chainage < start)
                {
                    start = chainage;
                }

                if (chainage > end)
                {
                    end = chainage;
                }
            }

            return start < double.MaxValue && end > double.MinValue;
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

        private static IReadOnlyList<TableValueStation> BuildTableValueStations(
            Route2D route,
            IReadOnlyList<double> pipeDesignStations,
            IReadOnlyList<SituationItem> situationItems)
        {
            var candidates = new List<TableValueStation>();
            candidates.Add(new TableValueStation(0.0, "Н", 100));
            candidates.Add(new TableValueStation(route.Length, "К", 100));

            for (var i = 1; i < route.Vertices.Count - 1; i++)
            {
                candidates.Add(new TableValueStation(route.GetVertexChainage(i), $"УП{i}", 90));
            }

            foreach (var chainage in pipeDesignStations)
            {
                candidates.Add(new TableValueStation(chainage, "ПТ", 95));
            }

            foreach (var item in situationItems)
            {
                if (item.IsCasing)
                {
                    candidates.Add(new TableValueStation(item.StartChainage, "ФутН", 95));
                    candidates.Add(new TableValueStation(item.EndChainage, "ФутК", 95));
                }
                else
                {
                    candidates.Add(new TableValueStation(item.Chainage, GetSituationKind(item.Type), 95));
                }
            }

            return MergeCloseTableStations(candidates);
        }

        private static IReadOnlyList<double> BuildPipeDesignStations(
            Route2D route,
            IReadOnlyList<SituationItem> situationItems)
        {
            var candidates = new List<double>
            {
                0.0,
                route.Length
            };

            for (var i = 1; i < route.Vertices.Count - 1; i++)
            {
                candidates.Add(route.GetVertexChainage(i));
            }

            foreach (var item in situationItems)
            {
                if (item.IsCasing)
                {
                    candidates.Add(Clamp(item.StartChainage, 0.0, route.Length));
                    candidates.Add(Clamp(item.EndChainage, 0.0, route.Length));
                }
                else
                {
                    candidates.Add(Clamp(item.Chainage, 0.0, route.Length));
                }
            }

            return candidates
                .Where(c => c >= -0.001 && c <= route.Length + 0.001)
                .OrderBy(c => c)
                .GroupBy(c => Math.Round(c, 3))
                .Select(g => g.First())
                .ToList();
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
                if (result.Count == 0)
                {
                    result.Add(candidate);
                    continue;
                }

                var previous = result[result.Count - 1];
                if (Math.Abs(candidate.Chainage - previous.Chainage) >= minGap)
                {
                    result.Add(candidate);
                    continue;
                }

                if (candidate.Priority > previous.Priority)
                {
                    result[result.Count - 1] = candidate;
                }
            }

            return result;
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static string GetSituationKind(string type)
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

        private static string GetStationKind(ProfilePointType type)
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

        private static int GetStationPriority(ProfilePointType type)
        {
            switch (type)
            {
                case ProfilePointType.Start:
                case ProfilePointType.End:
                    return 100;
                case ProfilePointType.RouteVertex:
                    return 90;
                default:
                    return 20;
            }
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

        private static void DrawDraftSlopeRows(
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
            var pad = 0.7;
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

        private static IReadOnlyList<PipeSlopeSegment> BuildDraftPipeSlopeSegments(
            Route2D route,
            TinSurface tin,
            IReadOnlyList<double> pipeStations,
            double depth)
        {
            var result = new List<PipeSlopeSegment>();
            for (var i = 1; i < pipeStations.Count; i++)
            {
                var startChainage = pipeStations[i - 1];
                var endChainage = pipeStations[i];
                if (endChainage - startChainage < 0.000001)
                {
                    continue;
                }

                var startPoint = route.GetPointAtChainage(startChainage);
                var endPoint = route.GetPointAtChainage(endChainage);
                if (!tin.TryGetElevation(startPoint, out var startGround) || !tin.TryGetElevation(endPoint, out var endGround))
                {
                    continue;
                }

                result.Add(new PipeSlopeSegment(startChainage, endChainage, startGround - depth, endGround - depth));
            }

            return result;
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
            AddText(
                modelSpace,
                transaction,
                value,
                x + 0.8,
                GetRowCenter(tableTop, rows, row) - textHeight * 0.45,
                textHeight,
                0.0,
                TextLayer,
                textStyleId,
                sessionId,
                ProfileEntityXData.RoleTable);
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
            line.LineWeight = GetLineWeight(layer, role);
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
            circle.LineWeight = GetLineWeight(layer, role);
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
            polyline.LineWeight = GetLineWeight(layer, role);
            for (var i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
            }

            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            ProfileEntityXData.Attach(polyline, sessionId ?? string.Empty, role ?? string.Empty);
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
            EnsureLayer(layerTable, transaction, GroundLayer, 3, GroundLineWeight);
            EnsureLayer(layerTable, transaction, PipeLayer, 1, PipeLineWeight);
            EnsureLayer(layerTable, transaction, GridLayer, 8, GridLineWeight);
            EnsureLayer(layerTable, transaction, TextLayer, 7, LineWeight.LineWeight013);
            EnsureLayer(layerTable, transaction, AddSituationCommand.LayerName, 2, SituationLineWeight);
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

        private static LineWeight GetLineWeight(string layer, string? role)
        {
            if (string.Equals(layer, PipeLayer, StringComparison.OrdinalIgnoreCase))
            {
                return PipeLineWeight;
            }

            if (string.Equals(layer, GroundLayer, StringComparison.OrdinalIgnoreCase))
            {
                return GroundLineWeight;
            }

            if (string.Equals(layer, AddSituationCommand.LayerName, StringComparison.OrdinalIgnoreCase))
            {
                return SituationLineWeight;
            }

            if (string.Equals(role, ProfileEntityXData.RoleTable, StringComparison.OrdinalIgnoreCase))
            {
                return TableLineWeight;
            }

            if (string.Equals(role, ProfileEntityXData.RoleUnfoldedPlan, StringComparison.OrdinalIgnoreCase))
            {
                return UnfoldedPlanLineWeight;
            }

            return GridLineWeight;
        }

        private static string FormatHashNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        private static string FormatStorageNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatPicket(double chainage)
        {
            return FormatPicketText(ChainageFormatter.Format(chainage));
        }

        private static string FormatPicketText(string value)
        {
            return value.Replace('.', ',');
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

        private sealed class SituationItem
        {
            public SituationItem(double chainage, string type, string name, string picket, double length)
                : this(chainage, type, name, picket, length, chainage - length * 0.5, chainage + length * 0.5)
            {
            }

            public SituationItem(
                double chainage,
                string type,
                string name,
                string picket,
                double length,
                double startChainage,
                double endChainage)
                : this(chainage, type, name, picket, length, startChainage, endChainage, null)
            {
            }

            public SituationItem(
                double chainage,
                string type,
                string name,
                string picket,
                double length,
                double startChainage,
                double endChainage,
                CasingData? casing)
            {
                Chainage = chainage;
                Type = type;
                Name = name;
                Picket = picket;
                Length = length;
                StartChainage = startChainage;
                EndChainage = endChainage;
                Casing = casing;
            }

            public double Chainage { get; }

            public string Type { get; }

            public string Name { get; }

            public string Picket { get; }

            public double Length { get; }

            public double StartChainage { get; }

            public double EndChainage { get; }

            public CasingData? Casing { get; }

            public bool IsCasing
            {
                get
                {
                    return Length > 0.001 &&
                        !string.IsNullOrWhiteSpace(Type) &&
                        Type.Trim().ToLowerInvariant().Contains("фут");
                }
            }
        }

    }
}
