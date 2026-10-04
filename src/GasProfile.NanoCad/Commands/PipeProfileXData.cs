using System;
using System.Collections.Generic;
using System.Globalization;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    internal static class PipeProfileXData
    {
        internal const string AppName = "GASPROF_PIPE";
        internal const double DefaultPipeOuterDiameter = 0.110;
        internal const double DefaultSandBeddingThickness = 0.100;
        internal const string DefaultPipeLabel = "Труба ПЭ100 ГАЗ SDR11 DN110";
        internal const string DefaultFoundationLabel = "Песчаная подушка 100 мм";
        private const string Version = "GASPROF_PIPE_PROFILE_V1";
        private const string Version2 = "GASPROF_PIPE_PROFILE_V2";
        private const string Version3 = "GASPROF_PIPE_PROFILE_V3";
        private const string Version4 = "GASPROF_PIPE_PROFILE_V4";
        private const string Version5 = "GASPROF_PIPE_PROFILE_V5";

        internal static void EnsureRegApp(Database database, Transaction transaction)
        {
            var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(AppName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        internal static void Attach(
            Entity entity,
            double insertX,
            double insertY,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double routeLength)
        {
            Attach(
                entity,
                insertX,
                insertY,
                baseElevation,
                horizontalScale,
                verticalScale,
                routeLength,
                string.Empty,
                string.Empty,
                string.Empty,
                insertY - 12.0);
        }

        internal static void Attach(
            Entity entity,
            double insertX,
            double insertY,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double routeLength,
            string sessionId,
            string routeHandle,
            string surfaceLayer,
            double tableTopY)
        {
            Attach(
                entity,
                insertX,
                insertY,
                baseElevation,
                horizontalScale,
                verticalScale,
                routeLength,
                sessionId,
                routeHandle,
                surfaceLayer,
                tableTopY,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        internal static void Attach(
            Entity entity,
            double insertX,
            double insertY,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double routeLength,
            string sessionId,
            string routeHandle,
            string surfaceLayer,
            double tableTopY,
            string routeSignature,
            string surfaceSignature,
            string pipeSignature)
        {
            Attach(
                entity,
                insertX,
                insertY,
                baseElevation,
                horizontalScale,
                verticalScale,
                routeLength,
                sessionId,
                routeHandle,
                surfaceLayer,
                tableTopY,
                routeSignature,
                surfaceSignature,
                pipeSignature,
                DefaultPipeOuterDiameter,
                DefaultSandBeddingThickness,
                DefaultPipeLabel,
                DefaultFoundationLabel);
        }

        internal static void Attach(
            Entity entity,
            double insertX,
            double insertY,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double routeLength,
            string sessionId,
            string routeHandle,
            string surfaceLayer,
            double tableTopY,
            string routeSignature,
            string surfaceSignature,
            string pipeSignature,
            double pipeOuterDiameter,
            string pipeLabel,
            string foundationLabel)
        {
            Attach(
                entity,
                insertX,
                insertY,
                baseElevation,
                horizontalScale,
                verticalScale,
                routeLength,
                sessionId,
                routeHandle,
                surfaceLayer,
                tableTopY,
                routeSignature,
                surfaceSignature,
                pipeSignature,
                pipeOuterDiameter,
                DefaultSandBeddingThickness,
                pipeLabel,
                foundationLabel);
        }

        internal static void Attach(
            Entity entity,
            double insertX,
            double insertY,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double routeLength,
            string sessionId,
            string routeHandle,
            string surfaceLayer,
            double tableTopY,
            string routeSignature,
            string surfaceSignature,
            string pipeSignature,
            double pipeOuterDiameter,
            double sandBeddingThickness,
            string pipeLabel,
            string foundationLabel)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, Version5),
                new TypedValue((int)DxfCode.ExtendedDataReal, insertX),
                new TypedValue((int)DxfCode.ExtendedDataReal, insertY),
                new TypedValue((int)DxfCode.ExtendedDataReal, baseElevation),
                new TypedValue((int)DxfCode.ExtendedDataReal, horizontalScale),
                new TypedValue((int)DxfCode.ExtendedDataReal, verticalScale),
                new TypedValue((int)DxfCode.ExtendedDataReal, routeLength),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sessionId ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, routeHandle ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceLayer ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataReal, tableTopY),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, routeSignature ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, surfaceSignature ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, pipeSignature ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataReal, pipeOuterDiameter),
                new TypedValue((int)DxfCode.ExtendedDataReal, sandBeddingThickness),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, pipeLabel ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, foundationLabel ?? string.Empty));
        }

        internal static bool TryRead(Entity entity, out PipeProfileMetadata metadata)
        {
            metadata = default!;
            var buffer = entity.GetXDataForApplication(AppName);
            if (buffer == null)
            {
                return false;
            }

            var values = new List<TypedValue>(buffer.AsArray());
            if (values.Count < 8 ||
                !TryGetReal(values[2], out var insertX) ||
                !TryGetReal(values[3], out var insertY) ||
                !TryGetReal(values[4], out var baseElevation) ||
                !TryGetReal(values[5], out var horizontalScale) ||
                !TryGetReal(values[6], out var verticalScale) ||
                !TryGetReal(values[7], out var routeLength))
            {
                return false;
            }

            var version = values[1].Value as string;
            if (version != Version && version != Version2 && version != Version3 && version != Version4 && version != Version5)
            {
                return false;
            }

            if (horizontalScale <= 0.0 || verticalScale <= 0.0 || routeLength <= 0.0)
            {
                return false;
            }

            var sessionId = string.Empty;
            var routeHandle = string.Empty;
            var surfaceLayer = string.Empty;
            var tableTopY = insertY - 12.0;
            var routeSignature = string.Empty;
            var surfaceSignature = string.Empty;
            var pipeSignature = string.Empty;
            var pipeOuterDiameter = DefaultPipeOuterDiameter;
            var sandBeddingThickness = DefaultSandBeddingThickness;
            var pipeLabel = DefaultPipeLabel;
            var foundationLabel = DefaultFoundationLabel;

            if (version == Version2 || version == Version3 || version == Version4 || version == Version5)
            {
                if (values.Count < 12 ||
                    !TryGetString(values[8], out sessionId) ||
                    !TryGetString(values[9], out routeHandle) ||
                    !TryGetString(values[10], out surfaceLayer) ||
                    !TryGetReal(values[11], out tableTopY))
                {
                    return false;
                }
            }

            if (version == Version3 || version == Version4 || version == Version5)
            {
                if (values.Count < 15 ||
                    !TryGetString(values[12], out routeSignature) ||
                    !TryGetString(values[13], out surfaceSignature) ||
                    !TryGetString(values[14], out pipeSignature))
                {
                    return false;
                }
            }

            if (version == Version4)
            {
                if (values.Count < 18 ||
                    !TryGetReal(values[15], out pipeOuterDiameter) ||
                    !TryGetString(values[16], out pipeLabel) ||
                    !TryGetString(values[17], out foundationLabel))
                {
                    return false;
                }

                if (pipeOuterDiameter <= 0.0)
                {
                    pipeOuterDiameter = DefaultPipeOuterDiameter;
                }

                if (string.IsNullOrWhiteSpace(pipeLabel))
                {
                    pipeLabel = DefaultPipeLabel;
                }

                if (string.IsNullOrWhiteSpace(foundationLabel))
                {
                    foundationLabel = DefaultFoundationLabel;
                }
            }

            if (version == Version5)
            {
                if (values.Count < 19 ||
                    !TryGetReal(values[15], out pipeOuterDiameter) ||
                    !TryGetReal(values[16], out sandBeddingThickness) ||
                    !TryGetString(values[17], out pipeLabel) ||
                    !TryGetString(values[18], out foundationLabel))
                {
                    return false;
                }

                if (pipeOuterDiameter <= 0.0)
                {
                    pipeOuterDiameter = DefaultPipeOuterDiameter;
                }

                if (sandBeddingThickness < 0.0)
                {
                    sandBeddingThickness = DefaultSandBeddingThickness;
                }

                if (string.IsNullOrWhiteSpace(pipeLabel))
                {
                    pipeLabel = DefaultPipeLabel;
                }

                if (string.IsNullOrWhiteSpace(foundationLabel))
                {
                    foundationLabel = DefaultFoundationLabel;
                }
            }

            metadata = new PipeProfileMetadata(
                insertX,
                insertY,
                baseElevation,
                horizontalScale,
                verticalScale,
                routeLength,
                sessionId,
                routeHandle,
                surfaceLayer,
                tableTopY,
                routeSignature,
                surfaceSignature,
                pipeSignature,
                pipeOuterDiameter,
                sandBeddingThickness,
                pipeLabel,
                foundationLabel);
            return true;
        }

        internal static string FormatNumber(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static bool TryGetReal(TypedValue value, out double result)
        {
            if (value.Value is double number)
            {
                result = number;
                return true;
            }

            result = 0.0;
            return false;
        }

        private static bool TryGetString(TypedValue value, out string result)
        {
            if (value.Value is string text)
            {
                result = text;
                return true;
            }

            result = string.Empty;
            return false;
        }
    }

    internal sealed class PipeProfileMetadata
    {
        internal PipeProfileMetadata(
            double insertX,
            double insertY,
            double baseElevation,
            double horizontalScale,
            double verticalScale,
            double routeLength,
            string sessionId,
            string routeHandle,
            string surfaceLayer,
            double tableTopY,
            string routeSignature,
            string surfaceSignature,
            string pipeSignature,
            double pipeOuterDiameter,
            double sandBeddingThickness,
            string pipeLabel,
            string foundationLabel)
        {
            InsertX = insertX;
            InsertY = insertY;
            BaseElevation = baseElevation;
            HorizontalScale = horizontalScale;
            VerticalScale = verticalScale;
            RouteLength = routeLength;
            SessionId = sessionId;
            RouteHandle = routeHandle;
            SurfaceLayer = surfaceLayer;
            TableTopY = tableTopY;
            RouteSignature = routeSignature;
            SurfaceSignature = surfaceSignature;
            PipeSignature = pipeSignature;
            PipeOuterDiameter = pipeOuterDiameter;
            SandBeddingThickness = sandBeddingThickness;
            PipeLabel = pipeLabel;
            FoundationLabel = foundationLabel;
        }

        internal double InsertX { get; }

        internal double InsertY { get; }

        internal double BaseElevation { get; }

        internal double HorizontalScale { get; }

        internal double VerticalScale { get; }

        internal double RouteLength { get; }

        internal string SessionId { get; }

        internal string RouteHandle { get; }

        internal string SurfaceLayer { get; }

        internal double TableTopY { get; }

        internal string RouteSignature { get; }

        internal string SurfaceSignature { get; }

        internal string PipeSignature { get; }

        internal double PipeOuterDiameter { get; }

        internal double SandBeddingThickness { get; }

        internal string PipeLabel { get; }

        internal string FoundationLabel { get; }

        internal double GetChainage(double x)
        {
            return (x - InsertX) / HorizontalScale;
        }

        internal double GetElevation(double y)
        {
            return BaseElevation + (y - InsertY) / VerticalScale;
        }
    }
}
