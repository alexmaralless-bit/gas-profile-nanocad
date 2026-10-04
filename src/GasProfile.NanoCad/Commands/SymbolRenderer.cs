using System;
using System.Collections.Generic;
using System.Linq;
using GasProfile.Core.Geometry;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    internal static class SymbolRenderer
    {
        private static readonly LineWeight SymbolLineWeight = LineWeight.LineWeight025;

        internal static void DrawUnfoldedPlanCasing(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double startX,
            double endX,
            double planY,
            string label,
            string startPicket,
            string endPicket,
            double textHeight,
            ObjectId textStyleId,
            string sessionId,
            string geometryLayer,
            string textLayer,
            double? controlTubeX = null,
            string note = "")
        {
            if (endX < startX)
            {
                var temp = startX;
                startX = endX;
                endX = temp;
            }

            var width = Math.Max(0.01, endX - startX);
            AddLine(modelSpace, transaction, startX, planY + 1.25, endX, planY + 1.25, geometryLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, startX, planY - 1.25, endX, planY - 1.25, geometryLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, startX, planY + 1.25, startX, planY - 1.25, geometryLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            AddLine(modelSpace, transaction, endX, planY + 1.25, endX, planY - 1.25, geometryLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);

            if (controlTubeX.HasValue)
            {
                var x = Math.Max(startX, Math.Min(endX, controlTubeX.Value));
                AddCircle(modelSpace, transaction, x, planY + 1.25, 0.45, geometryLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            }

            var centerX = (startX + endX) * 0.5;
            AddText(
                modelSpace,
                transaction,
                label,
                centerX - EstimateTextWidth(label, textHeight) * 0.5,
                planY + 2.3,
                textHeight,
                0.0,
                textLayer,
                textStyleId,
                sessionId,
                ProfileEntityXData.RoleUnfoldedPlan);
            if (!string.IsNullOrWhiteSpace(note))
            {
                AddText(
                    modelSpace,
                    transaction,
                    note,
                    centerX - EstimateTextWidth(note, textHeight) * 0.5,
                    planY + 3.9,
                    textHeight,
                    0.0,
                    textLayer,
                    textStyleId,
                    sessionId,
                    ProfileEntityXData.RoleUnfoldedPlan);
            }

            AddText(
                modelSpace,
                transaction,
                $"{startPicket}-{endPicket}",
                startX,
                planY - 5.7,
                textHeight,
                0.0,
                textLayer,
                textStyleId,
                sessionId,
                ProfileEntityXData.RoleUnfoldedPlan);
        }

        internal static void DrawProfileCasing(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            Func<double, double?> pipeElevationAt,
            double startChainage,
            double endChainage,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            string sessionId,
            string layer,
            bool drawControlTube = true,
            double? controlTubeChainage = null)
        {
            if (endChainage < startChainage)
            {
                var temp = startChainage;
                startChainage = endChainage;
                endChainage = temp;
            }

            if (endChainage - startChainage < 0.001)
            {
                return;
            }

            var chainages = new List<double> { startChainage };
            for (var i = 1; i < route.Vertices.Count - 1; i++)
            {
                var vertexChainage = route.GetVertexChainage(i);
                if (vertexChainage > startChainage + 0.001 && vertexChainage < endChainage - 0.001)
                {
                    chainages.Add(vertexChainage);
                }
            }

            chainages.Add(endChainage);
            chainages = chainages.Distinct().OrderBy(c => c).ToList();

            const double casingOffset = 0.45;
            var upper = new List<Point2d>();
            var lower = new List<Point2d>();
            foreach (var chainage in chainages)
            {
                var elevation = pipeElevationAt(chainage);
                if (!elevation.HasValue)
                {
                    continue;
                }

                var x = insertPoint.X + chainage * horizontalScale;
                var y = insertPoint.Y + (elevation.Value - baseElevation) * verticalScale;
                upper.Add(new Point2d(x, y + casingOffset));
                lower.Add(new Point2d(x, y - casingOffset));
            }

            if (upper.Count < 2 || lower.Count < 2)
            {
                return;
            }

            AddPolyline(modelSpace, transaction, upper, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
            AddPolyline(modelSpace, transaction, lower, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
            AddLine(modelSpace, transaction, upper[0].X, upper[0].Y, lower[0].X, lower[0].Y, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
            AddLine(modelSpace, transaction, upper[upper.Count - 1].X, upper[upper.Count - 1].Y, lower[lower.Count - 1].X, lower[lower.Count - 1].Y, layer, sessionId, ProfileEntityXData.RoleProfileSituation);

            if (drawControlTube)
            {
                var controlChainage = controlTubeChainage ?? startChainage + (endChainage - startChainage) * 0.78;
                controlChainage = Math.Max(startChainage, Math.Min(endChainage, controlChainage));
                var controlElevation = pipeElevationAt(controlChainage);
                if (controlElevation.HasValue)
                {
                    var x = insertPoint.X + controlChainage * horizontalScale;
                    var y = insertPoint.Y + (controlElevation.Value - baseElevation) * verticalScale + 2.2;
                    AddCircle(modelSpace, transaction, x, y, 0.55, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
                    AddLine(modelSpace, transaction, x, y - 1.65, x, y - 0.55, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
                }
            }
        }

        internal static void DrawProfileCrossing(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x,
            double profileBottomY,
            double profileTopY,
            double? pipeY,
            string pipeElevationLabel,
            double textHeight,
            ObjectId textStyleId,
            string sessionId,
            string geometryLayer,
            string textLayer)
        {
            AddLine(modelSpace, transaction, x, profileBottomY, x, profileTopY, geometryLayer, sessionId, ProfileEntityXData.RoleProfileSituation);
            if (pipeY.HasValue && !string.IsNullOrWhiteSpace(pipeElevationLabel))
            {
                AddText(
                    modelSpace,
                    transaction,
                    pipeElevationLabel,
                    x + 0.8,
                    pipeY.Value + 1.2,
                    textHeight,
                    0.0,
                    textLayer,
                    textStyleId,
                    sessionId,
                    ProfileEntityXData.RoleProfileSituation);
            }
        }

        internal static void DrawProfileSituationSymbol(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point3d insertPoint,
            Route2D route,
            Func<double, double?> pipeElevationAt,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double profileHeight,
            SituationSymbol symbol,
            double labelTopY,
            double textHeight,
            ObjectId textStyleId,
            string sessionId,
            string layer,
            string textLayer)
        {
            if (symbol.IsCasing)
            {
                var startX = insertPoint.X + Clamp(symbol.StartChainage, 0.0, route.Length) * horizontalScale;
                var endX = insertPoint.X + Clamp(symbol.EndChainage, 0.0, route.Length) * horizontalScale;
                AddLine(modelSpace, transaction, startX, insertPoint.Y, startX, insertPoint.Y + profileHeight, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
                AddLine(modelSpace, transaction, endX, insertPoint.Y, endX, insertPoint.Y + profileHeight, layer, sessionId, ProfileEntityXData.RoleProfileSituation);
                DrawProfileCasing(
                    modelSpace,
                    transaction,
                    insertPoint,
                    route,
                    pipeElevationAt,
                    Clamp(symbol.StartChainage, 0.0, route.Length),
                    Clamp(symbol.EndChainage, 0.0, route.Length),
                    baseElevation,
                    horizontalScale,
                    verticalScale,
                    sessionId,
                    layer,
                    symbol.HasControlTube,
                    symbol.ControlTubeChainage);
            }
            else
            {
                var chainage = Clamp(symbol.Chainage, 0.0, route.Length);
                var x = insertPoint.X + chainage * horizontalScale;
                var elevation = pipeElevationAt(chainage);
                var y = elevation.HasValue
                    ? insertPoint.Y + (elevation.Value - baseElevation) * verticalScale
                    : (double?)null;
                DrawProfileCrossing(
                    modelSpace,
                    transaction,
                    x,
                    insertPoint.Y,
                    insertPoint.Y + profileHeight,
                    y,
                    elevation.HasValue ? $"Zтр={elevation.Value:0.00}" : string.Empty,
                    textHeight,
                    textStyleId,
                    sessionId,
                    layer,
                    textLayer);
            }

            var labelX = insertPoint.X + Clamp(symbol.Chainage, 0.0, route.Length) * horizontalScale + 0.8;
            AddText(
                modelSpace,
                transaction,
                symbol.Label,
                labelX,
                labelTopY,
                textHeight,
                Math.PI / 2.0,
                textLayer,
                textStyleId,
                sessionId,
                ProfileEntityXData.RoleProfileSituation);

            if (!string.IsNullOrWhiteSpace(symbol.Note))
            {
                AddText(
                    modelSpace,
                    transaction,
                    symbol.Note,
                    labelX,
                    labelTopY - 1.7,
                    textHeight * 0.9,
                    Math.PI / 2.0,
                    textLayer,
                    textStyleId,
                    sessionId,
                    ProfileEntityXData.RoleProfileSituation);
            }
        }

        internal static void DrawUnfoldedPlanPointMark(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x,
            double planY,
            SituationSymbol symbol,
            double laneOffset,
            double textHeight,
            ObjectId textStyleId,
            string sessionId,
            string gridLayer,
            string situationLayer,
            string textLayer)
        {
            var kind = symbol.Label;
            var picket = symbol.Picket;
            var isSituation = symbol.IsSituation;
            var layer = isSituation ? situationLayer : gridLayer;
            AddLine(modelSpace, transaction, x, planY + 4.0, x, planY - 4.0, gridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);

            if (kind.StartsWith("УП", StringComparison.OrdinalIgnoreCase))
            {
                AddLine(modelSpace, transaction, x - 1.0, planY - 0.7, x, planY, gridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
                AddLine(modelSpace, transaction, x, planY, x + 1.0, planY - 0.7, gridLayer, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            }

            AddCircle(
                modelSpace,
                transaction,
                x,
                planY,
                isSituation ? 0.9 : 0.55,
                layer,
                sessionId,
                ProfileEntityXData.RoleUnfoldedPlan);

            var xOffset = isSituation ? 1.1 : 0.8;
            var topOffset = isSituation ? 1.6 : 1.0;
            var bottomOffset = isSituation ? 4.8 : 3.3;
            AddText(modelSpace, transaction, kind, x + xOffset, planY + topOffset + laneOffset, textHeight, 0.0, textLayer, textStyleId, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            if (!string.IsNullOrWhiteSpace(picket))
            {
                AddText(modelSpace, transaction, picket, x + xOffset, planY - bottomOffset - laneOffset, textHeight, 0.0, textLayer, textStyleId, sessionId, ProfileEntityXData.RoleUnfoldedPlan);
            }
        }

        private static void AddPolyline(
            BlockTableRecord modelSpace,
            Transaction transaction,
            IReadOnlyList<Point2d> points,
            string layer,
            string sessionId,
            string role)
        {
            if (points.Count < 2)
            {
                return;
            }

            var polyline = new Polyline
            {
                Layer = layer,
                Linetype = "Continuous",
                LineWeight = SymbolLineWeight
            };

            for (var i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
            }

            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            ProfileEntityXData.Attach(polyline, sessionId, role);
        }

        private static void AddLine(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x1,
            double y1,
            double x2,
            double y2,
            string layer,
            string sessionId,
            string role)
        {
            var line = new Line(new Point3d(x1, y1, 0.0), new Point3d(x2, y2, 0.0))
            {
                Layer = layer,
                Linetype = "Continuous",
                LineWeight = SymbolLineWeight
            };

            modelSpace.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            ProfileEntityXData.Attach(line, sessionId, role);
        }

        private static void AddCircle(
            BlockTableRecord modelSpace,
            Transaction transaction,
            double x,
            double y,
            double radius,
            string layer,
            string sessionId,
            string role)
        {
            var circle = new Circle(new Point3d(x, y, 0.0), Vector3d.ZAxis, radius)
            {
                Layer = layer,
                Linetype = "Continuous",
                LineWeight = SymbolLineWeight
            };

            modelSpace.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
            ProfileEntityXData.Attach(circle, sessionId, role);
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
            string sessionId,
            string role)
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
            ProfileEntityXData.Attach(dbText, sessionId, role);
        }

        private static double EstimateTextWidth(string value, double height)
        {
            return value.Length * height * 0.55;
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
