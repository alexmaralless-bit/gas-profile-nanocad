using System;
using System.Globalization;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class SettingsCommand
    {
        [CommandMethod("GASPROF_SETTINGS")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;
            var pipeResult = editor.GetEntity("\nВыберите проектную линию трубы профиля: ");
            if (pipeResult.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var pipePolyline = transaction.GetObject(pipeResult.ObjectId, OpenMode.ForWrite, false) as Polyline;
                if (pipePolyline == null)
                {
                    editor.WriteMessage("\nДля настроек нужна 2D Polyline проектной трубы на профиле.");
                    return;
                }

                if (!PipeProfileXData.TryRead(pipePolyline, out var metadata))
                {
                    editor.WriteMessage("\nУ проектной линии нет метаданных GASPROF_PIPE. Постройте профиль текущей GASPROF_DRAW_DRAFT.");
                    return;
                }

                var pipeOuterDiameter = GetDouble(editor, "\nНаружный диаметр газопровода, м", metadata.PipeOuterDiameter);
                var sandBeddingThickness = GetDouble(editor, "\nТолщина песчаной подушки, м", metadata.SandBeddingThickness);
                var pipeLabel = GetString(editor, "\nОбозначение трубы и тип изоляции", metadata.PipeLabel);
                var foundationLabel = GetString(editor, "\nОснование", metadata.FoundationLabel);

                PipeProfileXData.EnsureRegApp(database, transaction);
                PipeProfileXData.Attach(
                    pipePolyline,
                    metadata.InsertX,
                    metadata.InsertY,
                    metadata.BaseElevation,
                    metadata.HorizontalScale,
                    metadata.VerticalScale,
                    metadata.RouteLength,
                    metadata.SessionId,
                    metadata.RouteHandle,
                    metadata.SurfaceLayer,
                    metadata.TableTopY,
                    metadata.RouteSignature,
                    metadata.SurfaceSignature,
                    metadata.PipeSignature,
                    pipeOuterDiameter,
                    sandBeddingThickness,
                    pipeLabel,
                    foundationLabel);

                transaction.Commit();
                editor.WriteMessage("\n--- GASPROF_SETTINGS ---");
                editor.WriteMessage($"\nГоризонтальный коэффициент: {metadata.HorizontalScale.ToString("0.00", CultureInfo.InvariantCulture)} (только чтение в MVP)");
                editor.WriteMessage($"\nВертикальный коэффициент: {metadata.VerticalScale.ToString("0.00", CultureInfo.InvariantCulture)} (только чтение в MVP)");
                editor.WriteMessage($"\nНаружный диаметр: {pipeOuterDiameter.ToString("0.000", CultureInfo.InvariantCulture)} м");
                editor.WriteMessage($"\nПесчаная подушка: {sandBeddingThickness.ToString("0.000", CultureInfo.InvariantCulture)} м");
                editor.WriteMessage("\nНастройки сохранены. Запустите GREC для перестроения таблицы.");
                editor.WriteMessage("\n------------------------");
            }
        }

        private static double GetDouble(Editor editor, string message, double defaultValue)
        {
            var options = new PromptDoubleOptions($"{message} <{defaultValue.ToString("0.000", CultureInfo.InvariantCulture)}>: ")
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
    }
}
