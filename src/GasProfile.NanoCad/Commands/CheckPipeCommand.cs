using System;
using GasProfile.Core.Profile;
using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace GasProfile.NanoCad.Commands
{
    public sealed class CheckPipeCommand
    {
        [CommandMethod("GASPROF_CHECK_PIPE")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var database = document.Database;

            var pipeResult = editor.GetEntity("\nВыберите проектную линию трубы GASPROF_PROFILE_PIPE: ");
            if (pipeResult.Status != PromptStatus.OK)
            {
                return;
            }

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var pipe = transaction.GetObject(pipeResult.ObjectId, OpenMode.ForRead, false) as Polyline;
                if (pipe == null)
                {
                    editor.WriteMessage("\nВыбранный объект не является 2D Polyline.");
                    return;
                }

                if (!PipeProfileXData.TryRead(pipe, out var metadata))
                {
                    editor.WriteMessage("\nУ выбранной линии нет метаданных GASPROF_PIPE. Постройте профиль заново текущей версией GASPROF_DRAW_DRAFT.");
                    return;
                }

                editor.WriteMessage("\n--- GASPROF_CHECK_PIPE ---");
                editor.WriteMessage($"\nВершин проектной линии: {pipe.NumberOfVertices}");
                editor.WriteMessage($"\nБаза Z: {PipeProfileXData.FormatNumber(metadata.BaseElevation)}");
                editor.WriteMessage($"\nМасштаб: H={PipeProfileXData.FormatNumber(metadata.HorizontalScale)} ед./м, V={PipeProfileXData.FormatNumber(metadata.VerticalScale)} ед./м");

                var shown = Math.Min(pipe.NumberOfVertices, 12);
                for (var i = 0; i < shown; i++)
                {
                    var point = pipe.GetPoint2dAt(i);
                    var chainage = Clamp(metadata.GetChainage(point.X), 0.0, metadata.RouteLength);
                    var elevation = metadata.GetElevation(point.Y);
                    editor.WriteMessage($"\n{i + 1}: {ChainageFormatter.Format(chainage)} Zтр={PipeProfileXData.FormatNumber(elevation)}");
                }

                if (pipe.NumberOfVertices > shown)
                {
                    editor.WriteMessage($"\n... еще {pipe.NumberOfVertices - shown} вершин");
                }

                editor.WriteMessage("\n--------------------------");
                transaction.Commit();
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
